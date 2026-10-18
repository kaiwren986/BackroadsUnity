// Stand-in for Backroads.exe used only by tools/ci/tests/ci-selftest.ps1 (compiled at test time
// with Add-Type, never committed as a binary). It honours the player command line of lane I-a's
// AutotestRunner and SmokeRunner (-br-autotest <id>, -br-smoke, -br-out, -br-fps-cap, -br-silent,
// -logFile) and writes result.json / smoke.json with the same flat fields. By scenario id:
//   smoke, stub.pass, m0.crash.recover  -> pass, exit 0      stub.fail     -> fail, exit 1
//   stub.crash   -> exit 0xC0000005, no result               stub.hang     -> sleeps until killed
//   stub.noresult -> exit 0, no result.json                  stub.perf     -> p95FrameMs differs per run
//   stub.liar    -> exit 0 but status "fail"                 m0.net.egress -> a loopback TCP pair for 5 s
//   -br-smoke    -> smoke.json pass, exit 0
// C# 5 only (Windows PowerShell 5.1 compiles it).
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public static class StubPlayer
{
    static string Arg(string[] a, string name)
    {
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
        return null;
    }

    static bool Has(string[] a, string name)
    {
        foreach (string s in a) if (s == name) return true;
        return false;
    }

    static string Esc(string s) { return s.Replace("\\", "\\\\").Replace("\"", "\\\""); }

    static void Write(string dir, string file, string id, string status, string fields, string failure, string[] args)
    {
        if (dir == null) return;
        Directory.CreateDirectory(dir);
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"id\":\"").Append(Esc(id)).Append("\",\"status\":\"").Append(status).Append("\",");
        sb.Append("\"developmentBuild\":true,");
        sb.Append(fields).Append(",");
        sb.Append("\"affinity\":\"0x").Append(((long)Process.GetCurrentProcess().ProcessorAffinity).ToString("X")).Append("\",");
        sb.Append("\"failures\":[").Append(failure == null ? "" : "\"" + Esc(failure) + "\"").Append("],");
        sb.Append("\"endpoints\":[],");
        sb.Append("\"args\":[");
        for (int i = 0; i < args.Length; i++) { if (i > 0) sb.Append(","); sb.Append("\"").Append(Esc(args[i])).Append("\""); }
        sb.Append("]}");
        File.WriteAllText(Path.Combine(dir, file), sb.ToString());
    }

    public static int Main(string[] args)
    {
        string outDir = Arg(args, "-br-out");
        string log = Arg(args, "-logFile");
        string id = Arg(args, "-br-autotest");
        StringBuilder logText = new StringBuilder("stub player " + string.Join(" ", args) + "\n");
        Action<string> logLine = delegate (string l) { logText.Append(l).Append("\n"); if (log != null) { Directory.CreateDirectory(Path.GetDirectoryName(log)); File.WriteAllText(log, logText.ToString()); } };
        logLine("[BR] boot");

        if (Has(args, "-br-smoke"))
        {
            Write(outDir, "smoke.json", "smoke", "pass", "\"frames\":1300,\"seconds\":3.0,\"exceptions\":0,\"errors\":0", null, args);
            logLine("[BR] SMOKE OK frames=1300 seconds=3.0 endpoints=0");
            return 0;
        }
        if (id == null) { Thread.Sleep(3000); return 0; }
        switch (id)
        {
            case "smoke":
            case "stub.pass":
            case "m0.crash.recover":
                Write(outDir, "result.json", id, "pass", "\"frames\":180,\"p95FrameMs\":7.5,\"exceptions\":0", null, args);
                logLine("[BR] AUTOTEST " + id + " PASS (0 failures)");
                return 0;
            case "stub.fail":
                Write(outDir, "result.json", id, "fail", "\"frames\":12", "the canary was not where it should be", args);
                logLine("[BR] AUTOTEST " + id + " FAIL (1 failures)");
                return 1;
            case "stub.liar":
                Write(outDir, "result.json", id, "fail", "\"frames\":12", "said fail but exited 0", args);
                return 0;
            case "stub.crash":
                return unchecked((int)0xC0000005);
            case "stub.hang":
                Thread.Sleep(600000); return 0;
            case "stub.noresult":
                return 0;
            case "stub.perf":
            {
                string counter = Path.Combine(Path.GetDirectoryName(outDir), "perf-counter.txt");
                int n = File.Exists(counter) ? int.Parse(File.ReadAllText(counter)) : 0;
                File.WriteAllText(counter, (n + 1).ToString());
                double[] p95 = { 9.0, 7.0, 8.0 };
                Write(outDir, "result.json", id, "pass", "\"p95FrameMs\":" + p95[n % 3].ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"exceptions\":0", null, args);
                return 0;
            }
            case "m0.net.egress":
            {
                TcpListener l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                int port = ((IPEndPoint)l.LocalEndpoint).Port;
                TcpClient c = new TcpClient();
                c.Connect(IPAddress.Loopback, port);
                TcpClient s = l.AcceptTcpClient();
                Thread.Sleep(5000);
                c.Close(); s.Close(); l.Stop();
                Write(outDir, "result.json", id, "pass", "\"seconds\":5", null, args);
                return 0;
            }
        }
        Write(outDir, "result.json", id, "fail", "\"frames\":0", "unknown scenario", args);
        return 1;
    }
}
