using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Cruce
{
    /// <summary>System facts for the diagnostic log. Slow probes run on their own thread and are cached.</summary>
    public static class Diag
    {
        static volatile string wifi = "wifi: sin datos";

        public static string WifiShort() { return wifi; }

        public static void Start()
        {
            new Thread(() =>
            {
                while (true)
                {
                    try { wifi = ProbeWifi(); } catch (Exception ex) { wifi = "wifi: error " + ex.Message; }
                    Thread.Sleep(30000);
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "cruce-diag" }.Start();
        }

        static string ProbeWifi()
        {
            var psi = new ProcessStartInfo("netsh", "wlan show interfaces") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            string o;
            using (var p = Process.Start(psi)) { o = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000); }
            if (!Regex.IsMatch(o, @"(?im)^\s*(SSID)\s*:")) return "wifi: no (cable)";
            Func<string, string> g = pat => { var m = Regex.Match(o, @"(?im)^\s*(" + pat + @")\s*:\s*(.+)$"); return m.Success ? m.Groups[2].Value.Trim() : "?"; };
            return string.Format("wifi señal {0}, banda {1}, canal {2}, rx {3} / tx {4} Mbps",
                g("Se.{1,2}al|Signal"), g("Banda|Band"), g("Canal|Channel"), g("Velocidad de recepci.{1,2}n \\(Mbps\\)|Receive rate \\(Mbps\\)"), g("Velocidad de transmisi.{1,2}n \\(Mbps\\)|Transmit rate \\(Mbps\\)"));
        }

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
                var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                Log.Info("energía: {0}, batería {1:0}%", ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online ? "enchufada" : "batería", ps.BatteryLifePercent * 100);
            }
            catch (Exception ex) { Log.Error(ex, "diag"); }
        }
    }
}
