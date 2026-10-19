using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Backroads.EngineSim
{
    // C# mirror of native/EngineSimPlugin/src/EngineSimPlugin.h, ABI v1 (PLAN 3.3, research 01 7.4, ADR-002).
    // Every struct is [StructLayout(Sequential, Pack = 1)] like the packed C structs; EngineSim.StructSizes asserts each
    // size against the ESP_SIZEOF_* constants (PLAN 1.10 [T13]).

    public enum EspMode
    {
        Free = 0,
        Proxy = 1,
        Kinematic = 2,
        SlavedCrank = 3,
        FollowRpm = 4,
    }

    public static class EspStatus
    {
        public const int Ok = 0, ErrArg = -1, ErrCompile = -2, ErrState = -3, ErrTimeout = -4, ErrInternal = -5, Pending = 1;

        public static string Name(int s)
        {
            switch (s)
            {
                case Ok: return "ESP_OK";
                case ErrArg: return "ESP_ERR_ARG";
                case ErrCompile: return "ESP_ERR_COMPILE";
                case ErrState: return "ESP_ERR_STATE";
                case ErrTimeout: return "ESP_ERR_TIMEOUT";
                case ErrInternal: return "ESP_ERR_INTERNAL";
                case Pending: return "ESP_PENDING";
                default: return "status " + s;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EspCreateDesc
    {
        public const int Size = 40;
        public const int NoDriftTrim = 0x1, NoCoast = 0x2;
        public uint structSize;
        public int audioSampleRate, audioRingFrames, audioTargetFrames, audioDspBufferFrames;
        public int simFrequency, fluidSteps, workerThread, firMaxTaps, flags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct EspEngineInfo
    {
        public const int Size = 272;
        public uint structSize;
        public fixed byte name[64];
        public int cylinders, banks, exhaustSystems, intakes, simFrequency;
        public double displacementM3, redlineRadS, starterTorqueNm, starterSpeedRadS, crankInertiaKgm2;
        public int scriptGearCount, fluidSteps;
        public fixed double scriptGearRatios[12];
        public double scriptFinalDrive, scriptTireRadiusM, scriptMaxClutchNm, revLimitRadS;
        public int irTaps, sourceKind;

        public string Name
        {
            get
            {
                fixed (byte* p = name)
                {
                    int n = 0;
                    while (n < 64 && p[n] != 0) n++;
                    return Encoding.UTF8.GetString(p, n);
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EspControls
    {
        public const int Size = 32;
        public uint structSize;
        public float throttle, clutch;
        public int ignition, starter;
        public float simSpeed, frameDtS;
        public int frameIndex;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EspDrivelineIn
    {
        public const int Size = 160;
        public uint structSize;
        public int mode;
        public double ratio, maxClutchTorqueNm, inputInertia, wheelInertia, wheelSpeed;
        public int resyncWheelSpeed, tyreModel;
        public double loadTorqueNm, loadSlope, mfB, mfC, mfDn, mfE, contactSpeedMs, tyreRadiusM, brakeTorqueNm, followRpm;
        public double slaveFcHz, slaveInertiaKgm2, slaveTorqueLimitNm;

        public static EspDrivelineIn Of(EspMode m) => new EspDrivelineIn { structSize = Size, mode = (int)m };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EspStepOut
    {
        public const int Size = 152;
        public const int AfterCoast = 0x1, Repaid = 0x2, Capped = 0x4;
        public uint structSize;
        public int steps;
        public long stepIndex;
        public double simTimeS, rpm, crankSpeed, indicatedTorqueNm, dTdw, clutchTorqueNm, dynoTorqueNm;
        public double wheelSpeed, wheelForceMeanN, manifoldPressurePa, intakeFlowMolS, throttleOpen, afr, fuelUsedL, cpuMs;
        public int audioFramesBuffered, audioUnderruns, revLimitedHint, flags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EspAudioParams
    {
        public const int Size = 28;
        public uint structSize;
        public float volume, convolution, hfGain, lowFreqNoise, highFreqNoise, levelerTarget;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EspStats
    {
        public const int Size = 208;
        public uint structSize;
        public int simFrequency;
        public long commands, stepsRun, stepsTarget, stepsBorrowed, coastSteps;
        public int coastEvents, commandsPending;
        public long lastStepIndex;
        public int staleSteps, ringCapacityFrames;
        public double collectWaitMsTotal, collectWaitMsMax, cpuMsEma, workerMsEma, rtf, rtfCpu;
        public int audioUnderruns, audioUnderrunFrames, ringFillFrames, ringTargetFrames;
        public long audioFramesProduced, audioFramesConsumed, audioFramesDropped, clippedSamples;
        public double driftTrim, pCoreShare, frameP95Ms, workerFrameMs;
        public long offlineSteps;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EspWorkerOptions
    {
        public const int Size = 24;
        public uint structSize;
        public int priority, powerThrottlingOff, coastMaxMs;
        public ulong affinityMask;
    }

    /// <summary>P/Invoke into EngineSimPlugin.dll (Assets/Plugins/x86_64). Calls follow the thread contract of the header:
    /// one control thread per handle; <see cref="esp_read_audio"/> only from the audio thread.</summary>
    public static unsafe class Native
    {
        public const string Dll = "EngineSimPlugin";
        public const int AbiVersion = 1;
        public const int CrashTestMagic = 0x43524153;
        public const int ResultSlots = 64;
        public const int CommandSlots = 32;
        public const int MaxCrankSamples = 17;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int esp_version();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int esp_build_info(byte* dst, int capacity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int esp_set_crash_dir(byte* dirUtf8);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int esp_poll_log(byte* dst, int capacity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int esp_debug_crash_test(int magic);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr esp_create(in EspCreateDesc desc, byte* esLibraryDirUtf8, byte* tempDirUtf8);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void esp_destroy(IntPtr e);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int esp_load_builtin(IntPtr e, byte* nameUtf8, byte* err, int errCapacity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int esp_load_script(IntPtr e, byte* mrPathUtf8, byte* topNodeUtf8, byte* err, int errCapacity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int esp_get_info(IntPtr e, ref EspEngineInfo info);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int esp_set_controls(IntPtr e, in EspControls c);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int esp_set_audio_params(IntPtr e, in EspAudioParams p);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int esp_set_quality(IntPtr e, int fluidSteps, int firMaxTaps);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int esp_set_worker_options(IntPtr e, in EspWorkerOptions o);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int esp_step(IntPtr e, long stepIndex, double dt, in EspDrivelineIn d, double* crank, int n, ref EspStepOut o);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int esp_step_begin(IntPtr e, long stepIndex, double dt, in EspDrivelineIn d, double* crank, int n);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int esp_step_end(IntPtr e, long stepIndex, int timeoutUs, ref EspStepOut o);

        /// <summary>Audio thread only: lock-free, allocation-free.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int esp_read_audio(IntPtr e, float* dst, int frames, int channels);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int esp_get_stats(IntPtr e, ref EspStats s);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int esp_debug_tap(IntPtr e, byte* wavPathUtf8, double seconds);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int esp_dyno_sweep(IntPtr e, double rpmFrom, double rpmTo, int points, float throttle, double settleS, double* torqueNm, double* powerW);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int esp_calibrate_inertia(IntPtr e, out double jEff);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int esp_render_offline(IntPtr e, double rpm, float throttle, double seconds, float* dst, int frames);

        /// <summary>NUL-terminated UTF-8 (null stays null).</summary>
        public static byte[] Utf8(string s) => s == null ? null : Encoding.UTF8.GetBytes(s + "\0");

        public static string FromUtf8(byte[] b)
        {
            if (b == null) return "";
            int n = Array.IndexOf(b, (byte)0);
            return Encoding.UTF8.GetString(b, 0, n < 0 ? b.Length : n);
        }

        /// <summary>esp_build_info() as a JSON string, or null when the DLL is missing.</summary>
        public static string BuildInfo()
        {
            try
            {
                int need = esp_build_info(null, 0);
                if (need <= 0) return null;
                var buf = new byte[need + 1];
                fixed (byte* p = buf)
                    if (esp_build_info(p, buf.Length) != need) return null;
                return FromUtf8(buf);
            }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
        }

        /// <summary>True when EngineSimPlugin.dll loads and speaks ABI v1 (PLAN 3.3: otherwise torque map + loop bank).</summary>
        public static bool Available(out string why)
        {
            try
            {
                int v = esp_version();
                why = v == AbiVersion ? null : $"EngineSimPlugin ABI {v}, this build expects {AbiVersion}";
                return why == null;
            }
            catch (DllNotFoundException) { why = "EngineSimPlugin.dll not found"; return false; }
            catch (EntryPointNotFoundException) { why = "EngineSimPlugin.dll has no esp_version"; return false; }
            catch (BadImageFormatException) { why = "EngineSimPlugin.dll is not a 64-bit Windows DLL"; return false; }
        }
    }
}
