using UnityEngine;

namespace Backroads.Core
{
    /// <summary>
    /// JS (three.js, right-handed, +x = left) to Unity (left-handed, +x = right), docs/research/03-port-map.md section 2.
    /// Unity position = (-x, y, z); Unity yaw in degrees = -yawJsRad * 180 / pi.
    /// </summary>
    public static class BrCoords
    {
        public const int VN = 2049;
        public const float CELL = 0.5f;
        public const float HALF = 512f;

        public static Vector3 Pos(float x, float y, float z) => new Vector3(-x, y, z);

        /// <summary>three.js rotation.y (radians) to Unity euler y (degrees).</summary>
        public static float YawDeg(float yawJsRad) => -yawJsRad * Mathf.Rad2Deg;

        /// <summary>three.js quaternion (x, y, z, w) to Unity.</summary>
        public static Quaternion Rot(float qx, float qy, float qz, float qw) => new Quaternion(qx, -qy, -qz, qw);

        static readonly Matrix4x4 S = Matrix4x4.Scale(new Vector3(-1, 1, 1));

        /// <summary>three.js Matrix4.elements (column-major, 16 floats) to a Unity matrix: S * m * S.</summary>
        public static Matrix4x4 Mat(float[] e)
        {
            var m = new Matrix4x4(
                new Vector4(e[0], e[1], e[2], e[3]),
                new Vector4(e[4], e[5], e[6], e[7]),
                new Vector4(e[8], e[9], e[10], e[11]),
                new Vector4(e[12], e[13], e[14], e[15]));
            return S * m * S;
        }

        /// <summary>Unity position back to JS coordinates (the grid core stays in JS coordinates, 03 section 2).</summary>
        public static Vector3 ToJs(Vector3 unity) => new Vector3(-unity.x, unity.y, unity.z);

        /// <summary>Normals and direction vectors convert like positions: (-nx, ny, nz).</summary>
        public static Vector3 Dir(float x, float y, float z) => new Vector3(-x, y, z);
    }
}
