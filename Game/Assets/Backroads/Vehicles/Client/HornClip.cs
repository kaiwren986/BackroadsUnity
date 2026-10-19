using UnityEngine;

namespace Backroads.Vehicles.Client
{
    /// <summary>
    /// The browser game's horn (audio.js): two square waves at 392 and 494 Hz through a 1.8 kHz low-pass, made once as
    /// a seamless one-second loop (both frequencies are whole numbers of cycles per second).
    /// </summary>
    public static class HornClip
    {
        static AudioClip s_Clip;

        public static AudioClip Get()
        {
            if (s_Clip != null) return s_Clip;
            int rate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            int n = rate;
            var data = new float[n];
            double a = 1 - System.Math.Exp(-2 * System.Math.PI * 1800.0 / rate), y = 0;
            for (int pass = 0; pass < 2; pass++)          // run the filter twice so the loop starts settled
                for (int i = 0; i < n; i++)
                {
                    double t = (double)i / rate;
                    double x = (System.Math.Sin(2 * System.Math.PI * 392 * t) >= 0 ? 1 : -1) + (System.Math.Sin(2 * System.Math.PI * 494 * t) >= 0 ? 1 : -1);
                    y += a * (x - y);
                    if (pass == 1) data[i] = (float)(y * 0.22);
                }
            s_Clip = AudioClip.Create("BrHorn", n, 1, rate, false);
            s_Clip.SetData(data, 0);
            return s_Clip;
        }
    }
}
