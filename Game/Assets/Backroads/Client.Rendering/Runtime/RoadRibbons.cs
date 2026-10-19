using System.Collections.Generic;
using Backroads.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace Backroads.Client.Rendering
{
    /// <summary>
    /// T1.4: roads.js buildRoadMeshes, the M1 "JS-style" asphalt: one quad strip per paved road at p.y + 0.045 over the
    /// carved bed (03 section 4.5), roadUv = (0 | 1, d) and roadFade (the centre line fades 6-16 m from a junction end).
    /// Built from world.json's full-precision centrelines, converted to Unity space with the winding swapped. The
    /// proper junction meshes replace this in M3 (lane R).
    /// </summary>
    public static class RoadRibbons
    {
        public static Mesh Build(WorldInfo world, out int roads)
        {
            var pos = new List<Vector3>();
            var nrm = new List<Vector3>();
            var uv = new List<Vector2>();
            var fade = new List<Vector2>();
            var idx = new List<int>();
            roads = 0;
            foreach (var road in world.roads)
            {
                if (!road.paved) continue;
                roads++;
                int n = road.Count, b0 = pos.Count;
                double hw = road.hw;
                for (int i = 0; i < n; i++)
                {
                    int ia = System.Math.Max(0, i - 1), ib = System.Math.Min(n - 1, i + 1);
                    double tx = road.x[ib] - road.x[ia], tz = road.z[ib] - road.z[ia];
                    double tl = System.Math.Sqrt(tx * tx + tz * tz);
                    if (tl == 0) tl = 1;
                    tx /= tl; tz /= tl;
                    double sx = tz, sz = -tx; // side vector (JS)
                    double y = road.y[i] + 0.045;
                    // JS: left = p - s*hw, right = p + s*hw; Unity = (-x, y, z)
                    pos.Add(new Vector3((float)-(road.x[i] - sx * hw), (float)y, (float)(road.z[i] - sz * hw)));
                    pos.Add(new Vector3((float)-(road.x[i] + sx * hw), (float)y, (float)(road.z[i] + sz * hw)));
                    nrm.Add(Vector3.up); nrm.Add(Vector3.up);
                    uv.Add(new Vector2(0, (float)road.d[i])); uv.Add(new Vector2(1, (float)road.d[i]));
                    double f = 1;
                    if (road.startJ) f = System.Math.Min(f, Smooth(6, 16, road.d[i]));
                    if (road.endJ) f = System.Math.Min(f, Smooth(6, 16, road.len - road.d[i]));
                    fade.Add(new Vector2((float)f, 0)); fade.Add(new Vector2((float)f, 0));
                    if (i < n - 1)
                    {
                        int l0 = b0 + i * 2, r0 = l0 + 1, l1 = l0 + 2, r1 = l0 + 3;
                        // JS (l0, l1, r0), (r0, l1, r1); x negated, so swapped
                        idx.Add(l0); idx.Add(r0); idx.Add(l1);
                        idx.Add(r0); idx.Add(r1); idx.Add(l1);
                    }
                }
            }
            var m = new Mesh { name = "road_ribbons", indexFormat = pos.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            m.SetVertices(pos);
            m.SetNormals(nrm);
            m.SetUVs(0, uv);
            m.SetUVs(1, fade);
            m.SetTriangles(idx, 0);
            m.RecalculateBounds();
            return m;
        }

        static double Smooth(double a, double b, double x)
        {
            double t = System.Math.Min(1, System.Math.Max(0, (x - a) / (b - a)));
            return t * t * (3 - 2 * t);
        }

        public static GameObject Spawn(WorldInfo world, Material mat, Transform parent, out int roads)
        {
            var go = new GameObject("Roads");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = Build(world, out roads);
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = true;
            return go;
        }
    }

    /// <summary>water.js: one plane 1424 m square at y = 0 (WATER_Y); the terrain hides it except over the lake and ponds.</summary>
    public static class WaterSurface
    {
        public const float Size = 1424f;

        public static GameObject Spawn(Material mat, Texture2D normals, Transform parent)
        {
            var go = new GameObject("Water");
            go.transform.SetParent(parent, false);
            var m = new Mesh { name = "water" };
            float h = Size * 0.5f;
            m.vertices = new[] { new Vector3(-h, 0, -h), new Vector3(h, 0, -h), new Vector3(-h, 0, h), new Vector3(h, 0, h) };
            m.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            m.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            m.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = m;
            var r = go.AddComponent<MeshRenderer>();
            var inst = new Material(mat) { name = "water" };
            if (normals != null) inst.SetTexture("_NormalMap", normals);
            r.sharedMaterial = inst;
            r.shadowCastingMode = ShadowCastingMode.Off;
            return go;
        }
    }
}
