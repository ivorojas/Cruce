using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Cruce
{
    public static class Program
    {
        static Mutex mutex;

        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int pid);

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Contains("--selftest"))
            {
                AttachConsole(-1);
                Log.Init("cruce-prueba.log");
                Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
                Console.WriteLine();
                Link.Trace = Environment.GetEnvironmentVariable("CRUCE_TRACE") == "1";
                return SelfTest.Run(args.Contains("--inject"));
            }
            if (args.Length == 2 && args[0] == "--write-icon") { IconArt.WriteIco(args[1]); return 0; }
            if (args.Length == 4 && args[0] == "--ui-shot") { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); Log.Init("cruce-prueba.log"); return AppController.UiShot(args[1], args[2] == "es", args[3] == "1"); }
            if (args.Length == 6 && args[0] == "--fake-drop") { Log.Init("cruce-prueba.log"); Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); return SelfTest.FakeDrop(args[1], int.Parse(args[2]), int.Parse(args[3]), args[4], args[5]); }
            if (args.Length == 5 && args[0] == "--fake-peer") { Log.Init("cruce-prueba.log"); return SelfTest.FakePeer(args[1], int.Parse(args[2]), int.Parse(args[3]), args[4]); }

            Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2: exact physical pixels
            AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
            Log.Init();

            bool owner;
            mutex = new Mutex(true, @"Local\Cruce.Instance", out owner);
            if (!owner && args.Contains("--replace"))
            {
                try { owner = mutex.WaitOne(15000); } catch (AbandonedMutexException) { owner = true; }
            }
            if (!owner)
            {
                try { using (var ev = EventWaitHandle.OpenExisting(@"Local\Cruce.Show")) ev.Set(); } catch { }
                return 0;
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                CursorHider.Restore();
                Log.Error(e.ExceptionObject as Exception ?? new Exception("unknown"), "fatal");
            };
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; } catch { }
            NoPowerThrottling();
            Log.Info("Cruce {0} starting (admin={1})", AppController.Version, Autostart.IsAdmin());
            return RunApp(args);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

        /// <summary>Windows 11 puts tray apps in "efficiency mode" (EcoQoS: slow cores, coalesced timers, ignored
        /// timer-resolution requests), worst in the "best power efficiency" mode the laptop uses. Opt out: every
        /// millisecond of Cruce's timers is input latency.</summary>
        static void NoPowerThrottling()
        {
            try
            {
                var s = new PowerThrottlingState { Version = 1, ControlMask = 0x1 | 0x4, StateMask = 0 }; // EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION, both off
                bool ok = SetProcessInformation(Process.GetCurrentProcess().Handle, 4 /* ProcessPowerThrottling */, ref s, Marshal.SizeOf(typeof(PowerThrottlingState)));
                Log.Info("modo eficiencia de Windows desactivado para Cruce: {0}", ok ? "sí" : "no (error " + Marshal.GetLastWin32Error() + ")");
            }
            catch (Exception ex) { Log.Info("modo eficiencia: {0}", ex.Message); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int RunApp(string[] args)
        {
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.DispatcherUnhandledException += (s, e) => { Log.Error(e.Exception, "ui"); e.Handled = true; };
            var ctl = new AppController(app);
            ctl.Start(!args.Contains("--tray"));
            app.Run();
            GC.KeepAlive(mutex);
            Environment.Exit(0);
            return 0;
        }
    }

    public sealed class AppController
    {
        public const string Version = "1.19";

        readonly Application app;
        public readonly Config Cfg;
        public Engine Engine { get; private set; }
        public bool IsElevated { get; private set; }
        public string LinkError { get; private set; }
        Link link;
        ClipSync clip;
        Tray tray;
        MainWindow win;
        EventWaitHandle showEvent;

        public Link Link { get { return link; } }
        public ClipSync Clip { get { return clip; } }

        public AppController(Application app)
        {
            this.app = app;
            Cfg = Config.Load();
            L.English = Cfg.Language != "es";
        }

        public void Start(bool show)
        {
            IsElevated = Autostart.IsAdmin();
            CursorHider.Init();
            Engine = new Engine(Cfg);
            Engine.Ui = app.Dispatcher;
            Engine.Clip = () => clip;
            DropUi.Init(app.Dispatcher);
            Engine.IsElevated = () => IsElevated;
            Engine.Notify += Notify;
            Engine.Start();
            Api.Start(Engine, Cfg, () => clip);
            Presence.Changed = Api.PushState;
            Presence.Start(() => Engine.Mode, () => { var p = Engine.Peer; return p != null ? p.Name : ""; });
            if (!Engine.HooksOk) LinkError = L.T("No se pudo capturar el mouse/teclado");
            Diag.Start();
            Diag.LogStartup(Cfg, IsElevated);
            WifiNative.CurrentMode = () => Engine.Mode;
            WifiNative.Notify += Notify;
            WifiNative.Start();
            StartWatchers();
            RestartLink();

            tray = new Tray(this);
            var upd = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            upd.Tick += (s, e) => { upd.Interval = TimeSpan.FromHours(3); CheckUpdates(true); };
            upd.Start();
            ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(15000); Autostart.SyncInstalled(); });
            win = new MainWindow(this);
            if (show || string.IsNullOrEmpty(Cfg.Secret)) ShowWindow();

            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Cruce.Show");
            var t = new Thread(() =>
            {
                while (true)
                {
                    showEvent.WaitOne();
                    app.Dispatcher.BeginInvoke(new Action(ShowWindow));
                }
            }) { IsBackground = true, Name = "cruce-show" };
            t.Start();
        }

        /// <summary>Testing: renders the window off-screen to a PNG (no hooks, no network, no focus change, config untouched).</summary>
        public static int UiShot(string path, bool spanish, bool settings)
        {
            var wpf = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var ctl = new AppController(wpf);
            L.English = !spanish;
            ctl.IsElevated = Autostart.IsAdmin();
            ctl.Engine = new Engine(ctl.Cfg);
            var w = new MainWindow(ctl) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0 };
            w.ShowSettings(settings);
            w.Show();
            var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            later.Tick += (s, e) =>
            {
                later.Stop();
                w.MaxHeight = 4000;
                w.UpdateLayout();
                var root = (FrameworkElement)w.Content;
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                var bg = new System.Windows.Media.DrawingVisual();
                using (var dc = bg.RenderOpen()) dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x13, 0x15, 0x1B)), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                rtb.Render(bg);
                rtb.Render(root);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using (var f = System.IO.File.Create(path)) enc.Save(f);
                w.ReallyClose = true;
                w.Close();
                wpf.Shutdown();
            };
            later.Start();
            wpf.Run();
            return 0;
        }

        public void RestartLink()
        {
            Engine.DetachLink();
            if (link != null) { link.Dispose(); link = null; }
            if (clip != null) { clip.Dispose(); clip = null; }
            LinkError = Engine.HooksOk ? null : LinkError;
            if (string.IsNullOrEmpty(Cfg.Secret)) { LinkError = L.T("Falta elegir la clave"); return; }
            try
            {
                var keys = new Keys(Cfg.Secret);
                IPAddress fixedIp = null;
                if (!string.IsNullOrWhiteSpace(Cfg.PeerIp) && !IPAddress.TryParse(Cfg.PeerIp.Trim(), out fixedIp))
                {
                    try { fixedIp = Dns.GetHostAddresses(Cfg.PeerIp.Trim()).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork); } catch { }
                }
                Link l = null;
                for (int attempt = 0; l == null; attempt++)
                {
                    try { l = new Link(keys, Cfg.Port, Cfg.PeerPort > 0 ? Cfg.PeerPort : Cfg.Port, fixedIp, Engine); }
                    catch (SocketException) { if (attempt >= 20) throw; Log.Info("puerto {0} ocupado, reintento {1}", Cfg.Port, attempt + 1); Thread.Sleep(500); }
                }
                l.MinMoveIntervalUs = Cfg.MoveIntervalUs;
                Engine.AttachLink(l);
                l.Start();
                link = l;
                clip = new ClipSync(keys, Cfg.Port, () =>
                {
                    var p = Engine.Peer;
                    return p != null && p.Ep != null ? new IPEndPoint(p.Ep.Address, p.TcpPort > 0 ? p.TcpPort : Cfg.Port) : null;
                }, () => Cfg, app.Dispatcher);
                clip.Notify += Notify;
                Engine.TcpPort = clip.ListenPort;
                l.AnnounceNow();
                Log.Info("enlace iniciado: puerto {0}, ip fija {1}", Cfg.Port, fixedIp != null ? fixedIp.ToString() : "no (descubrimiento automático)");
            }
            catch (SocketException ex)
            {
                LinkError = L.F("Ningún puerto de red disponible ({0})", ex.SocketErrorCode);
                Log.Error(ex, "link start");
            }
            catch (Exception ex)
            {
                LinkError = L.F("Error de red: {0}", ex.Message);
                Log.Error(ex, "link start");
            }
        }

        void Notify(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            app.Dispatcher.BeginInvoke(new Action(() => { if (tray != null) tray.Balloon(text); }));
        }

        public void UpdateTray(string status, int state)
        {
            if (tray != null) tray.Update(status, state, Engine.Paused);
        }

        [DllImport("user32.dll", EntryPoint = "ShowWindow")] static extern bool ShowWindowNative(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);

        public void ShowWindow()
        {
            win.Show();
            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
            var h = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            if (h != IntPtr.Zero && IsIconic(h)) ShowWindowNative(h, 9); // SW_RESTORE, in case WPF missed an outside minimize
            win.Activate();
            win.Topmost = true; win.Topmost = false;
            if (h != IntPtr.Zero) Native.SetForegroundWindow(h);
        }

        double uiHangMs;
        readonly object uiGate = new object();

        /// <summary>Power/session events and a watchdog that notices if the UI thread hangs.</summary>
        void StartWatchers()
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged += (s, e) =>
            {
                Log.Info("ENERGÍA: {0}", e.Mode == Microsoft.Win32.PowerModes.Suspend ? "SUSPENDIENDO" : e.Mode == Microsoft.Win32.PowerModes.Resume ? "volvió de suspensión" : "cambio de estado (batería/enchufe)");
                Flight.Add(Flight.K_MODE, 100 + (int)e.Mode, 0);
                if (e.Mode == Microsoft.Win32.PowerModes.Resume) WifiNative.SetLowLatency(true);
            };
            Microsoft.Win32.SystemEvents.SessionSwitch += (s, e) => Log.Info("SESIÓN: {0}", e.Reason);
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, e) => Log.Info("PANTALLA: cambió la configuración de monitores");
            Engine.TakeUiHang = () => { lock (uiGate) { var v = uiHangMs; uiHangMs = 0; return v; } };

            new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(2000);
                    var sw = Stopwatch.StartNew();
                    var op = app.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => { }));
                    if (op.Wait(TimeSpan.FromSeconds(30)) != DispatcherOperationStatus.Completed) { Log.Info("UI COLGADA más de 30 s"); continue; }
                    long ms = sw.ElapsedMilliseconds;
                    if (ms > 250)
                    {
                        lock (uiGate) uiHangMs += ms;
                        Log.Info("UI: la ventana de Cruce estuvo trabada {0} ms", ms);
                        Flight.Add(Flight.K_UI, (int)ms, 0);
                    }
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "cruce-uiwatch" }.Start();
        }

        public void CheckUpdates(bool install)
        {
            Updater.CheckAsync(install, () => Engine.Mode == Mode.Local, () => app.Dispatcher.BeginInvoke(new Action(Exit)));
        }

        public void RestartElevated()
        {
            try
            {
                var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--replace");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                Exit();
            }
            catch (System.ComponentModel.Win32Exception) { /* UAC cancelled */ }
        }

        public void Exit()
        {
            Log.Info("exiting");
            if (tray != null) { tray.Dispose(); tray = null; }
            Engine.DetachLink();
            if (link != null) link.Dispose();
            if (clip != null) clip.Dispose();
            Engine.Dispose();
            Presence.Stop();
            WifiNative.Stop();
            CursorHider.Restore();
            win.ReallyClose = true;
            win.Close();
            app.Shutdown();
            new Thread(() => { Thread.Sleep(1500); Log.Info("forzando cierre del proceso"); Thread.Sleep(200); Environment.Exit(0); }) { IsBackground = true }.Start();
        }
    }

    sealed class Tray : IDisposable
    {
        readonly Forms.NotifyIcon ni;
        readonly Icon[] icons;
        readonly Forms.ToolStripMenuItem status, pause, open, quit;
        int lastState = -1;

        sealed class DarkColors : Forms.ProfessionalColorTable
        {
            static readonly Color Bg = Color.FromArgb(27, 30, 38), Sel = Color.FromArgb(45, 51, 64), Line = Color.FromArgb(51, 58, 72);
            public override Color ToolStripDropDownBackground { get { return Bg; } }
            public override Color ImageMarginGradientBegin { get { return Bg; } }
            public override Color ImageMarginGradientMiddle { get { return Bg; } }
            public override Color ImageMarginGradientEnd { get { return Bg; } }
            public override Color MenuBorder { get { return Line; } }
            public override Color MenuItemBorder { get { return Sel; } }
            public override Color MenuItemSelected { get { return Sel; } }
            public override Color SeparatorDark { get { return Line; } }
            public override Color SeparatorLight { get { return Bg; } }
        }

        public Tray(AppController app)
        {
            int sz = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);
            icons = new[]
            {
                IconArt.MakeIcon(sz, IconArt.Green), IconArt.MakeIcon(sz, IconArt.Amber),
                IconArt.MakeIcon(sz, IconArt.Gray), IconArt.MakeIcon(sz, IconArt.Red)
            };
            var menu = new Forms.ContextMenuStrip();
            menu.Renderer = new Forms.ToolStripProfessionalRenderer(new DarkColors());
            menu.ForeColor = Color.FromArgb(236, 238, 244);
            menu.Font = new Font("Segoe UI", 9.5f);
            menu.ShowImageMargin = false;
            status = new Forms.ToolStripMenuItem(L.T("Buscando la otra PC…")) { Enabled = false };
            menu.Items.Add(status);
            menu.Items.Add(new Forms.ToolStripSeparator());
            open = new Forms.ToolStripMenuItem(L.T("Abrir Cruce"), null, (s, e) => app.ShowWindow()) { Font = new Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold) };
            menu.Items.Add(open);
            pause =new Forms.ToolStripMenuItem(L.T("Pausar el cruce"), null, (s, e) => { app.Engine.Paused = !app.Engine.Paused; });
            menu.Items.Add(pause);
            menu.Items.Add(new Forms.ToolStripSeparator());
            quit = new Forms.ToolStripMenuItem(L.T("Salir"), null, (s, e) => app.Exit());
            menu.Items.Add(quit);

            ni = new Forms.NotifyIcon();
            ni.Icon = icons[1];
            ni.Text = "Cruce";
            ni.ContextMenuStrip = menu;
            ni.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) app.ShowWindow(); };
            ni.BalloonTipClicked += (s, e) => app.ShowWindow();
            ni.Visible = true;
        }

        public void Update(string text, int state, bool paused)
        {
            string t = "Cruce · " + text;
            if (t.Length > 63) t = t.Substring(0, 62) + "…";
            if (ni.Text != t) ni.Text = t;
            status.Text = text;
            status.ForeColor = Color.FromArgb(144, 151, 170);
            pause.Text = L.T(paused ? "Reanudar el cruce" : "Pausar el cruce");
            open.Text = L.T("Abrir Cruce");
            quit.Text = L.T("Salir");
            if (state != lastState) { ni.Icon = icons[state]; lastState = state; }
        }

        public void Balloon(string text)
        {
            ni.BalloonTipTitle = "Cruce";
            ni.BalloonTipText = text;
            ni.ShowBalloonTip(2500);
        }

        public void Dispose()
        {
            ni.Visible = false;
            ni.Dispose();
        }
    }
}
