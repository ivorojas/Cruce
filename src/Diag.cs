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

            var old = wifi;
            w.SameChannel = old.SameChannel; w.Overlapping = old.Overlapping; w.NeighborMax = old.NeighborMax; w.NetworksSeen = old.NetworksSeen;
            if (scanNeighbours && w.Channel > 0)
            {
                // Every access point the laptop can hear, with its channel and strength.
                string n = Netsh("wlan show networks mode=bssid");
                var aps = new List<int[]>(); // {signal, channel}
                string bssid = null; int sig = -1, ch = -1;
                Action flush = () => { if (bssid != null && bssid != myBssid && ch > 0) aps.Add(new[] { sig, ch }); bssid = null; sig = -1; ch = -1; };
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
                                if (r.Status == IPStatus.Success) pingMs.Add(r.RoundtripTime);
                                else pingFails++;
                            }
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
                var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                Log.Info("energía: {0}, batería {1:0}%", ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online ? "enchufada" : "batería", ps.BatteryLifePercent * 100);
            }
            catch (Exception ex) { Log.Error(ex, "diag"); }
        }
    }
}
