#if DEVELOPMENT_BUILD || UNITY_EDITOR
using Backroads.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Backroads.Roads.Client
{
    /// <summary>
    /// Dev-build road graph overlay (T2.8 step 3): registered with PresentationRegistry, so it is there in every scene of a
    /// Development build (and in the editor), Millbrook included, without editing any scene. Hidden until F7; built on first
    /// use from Resources (BrRoadNetwork asset, BrRoadsOverlay material). Not compiled into Client release builds.
    /// RoadsDebug.unity has its own overlay and takes F7 there.
    /// </summary>
    public static class RoadsDevOverlay
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Register() => PresentationRegistry.Register("roads-graph-overlay", Start);

        static void Start(GameObject root)
        {
            if (Application.isBatchMode) return;
            root.AddComponent<RoadsDevOverlayToggle>();
        }
    }

    public sealed class RoadsDevOverlayToggle : MonoBehaviour
    {
        RoadGraphOverlay _overlay;

        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null || !kb.f7Key.wasPressedThisFrame || RoadsDebugView.Active != null) return;
            if (_overlay == null)
            {
                var net = RoadNetworkAsset.Load();
                if (net == null) { Log.Warn("road graph overlay: no BrRoadNetwork asset in Resources"); return; }
                _overlay = RoadGraphOverlay.Create(transform, net, null);
                Log.Info($"road graph overlay on: {net.nodes.Length} nodes, {net.edges.Length} edges (F7 toggles)");
                return;
            }
            _overlay.Visible = !_overlay.Visible;
        }
    }
}
#endif
