using System.Collections;
using Backroads.Client;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Backroads.Roads.Client
{
    /// <summary>
    /// RoadsDebug.unity (lane R, R0): Millbrook (lane W's MillbrookWorld, unchanged) with the road graph overlay, and the R0
    /// plain ribbons next to the M1 ribbons. Modes: 1 graph over Millbrook with the M1 ribbons, 2 the R0 plain ribbons
    /// instead, 3 side by side (left half M1 ribbons, right half R0 ribbons, same view). F7 overlay, L labels, T town from
    /// above, O whole network, J downtown junctions (nodes 7, 9), N next node. The fly camera (WASD, right mouse) moves freely.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class RoadsDebugView : MonoBehaviour
    {
        public enum Mode { Graph = 1, Ribbons = 2, SideBySide = 3 }

        public RoadNetworkAsset network;
        public Material ribbonMaterial, overlayMaterial;
        public Camera mainCamera;
        public Mode mode = Mode.Graph;
        public bool overlay = true;

        public static RoadsDebugView Active { get; private set; }
        public bool Ready { get; private set; }
        public RoadGraphOverlay Overlay { get; private set; }
        public MeshRenderer R0Ribbons { get; private set; }
        public Renderer M1Ribbons { get; private set; }
        public Camera RightCamera { get; private set; }
        int _node = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Active = null;

        void Awake()
        {
            Active = this;
            if (network == null) network = RoadNetworkAsset.Load();
            if (mainCamera == null) mainCamera = Camera.main;
        }

        void OnDestroy()
        {
            if (Active == this) Active = null;
        }

        void OnEnable() => RenderPipelineManager.beginCameraRendering += BeginCamera;
        void OnDisable() => RenderPipelineManager.beginCameraRendering -= BeginCamera;

        IEnumerator Start()
        {
            if (network == null)
            {
                Debug.LogError("[BR] RoadsDebug: no RoadNetworkAsset (Backroads > Roads > Bake road network (R0))");
                yield break;
            }
            var ribbons = new GameObject("R0 plain ribbons");
            ribbons.transform.SetParent(transform, false);
            ribbons.AddComponent<MeshFilter>().sharedMesh = PlainRibbonMesh.ToMesh(PlainRibbonMesh.Strips(network));
            R0Ribbons = ribbons.AddComponent<MeshRenderer>();
            R0Ribbons.sharedMaterial = ribbonMaterial;
            R0Ribbons.shadowCastingMode = ShadowCastingMode.Off;

            Overlay = RoadGraphOverlay.Create(transform, network, overlayMaterial);
            Overlay.LabelCamera = mainCamera;

            var world = MillbrookWorld.Current;
            while (world != null && !world.Ready && world.Error == null) yield return null;
            if (world != null)
            {
                var roads = world.transform.Find("Roads");
                if (roads != null) M1Ribbons = roads.GetComponent<Renderer>();
            }
            Apply();
            Ready = true;
            Debug.Log($"[BR] RoadsDebug ready: {network.nodes.Length} nodes, {network.edges.Length} edges; M1 ribbons {(M1Ribbons != null ? "found" : "missing")}");
        }

        public void SetMode(Mode m)
        {
            mode = m;
            Apply();
        }

        void Apply()
        {
            bool split = mode == Mode.SideBySide;
            if (split && RightCamera == null && mainCamera != null)
            {
                var go = new GameObject("Right camera (R0 ribbons)");
                go.transform.SetParent(mainCamera.transform, false);
                RightCamera = go.AddComponent<Camera>();
                RightCamera.CopyFrom(mainCamera);
                var src = mainCamera.GetComponent<UniversalAdditionalCameraData>();
                var dst = go.AddComponent<UniversalAdditionalCameraData>();
                if (src != null) { dst.renderPostProcessing = src.renderPostProcessing; dst.renderShadows = src.renderShadows; }
            }
            if (mainCamera != null) mainCamera.rect = split ? new Rect(0, 0, 0.5f, 1) : new Rect(0, 0, 1, 1);
            if (RightCamera != null)
            {
                RightCamera.enabled = split;
                RightCamera.rect = new Rect(0.5f, 0, 0.5f, 1);
                RightCamera.fieldOfView = mainCamera.fieldOfView;
                RightCamera.transform.localPosition = Vector3.zero;
                RightCamera.transform.localRotation = Quaternion.identity;
            }
            if (M1Ribbons != null) M1Ribbons.forceRenderingOff = mode == Mode.Ribbons;
            if (R0Ribbons != null) R0Ribbons.forceRenderingOff = mode == Mode.Graph;
            if (Overlay != null) Overlay.Visible = overlay;
        }

        /// <summary>Side by side: the left camera draws the M1 ribbons only, the right camera the R0 ribbons only.</summary>
        void BeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (mode != Mode.SideBySide) return;
            bool left = cam == mainCamera, right = cam == RightCamera;
            if (!left && !right) return;
            if (M1Ribbons != null) M1Ribbons.forceRenderingOff = !left;
            if (R0Ribbons != null) R0Ribbons.forceRenderingOff = !right;
            if (Overlay != null) Overlay.FaceCamera(cam);
        }

        // ------------------------------------------------------------------ camera presets (Unity space)

        public void ViewTown() => Pose(new Vector3(85f, 330f, 85f), Quaternion.Euler(90f, 0f, 0f));
        public void ViewOverview() => Pose(new Vector3(-15f, 560f, -36f), Quaternion.Euler(90f, 0f, 0f));

        /// <summary>Oblique view over downtown nodes 7 and 9 (and 2 and 16 behind), from the south-east.</summary>
        public void ViewDowntown() => LookAt(new Vector3(65f, 14f, 25f), new Vector3(65f, 120f, -95f));

        public void ViewNode(int id)
        {
            if (network == null || id < 0 || id >= network.nodes.Length) return;
            var p = network.nodes[id].position;
            LookAt(p, p + new Vector3(0f, 55f, -42f));
        }

        void LookAt(Vector3 target, Vector3 from) => Pose(from, Quaternion.LookRotation(target - from, Vector3.up));

        void Pose(Vector3 p, Quaternion r)
        {
            if (mainCamera == null) return;
            mainCamera.transform.SetPositionAndRotation(p, r);
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.digit1Key.wasPressedThisFrame) SetMode(Mode.Graph);
            if (kb.digit2Key.wasPressedThisFrame) SetMode(Mode.Ribbons);
            if (kb.digit3Key.wasPressedThisFrame) SetMode(Mode.SideBySide);
            if (kb.f7Key.wasPressedThisFrame) { overlay = !overlay; Apply(); }
            if (kb.lKey.wasPressedThisFrame && Overlay != null) Overlay.ShowLabels = !Overlay.ShowLabels;
            if (kb.tKey.wasPressedThisFrame) ViewTown();
            if (kb.oKey.wasPressedThisFrame) ViewOverview();
            if (kb.jKey.wasPressedThisFrame) ViewDowntown();
            if (kb.nKey.wasPressedThisFrame && network != null)
            {
                _node = (_node + (kb.shiftKey.isPressed ? network.nodes.Length - 1 : 1)) % network.nodes.Length;
                ViewNode(_node);
            }
        }

        void OnGUI()
        {
            if (Application.isBatchMode) return;
            const int w = 430;
            GUI.Box(new Rect(10, 10, w, 132), GUIContent.none);
            GUI.Label(new Rect(20, 16, w - 20, 120),
                "Roads R0 debug (lane R)\n" +
                $"mode {(int)mode} {mode}: 1 graph + M1 ribbons, 2 R0 ribbons, 3 side by side (left M1, right R0)\n" +
                "F7 overlay   L labels   T town   O network   J downtown   N / Shift+N nodes\n" +
                "nodes: red X, orange T, brown dirt apron, blue L-bend, grey dead end, green town edge\n" +
                "arms: cyan major road, red stops; yellow mouth, white kerb (provisional)\n" +
                (network != null ? $"{network.nodes.Length} nodes, {network.edges.Length} edges, {network.arms.Length} arm ends" : "no road asset"));
        }
    }
}
