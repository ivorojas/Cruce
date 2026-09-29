using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Cruce
{
    public sealed class WifiState
    {
        public bool OnWifi;
        public int Signal = -1, Channel = -1;
        public string Band = "";
        public double RxMbps = -1, TxMbps = -1;
        public int SameChannel = -1, Overlapping = -1, NeighborMax = -1, NetworksSeen = -1;
    }

    /// <summary>
    /// System facts for diagnostics. Everything slow runs on background threads and is cached,
    /// so none of it touches the mouse/keyboard path.
    ///  - Wi-Fi state (signal, band, channel, link rate) and neighbour networks on our channel.
    ///  - Ping to the router every second: isolates the Wi-Fi hop from everything else.
    ///  - Counters for stalls, slow hooks and blocked injections.
    /// </summary>
    public static class Diag
    {
        static volatile WifiState wifi = new WifiState();
        static volatile string wifiText = "wifi: sin datos";
        public static int Stalls, SlowHooks, InjectBlocked;

        public static WifiState Wifi { get { return wifi; } }
        public static string WifiShort() { return wifiText; }

        public static void Start()
        {
            new Thread(() =>
            {
                int n = 0;
                while (true)
                {
                    try { ProbeWifi(n++ % 2 == 0); } catch (Exception ex) { wifiText = "wifi: error " + ex.Message; }
                    Thread.Sleep(30000);
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "cruce-diag" }.Start();
            new Thread(PingLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "cruce-gwping" }.Start();
            GetSystemTimes(out lastIdle, out lastKernel, out lastUser);
            lastProcCpu = Process.GetCurrentProcess().TotalProcessorTime;
            lastCpuAt = DateTime.UtcNow;
        }

        // ------------------------------------------------------------ Wi-Fi

        static string Netsh(string args)
        {
            var psi = new ProcessStartInfo("netsh", args) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            using (var p = Process.Start(psi)) { var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000); return o; }
        }

        static int Num(string s)
        {
            var m = Regex.Match(s ?? "", @"\d+");
            return m.Success ? int.Parse(m.Value) : -1;
        }

        static double Dbl(string s)
        {
            var m = Regex.Match(s ?? "", @"\d+([\.,]\d+)?");
            double d;
            return m.Success && double.TryParse(m.Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : -1;
        }

        static void ProbeWifi(bool scanNeighbours)
        {
            string o = Netsh("wlan show interfaces");
            var w = new WifiState();
            Func<string, string> g = pat => { var m = Regex.Match(o, @"(?im)^\s*(" + pat + @")\s*:\s*(.+)$"); return m.Success ? m.Groups[2].Value.Trim() : null; };
            string state = g("Estado|State");
            w.OnWifi = g("SSID") != null && (state == null || Regex.IsMatch(state, "conectado|connected", RegexOptions.IgnoreCase));
            if (!w.OnWifi) { wifi = w; wifiText = "wifi: no (cable)"; return; }
            w.Signal = Num(g("Se.{1,2}al|Signal"));
            w.Band = (g("Banda|Band") ?? "").Replace(' ', ' ');
            w.Channel = Num(g("Canal|Channel"));
            w.RxMbps = Dbl(g("Velocidad de recepci.{1,2}n \\(Mbps\\)|Receive rate \\(Mbps\\)"));
            w.TxMbps = Dbl(g("Velocidad de transmisi.{1,2}n \\(Mbps\\)|Transmit rate \\(Mbps\\)"));
            string myBssid = (g("BSSID|AP BSSID") ?? "").ToLowerInvariant();
            if (WifiNative.Available && WifiNative.Connected)
            {
                // Driver values are fresher and more precise than netsh's text.
                if (WifiNative.Quality >= 0) w.Signal = WifiNative.Quality;
                if (WifiNative.Channel > 0) w.Channel = WifiNative.Channel;
                if (WifiNative.RxKbps > 0) w.RxMbps = WifiNative.RxKbps / 1000.0;
                if (WifiNative.TxKbps > 0) w.TxMbps = WifiNative.TxKbps / 1000.0;
            }

            var old = wifi;
            w.SameChannel = old.SameChannel; w.Overlapping = old.Overlapping; w.NeighborMax = old.NeighborMax; w.NetworksSeen = old.NetworksSeen;
            if (scanNeighbours && w.Channel > 0)
            {
                // Every access point the laptop can hear, with its channel and strength (%).
                List<int[]> aps = null;
                List<int[]> nat = null;
                try { nat = WifiNative.Neighbours(); } catch { }
                if (nat != null) aps = nat.Select(a => new[] { Math.Max(0, Math.Min(100, 2 * (a[0] + 100))), a[1] }).ToList();
                else
                {
                    string n = Netsh("wlan show networks mode=bssid");
                    var list = new List<int[]>();
                    string bssid = null; int sig = -1, ch = -1;
                    Action flush = () => { if (bssid != null && bssid != myBssid && ch > 0) list.Add(new[] { sig, ch }); bssid = null; sig = -1; ch = -1; };
                    foreach (var line in n.Split('\n'))
                    {
                        var mb = Regex.Match(line, @"^\s*BSSID\s*\d*\s*:\s*(\S+)", RegexOptions.IgnoreCase);
                        if (mb.Success) { flush(); bssid = mb.Groups[1].Value.ToLowerInvariant(); continue; }
                        if (Regex.IsMatch(line, @"^\s*SSID\s*\d+\s*:", RegexOptions.IgnoreCase)) { flush(); continue; }
                        if (bssid == null) continue;
                        var ms = Regex.Match(line, @"^\s*(Se.{1,2}al|Signal)\s*:\s*(\d+)", RegexOptions.IgnoreCase);
                        if (ms.Success) sig = int.Parse(ms.Groups[2].Value);
                        var mc = Regex.Match(line, @"^\s*(Canal|Channel)\s*:\s*(\d+)", RegexOptions.IgnoreCase);
                        if (mc.Success) ch = int.Parse(mc.Groups[2].Value);
                    }
                    flush();
                    aps = list;
                }
                bool is24 = w.Channel <= 14;
                w.NetworksSeen = aps.Count;
                w.SameChannel = aps.Count(a => a[1] == w.Channel);
                w.Overlapping = is24 ? aps.Count(a => a[1] <= 14 && a[1] != w.Channel && Math.Abs(a[1] - w.Channel) <= 4) : 0;
                var near = aps.Where(a => a[1] == w.Channel || (is24 && a[1] <= 14 && Math.Abs(a[1] - w.Channel) <= 4)).ToList();
                w.NeighborMax = near.Count > 0 ? near.Max(a => a[0]) : 0;
            }
            wifi = w;
            wifiText = string.Format("wifi señal {0}%, {1}, canal {2}, rx {3} Mbps, vecinos en tu canal {4} (+{5} que se pisan), vecino más fuerte {6}%",
                w.Signal, w.Band, w.Channel, w.RxMbps, w.SameChannel, w.Overlapping, w.NeighborMax);
        }

        // ------------------------------------------------------------ router ping

        static readonly object pingGate = new object();
        static readonly List<long> pingMs = new List<long>();
        static int pingFails;

        static IPAddress Gateway()
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var gw in ni.GetIPProperties().GatewayAddresses)
                    if (gw.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !gw.Address.Equals(IPAddress.Any)) return gw.Address;
            }
            return null;
        }

        static void PingLoop()
        {
            IPAddress gw = null;
            int n = 0;
            using (var p = new Ping())
            {
                while (true)
                {
                    try
                    {
                        if (gw == null || n++ % 60 == 0) gw = Gateway();
                        if (gw != null)
                        {
                            var r = p.Send(gw, 1000);
                            lock (pingGate)
                            {
                                if (r.Status == IPStatus.Success) { pingMs.Add(r.RoundtripTime); LastRouterMs = (int)r.RoundtripTime; }
                                else { pingFails++; LastRouterMs = -1; }
                            }
                            Flight.Add(Flight.K_PING, r.Status == IPStatus.Success ? (int)r.RoundtripTime : 9999, -1);
                            if (n % 2 == 0) { SampleNet(gw); PingInternet(p); }
                        }
                    }
                    catch { lock (pingGate) pingFails++; }
                    Thread.Sleep(1000);
                }
            }
        }

        /// <summary>Router ping stats since the last call: avg, p95, max (ms), failures. Count 0 means no data.</summary>
        public static void TakeRouter(out int count, out double avg, out long p95, out long max, out int fails)
        {
            lock (pingGate)
            {
                count = pingMs.Count; fails = pingFails;
                if (count > 0)
                {
                    var s = pingMs.OrderBy(v => v).ToList();
                    avg = s.Average(); p95 = s[Math.Min(s.Count - 1, (int)(s.Count * 0.95))]; max = s[s.Count - 1];
                }
                else { avg = -1; p95 = -1; max = -1; }
                pingMs.Clear(); pingFails = 0;
            }
        }

        // ------------------------------------------------------------ timing histograms (hot path: one Interlocked op)

        static readonly int[] hookHist = new int[201];   // 1 ms buckets
        static readonly int[] injHist = new int[1001];   // 10 µs buckets

        public static void HookDelay(int ms)
        {
            if (ms < 0 || ms > 100000) return;
            Interlocked.Increment(ref hookHist[Math.Min(200, ms)]);
        }

        public static void InjectTime(long ticks)
        {
            int us = (int)(ticks * 1000000 / Stopwatch.Frequency);
            Interlocked.Increment(ref injHist[Math.Min(1000, us / 10)]);
            Flight.Add(Flight.K_INJ, us, 0);
        }

        static double TakePct(int[] h, double q, double unit)
        {
            long total = 0;
            var copy = new int[h.Length];
            for (int i = 0; i < h.Length; i++) { copy[i] = Interlocked.Exchange(ref h[i], 0); total += copy[i]; }
            if (total == 0) return -1;
            long target = (long)(total * q), acc = 0;
            for (int i = 0; i < copy.Length; i++) { acc += copy[i]; if (acc > target) return i * unit; }
            return (copy.Length - 1) * unit;
        }

        // ------------------------------------------------------------ network throughput + internet ping

        static readonly object netGate = new object();
        static long netRxBytes, netTxBytes, netPeak, lastRx = -1, lastTx = -1;
        static DateTime netWindowStart = DateTime.UtcNow, lastNetSample = DateTime.UtcNow;
        static readonly List<long> inetMs = new List<long>();
        static int inetFails;
        public static volatile int LastRouterMs = -1, LastInetMs = -1;

        static NetworkInterface netIf;
        static int netIfUses;

        static void SampleNet(IPAddress gw)
        {
            try
            {
                if (netIf == null || netIfUses++ > 30)
                {
                    netIfUses = 0;
                    netIf = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.GetIPProperties().GatewayAddresses.Any(g => g.Address.Equals(gw)));
                }
                var ni = netIf;
                if (ni == null) return;
                var st = ni.GetIPv4Statistics();
                long rx = st.BytesReceived, tx = st.BytesSent;
                var now = DateTime.UtcNow;
                lock (netGate)
                {
                    if (lastRx >= 0 && rx >= lastRx && tx >= lastTx)
                    {
                        long drx = rx - lastRx, dtx = tx - lastTx;
                        netRxBytes += drx; netTxBytes += dtx;
                        double secs = Math.Max(0.5, (now - lastNetSample).TotalSeconds);
                        long kbs = (long)((drx + dtx) / 1024.0 / secs);
                        if (kbs > netPeak) netPeak = kbs;
                        Flight.Add(Flight.K_NET, (int)(drx / 1024 / secs), (int)(dtx / 1024 / secs));
                    }
                    lastRx = rx; lastTx = tx; lastNetSample = now;
                }
            }
            catch { }
        }

        static void PingInternet(Ping p)
        {
            try
            {
                var r = p.Send(IPAddress.Parse("1.1.1.1"), 1000);
                lock (netGate)
                {
                    if (r.Status == IPStatus.Success) { inetMs.Add(r.RoundtripTime); LastInetMs = (int)r.RoundtripTime; }
                    else { inetFails++; LastInetMs = -1; }
                }
                Flight.Add(Flight.K_PING, 0, r.Status == IPStatus.Success ? (int)r.RoundtripTime : 9999);
            }
            catch { lock (netGate) inetFails++; }
        }

        // ------------------------------------------------------------ CPU frequency + top processes

        [DllImport("powrprof.dll")]
        static extern int CallNtPowerInformation(int level, IntPtr inBuf, int inLen, IntPtr outBuf, int outLen);

        static double CpuMhzPct()
        {
            int n = Environment.ProcessorCount, sz = 24 * n;
            IntPtr buf = Marshal.AllocHGlobal(sz);
            try
            {
                if (CallNtPowerInformation(11, IntPtr.Zero, 0, buf, sz) != 0) return -1;
                double sum = 0; int cnt = 0;
                for (int i = 0; i < n; i++)
                {
                    int max = Marshal.ReadInt32(buf, i * 24 + 4), cur = Marshal.ReadInt32(buf, i * 24 + 8), limit = Marshal.ReadInt32(buf, i * 24 + 12);
                    if (max <= 0) continue;
                    sum += 100.0 * Math.Min(cur, limit > 0 ? limit : cur) / max; cnt++;
                }
                return cnt > 0 ? sum / cnt : -1;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static Dictionary<int, TimeSpan> lastProcTimes = new Dictionary<int, TimeSpan>();
        static DateTime lastProcAt = DateTime.UtcNow;

        static string TopCpu()
        {
            var now = DateTime.UtcNow;
            double wall = Math.Max(1, (now - lastProcAt).TotalMilliseconds) * Environment.ProcessorCount;
            var cur = new Dictionary<int, TimeSpan>();
            var use = new List<Tuple<string, double>>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var t = p.TotalProcessorTime;
                    cur[p.Id] = t;
                    TimeSpan prev;
                    if (lastProcTimes.TryGetValue(p.Id, out prev)) use.Add(Tuple.Create(p.ProcessName, 100.0 * (t - prev).TotalMilliseconds / wall));
                }
                catch { }
                finally { p.Dispose(); }
            }
            lastProcTimes = cur; lastProcAt = now;
            return string.Join(", ", use.Where(u => u.Item2 >= 1).OrderByDescending(u => u.Item2).Take(3).Select(u => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} {1:0}%", u.Item1, u.Item2)));
        }

        /// <summary>Fills this PC's per-minute extras. Runs off the latency-critical threads.</summary>
        public static void TakeExtra(MinuteRow r)
        {
            r.HookDelayP95 = TakePct(hookHist, 0.95, 1);
            r.InjectP95Us = TakePct(injHist, 0.95, 10);
            lock (netGate)
            {
                double secs = Math.Max(1, (DateTime.UtcNow - netWindowStart).TotalSeconds);
                if (lastRx >= 0) { r.NetRxKBs = netRxBytes / 1024.0 / secs; r.NetTxKBs = netTxBytes / 1024.0 / secs; r.NetPeakKBs = netPeak; }
                netRxBytes = netTxBytes = netPeak = 0; netWindowStart = DateTime.UtcNow;
                if (inetMs.Count > 0) { var s = inetMs.OrderBy(v => v).ToList(); r.InetAvg = s.Average(); r.InetP95 = s[Math.Min(s.Count - 1, (int)(s.Count * 0.95))]; }
                r.InetFails = inetFails; inetMs.Clear(); inetFails = 0;
            }
            try { r.CpuMhzPct = CpuMhzPct(); } catch { }
            try { r.TopCpu = TopCpu(); } catch { }
        }

        // ------------------------------------------------------------ power: plugged in, battery, saver, Windows power mode, plan

        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
        [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
        [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid g);
        [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

        static string ModeName(Guid g)
        {
            switch (g.ToString().ToLowerInvariant())
            {
                case "961cc777-2547-4f9d-8174-7d86181b8a7a": return "maxima_eficiencia";
                case "3af9b8d9-7c97-431d-ad78-34a8bfea439f": return "mejor_bateria";
                case "00000000-0000-0000-0000-000000000000": return "equilibrado";
                case "ded574b5-45a0-4f42-8737-46345c09c238": return "maximo_rendimiento";
            }
            return g.ToString();
        }

        static string PlanName(Guid g)
        {
            switch (g.ToString().ToLowerInvariant())
            {
                case "381b4222-f694-41f0-9685-ff5bb260df2e": return "equilibrado";
                case "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c": return "alto_rendimiento";
                case "a1841308-3541-4fab-bc81-f71556f20b4a": return "economizador";
                case "e9a42b02-d5df-448d-aa00-03f14749eb61": return "maximo_rendimiento";
            }
            return "personalizado";
        }

        public static void TakePower(MinuteRow r)
        {
            try
            {
                SYSTEM_POWER_STATUS s;
                if (GetSystemPowerStatus(out s))
                {
                    r.Power = s.ACLineStatus == 1 ? "enchufada" : s.ACLineStatus == 0 ? "bateria" : "";
                    r.BatteryPct = s.BatteryLifePercent == 255 ? -1 : s.BatteryLifePercent;
                    r.Saver = (s.SystemStatusFlag & 1) != 0 ? 1 : 0;
                }
            }
            catch { }
            try { Guid g; if (PowerGetEffectiveOverlayScheme(out g) == 0) r.PowerMode = ModeName(g); } catch { }
            try
            {
                IntPtr p;
                if (PowerGetActiveScheme(IntPtr.Zero, out p) == 0)
                {
                    var g = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
                    LocalFree(p);
                    r.Plan = PlanName(g);
                }
            }
            catch { }
        }

        public static string PowerText()
        {
            var r = new MinuteRow();
            TakePower(r);
            return string.Format("{0}, batería {1}%, ahorro de batería {2}, modo {3}, plan {4}", r.Power, r.BatteryPct, r.Saver == 1 ? "sí" : "no", r.PowerMode, r.Plan);
        }

        /// <summary>How this PC reaches the network, for the map: "Cable", "WiFi 5 GHz · canal 157"… ("" if unknown yet).</summary>
        public static string NetLabel()
        {
            try
            {
                if (WifiNative.Available && WifiNative.Connected && WifiNative.Channel > 0)
                    return "WiFi " + (WifiNative.Channel <= 14 ? "2,4 GHz" : "5 GHz") + " · canal " + WifiNative.Channel;
                var w = wifi;
                if (w.OnWifi && w.Channel > 0) return "WiFi " + (w.Channel <= 14 ? "2,4 GHz" : "5 GHz") + " · canal " + w.Channel;
                var ni = netIf;
                if (ni != null) return ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "WiFi" : "Cable";
            }
            catch { }
            return "";
        }

        /// <summary>One-line picture of the current state, for incident files.</summary>
        public static string Snapshot()
        {
            var sb = new StringBuilder();
            if (WifiNative.Available) sb.AppendFormat("wifi {0} dBm, calidad {1}%, canal {2}, rx {3} / tx {4} Mbps, baja latencia {5}; ", WifiNative.Rssi, WifiNative.Quality, WifiNative.Channel, WifiNative.RxKbps / 1000, WifiNative.TxKbps / 1000, WifiNative.LowLatency ? "sí" : "no");
            else sb.Append(wifiText + "; ");
            sb.AppendFormat("ping router {0} ms, internet {1} ms; energía: {2}", LastRouterMs, LastInetMs, PowerText());
            return sb.ToString();
        }

        // ------------------------------------------------------------ CPU

        [DllImport("kernel32.dll")]
        static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        static long lastIdle, lastKernel, lastUser;
        static TimeSpan lastProcCpu;
        static DateTime lastCpuAt;

        /// <summary>System and Cruce CPU usage (%) since the last call.</summary>
        public static void TakeCpu(out double system, out double app)
        {
            long idle, kernel, user;
            GetSystemTimes(out idle, out kernel, out user);
            long di = idle - lastIdle, dt = (kernel - lastKernel) + (user - lastUser);
            system = dt > 0 ? 100.0 * (dt - di) / dt : -1;
            lastIdle = idle; lastKernel = kernel; lastUser = user;
            var pc = Process.GetCurrentProcess().TotalProcessorTime;
            var now = DateTime.UtcNow;
            double wall = (now - lastCpuAt).TotalMilliseconds * Environment.ProcessorCount;
            app = wall > 0 ? 100.0 * (pc - lastProcCpu).TotalMilliseconds / wall : -1;
            lastProcCpu = pc; lastCpuAt = now;
        }

        // ------------------------------------------------------------ startup facts

        public static void LogStartup(Config c, bool admin)
        {
            try
            {
                Log.Info("=== Cruce {0} | admin={1} | Windows {2} | .NET {3} | PC {4}", AppController.Version, admin, Environment.OSVersion.Version, Environment.Version, c.Name);
                Log.Info("config: lado={0} ip_fija='{1}' puerto={2} velocidad={3} intervalo={4}us portapapeles={5} archivos={6}", c.Side, c.PeerIp, c.Port, c.Speed, c.MoveIntervalUs, c.Clipboard, c.Files);
                foreach (var m in Geo.Enumerate()) Log.Info("monitor {0},{1} {2}x{3} dpi {4}", m.L, m.T, m.W, m.H, m.Dpi);
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                {
                    var ips = string.Join(",", ni.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Select(a => a.Address.ToString()));
                    Log.Info("red: {0} [{1}] {2} Mbps ip {3}", ni.Name, ni.NetworkInterfaceType, ni.Speed / 1000000, ips);
                }
                var gw = Gateway();
                Log.Info("router: {0}", gw != null ? gw.ToString() : "no encontrado");
                Log.Info("energía: {0}", PowerText());
            }
            catch (Exception ex) { Log.Error(ex, "diag"); }
        }
    }
}
