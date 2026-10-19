using UnityEngine;

namespace Backroads.EngineSim
{
    /// <summary>
    /// Moves the plugin's log lines (esp_poll_log) into Unity's log on the main thread; the plugin never calls back from
    /// its own threads (PLAN 3.3). Warnings and errors keep their level.
    /// </summary>
    public static unsafe class EngineSimLog
    {
        static readonly byte[] s_Buf = new byte[16384];

        public static string Drain()
        {
            if (!Native.Available(out _)) return "";
            var sb = new System.Text.StringBuilder();
            for (int guard = 0; guard < 16; guard++)
            {
                int n;
                fixed (byte* p = s_Buf) n = Native.esp_poll_log(p, s_Buf.Length);
                if (n <= 0) break;
                sb.Append(System.Text.Encoding.UTF8.GetString(s_Buf, 0, n));
            }
            return sb.ToString();
        }

        /// <summary>Drains into Debug.Log (call once per frame from whoever owns engines).</summary>
        public static void Flush()
        {
            var text = Drain();
            if (string.IsNullOrEmpty(text)) return;
            foreach (var line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                if (line.Contains("] error:")) Debug.LogError(line);
                else if (line.Contains("] warn:")) Debug.LogWarning(line);
                else Debug.Log(line);
            }
        }
    }
}
