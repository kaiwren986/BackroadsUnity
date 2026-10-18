using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Backroads.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace Backroads.Canon.Editor
{
    /// <summary>
    /// Splits reference/static_props.glb (7 non-indexed buckets, JS space) into 64 m chunks (Unity space, x negated,
    /// winding flipped), leaving out the triangles of the 72 placed GLB buildings: props.js merged those into the same
    /// buckets, and BuildingSpawner draws them from buildings.glb. A triangle belongs to a building when all three
    /// corners lie inside the model's bounds (+2 cm) in the building's frame (inverse of placements.json "M"). The
    /// removed count per bucket must equal what the models contribute (buildings.glb primitives, panes by lit flag).
    /// </summary>
    public static class StaticPropsBaker
    {
        const float Pad = 0.02f;
        static readonly Regex Suffix = new Regex(@"\.\d+$");

        sealed class ModelInfo
        {
            public Vector3 min, max;
            /// <summary>Non-indexed vertex count per bucket for the non-glass parts; panes listed separately in traverse order.</summary>
            public int[] fixedVerts = new int[7];
            public List<int> paneVerts = new List<int>();
        }

        sealed class Placed
        {
            public double[] inv; // inverse of M (column-major 4x4)
            public ModelInfo model;
            public double cx, cz, radius;
        }

        public static StaticPropsSet Bake(string staticPropsGlb, string buildingsGlb, PlacementSet placements,
                                          Dictionary<string, (int vertices, string positionSha256)> manifest, List<string> notes, out List<Mesh> meshes)
        {
            var glb = GlbReader.Load(staticPropsGlb);
            var models = ReadModels(GlbReader.Load(buildingsGlb));
            var placed = new List<Placed>();
            var expected = new long[7];
            foreach (var b in placements.buildings)
            {
                if (!models.TryGetValue(b.model, out var mi)) throw new CanonImportException("placements.json names unknown model " + b.model);
                var inv = Invert(b.m);
                // world-space reach of the model's corners: sx, sz are clamped to [0.75, 1.25]
                double rx = Math.Max(Math.Abs(mi.min.x), Math.Abs(mi.max.x)) * Math.Max(1.0, b.sx);
                double rz = Math.Max(Math.Abs(mi.min.z), Math.Abs(mi.max.z)) * Math.Max(1.0, b.sz);
                placed.Add(new Placed { inv = inv, model = mi, cx = b.m[12], cz = b.m[14], radius = Math.Sqrt(rx * rx + rz * rz) + 1 });
                for (int k = 0; k < 7; k++) expected[k] += mi.fixedVerts[k];
                if (mi.paneVerts.Count != (b.lit ?? "").Length)
                    throw new CanonImportException($"{b.model}: {mi.paneVerts.Count} panes in buildings.glb, lit string has {b.lit?.Length}");
                for (int i = 0; i < mi.paneVerts.Count; i++) expected[b.lit[i] == '1' ? 2 : 3] += mi.paneVerts[i];
            }

            var set = ScriptableObject.CreateInstance<StaticPropsSet>();
            var stats = new StaticPropsSet.BucketStats[7];
            // per chunk, per bucket: kept triangle corners (Unity space)
            var chunkData = new Dictionary<int, List<(Vector3 p, Vector3 n, Color c)>[]>();
            int removedTotal = 0;
            for (int bucket = 0; bucket < 7; bucket++)
            {
                var name = StaticPropsSet.BucketNames[bucket];
                int node = glb.FindNode(name);
                if (node < 0) throw new CanonImportException("static_props.glb has no node " + name);
                int mesh = (int)CanonJson.Num(glb.Node(node), "mesh");
                var prim = glb.Primitives(mesh)[0];
                var rawPos = glb.RawBytes(prim.position);
                string sha;
                using (var h = SHA256.Create()) sha = CanonRuntime.Hex(h.ComputeHash(rawPos));
                var pos = glb.Floats(prim.position);
                var nor = glb.Floats(prim.normal);
                var col = prim.color >= 0 ? glb.Floats(prim.color) : null;
                int colStride = col != null ? col.Length / (pos.Length / 3) : 0;
                int count = pos.Length / 3;
                if (manifest.TryGetValue(name, out var want))
                {
                    if (want.vertices != count) throw new CanonImportException($"static_props {name}: {count} vertices, manifest says {want.vertices}");
                    if (!string.Equals(want.positionSha256, sha, StringComparison.OrdinalIgnoreCase)) throw new CanonImportException($"static_props {name}: position hash {sha} != {want.positionSha256}");
                }
                var st = new StaticPropsSet.BucketStats { name = name, vertices = count, positionSha256 = sha, sourceSum = new double[3], splitSum = new double[3] };
                for (int v = 0; v < count; v++) { st.sourceSum[0] += pos[v * 3]; st.sourceSum[1] += pos[v * 3 + 1]; st.sourceSum[2] += pos[v * 3 + 2]; }

                for (int t = 0; t + 2 < count; t += 3)
                {
                    bool isBuilding = BelongsToBuilding(pos, t, placed);
                    if (isBuilding)
                    {
                        st.buildingVertices += 3;
                        for (int c = 0; c < 3; c++) { st.splitSum[0] += pos[(t + c) * 3]; st.splitSum[1] += pos[(t + c) * 3 + 1]; st.splitSum[2] += pos[(t + c) * 3 + 2]; }
                        continue;
                    }
                    // chunk by centroid, Unity space (x negated)
                    double ux = -(pos[t * 3] + pos[(t + 1) * 3] + pos[(t + 2) * 3]) / 3.0;
                    double uz = (pos[t * 3 + 2] + pos[(t + 1) * 3 + 2] + pos[(t + 2) * 3 + 2]) / 3.0;
                    int cx = Mathf.Clamp((int)Math.Floor((ux + 512) / StaticPropsSet.ChunkSize), 0, StaticPropsSet.ChunksPerSide - 1);
                    int cz = Mathf.Clamp((int)Math.Floor((uz + 512) / StaticPropsSet.ChunkSize), 0, StaticPropsSet.ChunksPerSide - 1);
                    int key = cz * StaticPropsSet.ChunksPerSide + cx;
                    if (!chunkData.TryGetValue(key, out var lists))
                    {
                        lists = new List<(Vector3, Vector3, Color)>[7];
                        chunkData[key] = lists;
                    }
                    var list = lists[bucket] ??= new List<(Vector3, Vector3, Color)>();
                    // winding flip: (a, b, c) -> (a, c, b)
                    foreach (int c in new[] { 0, 2, 1 })
                    {
                        int v = t + c;
                        var p = new Vector3(-pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]);
                        var n = new Vector3(-nor[v * 3], nor[v * 3 + 1], nor[v * 3 + 2]);
                        var cc = col != null ? new Color(col[v * colStride], col[v * colStride + 1], col[v * colStride + 2], colStride > 3 ? col[v * colStride + 3] : 1f) : Color.white;
                        list.Add((p, n, cc));
                        st.splitSum[0] += pos[v * 3]; st.splitSum[1] += pos[v * 3 + 1]; st.splitSum[2] += pos[v * 3 + 2];
                    }
                    st.keptVertices += 3;
                }
                removedTotal += st.buildingVertices;
                if (st.buildingVertices != expected[bucket])
                    notes.Add($"static_props {name}: removed {st.buildingVertices} building vertices, buildings.glb accounts for {expected[bucket]}");
                stats[bucket] = st;
            }

            meshes = new List<Mesh>();
            var chunks = new List<StaticPropsSet.Chunk>();
            foreach (var kv in chunkData.OrderBy(k => k.Key))
            {
                int cx = kv.Key % StaticPropsSet.ChunksPerSide, cz = kv.Key / StaticPropsSet.ChunksPerSide;
                var verts = new List<Vector3>();
                var norms = new List<Vector3>();
                var cols = new List<Color>();
                var buckets = new List<int>();
                var subs = new List<int[]>();
                for (int b = 0; b < 7; b++)
                {
                    var l = kv.Value[b];
                    if (l == null || l.Count == 0) continue;
                    var idx = new int[l.Count];
                    for (int i = 0; i < l.Count; i++) { idx[i] = verts.Count; verts.Add(l[i].p); norms.Add(l[i].n); cols.Add(l[i].c); }
                    buckets.Add(b);
                    subs.Add(idx);
                }
                var m = new Mesh { name = $"props_{cx:00}_{cz:00}", indexFormat = verts.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                m.SetVertices(verts);
                m.SetNormals(norms);
                m.SetColors(cols);
                m.subMeshCount = subs.Count;
                for (int s = 0; s < subs.Count; s++) m.SetTriangles(subs[s], s, false);
                m.RecalculateBounds();
                m.UploadMeshData(false);
                meshes.Add(m);
                chunks.Add(new StaticPropsSet.Chunk { cx = cx, cz = cz, mesh = m, buckets = buckets.ToArray() });
            }
            set.buckets = stats;
            set.chunks = chunks.ToArray();
            notes.Add($"static_props.glb: {stats.Sum(s => s.vertices)} vertices, {removedTotal} in {placed.Count} buildings left to BuildingSpawner, {chunks.Count} chunks of 64 m");
            return set;
        }

        static bool BelongsToBuilding(float[] pos, int t, List<Placed> placed)
        {
            double x = pos[t * 3], z = pos[t * 3 + 2];
            foreach (var b in placed)
            {
                double dx = x - b.cx, dz = z - b.cz;
                if (dx * dx + dz * dz > b.radius * b.radius) continue;
                bool inside = true;
                for (int c = 0; c < 3 && inside; c++)
                {
                    int v = t + c;
                    double px = pos[v * 3], py = pos[v * 3 + 1], pz = pos[v * 3 + 2];
                    var e = b.inv;
                    double lx = e[0] * px + e[4] * py + e[8] * pz + e[12];
                    double ly = e[1] * px + e[5] * py + e[9] * pz + e[13];
                    double lz = e[2] * px + e[6] * py + e[10] * pz + e[14];
                    var mn = b.model.min; var mx = b.model.max;
                    inside = lx >= mn.x - Pad && lx <= mx.x + Pad && ly >= mn.y - Pad && ly <= mx.y + Pad && lz >= mn.z - Pad && lz <= mx.z + Pad;
                }
                if (inside) return true;
            }
            return false;
        }

        static Dictionary<string, ModelInfo> ReadModels(GlbReader g)
        {
            var scenes = CanonJson.Arr(CanonJson.Get(g.Json, "scenes"));
            var roots = CanonJson.Ints(CanonJson.Get(CanonJson.Obj(scenes[0]), "nodes"));
            var d = new Dictionary<string, ModelInfo>();
            foreach (var r in roots)
            {
                var mi = new ModelInfo { min = Vector3.positiveInfinity, max = Vector3.negativeInfinity };
                Walk(g, r, Matrix4x4.identity, mi);
                d[g.NodeName(r)] = mi;
            }
            return d;
        }

        static void Walk(GlbReader g, int node, Matrix4x4 parent, ModelInfo mi)
        {
            var m = parent * g.LocalMatrix(node);
            var n = g.Node(node);
            if (CanonJson.Get(n, "mesh") is double meshIdx)
            {
                var prims = g.Primitives((int)meshIdx);
                bool pane = prims.Count > 0 && prims.All(p => Suffix.Replace(g.MaterialName(p.material), "") == "Glass");
                int paneVerts = 0;
                foreach (var p in prims)
                {
                    var mat = Suffix.Replace(g.MaterialName(p.material), "");
                    int verts = g.NonIndexedCount(p);
                    foreach (var v in g.NonIndexedPositions(p))
                    {
                        var w = m.MultiplyPoint3x4(v);
                        mi.min = Vector3.Min(mi.min, w);
                        mi.max = Vector3.Max(mi.max, w);
                    }
                    if (mat == "Glass") paneVerts += verts;
                    else mi.fixedVerts[mat == "Metal" ? 1 : mat == "Bulb" ? 4 : mat == "Sign" ? 6 : 0] += verts;
                }
                if (pane) mi.paneVerts.Add(paneVerts);
            }
            foreach (var c in g.Children(node)) Walk(g, c, m, mi);
        }

        /// <summary>Inverse of a column-major affine 4x4 (three.js Matrix4.elements), double precision.</summary>
        static double[] Invert(double[] e)
        {
            // upper-left 3x3 A (column-major: A[r,c] = e[c*4 + r]), translation t = e[12..14]
            double a00 = e[0], a10 = e[1], a20 = e[2], a01 = e[4], a11 = e[5], a21 = e[6], a02 = e[8], a12 = e[9], a22 = e[10];
            double det = a00 * (a11 * a22 - a12 * a21) - a01 * (a10 * a22 - a12 * a20) + a02 * (a10 * a21 - a11 * a20);
            if (Math.Abs(det) < 1e-12) throw new CanonImportException("singular building matrix");
            double id = 1.0 / det;
            double b00 = (a11 * a22 - a12 * a21) * id, b01 = (a02 * a21 - a01 * a22) * id, b02 = (a01 * a12 - a02 * a11) * id;
            double b10 = (a12 * a20 - a10 * a22) * id, b11 = (a00 * a22 - a02 * a20) * id, b12 = (a02 * a10 - a00 * a12) * id;
            double b20 = (a10 * a21 - a11 * a20) * id, b21 = (a01 * a20 - a00 * a21) * id, b22 = (a00 * a11 - a01 * a10) * id;
            double tx = e[12], ty = e[13], tz = e[14];
            return new[]
            {
                b00, b10, b20, 0,
                b01, b11, b21, 0,
                b02, b12, b22, 0,
                -(b00 * tx + b01 * ty + b02 * tz), -(b10 * tx + b11 * ty + b12 * tz), -(b20 * tx + b21 * ty + b22 * tz), 1,
            };
        }
    }
}
