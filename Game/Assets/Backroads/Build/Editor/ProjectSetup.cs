using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Backroads.Core;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;

namespace Backroads.Build.Editor
{
    /// <summary>
    /// The only writer of ProjectSettings (PLAN 1.3, 1.10). Apply() normalises the project created from the URP template;
    /// Checks() lists every setting it owns, and the EditMode test Build.ProjectSettings re-checks each on every run.
    /// Batchmode: Unity.exe -batchmode -nographics -quit -projectPath Game -executeMethod Backroads.Build.Editor.ProjectSetup.Apply
    /// </summary>
    public static class ProjectSetup
    {
        public const string GameInfoPath = "Assets/Backroads/Core/GameInfo.asset";
        public const string RuntimeUiFolder = "Assets/Backroads/Client.UI/Resources";
        public const string ThemePath = RuntimeUiFolder + "/BrRuntimeTheme.tss";
        public const string PanelSettingsPath = RuntimeUiFolder + "/BrPanelSettings.asset";
        public const string DefaultPipelinePath = "Assets/Settings/PC_RPAsset.asset";
        public const string LowPipelinePath = "Assets/Settings/Mobile_RPAsset.asset";

        public const float FixedDeltaTime = 1f / 120f;
        public const float MaximumDeltaTime = 4f / 120f;
        public const int SampleRate = 48000;
        public const int BestLatencyDspBuffer = 256;
        public const int ActiveInputHandlerInputSystemOnly = 1;
        public const int SolverTypeTgs = 1;

        /// <summary>User layers 8.. in this order (PLAN 1.3).</summary>
        public static readonly string[] PhysicsLayers = { "Static", "Road", "Vehicle", "VehicleHull", "Wheel", "Prop", "Trigger", "TerrainProxy", "Puppet" };
        public const int FirstPhysicsLayer = 8;

        static readonly string[] ForbiddenPackages =
        {
            "com.unity.analytics", "com.unity.services.analytics", "com.unity.services.cloud-diagnostics",
            "com.unity.modules.unityanalytics", "com.unity.serialization", "com.unity.collab-proxy",
        };

        public static int Layer(string name) => FirstPhysicsLayer + Array.IndexOf(PhysicsLayers, name);

        /// <summary>The 06 section 5.2 matrix: VehicleHull and Wheel never touch TerrainProxy; Wheel touches only Static, Vehicle, Prop, Puppet.</summary>
        public static bool ShouldCollide(int a, int b)
        {
            int wheel = Layer("Wheel"), hull = Layer("VehicleHull"), proxy = Layer("TerrainProxy");
            if ((a == hull && b == proxy) || (a == proxy && b == hull)) return false;
            if (a == wheel || b == wheel)
            {
                int other = a == wheel ? b : a;
                return other == Layer("Static") || other == Layer("Vehicle") || other == Layer("Prop") || other == Layer("Puppet");
            }
            return true;
        }

        // ---------------------------------------------------------------------------------------------------------
        [MenuItem("Backroads/Project Setup/Apply")]
        public static void Apply()
        {
            int code = 0;
            try
            {
                ApplyAll();
                var problems = Verify();
                foreach (var p in problems) Debug.LogError("[BR] ProjectSetup check failed: " + p);
                Debug.Log($"[BR] ProjectSetup.Apply done: {Checks().Count} checks, {problems.Count} failed");
                code = problems.Count == 0 ? 0 : 1;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                code = 2;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        public static void ApplyAll()
        {
            var info = EnsureGameInfo();

            // Player
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);
            EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;
            PlayerSettings.companyName = info.companyName;
            PlayerSettings.productName = info.productName;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            PlayerSettings.insecureHttpOption = InsecureHttpOption.NotAllowed;
            PlayerSettings.runInBackground = true;           // autotests keep running when the window loses focus
            PlayerSettings.enableFrameTimingStats = true;    // MetricsRecorder main-thread time
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1600;
            PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.resizableWindow = true;
            var ps = new SerializedObject(Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings"));
            ps.FindProperty("activeInputHandler").intValue = ActiveInputHandlerInputSystemOnly;
            ps.FindProperty("submitAnalytics").boolValue = false; // "hardware statistics"; no public API in 6000.3
            ps.ApplyModifiedPropertiesWithoutUndo();

            // Privacy [C7]
            UnityEditor.Analytics.AnalyticsSettings.enabled = false;
            UnityEditor.CrashReporting.CrashReportingSettings.enabled = false;
            var connect = Settings("ProjectSettings/UnityConnectSettings.asset");
            SetInt(connect, "m_Enabled", 0);
            SetInt(connect, "InsightsSettings.m_Enabled", 0);
            SetInt(connect, "InsightsSettings.m_EngineDiagnosticsEnabled", 0);
            SetInt(connect, "CrashReportingSettings.m_EnableCloudDiagnosticsReporting", 0);
            SetInt(connect, "UnityAnalyticsSettings.m_Enabled", 0);
            SetInt(connect, "UnityAnalyticsSettings.m_InitializeOnStartup", 0);
            SetInt(connect, "PerformanceReportingSettings.m_Enabled", 0);
            SetInt(connect, "UnityPurchasingSettings.m_Enabled", 0);
            SetInt(connect, "UnityAdsSettings.m_Enabled", 0);
            connect.ApplyModifiedPropertiesWithoutUndo();

            // Editor
            EditorSettings.serializationMode = SerializationMode.ForceText;
            VersionControlSettings.mode = "Visible Meta Files";
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;

            // Time [S8]
            var time = Settings("ProjectSettings/TimeManager.asset");
            SetFixedTimestep(time, FixedDeltaTimeTicks);
            time.FindProperty("Maximum Allowed Timestep").floatValue = MaximumDeltaTime;
            time.ApplyModifiedPropertiesWithoutUndo();

            // Physics: TGS solver, layers and matrix (06 sections 3.4 and 5.2)
            var tags = Settings("ProjectSettings/TagManager.asset");
            var layers = tags.FindProperty("layers");
            for (int i = 0; i < PhysicsLayers.Length; i++) layers.GetArrayElementAtIndex(FirstPhysicsLayer + i).stringValue = PhysicsLayers[i];
            tags.ApplyModifiedPropertiesWithoutUndo();
            var dyn = Settings("ProjectSettings/DynamicsManager.asset");
            dyn.FindProperty("m_SolverType").intValue = SolverTypeTgs;
            dyn.ApplyModifiedPropertiesWithoutUndo();
            for (int a = 0; a < 32; a++)
                for (int b = a; b < 32; b++)
                    Physics.IgnoreLayerCollision(a, b, !ShouldCollide(a, b));

            // Audio (01 section 4.4)
            var audio = Settings("ProjectSettings/AudioManager.asset");
            audio.FindProperty("m_SampleRate").intValue = SampleRate;
            audio.FindProperty("m_DSPBufferSize").intValue = BestLatencyDspBuffer;
            var req = audio.FindProperty("m_RequestedDSPBufferSize");
            if (req != null) req.intValue = BestLatencyDspBuffer;
            audio.ApplyModifiedPropertiesWithoutUndo();

            // Graphics (04 section 2.2)
            // EditorGraphicsSettings.batchRendererGroupShaderStrippingMode is read-only in 6000.3: write the serialized field.
            var gfx = Settings("ProjectSettings/GraphicsSettings.asset");
            gfx.FindProperty("m_BrgStripping").intValue = (int)BatchRendererGroupStrippingMode.KeepAll;
            gfx.ApplyModifiedPropertiesWithoutUndo();
            var defaultRp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(DefaultPipelinePath);
            if (defaultRp == null) throw new InvalidOperationException("URP asset missing: " + DefaultPipelinePath);
            GraphicsSettings.defaultRenderPipeline = defaultRp;
            foreach (var rp in PipelineAssets())
            {
                rp.useSRPBatcher = true;
                rp.gpuResidentDrawerMode = GPUResidentDrawerMode.InstancedDrawing;
                rp.supportsDynamicBatching = false;
                foreach (var rd in rp.rendererDataList)
                {
                    if (rd is UniversalRendererData urd && urd.renderingMode != RenderingMode.ForwardPlus)
                    {
                        urd.renderingMode = RenderingMode.ForwardPlus;
                        EditorUtility.SetDirty(urd);
                    }
                }
                EditorUtility.SetDirty(rp);
            }
            // The Low tier (QualityPreset, "Mobile" level) renders at 0.7 with STP. URP strips the STP shaders from
            // players unless a pipeline asset in the build uses STP, so the Low asset carries it (lane W, M1).
            var lowRp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(LowPipelinePath);
            if (lowRp != null && lowRp.upscalingFilter != UpscalingFilterSelection.STP)
            {
                lowRp.upscalingFilter = UpscalingFilterSelection.STP;
                EditorUtility.SetDirty(lowRp);
            }

            SetBurstAot(true);
            EnsureNativePluginImport();
            EnsureRuntimeUiAssets();
            TestYardBuilder.EnsureScene(false);
            EditorBuildSettings.scenes = BuildCli.Scenes.Select(s => new EditorBuildSettingsScene(s, true)).ToArray();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        // ---------------------------------------------------------------------------------------------------------
        public sealed class Check
        {
            public readonly string Name;
            readonly Func<string> _problem;
            public Check(string name, Func<string> problem) { Name = name; _problem = problem; }
            /// <summary>null when the setting is right, else what is wrong.</summary>
            public string Problem()
            {
                try { return _problem(); }
                catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
            }
            public override string ToString() => Name;
        }

        public static List<string> Verify() =>
            Checks().Select(c => (c.Name, p: c.Problem())).Where(x => x.p != null).Select(x => $"{x.Name}: {x.p}").ToList();

        static string Expect<T>(T actual, T expected) => EqualityComparer<T>.Default.Equals(actual, expected) ? null : $"is {actual}, expected {expected}";
        static string ExpectF(float actual, float expected) => Mathf.Abs(actual - expected) < 1e-7f ? null : $"is {actual:R}, expected {expected:R}";

        public static List<Check> Checks()
        {
            var c = new List<Check>
            {
                new Check("colorSpace.Linear", () => Expect(PlayerSettings.colorSpace, ColorSpace.Linear)),
                new Check("urp.DefaultPipelineIsUrp", () => GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset ? null : "default render pipeline is not URP"),
                new Check("urp.EveryQualityLevelUsesUrp", () =>
                {
                    var bad = new List<string>();
                    for (int i = 0; i < QualitySettings.names.Length; i++)
                        if (!(QualitySettings.GetRenderPipelineAssetAt(i) is UniversalRenderPipelineAsset) && !(GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset))
                            bad.Add(QualitySettings.names[i]);
                    return bad.Count == 0 ? null : "no URP asset on " + string.Join(", ", bad);
                }),
                new Check("urp.ForwardPlus", () => EachRenderer(r => r.renderingMode == RenderingMode.ForwardPlus ? null : $"{r.name} is {r.renderingMode}")),
                new Check("urp.SrpBatcher", () => EachPipeline(rp => rp.useSRPBatcher ? null : rp.name + " has SRP Batcher off")),
                new Check("urp.GpuResidentDrawerInstanced", () => EachPipeline(rp => rp.gpuResidentDrawerMode == GPUResidentDrawerMode.InstancedDrawing ? null : $"{rp.name} is {rp.gpuResidentDrawerMode}")),
                new Check("urp.NoDynamicBatching", () => EachPipeline(rp => rp.supportsDynamicBatching ? rp.name + " has dynamic batching on" : null)),
                new Check("urp.LowTierKeepsStp", () =>
                {
                    var low = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(LowPipelinePath);
                    return low == null ? LowPipelinePath + " missing" : Expect(low.upscalingFilter, UpscalingFilterSelection.STP);
                }),
                new Check("graphics.BrgVariantsKeepAll", () => Expect(EditorGraphicsSettings.batchRendererGroupShaderStrippingMode, BatchRendererGroupStrippingMode.KeepAll)),
                new Check("editor.EnterPlayModeNoDomainNoSceneReload", () =>
                    EditorSettings.enterPlayModeOptionsEnabled &&
                    EditorSettings.enterPlayModeOptions == (EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload)
                        ? null : $"enabled={EditorSettings.enterPlayModeOptionsEnabled} options={EditorSettings.enterPlayModeOptions}"),
                new Check("time.FixedDeltaTime_1_120", () => ExpectF((float)GetFixedTimestep(Settings("ProjectSettings/TimeManager.asset")), FixedDeltaTime)),
                new Check("time.MaximumDeltaTime_4_120", () => ExpectF(Settings("ProjectSettings/TimeManager.asset").FindProperty("Maximum Allowed Timestep").floatValue, MaximumDeltaTime)),
                new Check("physics.SolverTgs", () => Expect(Settings("ProjectSettings/DynamicsManager.asset").FindProperty("m_SolverType").intValue, SolverTypeTgs)),
                new Check("input.InputSystemOnly", () => Expect(new SerializedObject(Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings")).FindProperty("activeInputHandler").intValue, ActiveInputHandlerInputSystemOnly)),
                new Check("input.PackageInstalled", () => ManifestHas("com.unity.inputsystem", "1.19.0")),
                new Check("audio.SampleRate48k", () => Expect(Settings("ProjectSettings/AudioManager.asset").FindProperty("m_SampleRate").intValue, SampleRate)),
                new Check("audio.DspBufferBestLatency", () => Expect(Settings("ProjectSettings/AudioManager.asset").FindProperty("m_DSPBufferSize").intValue, BestLatencyDspBuffer)),
                new Check("player.WindowsX64", () => Expect(EditorUserBuildSettings.activeBuildTarget, BuildTarget.StandaloneWindows64)),
                new Check("player.ScriptingBackendMono", () => Expect(PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone), ScriptingImplementation.Mono2x)),
                new Check("player.NamesFromGameInfo", () =>
                {
                    var gi = AssetDatabase.LoadAssetAtPath<GameInfo>(GameInfoPath);
                    if (gi == null) return "GameInfo asset missing at " + GameInfoPath;
                    return PlayerSettings.companyName == gi.companyName && PlayerSettings.productName == gi.productName
                        ? null : $"PlayerSettings {PlayerSettings.companyName}/{PlayerSettings.productName} vs GameInfo {gi.companyName}/{gi.productName}";
                }),
                new Check("player.GameInfoPlaceholders", () =>
                {
                    var gi = AssetDatabase.LoadAssetAtPath<GameInfo>(GameInfoPath);
                    if (gi == null) return "GameInfo asset missing";
                    return gi.companyName == GameInfo.PlaceholderCompany && gi.productName == GameInfo.PlaceholderProduct
                        ? null : $"GameInfo is {gi.companyName}/{gi.productName}; until U18 it stays {GameInfo.PlaceholderCompany}/{GameInfo.PlaceholderProduct}";
                }),
                new Check("player.RunInBackground", () => PlayerSettings.runInBackground ? null : "off"),
                new Check("burst.AotEnabled", () => GetBurstAot() ? null : "Burst AOT compilation off for StandaloneWindows64"),
                new Check("burst.NotEmbedded", () => UnityEditor.PackageManager.PackageInfo.FindForPackageName("com.unity.burst")?.source == UnityEditor.PackageManager.PackageSource.Embedded ? "Burst is embedded in Packages/ (04 section 1.2 rule 4)" : null),
                new Check("editor.ForceText", () => Expect(EditorSettings.serializationMode, SerializationMode.ForceText)),
                new Check("editor.VisibleMetaFiles", () => Expect(VersionControlSettings.mode, "Visible Meta Files")),
                new Check("privacy.SubmitAnalyticsOff", () => new SerializedObject(Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings")).FindProperty("submitAnalytics").boolValue ? "PlayerSettings submitAnalytics (hardware statistics) is on" : null),
                new Check("privacy.CloudDiagnosticsOff", () =>
                {
                    if (UnityEditor.CrashReporting.CrashReportingSettings.enabled) return "CrashReportingSettings.enabled";
                    var s = Settings("ProjectSettings/UnityConnectSettings.asset");
                    return GetInt(s, "CrashReportingSettings.m_EnableCloudDiagnosticsReporting") == 0 ? null : "cloud diagnostics reporting on";
                }),
                new Check("privacy.UnityServicesOff", () =>
                {
                    if (UnityEditor.Analytics.AnalyticsSettings.enabled) return "AnalyticsSettings.enabled";
                    var s = Settings("ProjectSettings/UnityConnectSettings.asset");
                    var on = new[] { "m_Enabled", "InsightsSettings.m_Enabled", "InsightsSettings.m_EngineDiagnosticsEnabled", "UnityAnalyticsSettings.m_Enabled", "PerformanceReportingSettings.m_Enabled" }
                        .Where(p => GetInt(s, p) != 0).ToList();
                    return on.Count == 0 ? null : "on: " + string.Join(", ", on);
                }),
                new Check("privacy.NoAnalyticsOrForbiddenPackages", () =>
                {
                    var lockText = File.ReadAllText(Path.Combine(RepoPaths.Game, "Packages", "packages-lock.json"));
                    var deps = BrJson.Obj(BrJson.Obj(BrJson.Parse(lockText))["dependencies"]);
                    var bad = ForbiddenPackages.Where(deps.ContainsKey).ToList();
                    return bad.Count == 0 ? null : "present: " + string.Join(", ", bad);
                }),
                new Check("privacy.InsecureHttpNotAllowed", () => Expect(PlayerSettings.insecureHttpOption, InsecureHttpOption.NotAllowed)),
                new Check("physics.CollisionMatrix", () =>
                {
                    var bad = new List<string>();
                    for (int a = 0; a < 32; a++)
                        for (int b = a; b < 32; b++)
                            if (Physics.GetIgnoreLayerCollision(a, b) == ShouldCollide(a, b))
                                bad.Add($"{Name(a)}x{Name(b)}");
                    return bad.Count == 0 ? null : "wrong: " + string.Join(", ", bad.Take(20));
                }),
                new Check("native.PluginExcludedFromServer", NativePluginProblem),
                new Check("ui.RuntimePanelSettings", () =>
                {
                    var p = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
                    return p == null ? "missing " + PanelSettingsPath : p.themeStyleSheet == null ? "no theme on PanelSettings" : null;
                }),
                new Check("build.ScenesMatchBuildCli", () =>
                {
                    var s = EditorBuildSettings.scenes.Select(x => x.path).ToArray();
                    var want = BuildCli.Scenes;
                    if (!s.SequenceEqual(want)) return $"scenes are [{string.Join(", ", s)}], BuildCli builds [{string.Join(", ", want)}]";
                    var missing = want.Where(p => !File.Exists(Path.Combine(RepoPaths.Game, p))).ToList();
                    return missing.Count == 0 ? null : "missing " + string.Join(", ", missing);
                }),
            };
            foreach (var l in PhysicsLayers)
            {
                var name = l;
                c.Add(new Check("physics.Layer." + name, () => Expect(LayerMask.LayerToName(Layer(name)), name)));
            }
            return c;
        }

        static string Name(int layer) { var n = LayerMask.LayerToName(layer); return string.IsNullOrEmpty(n) ? layer.ToString() : n; }

        static string ManifestHas(string id, string version)
        {
            var m = BrJson.Obj(BrJson.Parse(File.ReadAllText(Path.Combine(RepoPaths.Game, "Packages", "manifest.json"))));
            var deps = BrJson.Obj(m["dependencies"]);
            return deps.TryGetValue(id, out var v) && (string)v == version ? null : $"{id} is not pinned to {version}";
        }

        // ---------------------------------------------------------------------------------------------------------
        public static IEnumerable<UniversalRenderPipelineAsset> PipelineAssets()
        {
            var set = new HashSet<UniversalRenderPipelineAsset>();
            if (GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset d) set.Add(d);
            for (int i = 0; i < QualitySettings.names.Length; i++)
                if (QualitySettings.GetRenderPipelineAssetAt(i) is UniversalRenderPipelineAsset q) set.Add(q);
            return set;
        }

        static string EachPipeline(Func<UniversalRenderPipelineAsset, string> f)
        {
            var bad = PipelineAssets().Select(f).Where(x => x != null).ToList();
            return bad.Count == 0 ? null : string.Join("; ", bad);
        }

        static string EachRenderer(Func<UniversalRendererData, string> f)
        {
            var bad = new List<string>();
            foreach (var rp in PipelineAssets())
                foreach (var rd in rp.rendererDataList)
                    if (rd is UniversalRendererData urd) { var p = f(urd); if (p != null) bad.Add(p); }
                    else if (rd != null) bad.Add($"{rd.name} is not a Universal Renderer");
            return bad.Count == 0 ? null : string.Join("; ", bad);
        }

        /// <summary>Unity 6 stores Fixed Timestep as a rational (m_Count ticks at m_Rate per second); 141120000 / 120 = 1176000.</summary>
        public const long FixedTimestepRate = 141120000;
        public const long FixedDeltaTimeTicks = FixedTimestepRate / 120;

        static void SetFixedTimestep(SerializedObject time, long ticks)
        {
            var p = time.FindProperty("Fixed Timestep");
            if (p.propertyType == SerializedPropertyType.Float) { p.floatValue = (float)ticks / FixedTimestepRate; return; }
            p.FindPropertyRelative("m_Count").longValue = ticks;
            p.FindPropertyRelative("m_Rate.m_Numerator").longValue = FixedTimestepRate;
            p.FindPropertyRelative("m_Rate.m_Denominator").longValue = 1;
        }

        static double GetFixedTimestep(SerializedObject time)
        {
            var p = time.FindProperty("Fixed Timestep");
            if (p.propertyType == SerializedPropertyType.Float) return p.floatValue;
            double num = p.FindPropertyRelative("m_Rate.m_Numerator").longValue, den = p.FindPropertyRelative("m_Rate.m_Denominator").longValue;
            return num == 0 ? 0 : p.FindPropertyRelative("m_Count").longValue * den / num;
        }

        static SerializedObject Settings(string path)
        {
            var objs = AssetDatabase.LoadAllAssetsAtPath(path);
            if (objs == null || objs.Length == 0) throw new InvalidOperationException("cannot load " + path);
            return new SerializedObject(objs[0]);
        }

        static void SetInt(SerializedObject so, string path, int v)
        {
            var p = so.FindProperty(path);
            if (p == null) { Debug.LogWarning("[BR] ProjectSetup: no property " + path); return; }
            if (p.propertyType == SerializedPropertyType.Boolean) p.boolValue = v != 0; else p.intValue = v;
        }

        static int GetInt(SerializedObject so, string path)
        {
            var p = so.FindProperty(path);
            if (p == null) return 0;
            return p.propertyType == SerializedPropertyType.Boolean ? (p.boolValue ? 1 : 0) : p.intValue;
        }

        static GameInfo EnsureGameInfo()
        {
            var gi = AssetDatabase.LoadAssetAtPath<GameInfo>(GameInfoPath);
            if (gi != null) return gi;
            gi = ScriptableObject.CreateInstance<GameInfo>();
            AssetDatabase.CreateAsset(gi, GameInfoPath);
            return gi;
        }

        static Type BurstSettingsType => Type.GetType("Unity.Burst.Editor.BurstPlatformAotSettings, Unity.Burst.Editor");

        static object BurstSettings()
        {
            var t = BurstSettingsType ?? throw new InvalidOperationException("Burst editor assembly not found");
            var get = t.GetMethod("GetOrCreateSettings", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(BuildTarget?) }, null)
                      ?? throw new InvalidOperationException("BurstPlatformAotSettings.GetOrCreateSettings not found");
            return get.Invoke(null, new object[] { (BuildTarget?)BuildTarget.StandaloneWindows64 });
        }

        static bool GetBurstAot()
        {
            var s = BurstSettings();
            return (bool)BurstSettingsType.GetField("EnableBurstCompilation", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(s);
        }

        static void SetBurstAot(bool on)
        {
            var s = BurstSettings();
            var t = BurstSettingsType;
            t.GetField("EnableBurstCompilation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(s, on);
            t.GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(s, new object[] { (BuildTarget?)BuildTarget.StandaloneWindows64 });
        }

        public const string NativePluginPath = "Assets/Plugins/x86_64/EngineSimPlugin.dll";
        public const string NotServerDefine = "!UNITY_SERVER";

        /// <summary>
        /// Rule 8 by construction: EngineSimPlugin.dll (built by tools\ci\build-native.ps1, lane V) imports for the
        /// editor and Windows x64 players only, with the define constraint !UNITY_SERVER, so the ServerHeadless player
        /// never contains it. Skipped while the DLL is not built.
        /// </summary>
        static void EnsureNativePluginImport()
        {
            if (!File.Exists(Path.Combine(RepoPaths.Game, NativePluginPath))) return;
            AssetDatabase.ImportAsset(NativePluginPath, ImportAssetOptions.ForceSynchronousImport);
            var pi = AssetImporter.GetAtPath(NativePluginPath) as PluginImporter;
            if (pi == null) throw new InvalidOperationException("no PluginImporter for " + NativePluginPath);
            pi.SetCompatibleWithAnyPlatform(false);
            pi.SetExcludeEditorFromAnyPlatform(false);
            foreach (var t in new[] { BuildTarget.StandaloneWindows, BuildTarget.StandaloneWindows64, BuildTarget.StandaloneLinux64, BuildTarget.StandaloneOSX })
                pi.SetCompatibleWithPlatform(t, t == BuildTarget.StandaloneWindows64);
            pi.SetCompatibleWithEditor(true);
            pi.SetEditorData("CPU", "x86_64");
            pi.SetEditorData("OS", "Windows");
            pi.SetPlatformData(BuildTarget.StandaloneWindows64, "CPU", "x86_64");
            pi.DefineConstraints = new[] { NotServerDefine };
            pi.SaveAndReimport();
        }

        static string NativePluginProblem()
        {
            if (!File.Exists(Path.Combine(RepoPaths.Game, NativePluginPath))) return null; // not built here: nothing to exclude
            var pi = AssetImporter.GetAtPath(NativePluginPath) as PluginImporter;
            if (pi == null) return "not imported as a plugin";
            if (pi.DefineConstraints == null || !pi.DefineConstraints.Contains(NotServerDefine)) return "define constraint !UNITY_SERVER missing (rule 8)";
            if (!pi.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64)) return "not enabled for StandaloneWindows64";
            if (!pi.GetCompatibleWithEditor()) return "not enabled for the editor";
            return null;
        }

        static void EnsureRuntimeUiAssets()
        {
            Directory.CreateDirectory(Path.Combine(RepoPaths.Game, RuntimeUiFolder));
            var themeFile = Path.Combine(RepoPaths.Game, ThemePath);
            if (!File.Exists(themeFile))
            {
                File.WriteAllText(themeFile, "@import url(\"unity-theme://default\");\nVisualElement {}\n");
                AssetDatabase.ImportAsset(ThemePath, ImportAssetOptions.ForceUpdate);
            }
            var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (panel == null)
            {
                panel = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(panel, PanelSettingsPath);
            }
            panel.themeStyleSheet = theme;
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1920, 1080);
            panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            panel.match = 0.5f;
            EditorUtility.SetDirty(panel);
        }
    }
}
