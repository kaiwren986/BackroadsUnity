using System;
using System.Collections.Generic;
using Unity.Mathematics;
using Backroads.World;
using UnityEngine;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;

namespace Backroads.Client.Rendering
{
    /// <summary>
    /// T1.4: the 5,265 canon trees as LODGroup GameObjects built from the frozen trees.glb (species "Pine" and
    /// "Pine_LOD1", ...), drawn by the GPU Resident Drawer. Mesh at y = ground - 0.2 (CanonTransforms.Tree); the
    /// three.js instance tint (03 section 4.8) rides in Renderer.SetShaderUserValue (no per-renderer property blocks,
    /// PLAN 1.5 rule 4). The LOD switch matches props.js updateTrees: the detailed mesh within the near radius
    /// (QualityPreset TreeNearRadius: 90 / 140 / 190 m), the far mesh beyond; only the detailed one casts shadows.
    /// Also the rocks (vegetation.json, props.js buildRocks: jittered dodecahedron, flat shaded, grey instance tint).
    /// </summary>
    public sealed class VegetationRenderer
    {
        public Transform Root { get; }
        public int Trees { get; private set; }
        public int Rocks { get; private set; }
        public readonly List<Material> Created = new List<Material>();
        Mesh _rockMesh;

        // T2.5 re-snap (props.js onTerrainChanged): trees by 16 m cell over the world's grid (T3.1: the cells and the
        // half size come from the canon's WorldGridLayout), their transforms and current ground height
        const float SnapCell = 16f;
        readonly int SnapCells;
        readonly float _half;
        readonly VegetationSet _veg;
        readonly Transform[] _treeTf;
        readonly float[] _treeY;
        readonly List<int>[] _treeCells;
        int[] _treeCollider;
        /// <summary>Trees moved by Resnap since start.</summary>
        public int Resnapped { get; private set; }

        public VegetationRenderer(CanonAsset canon, MillbrookLook look, Transform parent, float nearRadius)
            : this(canon.vegetation, canon.trees, WorldGrid.LayoutOf(canon), look, parent, nearRadius) { }

        /// <summary>Any world's vegetation (T3.3: a baked world's vegetation.json) with the canon's tree models.</summary>
        public VegetationRenderer(VegetationSet veg, CanonModelSet models, WorldGridLayout layout, MillbrookLook look, Transform parent, float nearRadius)
        {
            SnapCells = layout.ColliderCellsPerSide;
            _half = layout.Half;
            _treeCells = new List<int>[SnapCells * SnapCells];
            Root = new GameObject("Vegetation").transform;
            Root.SetParent(parent, false);
            _veg = veg;
            _treeTf = new Transform[veg.trees.Length];
            _treeY = new float[veg.trees.Length];
            var lod0 = new Mesh[veg.species.Length];
            var lod1 = new Mesh[veg.species.Length];
            var mats = new Material[veg.species.Length];
            for (int s = 0; s < veg.species.Length; s++)
            {
                var name = veg.species[s].name;
                lod0[s] = models.Find(name)?.parts[0].mesh;
                lod1[s] = models.Find(name + "_LOD1")?.parts[0].mesh;
                mats[s] = new Material(look.tree) { name = "tree_" + name };
                mats[s].SetFloat("_Sway", veg.species[s].sway);
                Created.Add(mats[s]);
            }
            // LODGroup: relative screen height h = size / (2 d tan(fov/2)) / lodBias; size 1 m in world space
            float bias = Mathf.Max(0.01f, QualitySettings.lodBias);
            float near = 1f / (2f * nearRadius * Mathf.Tan(30f * Mathf.Deg2Rad)) * bias;

            var speciesRoots = new Transform[veg.species.Length];
            for (int s = 0; s < speciesRoots.Length; s++)
            {
                speciesRoots[s] = new GameObject(veg.species[s].name).transform;
                speciesRoots[s].SetParent(Root, false);
            }
            for (int i = 0; i < veg.trees.Length; i++)
            {
                var t = veg.trees[i];
                if (lod0[t.sp] == null) continue;
                var pose = CanonTransforms.Tree(t);
                var go = new GameObject("t" + i);
                var tr = go.transform;
                _treeTf[i] = tr;
                _treeY[i] = t.y;
                int cell = SnapIndex(t.x) + SnapIndex(t.z) * SnapCells;
                (_treeCells[cell] ??= new List<int>()).Add(i);
                tr.SetParent(speciesRoots[t.sp], false);
                tr.SetLocalPositionAndRotation(pose.position, pose.rotation);
                tr.localScale = pose.scale;
                uint user = PackTint(t.tint, t.hue);
                var r0 = Child(tr, "LOD0", lod0[t.sp], mats[t.sp], ShadowCastingMode.On, user);
                var r1 = lod1[t.sp] != null ? Child(tr, "LOD1", lod1[t.sp], mats[t.sp], ShadowCastingMode.Off, user) : null;
                var lg = go.AddComponent<LODGroup>();
                lg.fadeMode = LODFadeMode.None;
                lg.size = 1f / Mathf.Max(0.01f, t.s);
                lg.SetLODs(r1 != null
                    ? new[] { new LOD(near, new Renderer[] { r0 }), new LOD(0.00002f, new Renderer[] { r1 }) }
                    : new[] { new LOD(0.00002f, new Renderer[] { r0 }) });
                Trees++;
            }

            // rocks
            _rockMesh = RockMesh();
            var rockRoot = new GameObject("Rocks").transform;
            rockRoot.SetParent(Root, false);
            for (int i = 0; i < veg.rocks.Length; i++)
            {
                var rk = veg.rocks[i];
                var pose = CanonTransforms.Rock(rk);
                var go = new GameObject("r" + i);
                go.transform.SetParent(rockRoot, false);
                go.transform.SetLocalPositionAndRotation(pose.position, pose.rotation);
                go.transform.localScale = new Vector3(rk.s, rk.s, rk.s);   // the mesh carries the (1.15, 0.7, 1) stretch
                go.AddComponent<MeshFilter>().sharedMesh = _rockMesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = look.rock;
                r.SetShaderUserValue(PackGrey(rk.v));
                Rocks++;
            }
        }

        /// <summary>The 16 m snap cell of a JS x (or z), clamped to the grid.</summary>
        int SnapIndex(float v) => Mathf.Clamp(Mathf.FloorToInt((v + _half) / SnapCell), 0, SnapCells - 1);

        /// <summary>
        /// props.js onTerrainChanged: trees inside the changed vertex rectangle (+1 m) follow the ground (mesh 0.2 m
        /// below it) and their ColliderGrid circle's y range moves with them. Returns the number of trees moved.
        /// </summary>
        public int Resnap(int i0, int j0, int i1, int j1, WorldGridView grid, ColliderGrid colliders)
        {
            float cell = grid.Layout.Cell, half = grid.Layout.Half;
            float x0 = i0 * cell - half - 1f, x1 = i1 * cell - half + 1f, z0 = j0 * cell - half - 1f, z1 = j1 * cell - half + 1f;
            int c0 = SnapIndex(x0), c1 = SnapIndex(x1);
            int r0 = SnapIndex(z0), r1 = SnapIndex(z1);
            int moved = 0;
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    var list = _treeCells[c + r * SnapCells];
                    if (list == null) continue;
                    foreach (int i in list)
                    {
                        var t = _veg.trees[i];
                        if (t.x < x0 || t.x > x1 || t.z < z0 || t.z > z1 || _treeTf[i] == null) continue;
                        float ny = WorldGridQueries.HeightAtJs(grid, t.x, t.z);
                        if (Mathf.Abs(ny - _treeY[i]) < 0.01f) continue;
                        _treeY[i] = ny;
                        var p = _treeTf[i].localPosition;
                        _treeTf[i].localPosition = new Vector3(p.x, ny - 0.2f, p.z);
                        moved++;
                        if (colliders != null && colliders.Records.IsCreated)
                        {
                            if (_treeCollider == null) BuildColliderMap(colliders);
                            int ci = _treeCollider[i];
                            if (ci >= 0)
                            {
                                var rec = colliders.Records[ci];
                                rec.y0 = ny - 1f;
                                rec.y1 = ny + _veg.species[t.sp].h * t.s;
                                colliders.Records[ci] = rec;
                            }
                        }
                    }
                }
            Resnapped += moved;
            return moved;
        }

        /// <summary>The current ground height under tree i (canon y until the ground there changes).</summary>
        public float TreeGround(int i) => _treeY[i];

        void BuildColliderMap(ColliderGrid colliders)
        {
            _treeCollider = new int[_veg.trees.Length];
            for (int i = 0; i < _treeCollider.Length; i++) _treeCollider[i] = -1;
            for (int ci = 0; ci < colliders.Records.Length; ci++)
            {
                int t = colliders.Records[ci].tree;
                if (t >= 0 && t < _treeCollider.Length) _treeCollider[t] = ci;
            }
        }

        static MeshRenderer Child(Transform parent, string name, Mesh mesh, Material mat, ShadowCastingMode shadows, uint user)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = shadows;
            r.SetShaderUserValue(user);
            return r;
        }

        /// <summary>Tree tint for the shader: bits 0-15 = tint * 4096, bits 16-31 = hue * 65535 (BrLitColor.shader).</summary>
        public static uint PackTint(float tint, float hue) =>
            (uint)Mathf.Clamp(Mathf.RoundToInt(tint * 4096f), 0, 65535) | ((uint)Mathf.Clamp(Mathf.RoundToInt(hue * 65535f), 0, 65535) << 16);

        /// <summary>Rock brightness v: (v, 0.97 v, 0.92 v) in the shader's grey mode (hue bits all set).</summary>
        public static uint PackGrey(float v) => (uint)Mathf.Clamp(Mathf.RoundToInt(v * 4096f), 0, 65535) | (0xFFFFu << 16);

        /// <summary>
        /// three's DodecahedronGeometry(1, 1) (PolyhedronGeometry, detail 1, non-indexed) with props.js's per-vertex
        /// jitter and (1.15, 0.7, 1) stretch, flat normals; converted to Unity space (x negated, winding swapped).
        /// </summary>
        public static Mesh RockMesh()
        {
            double t = (1 + Math.Sqrt(5)) / 2, r = 1 / t;
            double[] v =
            {
                -1, -1, -1, -1, -1, 1, -1, 1, -1, -1, 1, 1, 1, -1, -1, 1, -1, 1, 1, 1, -1, 1, 1, 1,
                0, -r, -t, 0, -r, t, 0, r, -t, 0, r, t,
                -r, -t, 0, -r, t, 0, r, -t, 0, r, t, 0,
                -t, 0, -r, t, 0, -r, -t, 0, r, t, 0, r,
            };
            int[] ix =
            {
                3, 11, 7, 3, 7, 15, 3, 15, 13, 7, 19, 17, 7, 17, 6, 7, 6, 15, 17, 4, 8, 17, 8, 10, 17, 10, 6, 8, 0, 16, 8, 16, 2,
                8, 2, 10, 0, 12, 1, 0, 1, 18, 0, 18, 16, 6, 10, 2, 6, 2, 13, 6, 13, 15, 2, 16, 18, 2, 18, 3, 2, 3, 13, 18, 1, 9,
                18, 9, 11, 18, 11, 3, 4, 14, 12, 4, 12, 0, 4, 0, 8, 11, 9, 5, 11, 5, 19, 11, 19, 7, 19, 5, 14, 19, 14, 4, 19, 4,
                17, 1, 12, 14, 1, 14, 5, 1, 5, 9,
            };
            // PolyhedronGeometry (detail 1) in doubles, as three computes it
            var pts = new List<double3>();
            const int cols = 2;
            for (int f = 0; f < ix.Length; f += 3)
            {
                double3 V(int k) => new double3(v[k * 3], v[k * 3 + 1], v[k * 3 + 2]);
                double3 a = V(ix[f]), b = V(ix[f + 1]), c = V(ix[f + 2]);
                var grid = new double3[cols + 1][];
                for (int i = 0; i <= cols; i++)
                {
                    grid[i] = new double3[cols - i + 1];
                    var aj = math.lerp(a, c, (double)i / cols);
                    var bj = math.lerp(b, c, (double)i / cols);
                    int rows = cols - i;
                    for (int j = 0; j <= rows; j++)
                        grid[i][j] = j == 0 && i == cols ? aj : math.lerp(aj, bj, rows == 0 ? 0 : (double)j / rows);
                }
                for (int i = 0; i < cols; i++)
                    for (int j = 0; j < 2 * (cols - i) - 1; j++)
                    {
                        int k = j / 2;
                        if (j % 2 == 0) { pts.Add(grid[i][k + 1]); pts.Add(grid[i + 1][k]); pts.Add(grid[i][k]); }
                        else { pts.Add(grid[i][k + 1]); pts.Add(grid[i + 1][k + 1]); pts.Add(grid[i + 1][k]); }
                    }
            }
            var verts = new Vector3[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                var p = math.normalize(pts[i]);                       // applyRadius(1), stored as float32
                double vx = (float)p.x, vy = (float)p.y, vz = (float)p.z;
                double h = Math.Sin(vx * 41.3 + vy * 17.1 + vz * 29.7) * 9871.13;
                double f = 0.82 + (h - Math.Floor(h)) * 0.32;
                verts[i] = new Vector3(-(float)(vx * f * 1.15), (float)(vy * f * 0.7), (float)(vz * f)); // JS -> Unity: x negated
            }
            var tris = new int[verts.Length];
            for (int i = 0; i < tris.Length; i += 3) { tris[i] = i; tris[i + 1] = i + 2; tris[i + 2] = i + 1; } // winding swapped
            var m = new Mesh { name = "rock" };
            m.vertices = verts;
            m.triangles = tris;
            m.RecalculateNormals(); // non-indexed: flat normals
            m.RecalculateBounds();
            return m;
        }

        public void Destroy()
        {
            foreach (var m in Created) TerrainChunkSystem.Kill(m);
            Created.Clear();
            TerrainChunkSystem.Kill(_rockMesh);
            if (Root != null) TerrainChunkSystem.Kill(Root.gameObject);
        }
    }
}
