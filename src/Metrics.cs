using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Cruce
{
    /// <summary>One row per PC per minute in metricas.csv (both PCs' rows end up on each PC).</summary>
    public sealed class MinuteRow
    {
        public DateTime Time;
        public string Pc = "";
        public double ActiveS, RttAvg = -1, RttP95 = -1, RttMax = -1, LossPct, RouterAvg = -1, RouterP95 = -1, RouterMax = -1, RxMbps = -1, CpuSys = -1, CpuApp = -1;
        public int Crossings, Spikes, Packets, RouterFails, Signal = -1, Channel = -1, SameCh = -1, Overlap = -1, NeighborMax = -1, Stalls, SlowHooks, Blocked;
        public string Band = "", Cause = "ok";

        public const string Header = "fecha_hora;pc;activo_s;cruces;rtt_prom_ms;rtt_p95_ms;rtt_max_ms;picos_30ms;perdida_pct;paquetes;router_prom_ms;router_p95_ms;router_max_ms;router_fallas;wifi_senal_pct;wifi_banda;wifi_canal;wifi_rx_mbps;redes_mismo_canal;redes_solapadas;vecino_mas_fuerte_pct;cpu_sistema_pct;cpu_cruce_pct;frenadas;hook_lento;bloqueos_windows;causa";

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string D(double v) { return v < 0 ? "" : v.ToString("0.#", Inv); }
        static string I(int v) { return v < 0 ? "" : v.ToString(Inv); }

        public bool Laggy { get { return RttP95 > 40 || Spikes >= 5 || LossPct > 3; } }

        /// <summary>Local verdict using only this PC's data.</summary>
        public void Classify()
        {
            if (!Laggy) { Cause = "ok"; return; }
            bool wifiBad = RouterP95 > 20 || RouterFails > 0;
            if (Signal >= 0 && wifiBad)
            {
                if (Signal < 50) Cause = "wifi_senal_debil";
                else if (SameCh + Math.Max(0, Overlap) >= 3) Cause = "wifi_congestion_vecinos";
                else Cause = "wifi";
                return;
            }
            if (Stalls > 0 || CpuSys > 90) { Cause = "pc_ocupada"; return; }
            Cause = "otra_pc_o_router";
        }

        public string ToCsv()
        {
            return string.Join(";", new[]
            {
                Time.ToString("yyyy-MM-dd HH:mm:ss", Inv), Pc.Replace(";", ","), D(ActiveS), I(Crossings), D(RttAvg), D(RttP95), D(RttMax), I(Spikes), D(LossPct), I(Packets),
                D(RouterAvg), D(RouterP95), D(RouterMax), I(RouterFails), I(Signal), Band.Replace(";", ","), I(Channel), D(RxMbps), I(SameCh), I(Overlap), I(NeighborMax),
                D(CpuSys), D(CpuApp), I(Stalls), I(SlowHooks), I(Blocked), Cause
            });
        }

        public static MinuteRow Parse(string line)
        {
            var f = line.Split(';');
            if (f.Length < 27) return null;
            Func<int, double> d = i => { double v; return double.TryParse(f[i], NumberStyles.Float, Inv, out v) ? v : -1; };
            Func<int, int> n = i => { int v; return int.TryParse(f[i], NumberStyles.Integer, Inv, out v) ? v : -1; };
            DateTime t;
            if (!DateTime.TryParseExact(f[0], "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.None, out t)) return null;
            return new MinuteRow
            {
                Time = t, Pc = f[1], ActiveS = d(2), Crossings = n(3), RttAvg = d(4), RttP95 = d(5), RttMax = d(6), Spikes = n(7), LossPct = d(8), Packets = n(9),
                RouterAvg = d(10), RouterP95 = d(11), RouterMax = d(12), RouterFails = n(13), Signal = n(14), Band = f[15], Channel = n(16), RxMbps = d(17),
                SameCh = n(18), Overlap = n(19), NeighborMax = n(20), CpuSys = d(21), CpuApp = d(22), Stalls = n(23), SlowHooks = n(24), Blocked = n(25), Cause = f[26]
            };
        }
    }

    public static class Metrics
    {
        public static readonly string FilePath = Path.Combine(Config.Dir, "metricas.csv");
        static readonly object gate = new object();

        public static void Append(string csvLine)
        {
            lock (gate)
            {
                try
                {
                    bool fresh = !File.Exists(FilePath);
                    using (var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (var w = new StreamWriter(fs, new UTF8Encoding(true)))
                    {
                        if (fresh) w.WriteLine(MinuteRow.Header);
                        w.WriteLine(csvLine);
                    }
                }
                catch (Exception ex) { Log.Error(ex, "metrics"); }
            }
        }

        public static List<MinuteRow> Load(DateTime day)
        {
            var rows = new List<MinuteRow>();
            try
            {
                if (!File.Exists(FilePath)) return rows;
                using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var r = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        var m = MinuteRow.Parse(line);
                        if (m != null && m.Time.Date == day.Date) rows.Add(m);
                    }
                }
            }
            catch (Exception ex) { Log.Error(ex, "metrics load"); }
            return rows;
        }

        // ------------------------------------------------------------ daily report

        static readonly Dictionary<string, string> CauseText = new Dictionary<string, string>
        {
            { "wifi_congestion_vecinos", "WiFi saturado por vecinos" },
            { "wifi_senal_debil", "Señal WiFi débil" },
            { "wifi", "WiFi (otro motivo)" },
            { "pc_ocupada", "PC ocupada (CPU / suspensión)" },
            { "otra_pc_o_router", "Router u otra causa" },
        };
        static readonly Dictionary<string, string> CauseColor = new Dictionary<string, string>
        {
            { "wifi_congestion_vecinos", "#ff8a4c" }, { "wifi_senal_debil", "#ffb547" }, { "wifi", "#f5d05b" },
{ "pc_ocupada", "#9b7bff" }, { "otra_pc_o_router", "#8c93a6" },
        };
        static readonly string[] CauseOrder = { "wifi_congestion_vecinos", "wifi_senal_debil", "wifi", "pc_ocupada", "otra_pc_o_router" };

        /// <summary>For each minute, combine both PCs' rows and pick the most specific cause.</summary>
        static string MinuteCause(List<MinuteRow> rows)
        {
            if (!rows.Any(r => r.Laggy)) return "ok";
            foreach (var c in CauseOrder) if (rows.Any(r => r.Cause == c)) return c;
            return "otra_pc_o_router";
        }

        static string H(string s) { return System.Net.WebUtility.HtmlEncode(s ?? ""); }

        public static string BuildReport(DateTime day)
        {
            var rows = Load(day);
            var inv = CultureInfo.InvariantCulture;
            var byMinute = rows.GroupBy(r => new DateTime(r.Time.Year, r.Time.Month, r.Time.Day, r.Time.Hour, r.Time.Minute, 0)).OrderBy(g => g.Key).ToList();
            var minutes = byMinute.Select(g => new { T = g.Key, Rows = g.ToList(), Active = g.Max(r => r.ActiveS) >= 5, Cause = MinuteCause(g.ToList()) }).ToList();
            var wifiRows = rows.Where(r => r.Signal >= 0).ToList();
            string wifiPc = wifiRows.Count > 0 ? wifiRows.GroupBy(r => r.Pc).OrderByDescending(g => g.Count()).First().Key : null;

            int total = minutes.Count, activeMin = minutes.Count(m => m.Active);
            var lagActive = minutes.Where(m => m.Active && m.Cause != "ok").ToList();
            var lagAll = minutes.Where(m => m.Cause != "ok").ToList();
            var basis = lagActive.Count > 0 ? lagActive : lagAll;

            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang='es'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Cruce · Reporte</title><style>");
            sb.Append(":root{--bg:#101218;--card:#1a1d26;--line:#2a2f3b;--tx:#eceef4;--mu:#9097aa;--ac:#5b8cff;--ok:#3ddc97}");
            sb.Append("body{margin:0;background:var(--bg);color:var(--tx);font:14px/1.5 'Segoe UI',system-ui,sans-serif}main{max-width:1000px;margin:0 auto;padding:24px 16px 48px}");
            sb.Append("h1{font-size:26px;margin:0 0 4px}h2{font-size:13px;letter-spacing:.06em;color:var(--mu);font-weight:600;margin:28px 0 10px;text-transform:uppercase}");
            sb.Append(".sub{color:var(--mu)}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px}.card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:14px 16px}");
            sb.Append(".k{color:var(--mu);font-size:12px}.v{font-size:26px;font-weight:600}.verdict{background:var(--card);border:1px solid var(--line);border-left:4px solid var(--ac);border-radius:12px;padding:14px 16px;font-size:15px}");
            sb.Append("table{width:100%;border-collapse:collapse;font-variant-numeric:tabular-nums}th,td{text-align:right;padding:7px 8px;border-bottom:1px solid var(--line)}th:first-child,td:first-child{text-align:left}th{color:var(--mu);font-weight:600;font-size:12px}");
            sb.Append(".legend{display:flex;flex-wrap:wrap;gap:14px;color:var(--mu);font-size:12px;margin-top:8px}.sw{display:inline-block;width:10px;height:10px;border-radius:3px;margin-right:6px;vertical-align:-1px}");
            sb.Append(".scroll{overflow-x:auto}svg text{fill:var(--mu);font-size:11px}</style></head><body><main>");
            sb.AppendFormat("<h1>Reporte de Cruce</h1><div class='sub'>{0} · generado {1}</div>", day.ToString("dddd d 'de' MMMM yyyy", new CultureInfo("es-AR")), DateTime.Now.ToString("HH:mm"));

            if (total == 0)
            {
                sb.Append("<p class='verdict'>Todavía no hay datos de este día. Las dos PCs tienen que estar conectadas: se registra un resumen por minuto.</p></main></body></html>");
                return sb.ToString();
            }

            // verdict
            sb.Append("<h2>Veredicto</h2><div class='verdict'>");
            if (basis.Count == 0) sb.Append("Sin lag registrado: la conexión anduvo bien todo el día. ✓");
            else
            {
                var top = basis.GroupBy(m => m.Cause).OrderByDescending(g => g.Count()).First();
                int wifiLag = basis.Count(m => m.Cause.StartsWith("wifi"));
                sb.AppendFormat("Hubo <b>{0} minuto(s) con lag</b>{1}. ", basis.Count, lagActive.Count > 0 ? " mientras usabas Cruce" : "");
                sb.AppendFormat("<b>{0:0}%</b> fue por el WiFi{1}. ", 100.0 * wifiLag / basis.Count, wifiPc != null ? " de " + H(wifiPc) : "");
                sb.AppendFormat("Causa principal: <b>{0}</b> ({1:0}%).", H(CauseText.ContainsKey(top.Key) ? CauseText[top.Key] : top.Key), 100.0 * top.Count() / basis.Count);
                var worst = basis.GroupBy(m => m.T.Hour).OrderByDescending(g => g.Count()).First();
                sb.AppendFormat(" La peor hora fue <b>{0:00}:00–{0:00}:59</b> ({1} min con lag).", worst.Key, worst.Count());
                if (top.Key == "wifi_congestion_vecinos") sb.Append("<br><br>Cambiá el canal del router (o pasá a 5 GHz): tu canal está lleno de redes vecinas.");
                else if (top.Key == "wifi_senal_debil") sb.Append("<br><br>La señal llega débil: acercá la notebook al router o usá un repetidor / 5 GHz.");
            }
            sb.Append("</div>");

            // cards
            var linkRows = rows.Where(r => r.RttP95 >= 0).ToList();
            sb.Append("<h2>Resumen</h2><div class='cards'>");
            Action<string, string> card = (k, v) => sb.AppendFormat("<div class='card'><div class='k'>{0}</div><div class='v'>{1}</div></div>", k, v);
            card("Minutos conectadas", total.ToString());
            card("Minutos usando la otra PC", activeMin.ToString());
            card("Minutos con lag", lagAll.Count.ToString());
            card("Latencia típica (p95)", linkRows.Count > 0 ? Median(linkRows.Select(r => r.RttP95)).ToString("0", inv) + " ms" : "–");
            if (wifiRows.Count > 0)
            {
                card("Señal WiFi promedio", wifiRows.Where(r => r.Signal >= 0).Average(r => r.Signal).ToString("0", inv) + "%");
                var nb = wifiRows.Where(r => r.SameCh >= 0).ToList();
                card("Redes vecinas en tu canal", nb.Count > 0 ? nb.Average(r => r.SameCh + Math.Max(0, r.Overlap)).ToString("0.#", inv) : "–");
                var ch = wifiRows.Where(r => r.Channel > 0).GroupBy(r => r.Channel + " · " + r.Band).OrderByDescending(g => g.Count()).FirstOrDefault();
                if (ch != null) card("Canal / banda", H(ch.Key));
            }
            sb.Append("</div>");

            // hourly chart: stacked lag minutes by cause + p95 line
            var hours = minutes.GroupBy(m => m.T.Hour).OrderBy(g => g.Key).ToList();
            int h0 = hours.First().Key, h1 = hours.Last().Key, nh = h1 - h0 + 1;
            int maxLag = Math.Max(1, hours.Max(g => g.Count(m => m.Cause != "ok")));
            double W = Math.Max(600, nh * 44), Hc = 220, pad = 30, padTop = 12;
            double bw = (W - pad) / nh;
            sb.Append("<h2>Lag por hora</h2><div class='card scroll'>");
            sb.AppendFormat(inv, "<svg width='{0}' height='{1}' viewBox='0 {2} {0} {1}' role='img' aria-label='Minutos con lag por hora'>", W, Hc + 24 + padTop, -padTop);
            for (int gl = 0; gl <= 4; gl++)
            {
                double y = Hc - gl * Hc / 4.0;
                sb.AppendFormat(inv, "<line x1='{0}' x2='{1}' y1='{2}' y2='{2}' stroke='#2a2f3b'/><text x='0' y='{3}'>{4}</text>", pad, W, y, y + 4, Math.Round(maxLag * gl / 4.0));
            }
            foreach (var g in hours)
            {
                double x = pad + (g.Key - h0) * bw + bw * 0.18, bwi = bw * 0.64, y = Hc;
                foreach (var c in CauseOrder)
                {
                    int cnt = g.Count(m => m.Cause == c);
                    if (cnt == 0) continue;
                    double hgt = cnt * Hc / maxLag;
                    y -= hgt;
                    sb.AppendFormat(inv, "<rect x='{0}' y='{1}' width='{2}' height='{3}' rx='3' fill='{4}'><title>{5:00}h · {6}: {7} min</title></rect>", x, y, bwi, Math.Max(0, hgt - 1), CauseColor[c], g.Key, H(CauseText[c]), cnt);
                }
                sb.AppendFormat(inv, "<text x='{0}' y='{1}' text-anchor='middle'>{2:00}h</text>", x + bwi / 2, Hc + 18, g.Key);
            }
            sb.Append("</svg><div class='legend'>");
            foreach (var c in CauseOrder) sb.AppendFormat("<span><span class='sw' style='background:{0}'></span>{1}</span>", CauseColor[c], H(CauseText[c]));
            sb.Append("</div></div>");

            // hourly table
            sb.Append("<h2>Detalle por hora</h2><div class='card scroll'><table><tr><th>Hora</th><th>Min. conectadas</th><th>Min. usando</th><th>Min. con lag</th><th>Latencia p95</th><th>Pico máx</th><th>Ping router (WiFi) p95</th><th>Señal</th><th>Vecinos en canal</th><th>Causa principal</th></tr>");
            foreach (var g in hours)
            {
                var hr = g.SelectMany(m => m.Rows).ToList();
                var lk = hr.Where(r => r.RttP95 >= 0).ToList();
                var wr = hr.Where(r => r.Signal >= 0).ToList();
                var lagm = g.Where(m => m.Cause != "ok").ToList();
                string main = lagm.Count > 0 ? CauseText[lagm.GroupBy(m => m.Cause).OrderByDescending(x => x.Count()).First().Key] : "—";
                sb.AppendFormat(inv, "<tr><td>{0:00}:00</td><td>{1}</td><td>{2}</td><td>{3}</td><td>{4}</td><td>{5}</td><td>{6}</td><td>{7}</td><td>{8}</td><td>{9}</td></tr>",
                    g.Key, g.Count(), g.Count(m => m.Active), lagm.Count,
                    lk.Count > 0 ? Median(lk.Select(r => r.RttP95)).ToString("0", inv) + " ms" : "–",
                    lk.Count > 0 ? lk.Max(r => r.RttMax).ToString("0", inv) + " ms" : "–",
                    wr.Any(r => r.RouterP95 >= 0) ? Median(wr.Where(r => r.RouterP95 >= 0).Select(r => r.RouterP95)).ToString("0", inv) + " ms" : "–",
                    wr.Count > 0 ? wr.Average(r => r.Signal).ToString("0", inv) + "%" : "–",
                    wr.Any(r => r.SameCh >= 0) ? wr.Where(r => r.SameCh >= 0).Average(r => r.SameCh + Math.Max(0, r.Overlap)).ToString("0.#", inv) : "–",
                    H(main));
            }
            sb.Append("</table></div>");
            sb.Append("<p class='sub'>Cómo se decide la causa: si cuando hay lag también sube el ping de la notebook a su router, el problema es el tramo WiFi (señal débil si la señal está por debajo del 50%; vecinos si hay 3 o más redes en tu canal o en canales que se pisan). Si el WiFi está bien pero la PC estaba saturada o suspendida, es la PC. Datos crudos: metricas.csv.</p>");
            sb.Append("</main></body></html>");
            return sb.ToString();
        }

        static double Median(IEnumerable<double> v)
        {
            var s = v.OrderBy(x => x).ToList();
            return s.Count == 0 ? 0 : s[s.Count / 2];
        }

        public static string WriteReport(DateTime day)
        {
            var path = Path.Combine(Config.Dir, "reporte-" + day.ToString("yyyy-MM-dd") + ".html");
            File.WriteAllText(path, BuildReport(day), new UTF8Encoding(false));
            return path;
        }
    }
}
