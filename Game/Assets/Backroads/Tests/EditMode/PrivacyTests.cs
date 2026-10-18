using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Backroads.Tests.EditMode
{
    /// <summary>
    /// Backroads.Tests.EditMode.Privacy.* (PLAN 5.9 [C7]). The static half of the m0.net.egress method: no game code
    /// outside the netcode assemblies uses a network API. The settings half is in Build.ProjectSettings (privacy.*),
    /// the runtime half is the player autotest m0.net.egress (the process's own TCP/UDP tables).
    /// </summary>
    [TestFixture]
    public class Privacy
    {
        // Built by concatenation so this file does not match itself.
        static readonly string[] NetworkApis =
        {
            "UnityWeb" + "Request", "System.Net." + "Sockets", "Tcp" + "Client", "Udp" + "Client", "Tcp" + "Listener",
            "Http" + "Client", "Web" + "Client", "WebRequest." + "Create", "new " + "Socket(", "Dns." + "Get", "new " + "Ping(",
            "Application." + "OpenURL",
        };

        static bool OutsideNet(string path) =>
            !path.Contains("/Backroads/Net/") && !path.Contains("/Backroads/Net.Steam/") && !path.Contains("/Tests/");

        [Test]
        public void NoNetworkApiUse()
        {
            var hits = new List<string>();
            foreach (var api in NetworkApis) hits.AddRange(TestUtil.Find(api, OutsideNet).Select(h => api + ": " + h));
            Assert.IsEmpty(hits, "network APIs outside Backroads.Net:\n" + string.Join("\n", hits));
        }
    }
}
