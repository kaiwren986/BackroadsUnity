using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Backroads.Art.Editor
{
    /// <summary>
    /// The project's vehicle material slots (07 section 4.3) and the name map the import remaps by. Phase B FBX files
    /// name their materials after the slots (identity remap); today's GLB vehicles keep their legacy names, which fold
    /// into the slots as 07 section 4.3 lists (SLOT_MAP), plus the names 07 does not list (BoxPanel, Rib, Brake,
    /// DarkAlloy, Caliper, Yellow), taken from art/legacy/vehicles.py MATDEF.
    /// </summary>
    public static class VehicleMaterialSlots
    {
        public const string MaterialRoot = "Assets/Art/Materials/Vehicle/";

        public static readonly string[] Slots =
        {
            "M_Paint", "M_Trim", "M_Glass", "M_Lights", "M_Tyre", "M_Rim", "M_Interior", "M_Chassis", "M_Decal", "M_VehicleFar",
        };

        /// <summary>Legacy material name (vehicles.py) to slot.</summary>
        public static readonly IReadOnlyDictionary<string, string> Legacy = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Paint"] = "M_Paint", ["Stripe"] = "M_Paint", ["Livery"] = "M_Paint", ["Livery2"] = "M_Paint",
            ["BoxPanel"] = "M_Paint", ["Yellow"] = "M_Paint",
            ["Trim"] = "M_Trim", ["Grille"] = "M_Trim", ["Chrome"] = "M_Trim", ["Steel"] = "M_Trim", ["Alloy"] = "M_Trim",
            ["DarkAlloy"] = "M_Trim", ["Plate"] = "M_Trim", ["Under"] = "M_Trim", ["Bedliner"] = "M_Trim",
            ["Mirror"] = "M_Trim", ["Rib"] = "M_Trim", ["Brake"] = "M_Trim", ["Caliper"] = "M_Trim",
            ["Headlight"] = "M_Lights", ["Taillight"] = "M_Lights", ["Reverse"] = "M_Lights", ["Indicator"] = "M_Lights",
            ["Glass"] = "M_Glass",
            ["Rubber"] = "M_Tyre", ["Tread"] = "M_Tyre",
            ["Seat"] = "M_Interior", ["Interior"] = "M_Interior",
        };

        static readonly Regex DupSuffix = new Regex(@"\.\d{3}$", RegexOptions.CultureInvariant);

        public static string PathOf(string slot) => MaterialRoot + slot + ".mat";

        /// <summary>The slot a source material name maps to, or null. Blender's ".001" duplicate suffix is ignored.</summary>
        public static string SlotFor(string sourceName)
        {
            if (string.IsNullOrEmpty(sourceName)) return null;
            var n = DupSuffix.Replace(sourceName, "");
            if (Array.IndexOf(Slots, n) >= 0) return n;
            return Legacy.TryGetValue(n, out var s) ? s : null;
        }

        /// <summary>The project material for a source material name, or null when unmapped or not created yet.</summary>
        public static Material Target(string sourceName)
        {
            var slot = SlotFor(sourceName);
            return slot == null ? null : AssetDatabase.LoadAssetAtPath<Material>(PathOf(slot));
        }

        /// <summary>Every source name the remap knows: the slots themselves, then the legacy names.</summary>
        public static IEnumerable<string> SourceNames()
        {
            foreach (var s in Slots) yield return s;
            foreach (var k in Legacy.Keys) yield return k;
        }
    }
}
