// Off-screen parent window for player autotests (QUEUE U31, tools/ci/README.md "Test windows").
// ci-lib.ps1 (Get-BrOffscreenHostExe) compiles this file with Add-Type into a Windows (not console)
// program under artifacts\ci-tools\ and starts it before a hidden player. The host makes one
// borderless popup window off screen (default -32000,-32000), sized to the test resolution, never
// activated (disabled, WS_EX_NOACTIVATE, shown with SW_SHOWNOACTIVATE), with no taskbar or Alt-Tab entry
// (WS_EX_TOOLWINDOW), writes its HWND to --hwnd-file and pumps messages. The player is then started
// with Unity's standalone switch -parentHWND <hwnd> and renders into a child window of it. A parent
// and child in two processes share one input queue, so the host must keep pumping while the player
// runs (that is why it is a process of its own with a message loop).
// It exits when the player's child window goes away, on WM_CLOSE, when --owner-pid exits, or after
// --max-seconds. Meanwhile it watches, through WinEvent hooks, every foreground change and every
// top-level window shown by a process named --watch-name, and writes them to --report (JSON): the
// evidence that the user saw nothing and that focus never moved.
// C# 5 only (Windows PowerShell 5.1 compiles it). ASCII only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace BackroadsCi
{
    public static class OffscreenHostMain
    {
        [STAThread]
        public static int Main(string[] args)
        {
            HostOptions o = new HostOptions(args);
            Native.MakeDpiAware();
            HostWindow w = new HostWindow(o);
            Application.Run();
            w.Finish();
            return 0;
        }
    }

    public sealed class HostOptions
    {
        public int X = -32000, Y = -32000, Width = 1280, Height = 720, OwnerPid, MaxSeconds;
        public string HwndFile, Report, WatchName = "Backroads", Show = "disabled";

        public HostOptions(string[] a)
        {
            for (int i = 0; i + 1 < a.Length; i += 2)
            {
                string k = a[i], v = a[i + 1];
                switch (k)
                {
                    case "--x": X = int.Parse(v, CultureInfo.InvariantCulture); break;
                    case "--y": Y = int.Parse(v, CultureInfo.InvariantCulture); break;
                    case "--width": Width = int.Parse(v, CultureInfo.InvariantCulture); break;
                    case "--height": Height = int.Parse(v, CultureInfo.InvariantCulture); break;
                    case "--owner-pid": OwnerPid = int.Parse(v, CultureInfo.InvariantCulture); break;
                    case "--max-seconds": MaxSeconds = int.Parse(v, CultureInfo.InvariantCulture); break;
                    case "--hwnd-file": HwndFile = v; break;
                    case "--report": Report = v; break;
                    case "--watch-name": WatchName = v; break;
                    case "--show": Show = v; break;
                }
            }
        }
    }

    public sealed class HostWindow : NativeWindow
    {
        const int WM_DESTROY = 0x0002, WM_ACTIVATE = 0x0006, WM_CLOSE = 0x0010, WM_ACTIVATEAPP = 0x001C;
        const int WM_MOUSEACTIVATE = 0x0021, WM_GETMINMAXINFO = 0x0024, WM_TIMER = 0x0113, MA_NOACTIVATE = 3;
        const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_OBJECT_SHOW = 0x8002, WINEVENT_OUTOFCONTEXT = 0;

        readonly HostOptions _o;
        readonly Stopwatch _sw = Stopwatch.StartNew();
        readonly DateTime _start = DateTime.Now;
        readonly int _myPid = Process.GetCurrentProcess().Id;
        readonly Dictionary<int, bool> _watched = new Dictionary<int, bool>();
        readonly List<string> _foreground = new List<string>();
        readonly List<string> _shown = new List<string>();
        readonly Native.WinEventProc _onEvent;
        IntPtr _hookFg, _hookShow;
        bool _done;
        string _exitReason = "";
        IntPtr _child = IntPtr.Zero;
        int _childPid;
        string _childClass = "", _childRect = "";
        double _childSeenAt = -1, _childGoneAt = -1;
        int _fgChanges, _fgOurs, _activateMsgs, _shownWatched, _shownOnScreen;
        string _fgAtStart, _fgAtEnd;

        public HostWindow(HostOptions o)
        {
            _o = o;
            _fgAtStart = Describe(Native.GetForegroundWindow());
            CreateParams cp = new CreateParams();
            cp.Caption = "Backroads autotest host";
            cp.X = o.X; cp.Y = o.Y; cp.Width = o.Width; cp.Height = o.Height;
            cp.Style = unchecked((int)(0x80000000u | 0x02000000u));   // WS_POPUP | WS_CLIPCHILDREN, no border, not visible yet
            cp.ExStyle = 0x00000080 | 0x08000000;                       // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            CreateHandle(cp);
            // Exact size and place even when larger than the screen (WM_GETMINMAXINFO below lifts the limit).
            Native.SetWindowPos(Handle, IntPtr.Zero, o.X, o.Y, o.Width, o.Height, 0x0010 | 0x0004 | 0x0200);   // NOACTIVATE | NOZORDER | NOOWNERZORDER
            // --show disabled (default): shown without activation and disabled. Unity's child calls SetFocus on
            // start; through the shared input queue that activates an enabled host (WM_ACTIVATE and a system
            // EVENT_SYSTEM_FOREGROUND, although GetForegroundWindow never changes). A disabled host is never
            // activated. The player still renders, plays audio and reads the autotests' virtual devices.
            // --show noactivate: shown, enabled (diagnostics). --show hidden: never shown (diagnostics).
            if (o.Show == "disabled") Native.EnableWindow(Handle, false);
            if (o.Show != "hidden") Native.ShowWindow(Handle, 4);   // SW_SHOWNOACTIVATE
            _onEvent = OnWinEvent;
            _hookFg = Native.SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _onEvent, 0, 0, WINEVENT_OUTOFCONTEXT);
            _hookShow = Native.SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _onEvent, 0, 0, WINEVENT_OUTOFCONTEXT);
            Native.SetTimer(Handle, (IntPtr)1, 100, IntPtr.Zero);
            if (!string.IsNullOrEmpty(o.HwndFile))
            {
                string tmp = o.HwndFile + ".tmp";
                File.WriteAllText(tmp, Handle.ToInt64().ToString(CultureInfo.InvariantCulture));
                if (File.Exists(o.HwndFile)) File.Delete(o.HwndFile);
                File.Move(tmp, o.HwndFile);
            }
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_MOUSEACTIVATE: m.Result = (IntPtr)MA_NOACTIVATE; return;
                case WM_ACTIVATE: case WM_ACTIVATEAPP: if (m.WParam != IntPtr.Zero) _activateMsgs++; break;
                case WM_GETMINMAXINFO:
                    base.WndProc(ref m);
                    Native.MINMAXINFO mm = (Native.MINMAXINFO)Marshal.PtrToStructure(m.LParam, typeof(Native.MINMAXINFO));
                    mm.ptMaxTrackSize.x = 32767; mm.ptMaxTrackSize.y = 32767;
                    mm.ptMaxSize.x = 32767; mm.ptMaxSize.y = 32767;
                    Marshal.StructureToPtr(mm, m.LParam, false);
                    return;
                case WM_TIMER: Tick(); return;
                case WM_CLOSE: Stop("WM_CLOSE"); return;
                case WM_DESTROY: Stop("destroyed"); break;
            }
            base.WndProc(ref m);
        }

        void Tick()
        {
            IntPtr c = Native.GetWindow(Handle, 5);   // GW_CHILD
            if (c != IntPtr.Zero && _child == IntPtr.Zero)
            {
                _child = c;
                _childSeenAt = _sw.Elapsed.TotalSeconds;
                uint pid; Native.GetWindowThreadProcessId(c, out pid); _childPid = (int)pid;
                StringBuilder sb = new StringBuilder(256); Native.GetClassName(c, sb, sb.Capacity); _childClass = sb.ToString();
            }
            if (_child != IntPtr.Zero)
            {
                Native.RECT r;
                if (Native.IsWindow(_child) && Native.GetWindowRect(_child, out r)) _childRect = Rect(r);
            }
            if (_child != IntPtr.Zero && c == IntPtr.Zero) { _childGoneAt = _sw.Elapsed.TotalSeconds; Stop("the player's window closed"); return; }
            if (_o.OwnerPid > 0 && !Alive(_o.OwnerPid)) { Stop("owner process " + _o.OwnerPid + " exited"); return; }
            if (_o.MaxSeconds > 0 && _sw.Elapsed.TotalSeconds > _o.MaxSeconds) { Stop("max-seconds " + _o.MaxSeconds); return; }
        }

        void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (hwnd == IntPtr.Zero) return;
            if (ev == EVENT_SYSTEM_FOREGROUND)
            {
                _fgChanges++;
                uint pid; Native.GetWindowThreadProcessId(hwnd, out pid);
                bool ours = IsOurs((int)pid);
                if (ours) _fgOurs++;
                if (_foreground.Count < 200) _foreground.Add(string.Format(CultureInfo.InvariantCulture, "{{\"t\":{0:F2},\"ours\":{1},\"pid\":{2}}}", _sw.Elapsed.TotalSeconds, ours ? "true" : "false", ours ? (int)pid : 0));
                return;
            }
            if (ev == EVENT_OBJECT_SHOW && idObject == 0 && idChild == 0)
            {
                if (Native.GetAncestor(hwnd, 2) != hwnd) return;   // GA_ROOT: top-level windows only
                uint pid; Native.GetWindowThreadProcessId(hwnd, out pid);
                if ((int)pid == _myPid || !IsWatched((int)pid)) return;
                _shownWatched++;
                Native.RECT r; Native.GetWindowRect(hwnd, out r);
                bool on = OnScreen(r) && Native.IsWindowVisible(hwnd);
                if (on) _shownOnScreen++;
                StringBuilder sb = new StringBuilder(256); Native.GetClassName(hwnd, sb, sb.Capacity);
                if (_shown.Count < 100) _shown.Add(string.Format(CultureInfo.InvariantCulture, "{{\"t\":{0:F2},\"pid\":{1},\"class\":\"{2}\",\"rect\":\"{3}\",\"onScreen\":{4}}}", _sw.Elapsed.TotalSeconds, pid, Esc(sb.ToString()), Rect(r), on ? "true" : "false"));
            }
        }

        bool IsOurs(int pid) { return pid == _myPid || (pid != 0 && pid == _childPid) || IsWatched(pid); }

        bool IsWatched(int pid)
        {
            if (pid == 0) return false;
            bool w;
            if (_watched.TryGetValue(pid, out w)) return w;
            w = false;
            try
            {
                Process p = Process.GetProcessById(pid);
                w = string.Equals(p.ProcessName, _o.WatchName, StringComparison.OrdinalIgnoreCase) && p.StartTime >= _start.AddSeconds(-2);
            }
            catch { }
            _watched[pid] = w;
            return w;
        }

        static bool Alive(int pid)
        {
            try { Process p = Process.GetProcessById(pid); return !p.HasExited; } catch { return false; }
        }

        static bool OnScreen(Native.RECT r)
        {
            int x = Native.GetSystemMetrics(76), y = Native.GetSystemMetrics(77), w = Native.GetSystemMetrics(78), h = Native.GetSystemMetrics(79);
            return r.right > x && r.left < x + w && r.bottom > y && r.top < y + h && r.right > r.left && r.bottom > r.top;
        }

        string Describe(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "none";
            uint pid; Native.GetWindowThreadProcessId(hwnd, out pid);
            return IsOurs((int)pid) ? "ours" : "other";
        }

        static string Rect(Native.RECT r) { return string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}x{3}", r.left, r.top, r.right - r.left, r.bottom - r.top); }

        static string Esc(string s) { return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\""); }

        void Stop(string why)
        {
            if (_done) return;
            _done = true;
            _exitReason = why;
            _fgAtEnd = Describe(Native.GetForegroundWindow());
            Native.KillTimer(Handle, (IntPtr)1);
            if (_hookFg != IntPtr.Zero) Native.UnhookWinEvent(_hookFg);
            if (_hookShow != IntPtr.Zero) Native.UnhookWinEvent(_hookShow);
            _hookFg = _hookShow = IntPtr.Zero;
            Application.ExitThread();
        }

        public void Finish()
        {
            if (!_done) Stop("message loop ended");
            Native.RECT hr; Native.GetWindowRect(Handle, out hr);
            bool hostOnScreen = OnScreen(hr) && Native.IsWindowVisible(Handle);
            if (!string.IsNullOrEmpty(_o.Report))
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("{\n");
                sb.AppendFormat(CultureInfo.InvariantCulture, "  \"hostPid\": {0},\n  \"hwnd\": {1},\n  \"hostRect\": \"{2}\",\n  \"hostOnScreen\": {3},\n", _myPid, Handle.ToInt64(), Rect(hr), hostOnScreen ? "true" : "false");
                sb.AppendFormat(CultureInfo.InvariantCulture, "  \"requested\": \"{0},{1},{2}x{3}\",\n  \"show\": \"{4}\",\n", _o.X, _o.Y, _o.Width, _o.Height, Esc(_o.Show));
                sb.AppendFormat(CultureInfo.InvariantCulture, "  \"childSeen\": {0},\n  \"childPid\": {1},\n  \"childClass\": \"{2}\",\n  \"childRect\": \"{3}\",\n  \"childSeenAtS\": {4:F2},\n  \"childGoneAtS\": {5:F2},\n",
                    _child != IntPtr.Zero ? "true" : "false", _childPid, Esc(_childClass), _childRect, _childSeenAt, _childGoneAt);
                sb.AppendFormat(CultureInfo.InvariantCulture, "  \"foregroundAtStart\": \"{0}\",\n  \"foregroundAtEnd\": \"{1}\",\n  \"foregroundChanges\": {2},\n  \"foregroundToOurs\": {3},\n  \"activateMessages\": {4},\n",
                    _fgAtStart, _fgAtEnd, _fgChanges, _fgOurs, _activateMsgs);
                sb.AppendFormat(CultureInfo.InvariantCulture, "  \"playerTopLevelShown\": {0},\n  \"playerTopLevelOnScreen\": {1},\n", _shownWatched, _shownOnScreen);
                sb.AppendFormat(CultureInfo.InvariantCulture, "  \"seconds\": {0:F2},\n  \"exitReason\": \"{1}\",\n", _sw.Elapsed.TotalSeconds, Esc(_exitReason));
                sb.Append("  \"foreground\": [").Append(string.Join(",", _foreground.ToArray())).Append("],\n");
                sb.Append("  \"shown\": [").Append(string.Join(",", _shown.ToArray())).Append("]\n}\n");
                File.WriteAllText(_o.Report, sb.ToString());
            }
            DestroyHandle();
        }
    }

    public static class Native
    {
        public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern bool EnableWindow(IntPtr hwnd, bool enable);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder s, int n);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] public static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr proc);
        [DllImport("user32.dll")] public static extern bool KillTimer(IntPtr hwnd, IntPtr id);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);

        public static void MakeDpiAware()
        {
            // Per-monitor v2, so --width/--height are physical pixels (the player is DPI aware too).
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch (EntryPointNotFoundException) { }
            try { SetProcessDPIAware(); } catch (EntryPointNotFoundException) { }
        }
    }
}
