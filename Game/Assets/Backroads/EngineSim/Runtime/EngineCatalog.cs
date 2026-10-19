using System;
using System.Collections.Generic;
using System.IO;
using Backroads.Core;
using UnityEngine;

namespace Backroads.EngineSim
{
    /// <summary>
    /// The engines a build offers (PLAN 3.10, 3.2 [C2]). Client builds list the roster engines only, under fictional ids.
    /// Dev builds (BACKROADS_TESTING) and the editor add the 25 upstream piranha scripts, read straight from the
    /// vendored tree (native/EngineSimPlugin/vendor/engine-sim), which only exists on this PC; nothing with a real name is
    /// ever copied into StreamingAssets.
    /// </summary>
    public static class EngineCatalog
    {
        public const string StreamingFolder = "EngineSim";

        static readonly EngineDef[] s_Roster =
        {
            new EngineDef
            {
                id = "f4_boxer_a", displayName = "Flat-four 2.5", builtinName = "f4_boxer_a", cylinders = 4,
                idleRpmMin = 700, idleRpmMax = 900, redlineRpm = 6500, peakTorqueNm = 229, peakTorqueRpm = 4500,
                pitchOrders = EngineDef.FiringOrders(4), roster = true,
                notes = "Engine Bench and rally coupe; validated: 229 N m at 4,500 rpm, 170 hp at 6,500 rpm (research 02 6.2)",
            },
            new EngineDef
            {
                id = "v8_cross_a", displayName = "Cross-plane V8 5.3", builtinName = "v8_cross_a", cylinders = 8,
                idleRpmMin = 600, idleRpmMax = 900, redlineRpm = 6500, peakTorqueNm = 715, peakTorqueRpm = 4000,
                pitchOrders = EngineDef.CycleHarmonics(16), roster = true,
                notes = "Pickup and trophy truck; the C++ rebuild runs hot (research 01 9.2)",
            },
            new EngineDef
            {
                id = "i4_synth", displayName = "Inline-four 2.7 (synthetic)", builtinName = "i4_synth", cylinders = 4,
                idleRpmMin = 600, idleRpmMax = 900, redlineRpm = 6500, peakTorqueNm = 357, peakTorqueRpm = 4000,
                pitchOrders = EngineDef.FiringOrders(4), roster = true,
                notes = "Hatch placeholder (PLAN 3.10)",
            },
        };

        public static IReadOnlyList<EngineDef> Roster => s_Roster;

        public static EngineDef Find(string id)
        {
            foreach (var d in Available()) if (d.id == id) return d;
            return null;
        }

        /// <summary>Roster engines; in Dev builds and the editor also the upstream scripts when the vendored tree is there.</summary>
        public static IReadOnlyList<EngineDef> Available()
        {
            var list = new List<EngineDef>(s_Roster);
#if UNITY_EDITOR || BACKROADS_TESTING
            list.AddRange(DevScripts());
#endif
            return list;
        }

        /// <summary>StreamingAssets/EngineSim/es: the impulse responses the builtins need (sound-library/...).</summary>
        public static string StreamingEsDir => Path.Combine(Application.streamingAssetsPath, StreamingFolder, "es");

        /// <summary>Scratch folder for script wrappers.</summary>
        public static string TempDir
        {
            get
            {
                var d = Path.Combine(Application.temporaryCachePath, StreamingFolder);
                Directory.CreateDirectory(d);
                return d;
            }
        }

        /// <summary>The es library folder for an engine: the shipped IRs for builtins, the vendored library for scripts.</summary>
        public static string EsDirFor(EngineDef def)
        {
            if (def.source == EngineSource.Builtin && Directory.Exists(Path.Combine(StreamingEsDir, "sound-library")))
                return StreamingEsDir;
#if UNITY_EDITOR || BACKROADS_TESTING
            var v = VendorRoot();
            if (v != null) return Path.Combine(v, "es");
#endif
            return StreamingEsDir;
        }

#if UNITY_EDITOR || BACKROADS_TESTING
        static string s_VendorRoot;
        static bool s_VendorSearched;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_VendorRoot = null;
            s_VendorSearched = false;
            s_DevScripts = null;
        }

        /// <summary>
        /// Dev builds and the editor only: native/EngineSimPlugin/vendor/engine-sim of this checkout. From -br-es-root,
        /// the BR_ES_ROOT environment variable, or the first parent of Application.dataPath that holds it (a Dev build
        /// lives in builds\M(n)-dev inside the repository or a lane worktree).
        /// </summary>
        public static string VendorRoot()
        {
            if (s_VendorSearched) return s_VendorRoot;
            s_VendorSearched = true;
            var explicitRoot = CommandLine.Get("-br-es-root") ?? Environment.GetEnvironmentVariable("BR_ES_ROOT");
            if (!string.IsNullOrEmpty(explicitRoot) && File.Exists(Path.Combine(explicitRoot, "es", "engine_sim.mr")))
                return s_VendorRoot = Path.GetFullPath(explicitRoot);
            var dir = new DirectoryInfo(Application.dataPath);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "native", "EngineSimPlugin", "vendor", "engine-sim");
                if (File.Exists(Path.Combine(candidate, "es", "engine_sim.mr"))) return s_VendorRoot = candidate;
            }
            return null;
        }

        // The 25 upstream engine scripts (PLAN 3.2: 27 .mr files minus the two radial.mr helpers): path, top node for
        // the older scripts without main(), cylinders. Real names, so Dev builds only.
        static readonly (string path, string top, int cyl)[] s_Upstream =
        {
            ("atg-video-1/01_honda_trx520.mr", null, 1), ("atg-video-1/02_kohler_ch750.mr", null, 2),
            ("atg-video-1/03_harley_davidson_shovelhead.mr", null, 2), ("atg-video-1/04_hayabusa.mr", null, 4),
            ("atg-video-1/05_honda_vtec.mr", null, 4), ("atg-video-1/06_subaru_ej25.mr", null, 4),
            ("atg-video-1/07_audi_i5.mr", null, 5), ("atg-video-1/08_radial_5.mr", null, 5),
            ("atg-video-2/01_subaru_ej25_eh.mr", null, 4), ("atg-video-2/02_subaru_ej25_uh.mr", null, 4),
            ("atg-video-2/03_2jz.mr", null, 6), ("atg-video-2/04_60_degree_v6.mr", null, 6),
            ("atg-video-2/05_odd_fire_v6.mr", null, 6), ("atg-video-2/06_even_fire_v6.mr", null, 6),
            ("atg-video-2/07_gm_ls.mr", null, 8), ("atg-video-2/08_ferrari_f136_v8.mr", null, 8),
            ("atg-video-2/09_radial_9.mr", null, 9), ("atg-video-2/10_lfa_v10.mr", null, 10),
            ("atg-video-2/11_merlin_v12.mr", null, 12), ("atg-video-2/12_ferrari_412_t2.mr", null, 12),
            ("audi/i5.mr", "audi_i5_2_2L", 5), ("bmw/M52B28.mr", "M52B28", 6),
            ("chevrolet/chev_truck_454.mr", "chev_truck_454", 8), ("chevrolet/engine_03_for_e1.mr", "engine_03_for_e1", 8),
            ("kohler/kohler_ch750.mr", "kohler_ch750", 2),
        };

        static List<EngineDef> s_DevScripts;

        /// <summary>Dev builds and the editor: the upstream scripts, if the vendored tree is found.</summary>
        public static IReadOnlyList<EngineDef> DevScripts()
        {
            if (s_DevScripts != null) return s_DevScripts;
            s_DevScripts = new List<EngineDef>();
            var root = VendorRoot();
            if (root == null) return s_DevScripts;
            var engines = Path.Combine(root, "assets", "engines");
            foreach (var (path, top, cyl) in s_Upstream)
            {
                var full = Path.Combine(engines, path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) continue;
                s_DevScripts.Add(new EngineDef
                {
                    id = "upstream/" + path.Substring(0, path.Length - 3),
                    displayName = Path.GetFileNameWithoutExtension(path) + " (script)",
                    source = EngineSource.Script,
                    scriptPath = full,
                    topNode = top,
                    cylinders = cyl,
                    pitchOrders = cyl == 8 ? EngineDef.CycleHarmonics(16) : EngineDef.FiringOrders(cyl),
                    devOnly = true,
                    notes = "upstream engine-sim script, Dev builds only (PLAN 3.10)",
                });
            }
            return s_DevScripts;
        }
#endif
    }
}
