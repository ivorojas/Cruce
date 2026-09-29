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
        // v1.8
        public double OwdP50 = -1, OwdP95 = -1, OwdMax = -1, CaptureP95 = -1, HookDelayP95 = -1, InjectP95Us = -1, StutterMaxMs, RssiAvg = 0, RssiMin = 0, WifiRetryPct = -1;
        public double NetRxKBs = -1, NetTxKBs = -1, NetPeakKBs = -1, InetAvg = -1, InetP95 = -1, CpuMhzPct = -1, UiHangMs;
        public int Stutters, WifiFailures = -1, WifiScans = -1, WifiRoams = -1, WifiDisconnects = -1, WifiEvents = -1, InetFails, Incidents, LowLatency = -1;
        public string TopCpu = "";
        // v1.9: power
        public string Power = "", PowerMode = "", Plan = "";
        public int BatteryPct = -1, Saver = -1;

        public const string Header = "fecha_hora;pc;activo_s;cruces;rtt_prom_ms;rtt_p95_ms;rtt_max_ms;picos_30ms;perdida_pct;paquetes;router_prom_ms;router_p95_ms;router_max_ms;router_fallas;wifi_senal_pct;wifi_banda;wifi_canal;wifi_rx_mbps;redes_mismo_canal;redes_solapadas;vecino_mas_fuerte;cpu_sistema_pct;cpu_cruce_pct;frenadas;hook_lento;bloqueos_windows;causa"
            + ";tramo_red_p50_ms;tramo_red_p95_ms;tramo_red_max_ms;captura_p95_ms;demora_windows_p95_ms;aplicar_p95_us;tirones;tiron_max_ms;wifi_rssi_prom_dbm;wifi_rssi_min_dbm;wifi_reintentos_pct;wifi_fallas_tx;wifi_escaneos;wifi_roaming;wifi_desconexiones;wifi_eventos;red_bajada_kbs;red_subida_kbs;red_pico_kbs;internet_prom_ms;internet_p95_ms;internet_fallas;cpu_frecuencia_pct;top_cpu;incidentes;ui_colgada_ms;wifi_baja_latencia;energia;bateria_pct;ahorro_bateria;modo_energia;plan_energia";

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string D(double v) { return v < 0 ? "" : v.ToString("0.##", Inv); }
        static string Dn(double v) { return v == 0 ? "" : v.ToString("0.#", Inv); }
        static string I(int v) { return v < 0 ? "" : v.ToString(Inv); }
        static string S(string s) { return (s ?? "").Replace(";", ",").Replace("\n", " "); }

        public bool OnWifi { get { return Signal >= 0 || RssiAvg != 0; } }
        public double Traffic { get { return Math.Max(0, NetRxKBs) + Math.Max(0, NetTxKBs); } }
        public bool Laggy { get { return RttP95 > 40 || Spikes >= 5 || LossPct > 3 || Stutters >= 3 || StutterMaxMs > 100; } }

        /// <summary>Local verdict using only this PC's data (the report later merges both PCs per minute).</summary>
        public void Classify()
        {
            if (!Laggy) { Cause = "ok"; return; }
            if (OnWifi)
            {
                bool wifiBad = RouterP95 > 20 || RouterFails > 0 || WifiRetryPct > 25 || WifiDisconnects > 0;
                if (WifiDisconnects > 0 || WifiRoams > 0) { Cause = "wifi_desconexion"; return; }
                if (wifiBad && Traffic > 1500) { Cause = "wifi_saturado_descargas"; return; }
                if (wifiBad && WifiScans > 0) { Cause = "wifi_escaneo"; return; }
                if (wifiBad && ((Signal >= 0 && Signal < 50) || (RssiAvg != 0 && RssiAvg < -72))) { Cause = "wifi_senal_debil"; return; }
                if (wifiBad && SameCh + Math.Max(0, Overlap) >= 3) { Cause = "wifi_congestion_vecinos"; return; }
                if (wifiBad) { Cause = "wifi"; return; }
            }
            else if (RouterP95 > 10 || RouterFails > 0) { Cause = "router_saturado"; return; }
            if (Stalls > 0 || CpuSys > 90 || UiHangMs > 500) { Cause = "pc_ocupada"; return; }
            Cause = "otra_pc_o_router";
        }

        public string ToCsv()
        {
            return string.Join(";", new[]
            {
                Time.ToString("yyyy-MM-dd HH:mm:ss", Inv), S(Pc), D(ActiveS), I(Crossings), D(RttAvg), D(RttP95), D(RttMax), I(Spikes), D(LossPct), I(Packets),
                D(RouterAvg), D(RouterP95), D(RouterMax), I(RouterFails), I(Signal), S(Band), I(Channel), D(RxMbps), I(SameCh), I(Overlap), I(NeighborMax),
                D(CpuSys), D(CpuApp), I(Stalls), I(SlowHooks), I(Blocked), Cause,
                D(OwdP50), D(OwdP95), D(OwdMax), D(CaptureP95), D(HookDelayP95), D(InjectP95Us), I(Stutters), D(StutterMaxMs), Dn(RssiAvg), Dn(RssiMin), D(WifiRetryPct),
                I(WifiFailures), I(WifiScans), I(WifiRoams), I(WifiDisconnects), I(WifiEvents), D(NetRxKBs), D(NetTxKBs), D(NetPeakKBs), D(InetAvg), D(InetP95), I(InetFails),
                D(CpuMhzPct), S(TopCpu), I(Incidents), D(UiHangMs), I(LowLatency),
                S(Power), I(BatteryPct), I(Saver), S(PowerMode), S(Plan)
            });
        }

        public static MinuteRow Parse(string line)
        {
            var f = line.Split(';');
            if (f.Length < 27) return null;
            Func<int, double> d = i => { double v; return i < f.Length && double.TryParse(f[i], NumberStyles.Float, Inv, out v) ? v : -1; };
            Func<int, double> dn = i => { double v; return i < f.Length && double.TryParse(f[i], NumberStyles.Float, Inv, out v) ? v : 0; };
            Func<int, int> n = i => { int v; return i < f.Length && int.TryParse(f[i], NumberStyles.Integer, Inv, out v) ? v : -1; };
            DateTime t;
            if (!DateTime.TryParseExact(f[0], "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.None, out t)) return null;
            var m = new MinuteRow
            {
                Time = t, Pc = f[1], ActiveS = d(2), Crossings = n(3), RttAvg = d(4), RttP95 = d(5), RttMax = d(6), Spikes = n(7), LossPct = d(8), Packets = n(9),
                RouterAvg = d(10), RouterP95 = d(11), RouterMax = d(12), RouterFails = n(13), Signal = n(14), Band = f[15], Channel = n(16), RxMbps = d(17),
                SameCh = n(18), Overlap = n(19), NeighborMax = n(20), CpuSys = d(21), CpuApp = d(22), Stalls = n(23), SlowHooks = n(24), Blocked = n(25), Cause = f[26]
            };
            if (f.Length > 27)
            {
                m.OwdP50 = d(27); m.OwdP95 = d(28); m.OwdMax = d(29); m.CaptureP95 = d(30); m.HookDelayP95 = d(31); m.InjectP95Us = d(32);
                m.Stutters = Math.Max(0, n(33)); m.StutterMaxMs = Math.Max(0, d(34)); m.RssiAvg = dn(35); m.RssiMin = dn(36); m.WifiRetryPct = d(37);
                m.WifiFailures = n(38); m.WifiScans = n(39); m.WifiRoams = n(40); m.WifiDisconnects = n(41); m.WifiEvents = n(42);
                m.NetRxKBs = d(43); m.NetTxKBs = d(44); m.NetPeakKBs = d(45); m.InetAvg = d(46); m.InetP95 = d(47); m.InetFails = Math.Max(0, n(48));
                m.CpuMhzPct = d(49); m.TopCpu = f.Length > 50 ? f[50] : ""; m.Incidents = Math.Max(0, n(51)); m.UiHangMs = Math.Max(0, d(52)); m.LowLatency = n(53);
            }
            if (f.Length > 54)
            {
                m.Power = f[54]; m.BatteryPct = n(55); m.Saver = n(56); m.PowerMode = f.Length > 57 ? f[57] : ""; m.Plan = f.Length > 58 ? f[58] : "";
            }
            return m;
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
                    if (!fresh)
                    {
                        string first;
                        using (var r = new StreamReader(new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8)) first = r.ReadLine() ?? "";
                        if (first != MinuteRow.Header)
                        {
                            var all = File.ReadAllLines(FilePath, Encoding.UTF8).ToList();
                            if (all.Count > 0 && all[0].StartsWith("fecha_hora")) all[0] = MinuteRow.Header; else all.Insert(0, MinuteRow.Header);
                            File.WriteAllLines(FilePath, all, new UTF8Encoding(true));
                        }
                    }
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

        static readonly string[] CauseOrder = { "wifi_desconexion", "wifi_saturado_descargas", "wifi_escaneo", "wifi_congestion_vecinos", "wifi_senal_debil", "wifi", "router_saturado", "pc_ocupada", "otra_pc_o_router" };
        static readonly Dictionary<string, string> CauseText = new Dictionary<string, string>
        {
            { "wifi_desconexion", "WiFi se cortó / cambió de antena" },
            { "wifi_saturado_descargas", "WiFi saturado por descargas" },
            { "wifi_escaneo", "Escaneo del WiFi de Windows" },
            { "wifi_congestion_vecinos", "WiFi saturado por vecinos" },
            { "wifi_senal_debil", "Señal WiFi débil" },
            { "wifi", "WiFi (otro motivo)" },
            { "router_saturado", "Router saturado" },
            { "pc_ocupada", "PC ocupada (CPU / suspensión)" },
            { "otra_pc_o_router", "Otra causa" },
        };
        static readonly Dictionary<string, string> CauseColor = new Dictionary<string, string>
        {
            { "wifi_desconexion", "#ff6b6b" }, { "wifi_saturado_descargas", "#ff9f43" }, { "wifi_escaneo", "#ffd166" },
            { "wifi_congestion_vecinos", "#ff8a4c" }, { "wifi_senal_debil", "#ffb547" }, { "wifi", "#f5d05b" },
            { "router_saturado", "#4cc9f0" }, { "pc_ocupada", "#9b7bff" }, { "otra_pc_o_router", "#8c93a6" },
        };
        static readonly Dictionary<string, string> Advice = new Dictionary<string, string>
        {
            { "wifi_desconexion", "El WiFi de la notebook se corta o salta entre antenas: acercala al router o fijá una sola red/banda." },
            { "wifi_saturado_descargas", "Coincide con descargas o sincronizaciones (OneDrive, Windows Update, Steam...). Pausalas mientras usás Cruce." },
            { "wifi_escaneo", "Windows escanea redes y el WiFi se va unos cientos de ms. Cruce ya pide el modo baja latencia; si sigue, desactivá la búsqueda automática de redes." },
            { "wifi_congestion_vecinos", "Tu canal está lleno de redes vecinas: cambiá el canal del router (1, 6 u 11, el más libre) o pasá a 5 GHz." },
            { "wifi_senal_debil", "La señal llega débil: acercá la notebook al router, usá un repetidor o 5 GHz." },
            { "router_saturado", "El router está saturado (alguien descargando/subiendo mucho): probá QoS en el router o cable." },
            { "pc_ocupada", "Una de las PCs estaba al límite de CPU o suspendida: mirá la columna de procesos." },
        };

        static string CauseName(string c) { string t; return L.T(CauseText.TryGetValue(c, out t) ? t : "WiFi (otro motivo)"); }
        static string CauseCol(string c) { string t; return CauseColor.TryGetValue(c, out t) ? t : "#f5d05b"; }

        static string MinuteCause(List<MinuteRow> rows)
        {
            if (!rows.Any(r => r.Laggy)) return "ok";
            foreach (var c in CauseOrder) if (rows.Any(r => r.Cause == c)) return c;
            if (rows.Any(r => r.Cause.StartsWith("wifi"))) return "wifi";
            return "otra_pc_o_router";
        }

        static string H(string s) { return System.Net.WebUtility.HtmlEncode(s ?? ""); }
        static double Median(IEnumerable<double> v) { var s = v.OrderBy(x => x).ToList(); return s.Count == 0 ? 0 : s[s.Count / 2]; }
        static string Ms(IEnumerable<double> v) { var l = v.Where(x => x >= 0).ToList(); return l.Count > 0 ? Median(l).ToString("0.#", CultureInfo.InvariantCulture) + " ms" : "–"; }

        public static string BuildReport(DateTime day)
        {
            var rows = Load(day);
            var inv = CultureInfo.InvariantCulture;
            var minutes = rows.GroupBy(r => new DateTime(r.Time.Year, r.Time.Month, r.Time.Day, r.Time.Hour, r.Time.Minute, 0)).OrderBy(g => g.Key)
                .Select(g => new { T = g.Key, Rows = g.ToList(), Active = g.Max(r => r.ActiveS) >= 5, Cause = MinuteCause(g.ToList()) }).ToList();
            var wifiRows = rows.Where(r => r.OnWifi).ToList();
            string wifiPc = wifiRows.Count > 0 ? wifiRows.GroupBy(r => r.Pc).OrderByDescending(g => g.Count()).First().Key : null;

            int total = minutes.Count, activeMin = minutes.Count(m => m.Active);
            var lagActive = minutes.Where(m => m.Active && m.Cause != "ok").ToList();
            var lagAll = minutes.Where(m => m.Cause != "ok").ToList();
            var basis = lagActive.Count > 0 ? lagActive : lagAll;

            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang='" + (L.English ? "en" : "es") + "'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>" + L.T("Cruce · Reporte") + "</title><style>");
            sb.Append(":root{--bg:#101218;--card:#1a1d26;--line:#2a2f3b;--tx:#eceef4;--mu:#9097aa;--ac:#5b8cff;--ok:#3ddc97}");
            sb.Append("body{margin:0;background:var(--bg);color:var(--tx);font:14px/1.5 'Segoe UI',system-ui,sans-serif}main{max-width:1180px;margin:0 auto;padding:24px 16px 48px}");
            sb.Append("h1{font-size:26px;margin:0 0 4px}h2{font-size:13px;letter-spacing:.06em;color:var(--mu);font-weight:600;margin:28px 0 10px;text-transform:uppercase}");
            sb.Append(".sub{color:var(--mu)}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:12px}.card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:14px 16px}");
            sb.Append(".k{color:var(--mu);font-size:12px}.v{font-size:24px;font-weight:600}.verdict{background:var(--card);border:1px solid var(--line);border-left:4px solid var(--ac);border-radius:12px;padding:14px 16px;font-size:15px}");
            sb.Append("table{width:100%;border-collapse:collapse;font-variant-numeric:tabular-nums;font-size:13px}th,td{text-align:right;padding:6px 7px;border-bottom:1px solid var(--line);white-space:nowrap}th:first-child,td:first-child{text-align:left}th{color:var(--mu);font-weight:600;font-size:11.5px}");
            sb.Append(".legend{display:flex;flex-wrap:wrap;gap:14px;color:var(--mu);font-size:12px;margin-top:8px}.sw{display:inline-block;width:10px;height:10px;border-radius:3px;margin-right:6px;vertical-align:-1px}");
            sb.Append(".scroll{overflow-x:auto}svg text{fill:var(--mu);font-size:11px}ul{margin:0;padding-left:18px}li{margin:3px 0}a{color:var(--ac)}</style></head><body><main>");
            sb.AppendFormat("<h1>{2}</h1><div class='sub'>{0} · {3} {1}</div>", L.English ? day.ToString("dddd, MMMM d, yyyy", new CultureInfo("en-US")) : day.ToString("dddd d 'de' MMMM yyyy", new CultureInfo("es-AR")), DateTime.Now.ToString("HH:mm"), L.T("Reporte de Cruce"), L.T("generado"));

            if (total == 0)
            {
                sb.Append("<p class='verdict'>" + L.T("Todavía no hay datos de este día. Las dos PCs tienen que estar conectadas: se registra un resumen por minuto.") + "</p></main></body></html>");
                return sb.ToString();
            }

            sb.Append("<h2>" + L.T("Veredicto") + "</h2><div class='verdict'>");
            if (basis.Count == 0) sb.Append(L.T("Sin lag registrado: la conexión anduvo bien todo el día. ✓"));
            else
            {
                var top = basis.GroupBy(m => m.Cause).OrderByDescending(g => g.Count()).First();
                int wifiLag = basis.Count(m => m.Cause.StartsWith("wifi"));
                sb.AppendFormat(L.T("Hubo <b>{0} minuto(s) con lag</b>{1}. "), basis.Count, lagActive.Count > 0 ? L.T(" mientras usabas Cruce") : "");
                sb.AppendFormat(L.T("<b>{0:0}%</b> fue por el WiFi{1}. "), 100.0 * wifiLag / basis.Count, wifiPc != null ? L.F(" de {0}", H(wifiPc)) : "");
                sb.AppendFormat(L.T("Causa principal: <b>{0}</b> ({1:0}%)."), H(CauseName(top.Key)), 100.0 * top.Count() / basis.Count);
                var worst = basis.GroupBy(m => m.T.Hour).OrderByDescending(g => g.Count()).First();
                sb.AppendFormat(L.T(" La peor hora fue <b>{0:00}:00–{0:00}:59</b> ({1} min con lag)."), worst.Key, worst.Count());
                string adv;
                if (Advice.TryGetValue(top.Key, out adv)) sb.Append("<br><br>" + H(L.T(adv)));
            }
            sb.Append("</div>");

            var linkRows = rows.Where(r => r.RttP95 >= 0).ToList();
            sb.Append("<h2>" + L.T("Resumen") + "</h2><div class='cards'>");
            Action<string, string> card = (k, v) => sb.AppendFormat("<div class='card'><div class='k'>{0}</div><div class='v'>{1}</div></div>", L.T(k), v);
            card("Minutos conectadas", total.ToString());
            card("Minutos usando la otra PC", activeMin.ToString());
            card("Minutos con lag", lagAll.Count.ToString());
            card("Ida y vuelta típica (p95)", Ms(linkRows.Select(r => r.RttP95)));
            card("Tramo de red (solo ida) típico", Ms(rows.Select(r => r.OwdP95)));
            card("Tirones en el día", rows.Sum(r => r.Stutters).ToString());
            card("Incidentes guardados", rows.Sum(r => r.Incidents).ToString());
            if (wifiRows.Count > 0)
            {
                var rs = wifiRows.Where(r => r.RssiAvg != 0).ToList();
                var sigPct = wifiRows.Where(r => r.Signal >= 0).ToList();
                card("Señal WiFi", rs.Count > 0 ? rs.Average(r => r.RssiAvg).ToString("0", inv) + " dBm" : sigPct.Count > 0 ? sigPct.Average(r => r.Signal).ToString("0", inv) + "%" : "–");
                var nb = wifiRows.Where(r => r.SameCh >= 0).ToList();
                card("Redes vecinas en tu canal", nb.Count > 0 ? nb.Average(r => r.SameCh + Math.Max(0, r.Overlap)).ToString("0.#", inv) : "–");
                var ch = wifiRows.Where(r => r.Channel > 0).GroupBy(r => r.Channel + " · " + r.Band).OrderByDescending(g => g.Count()).FirstOrDefault();
                if (ch != null) card("Canal / banda", H(ch.Key));
                card("Escaneos del WiFi", wifiRows.Where(r => r.WifiScans > 0).Sum(r => r.WifiScans).ToString());
                var ll = wifiRows.LastOrDefault(r => r.LowLatency >= 0);
                card("Modo baja latencia", ll == null ? "–" : L.T(ll.LowLatency == 1 ? "activo ✓" : "rechazado"));
            }
            sb.Append("</div>");

            var hours = minutes.GroupBy(m => m.T.Hour).OrderBy(g => g.Key).ToList();
            int h0 = hours.First().Key, h1 = hours.Last().Key, nh = h1 - h0 + 1;
            int maxLag = Math.Max(1, hours.Max(g => g.Count(m => m.Cause != "ok")));
            double W = Math.Max(600, nh * 44), Hc = 220, pad = 30, padTop = 12;
            double bw = (W - pad) / nh;
            sb.Append("<h2>" + L.T("Lag por hora") + "</h2><div class='card scroll'>");
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
                    sb.AppendFormat(inv, "<rect x='{0}' y='{1}' width='{2}' height='{3}' rx='3' fill='{4}'><title>{5:00}h · {6}: {7} min</title></rect>", x, y, bwi, Math.Max(0, hgt - 1), CauseCol(c), g.Key, H(CauseName(c)), cnt);
                }
                sb.AppendFormat(inv, "<text x='{0}' y='{1}' text-anchor='middle'>{2:00}h</text>", x + bwi / 2, Hc + 18, g.Key);
            }
            sb.Append("</svg><div class='legend'>");
            foreach (var c in CauseOrder) sb.AppendFormat("<span><span class='sw' style='background:{0}'></span>{1}</span>", CauseCol(c), H(CauseName(c)));
            sb.Append("</div></div>");

            sb.Append("<h2>" + L.T("Detalle por hora") + "</h2><div class='card scroll'><table><tr><th>" + L.T("Hora") + "</th><th>" + L.T("Conectadas") + "</th><th>" + L.T("Usando") + "</th><th>" + L.T("Con lag") + "</th><th>" + L.T("Ida y vuelta p95") + "</th><th>" + L.T("Tramo red p95") + "</th><th>" + L.T("Tirones") + "</th><th>" + L.T("Ping router (WiFi)") + "</th><th>" + L.T("Ping internet") + "</th><th>" + L.T("Señal") + "</th><th>" + L.T("Reintentos WiFi") + "</th><th>" + L.T("Escaneos") + "</th><th>" + L.T("Vecinos") + "</th><th>" + L.T("Tráfico máx") + "</th><th>" + L.T("Energía") + "</th><th>" + L.T("Causa principal") + "</th></tr>");
            foreach (var g in hours)
            {
                var hr = g.SelectMany(m => m.Rows).ToList();
                var wr = hr.Where(r => r.OnWifi).ToList();
                var lagm = g.Where(m => m.Cause != "ok").ToList();
                string main = lagm.Count > 0 ? CauseName(lagm.GroupBy(m => m.Cause).OrderByDescending(x => x.Count()).First().Key) : "—";
                var rssi = wr.Where(r => r.RssiAvg != 0).ToList();
                string sig = rssi.Count > 0 ? rssi.Average(r => r.RssiAvg).ToString("0", inv) + " dBm" : wr.Any(r => r.Signal >= 0) ? wr.Where(r => r.Signal >= 0).Average(r => r.Signal).ToString("0", inv) + "%" : "–";
                var retr = wr.Where(r => r.WifiRetryPct >= 0).ToList();
                var pw = (wr.Count > 0 ? wr : hr).Where(r => r.Power != "").GroupBy(r => L.T((r.PowerMode ?? "").Replace('_', ' ')) + " · " + L.T(r.Power) + (r.Saver == 1 ? " · " + L.T("ahorro") : "")).OrderByDescending(x => x.Count()).FirstOrDefault();
                string energy = pw != null ? pw.Key : "–";
                sb.AppendFormat(inv, "<tr><td>{0:00}:00</td><td>{1}</td><td>{2}</td><td>{3}</td><td>{4}</td><td>{5}</td><td>{6}</td><td>{7}</td><td>{8}</td><td>{9}</td><td>{10}</td><td>{11}</td><td>{12}</td><td>{13}</td><td>{15}</td><td>{14}</td></tr>",
                    g.Key, g.Count(), g.Count(m => m.Active), lagm.Count,
                    Ms(hr.Select(r => r.RttP95)), Ms(hr.Select(r => r.OwdP95)), hr.Sum(r => r.Stutters),
                    Ms(wr.Select(r => r.RouterP95)), Ms(hr.Select(r => r.InetP95)), sig,
                    retr.Count > 0 ? retr.Average(r => r.WifiRetryPct).ToString("0", inv) + "%" : "–",
                    wr.Where(r => r.WifiScans > 0).Sum(r => r.WifiScans),
                    wr.Any(r => r.SameCh >= 0) ? wr.Where(r => r.SameCh >= 0).Average(r => r.SameCh + Math.Max(0, r.Overlap)).ToString("0.#", inv) : "–",
                    hr.Any(r => r.NetPeakKBs >= 0) ? (hr.Max(r => r.NetPeakKBs) / 1024.0).ToString("0.0", inv) + " MB/s" : "–",
                    H(main), H(energy));
            }
            sb.Append("</table></div>");

            var worstMin = minutes.Where(m => m.Cause != "ok").OrderByDescending(m => m.Rows.Max(r => Math.Max(r.RttP95, r.StutterMaxMs))).Take(12).OrderBy(m => m.T).ToList();
            if (worstMin.Count > 0)
            {
                sb.Append("<h2>" + L.T("Los peores minutos") + "</h2><div class='card scroll'><table><tr><th>" + L.T("Hora") + "</th><th>" + L.T("Causa") + "</th><th>" + L.T("Ida y vuelta p95") + "</th><th>" + L.T("Tirón máx") + "</th><th>" + L.T("Ping router") + "</th><th>" + L.T("Señal") + "</th><th>" + L.T("Tráfico") + "</th><th>" + L.T("CPU") + "</th><th>" + L.T("Procesos que más CPU usaban") + "</th></tr>");
                foreach (var m in worstMin)
                {
                    var w = m.Rows.FirstOrDefault(r => r.OnWifi) ?? m.Rows[0];
                    var busy = m.Rows.OrderByDescending(r => r.CpuSys).First();
                    double peak = m.Rows.Max(r => r.NetPeakKBs);
                    sb.AppendFormat(inv, "<tr><td>{0:HH:mm}</td><td>{1}</td><td>{2}</td><td>{3}</td><td>{4}</td><td>{5}</td><td>{6}</td><td>{7}</td><td style='text-align:left'>{8}</td></tr>",
                        m.T, H(CauseName(m.Cause)), Ms(m.Rows.Select(r => r.RttP95)), m.Rows.Max(r => r.StutterMaxMs).ToString("0", inv) + " ms",
                        w.RouterP95 >= 0 ? w.RouterP95.ToString("0", inv) + " ms" : "–", w.RssiAvg != 0 ? w.RssiAvg.ToString("0", inv) + " dBm" : "–",
                        peak >= 0 ? (peak / 1024.0).ToString("0.0", inv) + " MB/s" : "–",
                        busy.CpuSys >= 0 ? busy.CpuSys.ToString("0", inv) + "% (" + H(busy.Pc) + ")" : "–", H(busy.TopCpu));
                }
                sb.Append("</table></div>");
            }

            try
            {
                if (Directory.Exists(Flight.Dir))
                {
                    var inc = new DirectoryInfo(Flight.Dir).GetFiles(day.ToString("yyyyMMdd") + "-*.txt").OrderBy(f => f.Name).ToList();
                    if (inc.Count > 0)
                    {
                        sb.Append("<h2>" + L.T("Incidentes (detalle al milisegundo)") + "</h2><div class='card'><ul>");
                        foreach (var f in inc.Take(80))
                        {
                            var parts = Path.GetFileNameWithoutExtension(f.Name).Split('-');
                            string hhmmss = parts.Length > 1 && parts[1].Length == 6 ? parts[1].Substring(0, 2) + ":" + parts[1].Substring(2, 2) + ":" + parts[1].Substring(4, 2) : "";
                            sb.AppendFormat("<li>{0} — <a href='{1}'>{2}</a></li>", hhmmss, H(new Uri(f.FullName).AbsoluteUri), H(parts.Length > 2 ? string.Join("-", parts.Skip(2)) : f.Name));
                        }
                        sb.Append("</ul></div>");
                    }
                }
            }
            catch { }

            sb.Append("<p class='sub'>" + L.T("Cómo se decide la causa: si cuando hay lag también sube el ping de la notebook a su router o los reintentos del WiFi, el problema es el tramo WiFi; después se mira si coincidió con cortes, descargas, escaneos de Windows, señal débil o redes vecinas en tu canal. Si el WiFi está bien pero una PC estaba saturada o suspendida, es la PC. Datos crudos: metricas.csv; detalle al milisegundo: carpeta incidentes.") + "</p>");
            sb.Append("</main></body></html>");
            return sb.ToString();
        }

        public static string WriteReport(DateTime day)
        {
            var path = Path.Combine(Config.Dir, "reporte-" + day.ToString("yyyy-MM-dd") + ".html");
            File.WriteAllText(path, BuildReport(day), new UTF8Encoding(false));
            return path;
        }
    }
}
