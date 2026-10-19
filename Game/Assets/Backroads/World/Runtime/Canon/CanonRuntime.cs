using System;
using System.Security.Cryptography;
using System.Text;
using Backroads.Core;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Backroads.World
{
    /// <summary>
    /// Loads the imported canon at run time (PLAN 2.2 "Data import"): the CanonAsset from Resources, then the WorldGrid
    /// (TextAsset.GetData views copied into persistent NativeArrays) and the ColliderGrid. Shared code: the client, the
    /// server and the tests use the same path. Statics are reset on SubsystemRegistration (enter-play without domain reload).
    /// </summary>
    public static class CanonRuntime
    {
        public const string CanaryPrefix = "CANON v1 OK H=";

        static CanonAsset s_Asset;
        static WorldGrid s_Grid;
        static ColliderGrid s_Colliders;
        static bool s_QuitHooked;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Unload();

        /// <summary>The imported canon, or null when the importer has not run (Backroads > Import Canon v1).</summary>
        public static CanonAsset Asset
        {
            get
            {
                if (s_Asset == null) s_Asset = Resources.Load<CanonAsset>(CanonAsset.ResourceName);
                return s_Asset;
            }
        }

        public static bool IsAvailable => Asset != null;

        public static bool IsLoaded => s_Grid != null && s_Grid.IsCreated;

        /// <summary>The world grid, loaded on first use. Throws when the canon was never imported.</summary>
        public static WorldGrid Grid
        {
            get
            {
                if (!IsLoaded) Load();
                return s_Grid;
            }
        }

        public static ColliderGrid Colliders
        {
            get
            {
                if (s_Colliders == null)
                {
                    var a = Asset ?? throw new InvalidOperationException(NotImportedMessage);
                    s_Colliders = ColliderGrid.Build(a.placements.colliders);
                }
                return s_Colliders;
            }
        }

        public const string NotImportedMessage =
            "canon v1 is not imported: run Backroads > Import Canon v1 (or -executeMethod Backroads.Canon.Editor.ImportCli.Run)";

        /// <summary>Loads the WorldGrid from the imported canon (idempotent). Returns the load time in milliseconds.</summary>
        public static double Load()
        {
            if (IsLoaded) return 0;
            var a = Asset ?? throw new InvalidOperationException(NotImportedMessage);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            s_Grid = WorldGrid.FromCanon(a);
            if (!s_QuitHooked)
            {
                Application.quitting += Unload;
#if UNITY_EDITOR
                UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Unload;
                UnityEditor.EditorApplication.quitting += Unload;
#endif
                s_QuitHooked = true;
            }
            double ms = sw.Elapsed.TotalMilliseconds;
            Log.Info($"canon v1 loaded: WorldGrid {WorldGrid.VN}x{WorldGrid.VN} in {ms:F0} ms (manifest {Short(a.manifestSha256)})");
            return ms;
        }

        /// <summary>
        /// Server canary (T1.2): loads the canon, hashes the loaded H layer and returns
        /// "CANON v1 OK H=(first 16 hex digits)..." when it equals the manifest's heights hash, else "CANON v1 FAILED: ...".
        /// </summary>
        public static bool LoadAndVerify(out string line)
        {
            try
            {
                var a = Asset;
                if (a == null) { line = "CANON v1 FAILED: " + NotImportedMessage; return false; }
                Load();
                var h = Sha256Hex(s_Grid.H.Reinterpret<byte>(sizeof(float)));
                if (!string.Equals(h, a.heights.sha256, StringComparison.OrdinalIgnoreCase))
                {
                    line = $"CANON v1 FAILED: H={h} expected {a.heights.sha256}";
                    return false;
                }
                line = $"{CanaryPrefix}{h.Substring(0, 16)}... manifest={Short(a.manifestSha256)} trees={a.vegetation.trees.Length} colliders={a.placements.colliders.Length}";
                return true;
            }
            catch (Exception e)
            {
                line = "CANON v1 FAILED: " + e.Message;
                return false;
            }
        }

        public static unsafe string Sha256Hex(NativeArray<byte> bytes)
        {
            using var sha = SHA256.Create();
            var ptr = (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(bytes);
            using var s = new System.IO.UnmanagedMemoryStream(ptr, bytes.Length);
            return Hex(sha.ComputeHash(s));
        }

        public static string Hex(byte[] h)
        {
            var sb = new StringBuilder(h.Length * 2);
            foreach (var b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        static string Short(string s) => string.IsNullOrEmpty(s) ? "?" : s.Substring(0, Math.Min(12, s.Length));

        public static void Unload()
        {
            s_Grid?.Dispose();
            s_Grid = null;
            s_Colliders?.Dispose();
            s_Colliders = null;
            s_Asset = null;
        }
    }
}
