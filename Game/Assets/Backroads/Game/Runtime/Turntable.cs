using UnityEngine;

namespace Backroads.Game
{
    /// <summary>Slowly spins its transform around world up (the test yard's canary markers).</summary>
    public sealed class Turntable : MonoBehaviour
    {
        public float degreesPerSecond = 12f;

        void Update() => transform.Rotate(0f, degreesPerSecond * Time.deltaTime, 0f, Space.World);
    }
}
