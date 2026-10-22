using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Backroads.Core.WorldBake;
using Unity.Mathematics;
using UnityEngine;

namespace Backroads.Roads
{
    /// <summary>
    /// roads-R2's kerb frontage (overview contract 2), the asset next to the road network: one record per block face (a
    /// road side between two corners, running with the block on its left) with its kerb line, heights, walk back edge and
    /// forbidden intervals, plus the kerb returns with their ramps. Written by Backroads.Roads.Editor.KerbBaker into
    /// Assets/Backroads/Roads/Baked/Resources/BrKerbFrontage_(town).asset (committed, hash-checked); read through
    /// <see cref="KerbFrontageSource"/>. Stations are packed little-endian floats (8 per station: x, z, s, gutter, top of
    /// kerb, back x, back z, back y), so the asset stays small; the content hash covers every field.
    /// </summary>
    public sealed class KerbFrontageAsset : ScriptableObject
    {
        public const string ResourceName = "BrKerbFrontage";
        public const int FormatVersion = 1;
        public const int FloatsPerStation = 8;

        public static string TownResourceName(string townId) => ResourceName + "_" + townId;

        [Serializable]
        public sealed class Face
        {
            public int faceId, edge, side, loop;
            public string osmWay = "";
            public KerbKind kerbKind;
            public StreetClass streetClass;
            public float speedKmh;
            public bool oneway, walk, bridge;
            public int cornerStart = -1, cornerEnd = -1;
            public ForbiddenInterval[] forbidden = Array.Empty<ForbiddenInterval>();
            public int count;
            public byte[] stations = Array.Empty<byte>();
            /// <summary>Smallest kerb-to-building distance (NaN encoded as -1 with hasClearance false).</summary>
            public float minClearance;
            public bool hasClearance;
        }

        [Serializable]
        public struct Corner
        {
            public int id, node, corner, loop, faceA, faceB;
            public CornerKind kind;
            public float radius, arcLength;
            public bool kerbed;
            public RampKind ramp;
            /// <summary>The lowered stretch, ramps and perpendicular openings as loop arc lengths (see KerbCorner).</summary>
            public float t0, t1, dep0, dep1, open0, open1;
        }

        [Header("Identity")]
        public int formatVersion = FormatVersion;
        public int kerbVersion;
        public string source = "", sourceManifestSha256 = "";
        /// <summary>The road network asset this frontage was built with (its content hash).</summary>
        public string roadContentHash = "";
        public string contentHash = "";

        [Header("Frontage")]
        public Face[] faces = Array.Empty<Face>();
        public Corner[] corners = Array.Empty<Corner>();
        public float[] loopLengths = Array.Empty<float>();
        public int slivers;
        public string[] notes = Array.Empty<string>();

        public static KerbFrontageAsset LoadTown(string townId) => Resources.Load<KerbFrontageAsset>(TownResourceName(townId));

        public void SetFrom(KerbNetwork kn, string source, string manifestSha256, string roadHash, int version)
        {
            formatVersion = FormatVersion;
            kerbVersion = version;
            this.source = source ?? "";
            sourceManifestSha256 = manifestSha256 ?? "";
            roadContentHash = roadHash ?? "";
            faces = new Face[kn.faces.Count];
            for (int i = 0; i < faces.Length; i++)
            {
                var f = kn.faces[i];
                faces[i] = new Face
                {
                    faceId = f.id, edge = f.edge, side = f.side, loop = f.loop, osmWay = f.osmWay ?? "", kerbKind = f.kind, streetClass = f.cls,
                    speedKmh = (float)f.speedKmh, oneway = f.oneway, walk = f.walk, bridge = f.bridge, cornerStart = f.cornerStart, cornerEnd = f.cornerEnd,
                    forbidden = f.forbidden.ToArray(), count = f.Count, stations = Pack(f),
                    hasClearance = !double.IsNaN(f.minClearance), minClearance = double.IsNaN(f.minClearance) ? -1 : (float)f.minClearance,
                };
            }
            corners = new Corner[kn.corners.Count];
            for (int i = 0; i < corners.Length; i++)
            {
                var c = kn.corners[i];
                corners[i] = new Corner
                {
                    id = c.id, node = c.node, corner = c.corner, loop = c.loop, faceA = c.faceA, faceB = c.faceB, kind = c.kind, radius = (float)c.radius,
                    arcLength = (float)c.ArcLength, kerbed = c.kerbed, ramp = c.ramp, t0 = (float)c.t0, t1 = (float)c.t1,
                    dep0 = (float)c.dep0, dep1 = (float)c.dep1, open0 = (float)c.open0, open1 = (float)c.open1,
                };
            }
            loopLengths = new float[kn.loops.Count];
            for (int i = 0; i < loopLengths.Length; i++) loopLengths[i] = (float)kn.loops[i].Length;
            slivers = kn.slivers;
            notes = kn.notes.ToArray();
            contentHash = ComputeHash();
        }

        static byte[] Pack(KerbFace f)
        {
            var b = new byte[f.Count * FloatsPerStation * 4];
            int o = 0;
            void W(double v)
            {
                var bits = BitConverter.GetBytes((float)v);
                if (!BitConverter.IsLittleEndian) Array.Reverse(bits);
                Buffer.BlockCopy(bits, 0, b, o, 4);
                o += 4;
            }
            for (int i = 0; i < f.Count; i++)
            {
                W(f.kerb[i].x); W(f.kerb[i].z); W(f.s[i]); W(f.gutter[i]); W(f.top[i]); W(f.back[i].x); W(f.back[i].z); W(f.backY[i]);
            }
            return b;
        }

        /// <summary>Unpacks a face's stations into the contract's arrays.</summary>
        public static KerbFrontage Unpack(Face f)
        {
            int n = f.count;
            var kerb = new double2[n];
            var s = new float[n];
            var gutter = new float[n];
            var top = new float[n];
            var back = new double2[n];
            var backY = new float[n];
            var tmp = new byte[4];
            int o = 0;
            float R()
            {
                Buffer.BlockCopy(f.stations, o, tmp, 0, 4);
                o += 4;
                if (!BitConverter.IsLittleEndian) Array.Reverse(tmp);
                return BitConverter.ToSingle(tmp, 0);
            }
            for (int i = 0; i < n; i++)
            {
                float x = R(), z = R();
                kerb[i] = new double2(x, z);
                s[i] = R(); gutter[i] = R(); top[i] = R();
                float bx = R(), bz = R();
                back[i] = new double2(bx, bz);
                backY[i] = R();
            }
            return new KerbFrontage
            {
                faceId = f.faceId, osmWay = f.osmWay, kerb = kerb, s = s, topOfKerb = top, gutter = gutter, walkBack = back, walkBackY = backY,
                kerbKind = f.kerbKind, streetClass = f.streetClass, speedKmh = f.speedKmh, oneway = f.oneway,
                forbidden = (ForbiddenInterval[])f.forbidden.Clone(), provisional = false,
            };
        }

        /// <summary>SHA-256 (hex) over every content field in a fixed binary order.</summary>
        public string ComputeHash()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(formatVersion); w.Write(kerbVersion); w.Write(source ?? ""); w.Write(sourceManifestSha256 ?? ""); w.Write(roadContentHash ?? "");
                w.Write(faces.Length);
                foreach (var f in faces)
                {
                    w.Write(f.faceId); w.Write(f.edge); w.Write(f.side); w.Write(f.loop); w.Write(f.osmWay ?? ""); w.Write((byte)f.kerbKind); w.Write((byte)f.streetClass);
                    w.Write(f.speedKmh); w.Write(f.oneway); w.Write(f.walk); w.Write(f.bridge); w.Write(f.cornerStart); w.Write(f.cornerEnd);
                    w.Write(f.forbidden.Length);
                    foreach (var iv in f.forbidden) { w.Write(iv.s0); w.Write(iv.s1); w.Write((byte)iv.reason); }
                    w.Write(f.count); w.Write(f.stations.Length); w.Write(f.stations);
                    w.Write(f.minClearance); w.Write(f.hasClearance);
                }
                w.Write(corners.Length);
                foreach (var c in corners)
                {
                    w.Write(c.id); w.Write(c.node); w.Write(c.corner); w.Write(c.loop); w.Write(c.faceA); w.Write(c.faceB); w.Write((byte)c.kind); w.Write(c.radius);
                    w.Write(c.arcLength); w.Write(c.kerbed); w.Write((byte)c.ramp); w.Write(c.t0); w.Write(c.t1); w.Write(c.dep0); w.Write(c.dep1); w.Write(c.open0); w.Write(c.open1);
                }
                w.Write(loopLengths.Length);
                foreach (var l in loopLengths) w.Write(l);
                w.Write(slivers);
                w.Write(notes.Length);
                foreach (var n in notes) w.Write(n ?? "");
                w.Flush();
                using (var sha = SHA256.Create())
                {
                    var h = sha.ComputeHash(ms.ToArray());
                    var sb = new System.Text.StringBuilder(64);
                    foreach (var x in h) sb.Append(x.ToString("x2"));
                    return sb.ToString();
                }
            }
        }
    }

    /// <summary>
    /// R2's frontage for lane S (IKerbFrontageSource): every face of a baked <see cref="KerbFrontageAsset"/>, ordered by
    /// faceId. Replaces R0o's <see cref="ProvisionalKerbFrontage"/>.
    /// </summary>
    public sealed class KerbFrontageSource : IKerbFrontageSource
    {
        readonly List<KerbFrontage> m_Faces = new List<KerbFrontage>();
        readonly Dictionary<int, KerbFrontage> m_ById = new Dictionary<int, KerbFrontage>();

        public bool Provisional => false;
        public IReadOnlyList<KerbFrontage> Faces => m_Faces;
        public KerbFrontage Face(int faceId) => m_ById.TryGetValue(faceId, out var f) ? f : null;

        public KerbFrontageSource(KerbFrontageAsset a)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
            foreach (var f in a.faces)
            {
                var k = KerbFrontageAsset.Unpack(f);
                m_Faces.Add(k);
                m_ById[k.faceId] = k;
            }
            m_Faces.Sort((x, y) => x.faceId.CompareTo(y.faceId));
        }

        /// <summary>The frontage straight from a bake in memory (double positions, float heights as in the asset).</summary>
        public KerbFrontageSource(KerbNetwork kn)
        {
            foreach (var f in kn.faces)
            {
                int n = f.Count;
                var k = new KerbFrontage
                {
                    faceId = f.id, osmWay = f.osmWay ?? "", kerb = new double2[n], s = new float[n], topOfKerb = new float[n], gutter = new float[n],
                    walkBack = new double2[n], walkBackY = new float[n], kerbKind = f.kind, streetClass = f.cls, speedKmh = (float)f.speedKmh,
                    oneway = f.oneway, forbidden = f.forbidden.ToArray(), provisional = false,
                };
                for (int i = 0; i < n; i++)
                {
                    k.kerb[i] = new double2(f.kerb[i].x, f.kerb[i].z);
                    k.s[i] = (float)f.s[i];
                    k.topOfKerb[i] = (float)f.top[i];
                    k.gutter[i] = (float)f.gutter[i];
                    k.walkBack[i] = new double2(f.back[i].x, f.back[i].z);
                    k.walkBackY[i] = (float)f.backY[i];
                }
                m_Faces.Add(k);
                m_ById[k.faceId] = k;
            }
            m_Faces.Sort((x, y) => x.faceId.CompareTo(y.faceId));
        }

        /// <summary>A town's baked frontage, or null when it was not baked.</summary>
        public static KerbFrontageSource LoadTown(string townId)
        {
            var a = KerbFrontageAsset.LoadTown(townId);
            return a != null ? new KerbFrontageSource(a) : null;
        }
    }
}
