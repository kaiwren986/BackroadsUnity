using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Backroads.EngineSim;
using UnityEngine;

namespace Backroads.Testing
{
    /// <summary>
    /// m1.bench.audiospike, spike S-AUD-1 (PLAN 3.6 item 4, T1.8): which way of playing generated engine audio does Unity
    /// 6000.3 pan and attenuate, for remote and AI cars? Paths: (a) OnAudioFilterRead on a clip-less AudioSource with
    /// spatialBlend 0 and 1, (b) a 6.3 IAudioGenerator whose Burst realtime part calls esp_read_audio through
    /// [DllImport], (c) a looping DC clip multiplied by the filter, (d) a streamed AudioClip with a PCMReaderCallback.
    /// Each path plays a 440 Hz tone at 1 m and 20 m in front of the listener and 5 m to its left and right; an AudioTap
    /// on the listener's GameObject (the final mix) measures RMS per channel. Then each path plays a running engine for a
    /// few seconds at Best latency and the plugin counts underruns. Writes spike_report.json and listener WAVs.
    /// The listener is set to a low volume (-40 dB) when -br-silent has muted it, so the run stays nearly inaudible;
    /// every result is a ratio, so the level does not matter.
    /// </summary>
    public sealed class AudioSpikeScenario : IAutotestScenario
    {
        public string Kind => "audio-spike";

        [Serializable]
        sealed class Params
        {
            public float settleSeconds = 0.4f;
            public float measureSeconds = 0.6f;
            public float toneHz = 440f;
            public float toneAmplitude = 0.1f;
            public string engine = "f4_boxer_a";
            public float engineSeconds = 5f;
            public float minAttenuationDb = 12f;
            public float minPanDb = 6f;
            public float listenerVolume = 0.01f;
        }

        [Serializable]
        sealed class Measure
        {
            public string path, position;
            public float x, z;
            public double rmsL, rmsR, dbL, dbR, db;
            public long callbacks;
            public int blockFrames;
        }

        [Serializable]
        sealed class PathResult
        {
            public string path;
            public double db1m, db20m, attenuation20mDb, panLeftDb, panRightDb;
            public bool attenuates, pans, sounds;
        }

        [Serializable]
        sealed class EngineRun
        {
            public string path;
            public bool started;
            public double rpm, listenerDb;
            public int underruns, underrunFrames;
            public long framesConsumed, framesDropped, callbacks;
            public int blockFrames, ringTargetFrames;
            public string burst;
            public string notes;
        }

        [Serializable]
        sealed class Report
        {
            public string unity;
            public int sampleRate, dspBufferFrames, dspBuffers;
            public string speakerMode;
            public float listenerVolume;
            public List<Measure> measures = new List<Measure>();
            public List<PathResult> paths = new List<PathResult>();
            public List<EngineRun> engineRuns = new List<EngineRun>();
            public string chosen;
            public string channelBufferPointer = "no: ChannelBuffer has an indexer [channel, frame] and a Span constructor only";
            public List<string> failures = new List<string>();
        }

        enum PathKind { Filter2D, Filter3D, DcClip, Generator, PcmReader }

        static string Name(PathKind k)
        {
            switch (k)
            {
                case PathKind.Filter2D: return "filter-2d";
                case PathKind.Filter3D: return "filter-3d";
                case PathKind.DcClip: return "dcclip-3d";
                case PathKind.Generator: return "generator-3d";
                default: return "pcmreader-3d";
            }
        }

        static readonly (string name, Vector3 offset)[] s_Positions =
        {
            ("front-1m", new Vector3(0, 0, 1)), ("front-20m", new Vector3(0, 0, 20)),
            ("left-5m", new Vector3(-5, 0, 0)), ("right-5m", new Vector3(5, 0, 0)),
        };

        /// <summary>Steps an engine in FREE mode once per FixedUpdate (the Engine Bench without the panel).</summary>
        sealed class Driver : MonoBehaviour
        {
            public EngineSimEngine Engine;
            public EspStepOut Last;

            void FixedUpdate()
            {
                if (Engine != null) Last = Engine.FixedStep(Time.fixedDeltaTime, EspDrivelineIn.Of(EspMode.Free));
            }
        }

        /// <summary>One playing source of a path, and how to stop it safely.</summary>
        sealed class Playing
        {
            public GameObject go;
            public AudioSource src;
            public SpikeFilterSource filter;
            public EspAudioGenerator gen;
            public SpikePcmStream pcm;

            public long Callbacks => filter != null ? filter.Callbacks : gen != null ? gen.Read.calls : pcm != null ? pcm.Callbacks : 0;
            public int BlockFrames => filter != null ? filter.LastFrames : gen != null ? gen.Read.lastFrames : pcm != null ? pcm.MaxFrames : 0;

            public void Stop()
            {
                if (src != null) src.Stop();
                filter?.SetEngine(IntPtr.Zero);
                pcm?.Release();
                if (src != null && gen != null) src.generator = null;
            }
        }

        static Playing Start(PathKind k, Vector3 at, IntPtr engine, Params p)
        {
            var go = new GameObject("spike " + Name(k));
            go.transform.position = at;
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = true;
            src.spatialBlend = k == PathKind.Filter2D ? 0f : 1f;
            src.rolloffMode = AudioRolloffMode.Logarithmic;
            src.minDistance = 1f;
            src.maxDistance = 500f;
            src.dopplerLevel = 0f;
            src.spread = 0f;
            src.volume = 1f;
            src.priority = 0;
            var pl = new Playing { go = go, src = src };
            switch (k)
            {
                case PathKind.Filter2D:
                case PathKind.Filter3D:
                    pl.filter = go.AddComponent<SpikeFilterSource>();
                    pl.filter.How = SpikeFilterSource.Mode.Overwrite;
                    break;
                case PathKind.DcClip:
                    src.clip = SpikeFilterSource.DcClip();
                    pl.filter = go.AddComponent<SpikeFilterSource>();
                    pl.filter.How = SpikeFilterSource.Mode.MultiplyInput;
                    break;
                case PathKind.Generator:
                    pl.gen = go.AddComponent<EspAudioGenerator>();
                    pl.gen.EngineHandle = engine;
                    pl.gen.ToneHz = p.toneHz;
                    pl.gen.ToneAmplitude = p.toneAmplitude;
                    src.generator = pl.gen;
                    break;
                case PathKind.PcmReader:
                    pl.pcm = new SpikePcmStream(engine) { ToneHz = p.toneHz, ToneAmplitude = p.toneAmplitude };
                    src.clip = pl.pcm.Clip;
                    break;
            }
            if (pl.filter != null)
            {
                pl.filter.ToneHz = p.toneHz;
                pl.filter.ToneAmplitude = p.toneAmplitude;
                pl.filter.SetEngine(engine);
            }
            src.Play();
            return pl;
        }

        static IEnumerator Wait(float seconds)
        {
            float t0 = Time.unscaledTime;
            while (Time.unscaledTime - t0 < seconds) yield return null;
        }

        static void Rms(List<float> x, int ch, out double l, out double r)
        {
            double sl = 0, sr = 0;
            int n = ch > 0 ? x.Count / ch : 0;
            for (int i = 0; i < n; i++)
            {
                double a = x[i * ch], b = ch > 1 ? x[i * ch + 1] : a;
                sl += a * a;
                sr += b * b;
            }
            l = n > 0 ? Math.Sqrt(sl / n) : 0;
            r = n > 0 ? Math.Sqrt(sr / n) : 0;
        }

        static double Db(double v) => v > 1e-12 ? 20 * Math.Log10(v) : -240;

        public IEnumerator Run(AutotestContext ctx)
        {
            var p = ctx.Params<Params>();
            var report = new Report { unity = Application.unityVersion, sampleRate = AudioSettings.outputSampleRate, speakerMode = AudioSettings.speakerMode.ToString() };
            AudioSettings.GetDSPBufferSize(out report.dspBufferFrames, out report.dspBuffers);

            // our own listener, far from anything in the scene; the scene's listeners are switched off for the run
            var others = UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var l in others) l.enabled = false;
            var listenerGo = new GameObject("spike listener");
            listenerGo.transform.SetPositionAndRotation(new Vector3(0, 500, 0), Quaternion.identity);
            listenerGo.AddComponent<AudioListener>();
            var tap = listenerGo.AddComponent<AudioTap>();
            float volumeBefore = AudioListener.volume;
            if (AudioListener.volume <= 0f) AudioListener.volume = p.listenerVolume;
            report.listenerVolume = AudioListener.volume;
            var origin = listenerGo.transform.position;
            yield return Wait(0.3f);

            var buf = new List<float>(96000);
            var kinds = new[] { PathKind.Filter2D, PathKind.Filter3D, PathKind.DcClip, PathKind.Generator, PathKind.PcmReader };

            // ---- tone: attenuation and pan ----
            foreach (var k in kinds)
            {
                var pr = new PathResult { path = Name(k) };
                var byPos = new Dictionary<string, Measure>();
                foreach (var (pos, off) in s_Positions)
                {
                    var pl = Start(k, origin + off, IntPtr.Zero, p);
                    yield return Wait(p.settleSeconds);
                    tap.Clear();
                    buf.Clear();
                    yield return Wait(p.measureSeconds);
                    tap.Drain(buf);
                    var m = new Measure { path = pr.path, position = pos, x = off.x, z = off.z, callbacks = pl.Callbacks, blockFrames = pl.BlockFrames };
                    Rms(buf, tap.Channels, out m.rmsL, out m.rmsR);
                    m.dbL = Db(m.rmsL);
                    m.dbR = Db(m.rmsR);
                    m.db = Db(Math.Sqrt(0.5 * (m.rmsL * m.rmsL + m.rmsR * m.rmsR)));
                    if (pos == "front-1m")
                        AudioTap.SaveWav(Path.Combine(ctx.OutDir, $"listener_{pr.path}_1m.wav"), buf.ToArray(), buf.Count, tap.Channels, report.sampleRate);
                    pl.Stop();
                    yield return Wait(0.1f);
                    UnityEngine.Object.Destroy(pl.go);
                    yield return null;
                    report.measures.Add(m);
                    byPos[pos] = m;
                    ctx.Note($"{pr.path} {pos}: L {m.dbL:F1} dB, R {m.dbR:F1} dB ({m.callbacks} callbacks, {m.blockFrames} frames)");
                }
                pr.db1m = byPos["front-1m"].db;
                pr.db20m = byPos["front-20m"].db;
                pr.attenuation20mDb = pr.db1m - pr.db20m;
                pr.panLeftDb = byPos["left-5m"].dbL - byPos["left-5m"].dbR;
                pr.panRightDb = byPos["right-5m"].dbR - byPos["right-5m"].dbL;
                pr.sounds = pr.db1m > Db(p.toneAmplitude * report.listenerVolume) - 30;
                pr.attenuates = pr.sounds && pr.attenuation20mDb >= p.minAttenuationDb;
                pr.pans = pr.sounds && pr.panLeftDb >= p.minPanDb && pr.panRightDb >= p.minPanDb;
                report.paths.Add(pr);
                string key = pr.path.Replace("-", "_");
                ctx.Metric(key + "_atten20m_db", pr.attenuation20mDb);
                ctx.Metric(key + "_pan_left_db", pr.panLeftDb);
                ctx.Metric(key + "_pan_right_db", pr.panRightDb);
                ctx.Note($"{pr.path}: 1 m {pr.db1m:F1} dB, 20 m {pr.db20m:F1} dB, attenuation {pr.attenuation20mDb:F1} dB, pan L {pr.panLeftDb:F1} dB / R {pr.panRightDb:F1} dB");
            }

            // ---- a running engine through each consumer: underruns at the project's DSP buffer ----
            var def = EngineCatalog.Find(p.engine);
            var driverGo = new GameObject("spike engine driver");
            var driver = driverGo.AddComponent<Driver>();
            foreach (var k in new[] { PathKind.Filter2D, PathKind.DcClip, PathKind.Generator, PathKind.PcmReader })
            {
                var run = new EngineRun { path = Name(k) };
                var engine = def == null ? null : EngineSimEngine.Create(def, EngineSimEngine.Options.ForPlayback(), out var err);
                if (engine == null) { run.notes = "engine did not load"; report.engineRuns.Add(run); continue; }
                engine.SetWorker(1, true);
                driver.Engine = engine;
                engine.Ignition = true;
                engine.Starter = true;
                yield return Wait(1.5f);
                engine.Starter = false;
                engine.Throttle = 0.12f;
                var pl = Start(k, origin + new Vector3(0, 0, 2), engine.Handle, p);
                yield return Wait(0.6f);
                var s0 = engine.Stats();
                tap.Clear();
                buf.Clear();
                yield return Wait(p.engineSeconds);
                tap.Drain(buf);
                var s1 = engine.Stats();
                run.rpm = driver.Last.rpm;
                run.started = run.rpm > 400;
                run.underruns = s1.audioUnderruns - s0.audioUnderruns;
                run.underrunFrames = s1.audioUnderrunFrames - s0.audioUnderrunFrames;
                run.framesConsumed = s1.audioFramesConsumed - s0.audioFramesConsumed;
                run.framesDropped = s1.audioFramesDropped - s0.audioFramesDropped;
                run.ringTargetFrames = s1.ringTargetFrames;
                run.callbacks = pl.Callbacks;
                run.blockFrames = pl.BlockFrames;
                Rms(buf, tap.Channels, out var l, out var r);
                run.listenerDb = Db(Math.Sqrt(0.5 * (l * l + r * r)));
                if (pl.gen != null)
                {
                    var c = pl.gen.Read;
                    run.burst = c.calls == 0 ? "never called" : c.managedCalls == 0 ? "Burst" : $"managed ({c.managedCalls} of {c.calls} calls)";
                }
                AudioTap.SaveWav(Path.Combine(ctx.OutDir, $"listener_engine_{run.path}.wav"), buf.ToArray(), buf.Count, tap.Channels, report.sampleRate);
                pl.Stop();
                yield return Wait(0.25f);
                UnityEngine.Object.Destroy(pl.go);
                yield return null;
                driver.Engine = null;
                engine.Dispose();
                report.engineRuns.Add(run);
                string key = run.path.Replace("-", "_");
                ctx.Metric(key + "_engine_underruns", run.underruns);
                ctx.Metric(key + "_engine_frames_consumed", run.framesConsumed);
                ctx.Note($"engine via {run.path}: {run.rpm:F0} rpm, {run.underruns} underruns ({run.underrunFrames} frames), {run.framesConsumed} frames consumed, " +
                         $"block {run.blockFrames}, listener {run.listenerDb:F1} dB{(run.burst != null ? ", realtime part " + run.burst : "")}");
                EngineSimLog.Flush();
            }
            UnityEngine.Object.Destroy(driverGo);

            // ---- the choice: the first path (in order of preference) that attenuates, pans and plays the engine cleanly ----
            foreach (var want in new[] { "generator-3d", "dcclip-3d", "pcmreader-3d", "filter-3d" })
            {
                var pr = report.paths.Find(x => x.path == want);
                var er = report.engineRuns.Find(x => x.path == want) ?? (want == "filter-3d" ? report.engineRuns.Find(x => x.path == "filter-2d") : null);
                if (pr != null && pr.attenuates && pr.pans && er != null && er.started && er.underruns == 0 && er.framesConsumed > 0) { report.chosen = want; break; }
            }
            if (report.chosen == null) report.failures.Add("no 3D path attenuates >= 12 dB at 20 m, pans and plays the engine without underruns");
            ctx.Note("chosen remote path: " + (report.chosen ?? "none"));
            ctx.Metric("dsp_buffer_frames", report.dspBufferFrames);

            File.WriteAllText(Path.Combine(ctx.OutDir, "spike_report.json"), JsonUtility.ToJson(report, true));
            ctx.Evidence(Path.Combine(ctx.OutDir, "spike_report.json"));
            AudioListener.volume = volumeBefore;
            UnityEngine.Object.Destroy(listenerGo);
            foreach (var l in others) if (l != null) l.enabled = true;
            foreach (var f in report.failures) ctx.Fail(f);
        }
    }
}
