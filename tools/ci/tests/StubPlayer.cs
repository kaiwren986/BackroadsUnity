// Stand-in for Backroads.exe used only by tools/ci/tests/ci-selftest.ps1 (compiled at test time
// with Add-Type, never committed as a binary). It honours the player command line of lane I-a's
// AutotestRunner and SmokeRunner (-br-autotest <id>, -br-smoke, -br-out, -br-fps-cap, -br-silent,
// -logFile) and writes result.json / smoke.json with the same flat fields. By scenario id:
//   smoke, stub.pass, m0.crash.recover  -> pass, exit 0      stub.fail     -> fail, exit 1
//   stub.crash   -> exit 0xC0000005, no result               stub.hang     -> sleeps until killed
//   stub.noresult -> exit 0, no result.json                  stub.perf     -> p95FrameMs differs per run
//   stub.liar    -> exit 0 but status "fail"                 m0.net.egress -> a loopback TCP pair for 5 s
//   stub.timing  -> fail, exit 1, timing failures only (underruns, p95 frame, RTF)
//   stub.timingmixed -> fail, exit 1, a timing and a functional (pitch) failure
//   -br-smoke    -> smoke.json pass, exit 0
// With -parentHWND <hwnd> (the hidden window mode, QUEUE U31) it does what Unity does: it makes a
// child window inside that window and keeps pumping messages; result.json then reports the parent's
// facts (valid, enabled, visible, rect, extended style) so the self-test can check the off-screen host.
// C# 5 only (Windows PowerShell 5.1 compiles it).
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class StubPlayer
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public int x, y; }
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG m, IntPtr h, uint a, uint b, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO { public int cb; public string reserved, desktop, title; public int x, y, xSize, ySize, xChars, yChars, fill, flags; public short showWindow, reserved2; public IntPtr reserved3, stdIn, stdOut, stdErr; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern void GetStartupInfo(out STARTUPINFO si);

    // How the launcher asked this process to show its window (STARTUPINFO.wShowWindow; -1 = not set):
    // 0 SW_HIDE (BR_PLAYER_WINDOWSTYLE=Hidden), 1 SW_SHOWNORMAL, 2 SW_SHOWMINIMIZED.
    static int StartupShow()
    {
        STARTUPINFO si;
        GetStartupInfo(out si);
        return (si.flags & 1) != 0 ? si.showWindow : -1;
    }

    static string s_parent = "";

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

    // Sleeps while pumping this thread's messages (a child window of another process's window must).
    static void Idle(int ms)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            MSG m;
            while (PeekMessage(out m, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref m); DispatchMessage(ref m); }
            Thread.Sleep(10);
        }
    }

    static void AttachToParent(string[] args)
    {
        string ph = Arg(args, "-parentHWND");
        if (ph == null) return;
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }
        IntPtr parent = new IntPtr(long.Parse(ph, CultureInfo.InvariantCulture));
        bool valid = IsWindow(parent);
        RECT r = new RECT();
        if (valid) GetWindowRect(parent, out r);
        IntPtr child = IntPtr.Zero;
        if (valid) child = CreateWindowEx(0, "STATIC", "stub player", 0x40000000 | 0x10000000, 0, 0, r.right - r.left, r.bottom - r.top, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);   // WS_CHILD | WS_VISIBLE
        s_parent = string.Format(CultureInfo.InvariantCulture,
            "\"parentHwnd\":\"{0}\",\"parentValid\":{1},\"parentEnabled\":{2},\"parentVisible\":{3},\"parentRect\":\"{4},{5},{6}x{7}\",\"parentExStyle\":\"0x{8:X8}\",\"childCreated\":{9},",
            ph, valid ? "true" : "false", valid && IsWindowEnabled(parent) ? "true" : "false", valid && IsWindowVisible(parent) ? "true" : "false",
            r.left, r.top, r.right - r.left, r.bottom - r.top, valid ? GetWindowLong(parent, -20) : 0, child != IntPtr.Zero ? "true" : "false");
        Idle(300);   // the host sees the child (it polls every 100 ms)
    }

    static void Write(string dir, string file, string id, string status, string fields, string[] failures, string[] args)
    {
        if (dir == null) return;
        Directory.CreateDirectory(dir);
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"id\":\"").Append(Esc(id)).Append("\",\"status\":\"").Append(status).Append("\",");
        sb.Append("\"developmentBuild\":true,");
        sb.Append(fields).Append(",");
        sb.Append(s_parent);
        sb.Append("\"startupShowWindow\":").Append(StartupShow()).Append(",");
        sb.Append("\"affinity\":\"0x").Append(((long)Process.GetCurrentProcess().ProcessorAffinity).ToString("X")).Append("\",");
        sb.Append("\"failures\":[");
        for (int i = 0; failures != null && i < failures.Length; i++) { if (i > 0) sb.Append(","); sb.Append("\"").Append(Esc(failures[i])).Append("\""); }
        sb.Append("],");
        sb.Append("\"endpoints\":[],");
        sb.Append("\"args\":[");
        for (int i = 0; i < args.Length; i++) { if (i > 0) sb.Append(","); sb.Append("\"").Append(Esc(args[i])).Append("\""); }
        sb.Append("]}");
        File.WriteAllText(Path.Combine(dir, file), sb.ToString());
    }

    static void Write(string dir, string file, string id, string status, string fields, string failure, string[] args)
    {
        Write(dir, file, id, status, fields, failure == null ? null : new string[] { failure }, args);
    }

    public static int Main(string[] args)
    {
        string outDir = Arg(args, "-br-out");
        string log = Arg(args, "-logFile");
        string id = Arg(args, "-br-autotest");
        StringBuilder logText = new StringBuilder("stub player " + string.Join(" ", args) + "\n");
        Action<string> logLine = delegate (string l) { logText.Append(l).Append("\n"); if (log != null) { Directory.CreateDirectory(Path.GetDirectoryName(log)); File.WriteAllText(log, logText.ToString()); } };
        logLine("[BR] boot");
        AttachToParent(args);

        if (Has(args, "-br-smoke"))
        {
            Write(outDir, "smoke.json", "smoke", "pass", "\"frames\":1300,\"seconds\":3.0,\"exceptions\":0,\"errors\":0", (string)null, args);
            logLine("[BR] SMOKE OK frames=1300 seconds=3.0 endpoints=0");
            return 0;
        }
        if (id == null) { Idle(3000); return 0; }
        switch (id)
        {
            case "smoke":
            case "stub.pass":
            case "m0.crash.recover":
                Write(outDir, "result.json", id, "pass", "\"frames\":180,\"p95FrameMs\":7.5,\"exceptions\":0", (string)null, args);
                logLine("[BR] AUTOTEST " + id + " PASS (0 failures)");
                return 0;
            case "stub.fail":
                Write(outDir, "result.json", id, "fail", "\"frames\":12", "the canary was not where it should be", args);
                logLine("[BR] AUTOTEST " + id + " FAIL (1 failures)");
                return 1;
            case "stub.timing":
                Write(outDir, "result.json", id, "fail", "\"frames\":900,\"p95FrameMs\":21.3", new string[] { "3 underruns (768 frames)", "p95 frame 21.30 ms > 16.6 ms at High", "v8 (v8_bigblock_a): RTF 1.80 < 2.0 at High" }, args);
                logLine("[BR] AUTOTEST " + id + " FAIL (3 failures)");
                return 1;
            case "stub.timingmixed":
                Write(outDir, "result.json", id, "fail", "\"frames\":900", new string[] { "2 underruns (512 frames)", "pitch 60% at 5114 rpm: acf 30.1 Hz (order 0.71), peak 120.0 Hz (order 2.82)" }, args);
                logLine("[BR] AUTOTEST " + id + " FAIL (2 failures)");
                return 1;
            case "stub.liar":
                Write(outDir, "result.json", id, "fail", "\"frames\":12", "said fail but exited 0", args);
                return 0;
            case "stub.crash":
                return unchecked((int)0xC0000005);
            case "stub.hang":
                Idle(600000); return 0;
            case "stub.noresult":
                return 0;
            case "stub.perf":
            {
                string counter = Path.Combine(Path.GetDirectoryName(outDir), "perf-counter.txt");
                int n = File.Exists(counter) ? int.Parse(File.ReadAllText(counter)) : 0;
                File.WriteAllText(counter, (n + 1).ToString());
                double[] p95 = { 9.0, 7.0, 8.0 };
                Write(outDir, "result.json", id, "pass", "\"p95FrameMs\":" + p95[n % 3].ToString(CultureInfo.InvariantCulture) + ",\"exceptions\":0", (string)null, args);
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
                Idle(5000);
                c.Close(); s.Close(); l.Stop();
                Write(outDir, "result.json", id, "pass", "\"seconds\":5", (string)null, args);
                return 0;
            }
        }
        Write(outDir, "result.json", id, "fail", "\"frames\":0", "unknown scenario", args);
        return 1;
    }
}
