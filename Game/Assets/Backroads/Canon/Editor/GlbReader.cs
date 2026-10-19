using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Backroads.Canon.Editor
{
    /// <summary>
    /// Reads a binary glTF 2.0 file (header, JSON chunk, BIN chunk) without converting anything: accessor data comes
    /// back exactly as stored, in glTF (= three.js, JS) space. Used for static_props.glb (bucket counts and hashes, the
    /// 64 m split) and for the building bounds the split needs. Unity-side models come from glTFast.
    /// </summary>
    public sealed class GlbReader
    {
        public readonly Dictionary<string, object> Json;
        public readonly byte[] Bin;
        public readonly List<object> Nodes, Meshes, Accessors, BufferViews, Materials;

        public GlbReader(byte[] glb)
        {
            if (glb.Length < 20 || BitConverter.ToUInt32(glb, 0) != 0x46546C67) throw new FormatException("not a GLB file");
            int pos = 12;
            string json = null;
            byte[] bin = null;
            while (pos + 8 <= glb.Length)
            {
                int len = (int)BitConverter.ToUInt32(glb, pos);
                uint type = BitConverter.ToUInt32(glb, pos + 4);
                if (type == 0x4E4F534A) json = Encoding.UTF8.GetString(glb, pos + 8, len);
                else if (type == 0x004E4942) { bin = new byte[len]; Buffer.BlockCopy(glb, pos + 8, bin, 0, len); }
                pos += 8 + len;
            }
            if (json == null) throw new FormatException("GLB without a JSON chunk");
            Json = CanonJson.Obj(CanonJson.Parse(json), "glTF");
            Bin = bin ?? Array.Empty<byte>();
            Nodes = CanonJson.Get(Json, "nodes") as List<object> ?? new List<object>();
            Meshes = CanonJson.Get(Json, "meshes") as List<object> ?? new List<object>();
            Accessors = CanonJson.Get(Json, "accessors") as List<object> ?? new List<object>();
            BufferViews = CanonJson.Get(Json, "bufferViews") as List<object> ?? new List<object>();
            Materials = CanonJson.Get(Json, "materials") as List<object> ?? new List<object>();
        }

        public static GlbReader Load(string path) => new GlbReader(File.ReadAllBytes(path));

        public Dictionary<string, object> Node(int i) => CanonJson.Obj(Nodes[i], "node");
        public Dictionary<string, object> Mesh(int i) => CanonJson.Obj(Meshes[i], "mesh");
        public Dictionary<string, object> Accessor(int i) => CanonJson.Obj(Accessors[i], "accessor");

        public string NodeName(int i) => CanonJson.Str(Node(i), "name");

        public int FindNode(string name)
        {
            for (int i = 0; i < Nodes.Count; i++) if (NodeName(i) == name) return i;
            return -1;
        }

        public int[] Children(int node) => CanonJson.Ints(CanonJson.Get(Node(node), "children"));

        public string MaterialName(int material) =>
            material < 0 || material >= Materials.Count ? "" : CanonJson.Str(CanonJson.Obj(Materials[material]), "name") ?? "";

        /// <summary>Local matrix of a node (glTF space): "matrix" or T * R * S.</summary>
        public Matrix4x4 LocalMatrix(int node)
        {
            var n = Node(node);
            if (CanonJson.Get(n, "matrix") is List<object> m && m.Count == 16)
            {
                var e = CanonJson.Floats(m);
                return new Matrix4x4(new Vector4(e[0], e[1], e[2], e[3]), new Vector4(e[4], e[5], e[6], e[7]),
                                     new Vector4(e[8], e[9], e[10], e[11]), new Vector4(e[12], e[13], e[14], e[15]));
            }
            var t = CanonJson.Floats(CanonJson.Get(n, "translation"));
            var r = CanonJson.Floats(CanonJson.Get(n, "rotation"));
            var s = CanonJson.Floats(CanonJson.Get(n, "scale"));
            var tv = t.Length == 3 ? new Vector3(t[0], t[1], t[2]) : Vector3.zero;
            // glTF quaternions are right-handed (x, y, z, w); as a pure matrix the composition is the same maths.
            var rq = r.Length == 4 ? new Quaternion(r[0], r[1], r[2], r[3]) : Quaternion.identity;
            var sv = s.Length == 3 ? new Vector3(s[0], s[1], s[2]) : Vector3.one;
            return Matrix4x4.TRS(tv, rq, sv);
        }

        public int Count(int accessor) => (int)CanonJson.Num(Accessor(accessor), "count");

        /// <summary>The accessor's bytes, tightly packed (stride removed), exactly as stored.</summary>
        public byte[] RawBytes(int accessor)
        {
            var a = Accessor(accessor);
            int count = (int)CanonJson.Num(a, "count");
            int comp = ComponentSize((int)CanonJson.Num(a, "componentType"));
            int ncomp = Components(CanonJson.Str(a, "type"));
            int elem = comp * ncomp;
            var bv = CanonJson.Obj(BufferViews[(int)CanonJson.Num(a, "bufferView")], "bufferView");
            int offset = (int)CanonJson.Num(bv, "byteOffset", 0) + (int)CanonJson.Num(a, "byteOffset", 0);
            int stride = (int)CanonJson.Num(bv, "byteStride", 0);
            if (stride == 0) stride = elem;
            var outB = new byte[count * elem];
            for (int i = 0; i < count; i++) Buffer.BlockCopy(Bin, offset + i * stride, outB, i * elem, elem);
            return outB;
        }

        public float[] Floats(int accessor)
        {
            var a = Accessor(accessor);
            if ((int)CanonJson.Num(a, "componentType") != 5126) throw new FormatException("accessor is not float");
            var raw = RawBytes(accessor);
            var f = new float[raw.Length / 4];
            Buffer.BlockCopy(raw, 0, f, 0, raw.Length);
            return f;
        }

        public int[] Indices(int accessor)
        {
            var a = Accessor(accessor);
            int ct = (int)CanonJson.Num(a, "componentType");
            var raw = RawBytes(accessor);
            int n = (int)CanonJson.Num(a, "count");
            var r = new int[n];
            for (int i = 0; i < n; i++)
                r[i] = ct == 5121 ? raw[i] : ct == 5123 ? BitConverter.ToUInt16(raw, i * 2) : (int)BitConverter.ToUInt32(raw, i * 4);
            return r;
        }

        static int ComponentSize(int ct) => ct == 5126 || ct == 5125 ? 4 : ct == 5123 || ct == 5122 ? 2 : 1;

        static int Components(string type)
        {
            switch (type)
            {
                case "SCALAR": return 1;
                case "VEC2": return 2;
                case "VEC3": return 3;
                case "VEC4": return 4;
                case "MAT4": return 16;
                default: throw new FormatException("accessor type " + type);
            }
        }

        public struct Primitive
        {
            public int position, normal, color, indices, material;
        }

        public List<Primitive> Primitives(int mesh)
        {
            var list = new List<Primitive>();
            foreach (var po in CanonJson.Arr(CanonJson.Get(Mesh(mesh), "primitives")))
            {
                var p = CanonJson.Obj(po);
                var at = CanonJson.Obj(CanonJson.Get(p, "attributes"));
                list.Add(new Primitive
                {
                    position = CanonJson.I(at, "POSITION"),
                    normal = CanonJson.I(at, "NORMAL"),
                    color = CanonJson.I(at, "COLOR_0"),
                    indices = CanonJson.I(p, "indices"),
                    material = CanonJson.I(p, "material"),
                });
            }
            return list;
        }

        /// <summary>Number of vertices a primitive becomes when made non-indexed (three.js toNonIndexed).</summary>
        public int NonIndexedCount(Primitive p) => p.indices >= 0 ? Count(p.indices) : Count(p.position);

        /// <summary>The primitive as a non-indexed triangle list of positions (glTF space, node-local).</summary>
        public Vector3[] NonIndexedPositions(Primitive p)
        {
            var pos = Floats(p.position);
            int[] idx = p.indices >= 0 ? Indices(p.indices) : null;
            int n = idx?.Length ?? pos.Length / 3;
            var r = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                int v = idx != null ? idx[i] : i;
                r[i] = new Vector3(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]);
            }
            return r;
        }
    }
}
