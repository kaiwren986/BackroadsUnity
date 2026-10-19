using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Backroads.Art.Editor
{
    /// <summary>
    /// Compares two imports of the same model, one through glTFast (GLB) and one through the FBX pipeline: node names,
    /// transforms and bounds relative to each asset root, submeshes, triangles per submesh, and whether each FBX
    /// material slot is the project material the GLB's material name maps to (card T2.9 step 3).
    /// </summary>
    public static class ModelCompare
    {
        [Serializable]
        public class Node
        {
            public string name;
            public bool inGlb, inFbx;
            public Vector3 glbPosition, fbxPosition;
            public float positionDelta, rotationDeltaDeg, scaleDelta;
            public Vector3 glbMin, glbMax, fbxMin, fbxMax;
            public float boundsDelta;
            public int[] glbTris, fbxTris;
            public string[] glbMaterials, fbxMaterials, expectedMaterials;
            public bool slotsMatch;
        }

        [Serializable]
        public class Result
        {
            public string glb, fbx;
            public bool namesMatch, slotsMatch, trisMatch;
            public float maxPositionDelta, maxRotationDeltaDeg, maxScaleDelta, maxBoundsDelta, totalBoundsDelta;
            public Vector3 glbMin, glbMax, fbxMin, fbxMax;
            public string[] duplicateNames;
            public Node[] nodes;
        }

        /// <summary>Mesh-bearing transforms by name (the names the browser game looks up), plus duplicates.</summary>
        public static Dictionary<string, Transform> MeshNodes(GameObject root, List<string> duplicates)
        {
            var d = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (d.ContainsKey(mf.name)) duplicates?.Add(mf.name);
                else d[mf.name] = mf.transform;
            }
            return d;
        }

        static Matrix4x4 Rel(GameObject root, Transform t) => root.transform.worldToLocalMatrix * t.localToWorldMatrix;

        static Bounds RootBounds(Matrix4x4 m, Mesh mesh)
        {
            var mb = mesh.bounds;
            var b = new Bounds(m.MultiplyPoint3x4(mb.min), Vector3.zero);
            for (int k = 0; k < 8; k++)
                b.Encapsulate(m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents,
                    new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1))));
            return b;
        }

        static float MaxAbs(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));

        static int[] Tris(Mesh m)
        {
            var t = new int[m.subMeshCount];
            for (int i = 0; i < t.Length; i++) t[i] = (int)(m.GetIndexCount(i) / 3);
            return t;
        }

        static Material[] Materials(Transform t)
        {
            var r = t.GetComponent<Renderer>();
            return r != null ? r.sharedMaterials : new Material[0];   // no ?. on Unity objects (fake null in the editor)
        }

        public static Result Compare(GameObject glb, GameObject fbx)
        {
            var dup = new List<string>();
            var a = MeshNodes(glb, dup);
            var b = MeshNodes(fbx, dup);
            var res = new Result
            {
                glb = AssetDatabase.GetAssetPath(glb), fbx = AssetDatabase.GetAssetPath(fbx),
                namesMatch = a.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(b.Keys.OrderBy(k => k, StringComparer.Ordinal)),
                slotsMatch = true, trisMatch = true, duplicateNames = dup.ToArray(),
            };
            var nodes = new List<Node>();
            bool anyA = false, anyB = false;
            Bounds ua = default, ub = default;
            foreach (var name in a.Keys.Union(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                var n = new Node { name = name, inGlb = a.ContainsKey(name), inFbx = b.ContainsKey(name) };
                nodes.Add(n);
                if (!n.inGlb || !n.inFbx) { res.slotsMatch = false; res.trisMatch = false; continue; }
                var ta = a[name]; var tb = b[name];
                var ma = Rel(glb, ta); var mb = Rel(fbx, tb);
                n.glbPosition = ma.GetColumn(3); n.fbxPosition = mb.GetColumn(3);
                n.positionDelta = MaxAbs(n.glbPosition - n.fbxPosition);
                n.rotationDeltaDeg = Quaternion.Angle(ma.rotation, mb.rotation);
                n.scaleDelta = MaxAbs(ma.lossyScale - mb.lossyScale);
                var meshA = ta.GetComponent<MeshFilter>().sharedMesh;
                var meshB = tb.GetComponent<MeshFilter>().sharedMesh;
                if (meshA != null && meshB != null)
                {
                    var ba = RootBounds(ma, meshA); var bb = RootBounds(mb, meshB);
                    n.glbMin = ba.min; n.glbMax = ba.max; n.fbxMin = bb.min; n.fbxMax = bb.max;
                    n.boundsDelta = Mathf.Max(MaxAbs(ba.min - bb.min), MaxAbs(ba.max - bb.max));
                    if (!anyA) { ua = ba; anyA = true; } else ua.Encapsulate(ba);
                    if (!anyB) { ub = bb; anyB = true; } else ub.Encapsulate(bb);
                    n.glbTris = Tris(meshA); n.fbxTris = Tris(meshB);
                    if (!n.glbTris.SequenceEqual(n.fbxTris)) res.trisMatch = false;
                }
                var matsA = Materials(ta);
                var matsB = Materials(tb);
                n.glbMaterials = matsA.Select(m => m == null ? "" : m.name).ToArray();
                n.fbxMaterials = matsB.Select(m => m == null ? "" : AssetDatabase.GetAssetPath(m) == res.fbx ? m.name + " (embedded)" : AssetDatabase.GetAssetPath(m)).ToArray();
                n.expectedMaterials = n.glbMaterials.Select(s => { var slot = VehicleMaterialSlots.SlotFor(s); return slot == null ? "(unmapped " + s + ")" : VehicleMaterialSlots.PathOf(slot); }).ToArray();
                n.slotsMatch = n.fbxMaterials.SequenceEqual(n.expectedMaterials);
                if (!n.slotsMatch) res.slotsMatch = false;
                res.maxPositionDelta = Mathf.Max(res.maxPositionDelta, n.positionDelta);
                res.maxRotationDeltaDeg = Mathf.Max(res.maxRotationDeltaDeg, n.rotationDeltaDeg);
                res.maxScaleDelta = Mathf.Max(res.maxScaleDelta, n.scaleDelta);
                res.maxBoundsDelta = Mathf.Max(res.maxBoundsDelta, n.boundsDelta);
            }
            res.nodes = nodes.ToArray();
            if (anyA && anyB)
            {
                res.glbMin = ua.min; res.glbMax = ua.max; res.fbxMin = ub.min; res.fbxMax = ub.max;
                res.totalBoundsDelta = Mathf.Max(MaxAbs(ua.min - ub.min), MaxAbs(ua.max - ub.max));
            }
            return res;
        }
    }
}
