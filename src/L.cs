using System;
using System.Collections.Generic;

namespace Cruce
{
    /// <summary>
    /// UI language. The source texts are Spanish; English (the default) comes from this table.
    /// Internal logs stay in Spanish on purpose (they are diagnostics, not UI).
    /// </summary>
    public static class L
    {
        public static volatile bool English = true;
        public static event Action Changed;

        public static void Set(bool english)
        {
            English = english;
            var c = Changed;
            if (c != null) c();
        }

        public static string T(string es)
        {
            if (!English || string.IsNullOrEmpty(es)) return es;
            string v;
            return D.TryGetValue(es, out v) ? v : es;
        }

        public static string F(string es, params object[] args) { return string.Format(T(es), args); }

        /// <summary>Network labels travel between PCs in Spanish ("WiFi 5 GHz · canal 157"); localize on display.</summary>
        public static string Net(string label)
        {
            if (!English || string.IsNullOrEmpty(label)) return label;
            return label.Replace(" · canal ", " · ch ").Replace("2,4 GHz", "2.4 GHz").Replace("WiFi", "Wi-Fi").Replace("Cable", "Wired");
        }

        static readonly Dictionary<string, string> D = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ---- main window (static XAML)
            { "Un mouse y un teclado para tus dos PCs", "One mouse and keyboard for both your PCs" },
            { "Buscando la otra PC…", "Looking for the other PC…" },
            { "DISPOSICIÓN", "LAYOUT" },
            { "Estás usando esta PC", "You're using this PC" },
            { "¿Dónde está la otra PC?", "Where is the other PC?" },
            { "Se sincroniza sola en las dos PCs.", "Syncs automatically on both PCs." },
            { "←  Izquierda", "←  Left" },
            { "Derecha  →", "Right  →" },
            { "↑  Arriba", "↑  Top" },
            { "↓  Abajo", "↓  Bottom" },
            { "LATENCIA", "LATENCY" },
            { "ida y vuelta", "round trip" },
            { "PICOS (P95)", "SPIKES (P95)" },
            { "máximo –", "max –" },
            { "PÉRDIDA", "LOSS" },
            { "se recupera sola", "self-healing" },
            { "ÚLTIMOS 60 S", "LAST 60 S" },
            { " paquetes/s  ·  ", " packets/s  ·  " },
            { "prioridad de voz: –", "voice priority: –" },
            { "AJUSTES", "SETTINGS" },
            { "Clave compartida", "Shared key" },
            { "Ver", "Show" },
            { "Ocultar", "Hide" },
            { "Poné la misma clave en las dos PCs. Todo viaja cifrado con ella.", "Use the same key on both PCs. Everything is encrypted with it." },
            { "Elegí una clave y poné la misma en las dos PCs.", "Pick a key and use the same one on both PCs." },
            { "IP de la otra PC (opcional)", "Other PC's IP (optional)" },
            { "Dejalo vacío: se encuentran solas en tu red.", "Leave it empty: they find each other on your network." },
            { "Velocidad del puntero en la otra PC", "Pointer speed on the other PC" },
            { "1.00× ya compensa la escala (DPI) de cada pantalla.", "1.00× already compensates each screen's scaling (DPI)." },
            { "Portapapeles compartido (texto e imágenes)", "Shared clipboard (text and images)" },
            { "Copiar y pegar archivos entre PCs", "Copy and paste files between PCs" },
            { "Iniciar con Windows (con permisos de admin)", "Start with Windows (with admin rights)" },
            { "Reiniciar como admin", "Restart as admin" },
            { "cambia de PC", "switches PC" },
            { "Reporte", "Report" },
            { "Abrir registro", "Open log" },
            { "Buscar actualizaciones", "Check for updates" },
            { "Ajustes", "Settings" },
            { "Idioma", "Language" },

            // ---- main window (dynamic)
            { "En pausa", "Paused" },
            { "Conectado con {0}", "Connected to {0}" },
            { "Abrí Cruce en la otra PC con la misma clave", "Open Cruce on the other PC with the same key" },
            { "Estás usando {0}", "You're using {0}" },
            { "{0} está usando esta PC", "{0} is using this PC" },
            { "{0} corre sin admin (no maneja ventanas de admin).", "{0} runs without admin (can't drive admin windows)." },
            { "máximo {0} ms", "max {0} ms" },
            { "prioridad de voz: {0}", "voice priority: {0}" },
            { "activa", "on" },
            { "no disponible", "unavailable" },
            { "Corriendo con permisos de admin ✓", "Running with admin rights ✓" },
            { "Sin permisos de admin: no controla ventanas de administrador en esta PC.", "No admin rights: can't control admin windows on this PC." },
            { "sin red", "no network" },
            { "ESTA PC", "THIS PC" },
            { "OTRA PC", "OTHER PC" },
            { "sin conectar", "not connected" },
            { "No se pudo capturar el mouse/teclado", "Couldn't capture the mouse/keyboard" },
            { "Falta elegir la clave", "Choose a key first" },
            { "Ningún puerto de red disponible ({0})", "No network port available ({0})" },
            { "Error de red: {0}", "Network error: {0}" },

            // ---- tray and notifications
            { "Abrir Cruce", "Open Cruce" },
            { "Pausar el cruce", "Pause crossing" },
            { "Reanudar el cruce", "Resume crossing" },
            { "Salir", "Exit" },
            { "Se desconectó {0}", "{0} disconnected" },

            // ---- clipboard, files and drag & drop
            { "Archivos demasiado grandes para el portapapeles (más de 2 GB): arrastralos a la otra pantalla.", "Files too big for the clipboard (over 2 GB): drag them to the other screen instead." },
            { "Enviando archivos… {0:0}%", "Sending files… {0:0}%" },
            { "Recibiendo archivos de la otra PC…", "Receiving files from the other PC…" },
            { "Recibiendo archivos… {0:0}%", "Receiving files… {0:0}%" },
            { "Archivos listos: pegalos con Ctrl+V", "Files ready: paste them with Ctrl+V" },
            { "Recibiendo {0}", "Receiving {0}" },
            { "{0:0}% · {1} de {2} · {3}/s · faltan {4:0} s", "{0:0}% · {1} of {2} · {3}/s · {4:0} s left" },
            { "Listo ✓  {0}", "Done ✓  {0}" },
            { "en {0} · {1} en {2} s", "in {0} · {1} in {2} s" },
            { "el escritorio", "the desktop" },
            { "Descargas", "Downloads" },
            { "No se pudo copiar {0}", "Couldn't copy {0}" },
            { "{0} elementos", "{0} items" },
            { "Preparando la copia…", "Preparing the copy…" },
            { "Ahí no hay una carpeta: va a Descargas", "No folder there: it goes to Downloads" },

            // ---- updates and startup
            { "Buscando actualizaciones…", "Checking for updates…" },
            { "Estás en la última versión ({0})", "You're on the latest version ({0})" },
            { "Hay una versión nueva: {0}", "New version available: {0}" },
            { "Descargando {0}…", "Downloading {0}…" },
            { "Actualización lista: se instala cuando vuelvas a esta PC", "Update ready: it installs when you come back to this PC" },
            { "Instalando {0}…", "Installing {0}…" },
            { "No se encontró el repositorio (¿es privado?)", "Repository not found (is it private?)" },
            { "Sin conexión a GitHub", "Can't reach GitHub" },
            { "Error al actualizar: {0}", "Update error: {0}" },
            { "No se pudo crear la tarea de inicio (código {0}).", "Couldn't create the startup task (code {0})." },
            { "No se pudo quitar la tarea (código {0}).", "Couldn't remove the startup task (code {0})." },
            { "Cancelado.", "Cancelled." },

            // ---- Wi-Fi band keeper
            { "Buscando la red de 5 GHz…", "Looking for the 5 GHz network…" },
            { "No encuentro la red de 5 GHz: sigo en 2,4", "Can't find the 5 GHz network: staying on 2.4" },
            { "La red de 5 GHz llega muy débil: sigo en 2,4", "The 5 GHz network is too weak: staying on 2.4" },
            { "Pasando el WiFi a 5 GHz…", "Moving Wi-Fi to 5 GHz…" },
            { "Windows no dejó pasar a 5 GHz (error {0})", "Windows refused to switch to 5 GHz (error {0})" },
            { "WiFi en 5 GHz ✓", "Wi-Fi on 5 GHz ✓" },
            { "Sigue en 2,4 GHz: reintento en 10 min", "Still on 2.4 GHz: retrying in 10 min" },
            { "Pasé la notebook al WiFi de 5 GHz (va mucho mejor que 2,4)", "Moved this laptop to 5 GHz Wi-Fi (much better than 2.4)" },

            // ---- daily report
            { "Cruce · Reporte", "Cruce · Report" },
            { "Reporte de Cruce", "Cruce report" },
            { "generado", "generated" },
            { "Todavía no hay datos de este día. Las dos PCs tienen que estar conectadas: se registra un resumen por minuto.", "No data for this day yet. Both PCs need to be connected: a summary is recorded every minute." },
            { "Veredicto", "Verdict" },
            { "Sin lag registrado: la conexión anduvo bien todo el día. ✓", "No lag recorded: the connection was fine all day. ✓" },
            { "Hubo <b>{0} minuto(s) con lag</b>{1}. ", "There were <b>{0} minute(s) with lag</b>{1}. " },
            { " mientras usabas Cruce", " while you were using Cruce" },
            { "<b>{0:0}%</b> fue por el WiFi{1}. ", "<b>{0:0}%</b> was caused by the Wi-Fi{1}. " },
            { " de {0}", " of {0}" },
            { "Causa principal: <b>{0}</b> ({1:0}%).", "Main cause: <b>{0}</b> ({1:0}%)." },
            { " La peor hora fue <b>{0:00}:00–{0:00}:59</b> ({1} min con lag).", " The worst hour was <b>{0:00}:00–{0:00}:59</b> ({1} min with lag)." },
            { "Resumen", "Summary" },
            { "Minutos conectadas", "Minutes connected" },
            { "Minutos usando la otra PC", "Minutes using the other PC" },
            { "Minutos con lag", "Minutes with lag" },
            { "Ida y vuelta típica (p95)", "Typical round trip (p95)" },
            { "Tramo de red (solo ida) típico", "Typical one-way network delay" },
            { "Tirones en el día", "Stutters today" },
            { "Incidentes guardados", "Incidents saved" },
            { "Señal WiFi", "Wi-Fi signal" },
            { "Redes vecinas en tu canal", "Neighbor networks on your channel" },
            { "Canal / banda", "Channel / band" },
            { "Escaneos del WiFi", "Wi-Fi scans" },
            { "Modo baja latencia", "Low-latency mode" },
            { "activo ✓", "on ✓" },
            { "rechazado", "refused" },
            { "Lag por hora", "Lag per hour" },
            { "Detalle por hora", "Hourly detail" },
            { "Hora", "Hour" },
            { "Conectadas", "Connected" },
            { "Usando", "Using" },
            { "Con lag", "With lag" },
            { "Ida y vuelta p95", "Round trip p95" },
            { "Tramo red p95", "Network delay p95" },
            { "Tirones", "Stutters" },
            { "Ping router (WiFi)", "Router ping (Wi-Fi)" },
            { "Ping internet", "Internet ping" },
            { "Señal", "Signal" },
            { "Reintentos WiFi", "Wi-Fi retries" },
            { "Escaneos", "Scans" },
            { "Vecinos", "Neighbors" },
            { "Tráfico máx", "Peak traffic" },
            { "Energía", "Power" },
            { "Causa principal", "Main cause" },
            { "Los peores minutos", "Worst minutes" },
            { "Causa", "Cause" },
            { "Tirón máx", "Max stutter" },
            { "Ping router", "Router ping" },
            { "Tráfico", "Traffic" },
            { "Procesos que más CPU usaban", "Top CPU processes" },
            { "Incidentes (detalle al milisegundo)", "Incidents (millisecond detail)" },
            { "Cómo se decide la causa: si cuando hay lag también sube el ping de la notebook a su router o los reintentos del WiFi, el problema es el tramo WiFi; después se mira si coincidió con cortes, descargas, escaneos de Windows, señal débil o redes vecinas en tu canal. Si el WiFi está bien pero una PC estaba saturada o suspendida, es la PC. Datos crudos: metricas.csv; detalle al milisegundo: carpeta incidentes.",
              "How the cause is decided: if, when there is lag, the laptop's ping to its router or its Wi-Fi retries also go up, the problem is the Wi-Fi hop; then it checks whether it matched drops, downloads, Windows scans, weak signal or neighbor networks on your channel. If the Wi-Fi is fine but a PC was saturated or asleep, it's the PC. Raw data: metricas.csv; millisecond detail: incidentes folder." },
            { "WiFi se cortó / cambió de antena", "Wi-Fi dropped / switched access point" },
            { "WiFi saturado por descargas", "Wi-Fi saturated by downloads" },
            { "Escaneo del WiFi de Windows", "Windows Wi-Fi scan" },
            { "WiFi saturado por vecinos", "Wi-Fi crowded by neighbors" },
            { "Señal WiFi débil", "Weak Wi-Fi signal" },
            { "WiFi (otro motivo)", "Wi-Fi (other reason)" },
            { "Router saturado", "Router saturated" },
            { "PC ocupada (CPU / suspensión)", "PC busy (CPU / sleep)" },
            { "Otra causa", "Other cause" },
            { "El WiFi de la notebook se corta o salta entre antenas: acercala al router o fijá una sola red/banda.", "The laptop's Wi-Fi drops or jumps between access points: move it closer to the router or pin one network/band." },
            { "Coincide con descargas o sincronizaciones (OneDrive, Windows Update, Steam...). Pausalas mientras usás Cruce.", "It matches downloads or syncs (OneDrive, Windows Update, Steam...). Pause them while using Cruce." },
            { "Windows escanea redes y el WiFi se va unos cientos de ms. Cruce ya pide el modo baja latencia; si sigue, desactivá la búsqueda automática de redes.", "Windows scans for networks and the Wi-Fi goes away for a few hundred ms. Cruce already asks for low-latency mode; if it continues, turn off automatic network search." },
            { "Tu canal está lleno de redes vecinas: cambiá el canal del router (1, 6 u 11, el más libre) o pasá a 5 GHz.", "Your channel is full of neighbor networks: change the router's channel (1, 6 or 11, whichever is freest) or move to 5 GHz." },
            { "La señal llega débil: acercá la notebook al router, usá un repetidor o 5 GHz.", "The signal is weak: move the laptop closer to the router, use a repeater, or 5 GHz." },
            { "El router está saturado (alguien descargando/subiendo mucho): probá QoS en el router o cable.", "The router is saturated (someone downloading/uploading a lot): try QoS on the router, or a cable." },
            { "Una de las PCs estaba al límite de CPU o suspendida: mirá la columna de procesos.", "One of the PCs was maxed out on CPU or asleep: check the processes column." },
            { "maxima eficiencia", "best efficiency" },
            { "equilibrado", "balanced" },
            { "maximo rendimiento", "best performance" },
            { "mejor bateria", "better battery" },
            { "enchufada", "plugged in" },
            { "bateria", "battery" },
            { "ahorro", "saver" },
        };
    }
}
