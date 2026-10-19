using Backroads.Core;
using UnityEngine;

namespace Backroads.World
{
    /// <summary>
    /// The one place where canon placements (JS space) become Unity transforms (03 section 2: position (-x, y, z),
    /// yaw -rot, matrices S * M * S). Spawners and World.PlacementsRoundTrip both use it.
    /// </summary>
    public static class CanonTransforms
    {
        public struct Pose
        {
            public Vector3 position;
            public Quaternion rotation;
            public Vector3 scale;

            public Matrix4x4 Matrix => Matrix4x4.TRS(position, rotation, scale);
        }

        /// <summary>A placed GLB building from its three.js matrix M (T * R_y(rot) * S(sx, 1, sz)).</summary>
        public static Pose Building(PlacementSet.Building b)
        {
            var e = new float[16];
            for (int i = 0; i < 16; i++) e[i] = (float)b.m[i];
            return Decompose(BrCoords.Mat(e));
        }

        /// <summary>A tree: the mesh sits 0.2 m below the ground height, yaw -rot, uniform scale s (props.js treeMatrix).</summary>
        public static Pose Tree(in VegetationSet.Tree t) => new Pose
        {
            position = BrCoords.Pos(t.x, t.y - 0.2f, t.z),
            rotation = Quaternion.Euler(0f, BrCoords.YawDeg(t.rot), 0f),
            scale = new Vector3(t.s, t.s, t.s),
        };

        /// <summary>A rock: mesh at y - 0.25 s, yaw -rot, uniform scale s; the (1.15, 0.7, 1) stretch is in the mesh (props.js buildRocks).</summary>
        public static Pose Rock(in VegetationSet.Rock r) => new Pose
        {
            position = BrCoords.Pos(r.x, r.y - 0.25f * r.s, r.z),
            rotation = Quaternion.Euler(0f, BrCoords.YawDeg(r.rot), 0f),
            scale = new Vector3(r.s, r.s, r.s),
        };

        /// <summary>
        /// A typed record's pose: position (x, y, z) and yaw from rot (lamps: faceRot; docks: the quay direction).
        /// Records without a y (dock) take y = 0.
        /// </summary>
        public static Pose Typed(PlacementSet.Typed t)
        {
            float yawJs = (float)t.rot;
            if (t.type == "lamp" && t.TryGet("faceRot", out var fr)) yawJs = (float)fr;
            else if (t.type == "dock" && t.TryGet("q.ux", out var ux) && t.TryGet("q.uz", out var uz)) yawJs = Mathf.Atan2((float)ux, (float)uz);
            return new Pose
            {
                position = BrCoords.Pos((float)t.x, (float)t.y, (float)t.z),
                rotation = Quaternion.Euler(0f, BrCoords.YawDeg(yawJs), 0f),
                scale = Vector3.one,
            };
        }

        /// <summary>JS yaw (radians) of a typed record as Typed() reads it.</summary>
        public static double TypedYawJs(PlacementSet.Typed t)
        {
            if (t.type == "lamp" && t.TryGet("faceRot", out var fr)) return fr;
            if (t.type == "dock" && t.TryGet("q.ux", out var ux) && t.TryGet("q.uz", out var uz)) return System.Math.Atan2(ux, uz);
            return t.rot;
        }

        /// <summary>Unity camera pose of a reference camera (cameras.json): position flipped, three.js quaternion converted.</summary>
        public static Pose Camera(in WorldInfo.CameraPose c) => new Pose
        {
            position = BrCoords.Pos(c.pos.x, c.pos.y, c.pos.z),
            rotation = CameraRotation(c),
            scale = Vector3.one,
        };

        /// <summary>
        /// three.js cameras look down their local -Z; Unity cameras down +Z. The flipped three.js quaternion maps
        /// three's -Z to Unity's (0,0,-1) rotated, so the Unity camera rotation is that times a half turn about Y.
        /// </summary>
        public static Quaternion CameraRotation(in WorldInfo.CameraPose c)
        {
            var q = BrCoords.Rot(c.quat.x, c.quat.y, c.quat.z, c.quat.w);
            return q * Quaternion.Euler(0f, 180f, 0f);
        }

        public static Pose Decompose(Matrix4x4 m)
        {
            var c0 = (Vector3)m.GetColumn(0);
            var c1 = (Vector3)m.GetColumn(1);
            var c2 = (Vector3)m.GetColumn(2);
            var scale = new Vector3(c0.magnitude, c1.magnitude, c2.magnitude);
            var rot = Quaternion.LookRotation(c2 / scale.z, c1 / scale.y);
            return new Pose { position = m.GetColumn(3), rotation = rot, scale = scale };
        }
    }
}
