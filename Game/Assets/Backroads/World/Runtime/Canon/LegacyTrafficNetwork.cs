using System;
using UnityEngine;

namespace Backroads.World
{
    /// <summary>
    /// traffic.json of CanonData v1: the browser game's lane network (trafficNet.js), golden reference only (PLAN 2.2).
    /// The baked LaneGraph of M3 replaces it for the simulation. JS space.
    /// </summary>
    public sealed class LegacyTrafficNetwork : ScriptableObject
    {
        [Serializable]
        public struct Arm { public int edge, @out; public float ang; }

        [Serializable]
        public sealed class Node
        {
            public int id;
            public float x, z, r;
            public bool dead, failed;
            public Arm[] arms;
            /// <summary>Major edge ids, or empty when the node has no major road.</summary>
            public int[] major;
            public bool hasRoundabout;
            public float rbCx, rbCz, rbR, rbClear, rbRing, rbScore;
            public int[] inLanes, outLanes, conns;
        }

        [Serializable]
        public sealed class Edge
        {
            public int id, a, b, road;
            /// <summary>Polyline x0, z0, x1, z1, ...</summary>
            public float[] pts;
        }

        [Serializable]
        public sealed class Seg
        {
            public int id;
            public string kind;
            public int n;
            public float len, kMax;
            public float[] x, z, s, k, cap;
            public int[] next, compat;
            // Optional fields; -1 / NaN / false when absent in the JSON.
            public int edge = -1, from = -1, to = -1, node = -1, dir, turn, yard = -1;
            public bool minor, major, noTruck, rb;
            public float phIn = float.NaN, phOut = float.NaN, rRadius = float.NaN, clear = float.NaN;
        }

        public Node[] nodes;
        public Edge[] edges;
        public Seg[] segs;
        public int[] lanes, conns, loops;
        public int skipped;

        public int CountCarOnlyLoops()
        {
            int c = 0;
            foreach (var id in loops)
                foreach (var s in segs)
                    if (s.id == id && s.noTruck) { c++; break; }
            return c;
        }
    }
}
