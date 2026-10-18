namespace Backroads.Tests.PlayMode
{
    /// <summary>
    /// Outcome of Audio.BatchmodeDspRuns (PLAN 1.9 [T20]), measured on this laptop with 6000.3.20f1 on 2026-10-18:
    /// OnAudioFilterRead ran 187.5 times per second (48 kHz / 256-sample "Best latency" buffer) on both an AudioSource
    /// and the AudioListener, in batchmode PlayMode with and without -nographics (artifacts\audio-batchmode.json).
    /// So audio assertions (underruns, pitch, attenuation) may run in batchmode PlayMode and are not tagged PlayerOnly
    /// by default. Tag a test [Category(PlayerOnly)] only when it needs the shipped player's audio path; re-run
    /// Audio.BatchmodeDspRuns after every editor update, and flip this constant if it becomes Inconclusive.
    /// </summary>
    public static class AudioPolicy
    {
        public const bool BatchmodeDspRuns = true;
        public const string PlayerOnly = "PlayerOnly";
    }
}
