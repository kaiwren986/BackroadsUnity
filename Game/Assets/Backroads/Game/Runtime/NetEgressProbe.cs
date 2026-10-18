using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace Backroads.Game
{
    /// <summary>
    /// Lists the TCP and UDP endpoints owned by this process, read from the Windows IP Helper tables
    /// (GetExtendedTcpTable / GetExtendedUdpTable with owner PIDs, the same data Get-NetTCPConnection and
    /// Get-NetUDPEndpoint show). It only reads tables; it opens no socket. Used by the smoke run and by autotest
    /// m0.net.egress (PLAN 1.9 [C7]): an outbound endpoint is a TCP connection to a non-loopback address, or a UDP
    /// endpoint bound to a non-loopback address.
    /// </summary>
    public static class NetEgressProbe
    {
        [Serializable]
        public sealed class Endpoint
        {
            public string protocol;     // tcp, tcp6, udp, udp6
            public string local;
            public int localPort;
            public string remote;
            public int remotePort;
            public string state;        // TCP state name; "bound" for UDP
            public bool outbound;
            public string note;
            /// <summary>Why an outbound endpoint is acceptable (development players only), else empty.</summary>
            public string allowedReason = "";
        }

        /// <summary>
        /// Marks the endpoints Unity's own PlayerConnection opens in development players: a TCP listener on 54997-55511 and
        /// the UDP socket (any port, bound to 0.0.0.0) that multicasts the player's presence to the profiler. Non-development
        /// builds get nothing allowed. Returns the number of outbound endpoints that are not allowed.
        /// </summary>
        public static int Classify(List<Endpoint> list, bool developmentBuild)
        {
            bool devListener = false;
            foreach (var e in list) if (e.state == "LISTEN" && IsPlayerConnectionPort(e.localPort)) devListener = true;
            int bad = 0;
            foreach (var e in list)
            {
                if (!e.outbound) continue;
                if (developmentBuild && devListener && e.protocol.StartsWith("udp") && (e.local == "0.0.0.0" || e.local == "::"))
                {
                    e.allowedReason = "development build: Unity PlayerConnection discovery (profiler); absent from Client builds";
                    e.note = "development player connection (profiler discovery)";
                    continue;
                }
                bad++;
            }
            return bad;
        }

        public static bool Supported =>
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            true;
#else
            false;
#endif

        /// <summary>Unity development players announce themselves for the profiler (PlayerConnection): a TCP listener and
        /// UDP multicast on 54997-55511. Client (non-development) builds have none; the probe marks them so a report can
        /// tell them apart.</summary>
        public static bool IsPlayerConnectionPort(int port) => port >= 54997 && port <= 55511;

        public static List<Endpoint> Sample()
        {
            var list = new List<Endpoint>();
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            int pid = Process.GetCurrentProcess().Id;
            ReadTcp(pid, AF_INET, list);
            ReadTcp(pid, AF_INET6, list);
            ReadUdp(pid, AF_INET, list);
            ReadUdp(pid, AF_INET6, list);
#endif
            return list;
        }

        static bool IsLoopbackOrAny(IPAddress a) =>
            IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.IPv6None);

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        const int AF_INET = 2, AF_INET6 = 23;
        const int TCP_TABLE_OWNER_PID_ALL = 5, UDP_TABLE_OWNER_PID = 1;
        const int MIB_TCP_STATE_LISTEN = 2;

        static readonly string[] TcpStates =
            { "?", "CLOSED", "LISTEN", "SYN_SENT", "SYN_RCVD", "ESTABLISHED", "FIN_WAIT1", "FIN_WAIT2", "CLOSE_WAIT", "CLOSING", "LAST_ACK", "TIME_WAIT", "DELETE_TCB" };

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);

        static int Port(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

        static IntPtr Fetch(bool tcp, int af, out int size)
        {
            size = 0;
            uint r = tcp ? GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0)
                         : GetExtendedUdpTable(IntPtr.Zero, ref size, false, af, UDP_TABLE_OWNER_PID, 0);
            for (int attempt = 0; attempt < 4 && size > 0; attempt++)
            {
                size += 4096;
                var buf = Marshal.AllocHGlobal(size);
                r = tcp ? GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0)
                        : GetExtendedUdpTable(buf, ref size, false, af, UDP_TABLE_OWNER_PID, 0);
                if (r == 0) return buf;
                Marshal.FreeHGlobal(buf);
                if (r != 122) break; // ERROR_INSUFFICIENT_BUFFER: retry with the new size
            }
            return IntPtr.Zero;
        }

        static IPAddress V6(IntPtr p, int offset)
        {
            var b = new byte[16];
            Marshal.Copy(IntPtr.Add(p, offset), b, 0, 16);
            return new IPAddress(b);
        }

        static IPAddress V4(uint raw) => new IPAddress(BitConverter.GetBytes(raw));

        static void ReadTcp(int pid, int af, List<Endpoint> list)
        {
            var buf = Fetch(true, af, out _);
            if (buf == IntPtr.Zero) return;
            try
            {
                int n = Marshal.ReadInt32(buf);
                int rowSize = af == AF_INET ? 24 : 56;
                for (int i = 0; i < n; i++)
                {
                    var row = IntPtr.Add(buf, 4 + i * rowSize);
                    IPAddress local, remote; int lp, rp, state, owner;
                    if (af == AF_INET)
                    {
                        state = Marshal.ReadInt32(row, 0);
                        local = V4((uint)Marshal.ReadInt32(row, 4)); lp = Port((uint)Marshal.ReadInt32(row, 8));
                        remote = V4((uint)Marshal.ReadInt32(row, 12)); rp = Port((uint)Marshal.ReadInt32(row, 16));
                        owner = Marshal.ReadInt32(row, 20);
                    }
                    else
                    {
                        local = V6(row, 0); lp = Port((uint)Marshal.ReadInt32(row, 20));
                        remote = V6(row, 24); rp = Port((uint)Marshal.ReadInt32(row, 44));
                        state = Marshal.ReadInt32(row, 48); owner = Marshal.ReadInt32(row, 52);
                    }
                    if (owner != pid) continue;
                    bool listen = state == MIB_TCP_STATE_LISTEN;
                    var e = new Endpoint
                    {
                        protocol = af == AF_INET ? "tcp" : "tcp6",
                        local = local.ToString(), localPort = lp, remote = remote.ToString(), remotePort = rp,
                        state = state >= 0 && state < TcpStates.Length ? TcpStates[state] : state.ToString(),
                        outbound = !listen && !IsLoopbackOrAny(remote),
                    };
                    if (listen) e.note = IsPlayerConnectionPort(lp) ? "listening (development player connection)" : "listening (inbound only)";
                    list.Add(e);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static void ReadUdp(int pid, int af, List<Endpoint> list)
        {
            var buf = Fetch(false, af, out _);
            if (buf == IntPtr.Zero) return;
            try
            {
                int n = Marshal.ReadInt32(buf);
                int rowSize = af == AF_INET ? 12 : 28;
                for (int i = 0; i < n; i++)
                {
                    var row = IntPtr.Add(buf, 4 + i * rowSize);
                    IPAddress local; int lp, owner;
                    if (af == AF_INET)
                    {
                        local = V4((uint)Marshal.ReadInt32(row, 0)); lp = Port((uint)Marshal.ReadInt32(row, 4)); owner = Marshal.ReadInt32(row, 8);
                    }
                    else
                    {
                        local = V6(row, 0); lp = Port((uint)Marshal.ReadInt32(row, 20)); owner = Marshal.ReadInt32(row, 24);
                    }
                    if (owner != pid) continue;
                    var e = new Endpoint
                    {
                        protocol = af == AF_INET ? "udp" : "udp6",
                        local = local.ToString(), localPort = lp, remote = "", remotePort = 0, state = "bound",
                        outbound = !IPAddress.IsLoopback(local),
                    };
                    if (IsPlayerConnectionPort(lp)) e.note = "development player connection (profiler discovery)";
                    list.Add(e);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
#endif
    }
}
