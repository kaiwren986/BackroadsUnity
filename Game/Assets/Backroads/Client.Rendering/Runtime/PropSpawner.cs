using Backroads.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace Backroads.Client.Rendering
{
    /// <summary>
    /// T1.4: the oracle's merged static props (reference/static_props.glb: lamp posts, site dressing, fences, mailboxes,
    /// silos, containers, ...) in the importer's 64 m chunks, one renderer per chunk with one submesh per bucket and the
    /// bucket's material (props.js buildProps: building, metal, windowLit, windowDark, bulb, neon, signRed). The
    /// buildings that props.js merged into the same buckets were left out by the importer; BuildingSpawner draws them.
    /// The typed records (lamps, silos, dock, fuel station, crusher, saw shed) stay data for lights and later 1:1 swaps.
    /// </summary>
    public sealed class PropSpawner
    {
        public Transform Root { get; }
        public int Chunks { get; private set; }
        public long Vertices { get; private set; }

        public PropSpawner(StaticPropsSet props, MillbrookLook look, Transform parent)
        {
            Root = new GameObject("StaticProps").transform;
            Root.SetParent(parent, false);
            foreach (var c in props.chunks)
            {
                var go = new GameObject($"props_{c.cx:00}_{c.cz:00}");
                go.transform.SetParent(Root, false);
                go.AddComponent<MeshFilter>().sharedMesh = c.mesh;
                var r = go.AddComponent<MeshRenderer>();
                var mats = new Material[c.buckets.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = look.propBuckets[c.buckets[i]];
                r.sharedMaterials = mats;
                r.shadowCastingMode = ShadowCastingMode.On;
                Chunks++;
                Vertices += c.mesh.vertexCount;
            }
        }

        public void Destroy()
        {
            if (Root != null) TerrainChunkSystem.Kill(Root.gameObject);
        }
    }
}
