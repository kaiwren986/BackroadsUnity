using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Backroads.Tests.Perf
{
    /// <summary>
    /// CPU cores for perf tests (Windows): one core of the fastest and one of the slowest efficiency class (P-cores and
    /// E-cores on a hybrid CPU), and pinning the calling thread to a core. Lives with the engine perf tests so that no
    /// vehicle code ever declares a native call (Architecture rule 3).
    /// </summary>
    public static class CpuCores
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);

        /// <summary>
        /// Affinity masks (processor group 0) of the first logical processor of one fast core (p) and one slow core (e);
        /// true when the CPU is hybrid (two classes). On a non-hybrid CPU p is a valid core and the result is false.
        /// </summary>
        public static bool Masks(out ulong p, out ulong e, out string what)
        {
            p = e = 0; what = "unknown";
            if (Application.platform != RuntimePlatform.WindowsEditor && Application.platform != RuntimePlatform.WindowsPlayer) return false;
            uint len = 0;
            GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref len);
            if (len == 0) return false;
            var buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformationEx(0, buf, ref len)) return false;
                // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship, Size, PROCESSOR_RELATIONSHIP { Flags,
                // EfficiencyClass, Reserved[20], GroupCount, GROUP_AFFINITY { Mask, Group, ... } }
                var cores = new List<(byte cls, ulong mask)>();
                for (int off = 0; off < len;)
                {
                    var q = IntPtr.Add(buf, off);
                    int rel = Marshal.ReadInt32(q), size = Marshal.ReadInt32(q, 4);
                    if (rel == 0 && (ushort)Marshal.ReadInt16(q, 40) == 0) cores.Add((Marshal.ReadByte(q, 9), (ulong)Marshal.ReadInt64(q, 32)));
                    if (size <= 0) break;
                    off += size;
                }
                if (cores.Count == 0) return false;
                byte hi = cores.Max(c => c.cls), lo = cores.Min(c => c.cls);
                ulong First(ulong m) => m & (~m + 1);
                p = First(cores.First(c => c.cls == hi).mask);
                e = First(cores.Last(c => c.cls == lo).mask);
                what = hi == lo
                    ? $"{cores.Count} cores, one class (not hybrid: no E-cores, every core counts as a P-core)"
                    : $"{cores.Count(c => c.cls == hi)} P-cores (class {hi}), {cores.Count(c => c.cls == lo)} E-cores (class {lo})";
                return hi != lo;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>Pins the calling thread to <paramref name="mask"/>; returns the previous mask (0 = not pinned).</summary>
        public static ulong Pin(ulong mask) => mask == 0 ? 0 : (ulong)SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)mask);

        /// <summary>Restores a mask returned by <see cref="Pin"/>.</summary>
        public static void Restore(ulong old)
        {
            if (old != 0) SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)old);
        }
    }
}
