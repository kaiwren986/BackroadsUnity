using System;
using System.Globalization;
using UnityEngine;

namespace Backroads.Core
{
    /// <summary>Reads -br-* switches from the process command line. Tests can override the argument list.</summary>
    public static class CommandLine
    {
        static string[] s_Args;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_Args = null;

        public static string[] Args => s_Args ??= Environment.GetCommandLineArgs();

        /// <summary>Replace the argument list (tests). Pass null to go back to the real command line.</summary>
        public static void Override(string[] args) => s_Args = args;

        public static bool Has(string flag)
        {
            foreach (var a in Args)
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>The value after <paramref name="flag"/>, or <paramref name="fallback"/> when absent.</summary>
        public static string Get(string flag, string fallback = null)
        {
            var a = Args;
            for (int i = 0; i < a.Length - 1; i++)
            {
                if (!string.Equals(a[i], flag, StringComparison.OrdinalIgnoreCase)) continue;
                var v = a[i + 1];
                bool isSwitch = v.StartsWith("-", StringComparison.Ordinal) && !(v.Length > 1 && char.IsDigit(v[1]));
                return isSwitch ? fallback : v;
            }
            return fallback;
        }

        public static int GetInt(string flag, int fallback)
        {
            var v = Get(flag);
            return v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : fallback;
        }

        public static float GetFloat(string flag, float fallback)
        {
            var v = Get(flag);
            return v != null && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : fallback;
        }
    }
}
