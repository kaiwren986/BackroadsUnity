using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Backroads.Roads
{
    /// <summary>Unity side of the R0 plain ribbons: sources from the baked asset, one mesh with a colour per road class.</summary>
    public static class PlainRibbonMesh
    {
        public static List<PlainRibbons.Source> Sources(RoadNetworkAsset a)
        {
            var list = new List<PlainRibbons.Source>(a.edges.Length);
            foreach (var e in a.edges)
            {
                var pts = new D3[e.points.Length];
                for (int i = 0; i < pts.Length; i++) pts[i] = new D3(e.points[i].x, e.points[i].y, e.points[i].z);
                list.Add(new PlainRibbons.Source
                {
                    edge = e.id, sourceRoad = e.sourceRoad, cls = e.cls, hw = e.halfWidth, sStart = e.sStart, sEnd = e.sEnd, pts = pts,
                });
            }
            return list;
        }

        public static List<RibbonStrip> Strips(RoadNetworkAsset a) => PlainRibbons.Build(Sources(a));

        /// <summary>Debug colours per class (MainStreet amber, Highway grey-blue, Street light grey, Dirt brown).</summary>
        public static Color32 ClassColor(RoadClass c)
        {
            switch (c)
            {
                case RoadClass.Highway: return new Color32(92, 104, 124, 255);
                case RoadClass.MainStreet: return new Color32(196, 150, 60, 255);
                case RoadClass.Street: return new Color32(150, 150, 146, 255);
                case RoadClass.Dirt: return new Color32(132, 96, 60, 255);
                default: return new Color32(120, 120, 120, 255);
            }
        }

        /// <summary>
        /// One mesh for every strip: vertex colour = class colour, UV0 = (0 | 1 across, s along), normals up. Winding is
        /// clockwise seen from above, so the faces point up in Unity's left-handed space.
        /// </summary>
        public static Mesh ToMesh(IReadOnlyList<RibbonStrip> strips, string name = "plain_ribbons")
        {
            var pos = new List<Vector3>();
            var col = new List<Color32>();
            var uv = new List<Vector2>();
            var idx = new List<int>();
            foreach (var st in strips)
            {
                int b0 = pos.Count;
                var c = ClassColor(st.cls);
                for (int i = 0; i < st.left.Length; i++)
                {
                    pos.Add(new Vector3((float)st.left[i].x, (float)st.left[i].y, (float)st.left[i].z));
                    pos.Add(new Vector3((float)st.right[i].x, (float)st.right[i].y, (float)st.right[i].z));
                    col.Add(c); col.Add(c);
                    uv.Add(new Vector2(0, (float)st.s[i])); uv.Add(new Vector2(1, (float)st.s[i]));
                    if (i == st.left.Length - 1) continue;
                    int l0 = b0 + i * 2, r0 = l0 + 1, l1 = l0 + 2, r1 = l0 + 3;
                    // left is counter-clockwise of travel; (l0, l1, r0) runs clockwise seen from above
                    idx.Add(l0); idx.Add(l1); idx.Add(r0);
                    idx.Add(r0); idx.Add(l1); idx.Add(r1);
                }
            }
            var m = new Mesh { name = name, indexFormat = pos.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            m.SetVertices(pos);
            m.SetColors(col);
            m.SetUVs(0, uv);
            var nrm = new Vector3[pos.Count];
            for (int i = 0; i < nrm.Length; i++) nrm[i] = Vector3.up;
            m.SetNormals(nrm);
            m.SetTriangles(idx, 0);
            m.RecalculateBounds();
            return m;
        }
    }
}
