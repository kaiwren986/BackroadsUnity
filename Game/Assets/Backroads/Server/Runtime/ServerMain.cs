using Backroads.Core;
using UnityEngine;

namespace Backroads.Server
{
    /// <summary>
    /// Dedicated-server entry (PLAN 1.5, [T15]); compiled only with UNITY_SERVER, so it exists only in the
    /// ServerHeadless player. M0 is the server canary: boot, log "SERVER BOOT OK", and with -br-quit-after N exit 0
    /// after N seconds. -br-bind (default 127.0.0.1) is recorded for the netcode lane (M4); nothing listens yet.
    /// </summary>
    public sealed class ServerMain : MonoBehaviour
    {
        public const string BootOkLine = "SERVER BOOT OK";

        public static string BindAddress { get; private set; } = "127.0.0.1";

        float _quitAfter;
        float _t0;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => BindAddress = "127.0.0.1";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Application.isEditor) return; // the editor defines UNITY_SERVER only for the server compile check
            BindAddress = CommandLine.Get("-br-bind", "127.0.0.1");
            Application.targetFrameRate = CommandLine.GetInt("-br-server-fps", 60);
            var go = new GameObject("ServerMain");
            DontDestroyOnLoad(go);
            var s = go.AddComponent<ServerMain>();
            s._quitAfter = CommandLine.GetFloat("-br-quit-after", -1f);
            s._t0 = Time.realtimeSinceStartup;
            var b = BuildInfo.Current;
            Log.Info($"{BootOkLine} build={b.Id} bind={BindAddress} batchmode={Application.isBatchMode} gfx={SystemInfo.graphicsDeviceType}");
            System.Console.Out.WriteLine(BootOkLine);
        }

        void Update()
        {
            if (_quitAfter > 0f && Time.realtimeSinceStartup - _t0 >= _quitAfter)
            {
                _quitAfter = -1f;
                Log.Info("SERVER QUIT (-br-quit-after)");
                Application.Quit(0);
            }
        }
    }
}
