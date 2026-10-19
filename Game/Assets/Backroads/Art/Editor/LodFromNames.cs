using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Backroads.Art.Editor
{
    /// <summary>
    /// LOD Groups from node names (07 sections 4.2 and 8.5). Every transform whose children carry an <c>_LOD&lt;n&gt;</c>
    /// suffix gets one LODGroup; same-suffix siblings join one level (Body_LOD1 and Glass_LOD1 are both level 1).
    /// Today's GLB vehicles name level 0 without a suffix (Body next to Body_LOD1); an unsuffixed sibling whose name
    /// is the base of a suffixed one, with no explicit _LOD0, is level 0. A child that has LOD children of its own
    /// (a PART_Door with Door_LOD0/1) gets its own group and is left out of its parent's.
    /// Unity's FBX importer also builds LOD Groups from these names; this replaces them, so one rule decides.
    /// </summary>
    public static class LodFromNames
    {
        /// <summary>Screen-relative heights where LOD0, LOD1 and LOD2 hand over to the next level (07 section 8.5).</summary>
        public static readonly float[] TransitionHeights = { 0.30f, 0.12f, 0.04f };

        /// <summary>Screen-relative height below which the last level is culled (07 section 8.5).</summary>
        public const float CullHeight = 0.01f;

        static readonly Regex Suffix = new Regex(@"^(?<base>.+)_LOD(?<n>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool TryParse(string name, out string baseName, out int level)
        {
            baseName = null; level = -1;
            if (string.IsNullOrEmpty(name)) return false;
            var m = Suffix.Match(name);
            if (!m.Success || !int.TryParse(m.Groups["n"].Value, out level)) { level = -1; return false; }
            baseName = m.Groups["base"].Value;
            return true;
        }

        /// <summary>Screen-relative transition height of level <paramref name="i"/> in a group of <paramref name="count"/> levels.</summary>
        public static float HeightFor(int i, int count)
        {
            if (i >= count - 1) return CullHeight;
            int last = TransitionHeights.Length - 1;
            if (i <= last) return TransitionHeights[i];
            // more than four levels: spread the extra ones evenly between LOD2's height and the cull height
            float h = TransitionHeights[last];
            return h - (h - CullHeight) * (i - last) / (count - 1 - last);
        }

        static bool HasLodChildren(Transform t)
        {
            foreach (Transform c in t)
                if (TryParse(c.name, out _, out _)) return true;
            return false;
        }

        static void CollectRenderers(Transform t, List<Renderer> into)
        {
            var r = t.GetComponent<Renderer>();
            if (r != null) into.Add(r);
            foreach (Transform c in t)
                if (!HasLodChildren(c)) CollectRenderers(c, into);
        }

        /// <summary>Builds the LOD Groups under <paramref name="root"/>; returns how many. Notes go to <paramref name="log"/>.</summary>
        public static int Build(GameObject root, List<string> log = null)
        {
            foreach (var old in root.GetComponentsInChildren<LODGroup>(true)) UnityEngine.Object.DestroyImmediate(old);
            int built = 0;
            foreach (var p in root.GetComponentsInChildren<Transform>(true))
            {
                var levels = new SortedDictionary<int, List<Renderer>>();
                var hasLevel0 = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (Transform c in p)
                {
                    if (!TryParse(c.name, out var b, out var n)) continue;
                    if (!levels.TryGetValue(n, out var list)) levels[n] = list = new List<Renderer>();
                    CollectRenderers(c, list);
                    hasLevel0.TryGetValue(b, out var had);
                    hasLevel0[b] = had || n == 0;
                }
                if (levels.Count == 0) continue;
                foreach (Transform c in p)
                {
                    if (TryParse(c.name, out _, out _) || !hasLevel0.TryGetValue(c.name, out var had) || had) continue;
                    if (!levels.TryGetValue(0, out var l0)) levels[0] = l0 = new List<Renderer>();
                    CollectRenderers(c, l0);   // legacy: "Body" next to "Body_LOD1"
                }

                var keys = new List<int>(levels.Keys);
                if (keys[0] != 0 || keys[keys.Count - 1] != keys.Count - 1)
                    log?.Add($"{p.name}: LOD levels {string.Join(",", keys)} are not 0..{keys.Count - 1}; used in that order");
                var lods = new LOD[keys.Count];
                for (int i = 0; i < keys.Count; i++)
                    lods[i] = new LOD(HeightFor(i, keys.Count), levels[keys[i]].ToArray());

                var g = p.gameObject.AddComponent<LODGroup>();
                g.fadeMode = LODFadeMode.CrossFade;
                g.animateCrossFading = true;
                g.SetLODs(lods);
                SetBounds(g, levels[keys[0]]);
                built++;
                log?.Add($"{p.name}: LODGroup with {keys.Count} levels ({string.Join(" | ", Describe(levels))})");
            }
            return built;
        }

        static IEnumerable<string> Describe(SortedDictionary<int, List<Renderer>> levels)
        {
            foreach (var kv in levels)
            {
                var names = new List<string>();
                foreach (var r in kv.Value) names.Add(r.name);
                yield return $"{kv.Key}: {string.Join(",", names)}";
            }
        }

        /// <summary>Reference point and size from the LOD0 meshes, in the group's local space (no scene needed).</summary>
        static void SetBounds(LODGroup g, List<Renderer> lod0)
        {
            var toLocal = g.transform.worldToLocalMatrix;
            bool any = false;
            var b = new Bounds();
            foreach (var r in lod0)
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var mb = mf.sharedMesh.bounds;
                var m = toLocal * r.transform.localToWorldMatrix;
                for (int k = 0; k < 8; k++)
                {
                    var c = mb.center + Vector3.Scale(mb.extents, new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1));
                    var w = m.MultiplyPoint3x4(c);
                    if (!any) { b = new Bounds(w, Vector3.zero); any = true; } else b.Encapsulate(w);
                }
            }
            if (!any) { g.RecalculateBounds(); return; }
            g.localReferencePoint = b.center;
            g.size = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
        }
    }

    /// <summary><c>UCX_</c> nodes are convex collision hulls (07 section 4.2): a convex MeshCollider, no renderer.</summary>
    public static class CollisionHulls
    {
        public const string Prefix = "UCX_";

        public static int Build(GameObject root)
        {
            int n = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                var mf = t.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var mc = t.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = true;
                var mr = t.GetComponent<MeshRenderer>();
                if (mr != null) UnityEngine.Object.DestroyImmediate(mr);
                UnityEngine.Object.DestroyImmediate(mf);
                n++;
            }
            return n;
        }
    }
}
