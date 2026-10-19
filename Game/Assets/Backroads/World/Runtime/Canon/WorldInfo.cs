using System;
using UnityEngine;

namespace Backroads.World
{
    /// <summary>
    /// world.json and reference/cameras.json of CanonData v1 (03 section 3.3), as the importer wrote them. Every
    /// coordinate here is in JS (three.js) space; convert with BrCoords at the point of use (Unity = (-x, y, z)).
    /// </summary>
    public sealed class WorldInfo : ScriptableObject
    {
        [Serializable]
        public struct Poi
        {
            public string id, name, kind;
            public float x, z, r, fall, y;
            public bool hasQuay;
            public float quayX, quayZ, quayUx, quayUz, quayLen, quayHw;
        }

        [Serializable]
        public struct Circle { public string name; public float x, z, r; }

        [Serializable]
        public struct Field { public float x, z, w, d, rot; }

        [Serializable]
        public struct Hamlet { public string name; public float x, z; }

        [Serializable]
        public sealed class Road
        {
            public int id;
            public string kind;
            public float hw;
            public bool paved, startJ, endJ;
            public double len;
            /// <summary>Centreline points at full precision (JS space): x, y, z and distance along the road d.</summary>
            public double[] x, y, z, d;
            public int Count => x?.Length ?? 0;
        }

        [Serializable]
        public struct Lot
        {
            public float x, z, nx, nz, tx, tz, w, depth, roadY, padY;
            public string zone, hamlet;
            public bool contiguous;
        }

        [Serializable]
        public struct CameraPose
        {
            public string id, name;
            /// <summary>JS-space position and look target; quat is the three.js camera quaternion (x, y, z, w).</summary>
            public Vector3 pos, target;
            public Vector4 quat;
            public string shot10, shot21;
        }

        [Header("Grid")]
        public float size = 1024f;
        public float half = 512f;
        public float cell = 0.5f;
        public int n = 2048;
        public int vn = 2049;
        public float waterY;
        public int tintN = 129;
        public float tintCell = 8f;

        [Header("Places")]
        public Poi[] pois;
        public Circle lake;
        public Circle[] bogs;
        public Field[] fields;
        public Hamlet[] hamlets;
        public float townRadius, townBlockLen, townBlockDepth;
        public Road[] roads;
        public Lot[] lots;

        [Header("Reference cameras (reference/cameras.json)")]
        public int shotWidth = 1600;
        public int shotHeight = 900;
        public float cameraFov = 60f;
        public float cameraNear = 0.3f;
        public float cameraFar = 6000f;
        public int[] shotHours;
        public CameraPose[] cameras;

        public int RoadPointCount
        {
            get
            {
                int c = 0;
                if (roads != null) foreach (var r in roads) c += r.Count;
                return c;
            }
        }

        public bool TryGetPoi(string id, out Poi poi)
        {
            if (pois != null)
                foreach (var p in pois)
                    if (p.id == id) { poi = p; return true; }
            poi = default;
            return false;
        }
    }
}
