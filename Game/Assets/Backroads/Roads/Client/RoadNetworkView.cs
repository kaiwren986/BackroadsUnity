using UnityEngine;

namespace Backroads.Roads.Client
{
    /// <summary>
    /// Shows a RoadNetworkAsset as editor gizmos (drawn by Backroads.Roads.Client.Editor.RoadNetworkGizmos): nodes by kind,
    /// arms, fillet tangent points, mouths and kerb arcs, with labels. No run-time behaviour.
    /// </summary>
    public sealed class RoadNetworkView : MonoBehaviour
    {
        public RoadNetworkAsset network;
        public bool labels = true;
        public bool corners = true;
        public bool centrelines = true;
        [Tooltip("Height of the gizmos above the canon road surface.")]
        public float lift = 0.4f;
    }
}
