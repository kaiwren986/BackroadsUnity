// Site meshes in a scene (card sites-S4 steps 5-6), on lane R's chunk format and loader.
using Backroads.Roads;
using UnityEngine;

namespace Backroads.Sites
{
    /// <summary>
    /// card sites-S4 steps 5 and 6: a town's site surfaces, kerbs, wheel stops and markings in 64 m chunks on the terrain's
    /// grid, in lane R's format (RoadChunkSet: one vertex buffer per chunk, LOD0 and LOD1 index lists of the asphalt,
    /// concrete and paint submeshes, and the collider: paved surfaces, kerbs and wheel stops), so lane R's loader builds
    /// them (LODGroup at about 250 m / 600 m) with lane R's three road materials. Data: `BrSiteChunks_(town)` in
    /// Sites/Baked/Resources. Fences get box colliders (their Synty pieces carry none of their own we rely on).
    /// Lane W's scene calls Spawn (and SiteChunkView in Backroads.Sites.Client adds the materials on a client).
    /// </summary>
    public static class SiteChunks
    {
        public const string ResourceName = "BrSiteChunks";
        public const string RootName = "Sites (S4 chunks)", FenceRoot = "Site fences (S4 colliders)";

        public static string TownResourceName(string townId) => ResourceName + "_" + townId;
        public static RoadChunkSet LoadTown(string townId) => Resources.Load<RoadChunkSet>(TownResourceName(townId));

        /// <summary>The chunks (with materials: renderers and LODs; without: colliders only, as on a server) and the fence colliders.</summary>
        public static GameObject Spawn(string townId, Transform parent, Material[] materials, bool colliders = true, int layer = 0)
        {
            var set = LoadTown(townId);
            if (set == null) { Debug.LogWarning("[BR] no site chunks baked for " + townId + " (SitesBakeCli.Bake)"); return null; }
            var root = RoadChunkLoader.Spawn(set, parent, materials, colliders, layer);
            root.name = RootName;
            if (colliders)
            {
                var props = SitePropSet.LoadTown(townId);
                if (props != null) SpawnFenceColliders(props, root.transform, layer);
            }
            return root;
        }

        /// <summary>One thin box collider per fence segment (0.1 m thick, the fence's height).</summary>
        public static GameObject SpawnFenceColliders(SitePropSet set, Transform parent, int layer = 0)
        {
            var root = new GameObject(FenceRoot) { layer = layer };
            root.transform.SetParent(parent, false);
            int n = 0;
            foreach (var f in set.fences)
            {
                if (f.line == null) continue;
                for (int i = 0; i + 1 < f.line.Length; i++)
                {
                    Vector3 a = f.line[i], b = f.line[i + 1];
                    var d = b - a;
                    d.y = 0;
                    float len = d.magnitude;
                    if (len < 0.05f) continue;
                    var go = new GameObject($"Fence {f.parcel} {n++}") { layer = layer };
                    go.transform.SetParent(root.transform, false);
                    go.transform.localPosition = (a + b) * 0.5f + Vector3.up * (f.height * 0.5f);
                    go.transform.localRotation = Quaternion.LookRotation(d / len, Vector3.up);
                    var box = go.AddComponent<BoxCollider>();
                    box.size = new Vector3(0.1f, f.height, len);
                }
            }
            return root;
        }
    }
}
