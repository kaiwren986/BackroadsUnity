using System.Collections.Generic;
using Backroads.World;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Backroads.Client.Rendering
{
    /// <summary>
    /// T1.4 "sun + gradient ambient": sky.js's day/night maths in Unity. For an hour of day it sets the sun (or moon)
    /// light, the trilight ambient (three's hemisphere light plus the sky IBL it stands in for), exp2 fog with the JS
    /// horizon colours, the gradient sky globals, night-dependent emission (_BrSky.x), exposure and bloom, and the
    /// nearest street-lamp point lights at night (main.js updateLights: the six nearest within 140 m).
    /// Directions: sky.js computes in JS space; x is negated for Unity (03 section 2).
    /// </summary>
    public sealed class DayNightPresenter
    {
        static readonly (float elev, int hex)[] Fog =
        {
            (-14, 0x070c1a), (-6, 0x161d36), (-1, 0x5e4660), (3, 0xe08658), (9, 0xe8b08a), (20, 0xbccbd8), (45, 0xadc2d6),
        };

        static readonly int SkyId = Shader.PropertyToID("_BrSky");
        static readonly int ZenithId = Shader.PropertyToID("_BrSkyZenith");
        static readonly int HorizonId = Shader.PropertyToID("_BrSkyHorizon");
        static readonly int GlowId = Shader.PropertyToID("_BrSkyGlow");
        static readonly int SunDirId = Shader.PropertyToID("_BrSunDir");
        static readonly int SunDiscId = Shader.PropertyToID("_BrSunDisc");

        readonly Light _sun;
        readonly Transform _parent;
        readonly MillbrookLook _look;
        readonly PlacementSet.Lamp[] _lamps;
        readonly List<Light> _lampLights = new List<Light>();
        readonly ColorAdjustments _exposure;
        readonly Bloom _bloom;

        public float Hour { get; private set; } = 10f;
        public float NightFactor { get; private set; }
        public float SunElevationDeg { get; private set; }
        public float WindTime;
        public int LampLightCount = 6;
        public float LampLightRadius = 140f;

        public DayNightPresenter(Light sun, MillbrookLook look, PlacementSet placements, Transform parent, Volume volume)
        {
            _sun = sun;
            _look = look;
            _lamps = placements.lamps;
            _parent = parent;
            if (look.lightPool != null) SpawnLightPools(look.lightPool, parent);
            if (volume != null && volume.profile != null)
            {
                volume.profile.TryGet(out _exposure);
                volume.profile.TryGet(out _bloom);
            }
            RenderSettings.skybox = look.sky;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            // reflections come from the analytic sky in the shaders (BrCommon BrSkyColor); URP's glossy fallback is off
            RenderSettings.reflectionIntensity = 0f;
        }

        static float Smooth(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3 - 2 * t);
        }

        /// <summary>Sets the time of day (hours, 0-24) and updates lights, sky, fog and emission. camera: for lamp lights.</summary>
        public void SetHour(float hour, Vector3 cameraUnity)
        {
            Hour = (hour % 24 + 24) % 24;
            // long days, short nights: sunrise 05:30, sunset 20:00
            const float SR = 5.5f, SS = 20f;
            float t = Hour >= SR && Hour <= SS ? (Hour - SR) / (SS - SR) * Mathf.PI : Mathf.PI + ((Hour - SS + 24) % 24) / (24 - (SS - SR)) * Mathf.PI;
            const float tilt = 0.98f;
            var sunJs = new Vector3(Mathf.Cos(t), Mathf.Sin(t) * Mathf.Sin(tilt), Mathf.Sin(t) * Mathf.Cos(tilt)).normalized;
            float elev = Mathf.Asin(sunJs.y) * Mathf.Rad2Deg;
            SunElevationDeg = elev;
            float nf = 1 - Smooth(-7, 3, elev);
            NightFactor = nf;
            float low = 1 - Smooth(0, 28, elev);

            // the one shadow-casting light: the sun by day, the moon by night
            Vector3 L;
            Color lightColor;
            float intensityJs;
            if (elev > -2.5f)
            {
                L = sunJs;
                if (L.y < 0.06f) { L.y = 0.06f; L.Normalize(); }
                float warm = Smooth(-2, 14, elev), hi = Smooth(10, 40, elev);
                lightColor = new Color(1.0f, 0.42f + 0.4f * warm + 0.14f * hi, 0.16f + 0.42f * warm + 0.3f * hi); // three setRGB: linear
                intensityJs = 3.4f * Smooth(-2.5f, 7, elev);
            }
            else
            {
                L = -sunJs;
                L.x += 0.25f;
                L.Normalize();
                if (L.y < 0.15f) { L.y = 0.15f; L.Normalize(); }
                lightColor = new Color(0.55f, 0.66f, 1.0f);
                // three's ACES fit crushes the darkest tones much harder than URP's ACES: the moon is dimmed to match
                intensityJs = 0.7f * Smooth(-2.5f, -9, elev) * 0.6f;
            }
            var lu = new Vector3(-L.x, L.y, L.z); // towards the light, Unity space
            _sun.transform.rotation = Quaternion.LookRotation(-lu, Vector3.up);
            _sun.color = lightColor.gamma;
            _sun.intensity = intensityJs * _look.sunScale;
            _sun.shadows = LightShadows.Soft;

            // ambient: hemisphere light (sky.js) scaled up to stand in for the sky IBL as well
            var hemiSky = Color.Lerp(new Color(0.55f + 0.2f * low * (1 - nf), 0.68f - 0.1f * low, 1.0f - 0.25f * low), new Color(0.32f, 0.42f, 0.75f), nf);
            var hemiGround = Color.Lerp(new Color(0.36f, 0.3f, 0.22f), new Color(0.1f, 0.1f, 0.14f), nf);
            var fog = FogColor(elev); // linear
            float amb = _look.ambientScale * (1.0f - nf * 0.945f);
            var aSky = hemiSky * amb;
            var aEq = Color.Lerp(hemiSky, fog, 0.5f) * amb * 0.85f;
            var aGround = hemiGround * amb * 0.9f;
            RenderSettings.ambientSkyColor = aSky.gamma;
            RenderSettings.ambientEquatorColor = aEq.gamma;
            RenderSettings.ambientGroundColor = aGround.gamma;
            RenderSettings.ambientIntensity = 1f;
            // set the probe explicitly: the trilight colours are not turned into SH by themselves at run time
            RenderSettings.ambientProbe = TrilightProbe(aSky, aEq, aGround);

            RenderSettings.fogColor = fog.gamma;
            RenderSettings.fogDensity = 0.00105f + nf * 0.0006f + low * 0.0002f;

            // sky globals (linear colours)
            var zenith = Color.Lerp(new Color(0.30f, 0.50f, 0.80f) * (0.6f + 0.5f * (1 - low)), new Color(0.013f, 0.027f, 0.085f), Smooth(-3, -12, elev));
            Shader.SetGlobalColor(ZenithId, zenith);
            // three's sky dome reads brighter than its fog colour by day (skyGain, Rayleigh); at night it is the fog colour
            Shader.SetGlobalColor(HorizonId, Color.Lerp(Color.Lerp(fog, Color.white, 0.12f) * 1.3f, fog * 1.6f, nf));
            float glowAmt = Smooth(-6, 0, elev) * (1 - Smooth(5, 18, elev)) * 0.8f;
            Shader.SetGlobalVector(GlowId, new Vector4(1.0f, 0.36f + 0.16f * Smooth(-4, 10, elev), 0.14f + 0.1f * Smooth(-4, 10, elev), glowAmt));
            var discDir = elev > -2.5f ? new Vector3(-sunJs.x, sunJs.y, sunJs.z) : lu;
            Shader.SetGlobalVector(SunDirId, new Vector4(discDir.x, discDir.y, discDir.z, 0));
            var disc = elev > -2.5f ? (Vector4)(lightColor * 6f) : new Vector4(1.4f, 1.4f, 1.3f, 0);
            disc.w = elev > -2.5f ? 0.0012f : 0.0009f;
            Shader.SetGlobalVector(SunDiscId, disc);
            Shader.SetGlobalVector(SkyId, new Vector4(nf, Hour, WindTime, _look.emissionScale));

            // exposure (three: ACES with toneMappingExposure 0.95 + 0.45 nf) and bloom (0.16 + 0.3 nf)
            if (_exposure != null) { _exposure.postExposure.overrideState = true; _exposure.postExposure.value = _look.dayExposureEv + Mathf.Log(1 + nf * 0.45f / 0.95f, 2); }
            if (_bloom != null) { _bloom.intensity.overrideState = true; _bloom.intensity.value = 0.16f + nf * 0.3f; }

            UpdateLamps(cameraUnity);
        }

        /// <summary>Per frame: the wind clock and the lamp lights follow the camera.</summary>
        public void Tick(float dt, Vector3 cameraUnity, bool advanceWind)
        {
            if (advanceWind) WindTime += dt;
            Shader.SetGlobalVector(SkyId, new Vector4(NightFactor, Hour, WindTime, _look.emissionScale));
            UpdateLamps(cameraUnity);
        }

        /// <summary>props.js buildLightPools: a 15 m additive quad at each lamp's ground + 0.09 (the shader fades it in at night).</summary>
        void SpawnLightPools(Material mat, Transform parent)
        {
            var v = new List<Vector3>();
            var uv = new List<Vector2>();
            var tri = new List<int>();
            foreach (var l in _lamps)
            {
                var c = new Vector3(-l.x, l.gy + 0.09f, l.z);
                int b = v.Count;
                v.Add(c + new Vector3(-7.5f, 0, -7.5f)); uv.Add(new Vector2(0, 0));
                v.Add(c + new Vector3(7.5f, 0, -7.5f)); uv.Add(new Vector2(1, 0));
                v.Add(c + new Vector3(-7.5f, 0, 7.5f)); uv.Add(new Vector2(0, 1));
                v.Add(c + new Vector3(7.5f, 0, 7.5f)); uv.Add(new Vector2(1, 1));
                tri.Add(b); tri.Add(b + 2); tri.Add(b + 1); tri.Add(b + 1); tri.Add(b + 2); tri.Add(b + 3);
            }
            var m = new Mesh { name = "light_pools" };
            m.SetVertices(v);
            m.SetUVs(0, uv);
            m.SetTriangles(tri, 0);
            m.RecalculateBounds();
            var go = new GameObject("LightPools");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = m;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        readonly List<(float d, int i)> _sorted = new List<(float, int)>();

        void UpdateLamps(Vector3 cam)
        {
            float cx = -cam.x, cz = cam.z;
            _sorted.Clear();
            for (int i = 0; i < _lamps.Length; i++)
            {
                float dx = _lamps[i].x - cx, dz = _lamps[i].z - cz;
                float d = dx * dx + dz * dz;
                if (d < LampLightRadius * LampLightRadius) _sorted.Add((d, i));
            }
            _sorted.Sort((a, b) => a.d.CompareTo(b.d));
            while (_lampLights.Count < LampLightCount)
            {
                var go = new GameObject("LampLight" + _lampLights.Count);
                go.transform.SetParent(_parent, false);
                var nl = go.AddComponent<Light>();
                nl.type = LightType.Point;
                nl.color = BrColor.Hex(0xffc98a);
                nl.range = _look.lampRange;
                nl.shadows = LightShadows.None;
                nl.enabled = false;
                _lampLights.Add(nl);
            }
            for (int k = 0; k < _lampLights.Count; k++)
            {
                var l = _lampLights[k];
                if (k >= LampLightCount || k >= _sorted.Count || NightFactor < 0.05f) { l.enabled = false; continue; }
                var lamp = _lamps[_sorted[k].i];
                l.transform.position = new Vector3(-lamp.x, lamp.y - 0.3f, lamp.z);
                l.intensity = _look.lampIntensity * NightFactor;
                l.enabled = true;
            }
        }

        /// <summary>
        /// An ambient probe whose irradiance is exactly sky (up), equator (horizontal) and ground (down): the L0, L1 (y)
        /// and L2 (y^2) coefficients solved against SphericalHarmonicsL2.Evaluate, so Unity's own SH conventions apply.
        /// </summary>
        public static SphericalHarmonicsL2 TrilightProbe(Color sky, Color equator, Color ground)
        {
            var dirs = new[] { Vector3.up, Vector3.right, Vector3.down };
            int[] idx = { 0, 1, 6 };
            var m = new float[3, 3];
            var res = new Color[3];
            for (int b = 0; b < 3; b++)
            {
                var basis = new SphericalHarmonicsL2();
                basis[0, idx[b]] = 1f;
                basis.Evaluate(dirs, res);
                for (int d = 0; d < 3; d++) m[d, b] = res[d].r;
            }
            var sh = new SphericalHarmonicsL2();
            for (int c = 0; c < 3; c++)
            {
                var x = Solve3(m, sky[c], equator[c], ground[c]);
                for (int b = 0; b < 3; b++) sh[c, idx[b]] = x[b];
            }
            return sh;
        }

        static float[] Solve3(float[,] a, float t0, float t1, float t2)
        {
            float Det(float[,] q) =>
                q[0, 0] * (q[1, 1] * q[2, 2] - q[1, 2] * q[2, 1]) - q[0, 1] * (q[1, 0] * q[2, 2] - q[1, 2] * q[2, 0]) + q[0, 2] * (q[1, 0] * q[2, 1] - q[1, 1] * q[2, 0]);
            float d = Det(a);
            var r = new float[3];
            var t = new[] { t0, t1, t2 };
            for (int col = 0; col < 3; col++)
            {
                var q = (float[,])a.Clone();
                for (int row = 0; row < 3; row++) q[row, col] = t[row];
                r[col] = Mathf.Abs(d) < 1e-12f ? 0 : Det(q) / d;
            }
            return r;
        }

        static Color FogColor(float elev)
        {
            int a = 0, b = Fog.Length - 1;
            for (int i = 0; i < Fog.Length - 1; i++)
                if (elev >= Fog[i].elev && elev <= Fog[i + 1].elev) { a = i; b = i + 1; break; }
            float f = elev <= Fog[0].elev ? 0 : elev >= Fog[b].elev ? 1 : (elev - Fog[a].elev) / (Fog[b].elev - Fog[a].elev);
            return Color.Lerp(BrColor.HexLinear(Fog[a].hex), BrColor.HexLinear(Fog[b].hex), f);
        }
    }
}
