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
                Log.Init();
                Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
                Console.WriteLine();
                return SelfTest.Run(args.Contains("--inject"));
            }
            if (args.Length == 2 && args[0] == "--write-icon") { IconArt.WriteIco(args[1]); return 0; }
            if (args.Length == 5 && args[0] == "--fake-peer") { Log.Init(); return SelfTest.FakePeer(args[1], int.Parse(args[2]), int.Parse(args[3]), args[4]); }

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
            Log.Info("Cruce {0} starting (admin={1})", AppController.Version, Autostart.IsAdmin());
            return RunApp(args);
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
        public const string Version = "1.4";

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
        }

        public void Start(bool show)
        {
            IsElevated = Autostart.IsAdmin();
            CursorHider.Init();
            Engine = new Engine(Cfg);
            Engine.IsElevated = () => IsElevated;
            Engine.Notify += Notify;
            Engine.Start();
            if (!Engine.HooksOk) LinkError = "No se pudo capturar el mouse/teclado";
            Diag.Start();
            Diag.LogStartup(Cfg, IsElevated);
            RestartLink();

            tray = new Tray(this);
            var upd = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            upd.Tick += (s, e) => { upd.Interval = TimeSpan.FromHours(3); CheckUpdates(true); };
            upd.Start();
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

        public void RestartLink()
        {
            Engine.DetachLink();
            if (link != null) { link.Dispose(); link = null; }
            if (clip != null) { clip.Dispose(); clip = null; }
            LinkError = Engine.HooksOk ? null : LinkError;
            if (string.IsNullOrEmpty(Cfg.Secret)) { LinkError = "Falta elegir la clave"; return; }
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
                    return p != null && p.Ep != null ? p.Ep.Address : null;
                }, () => Cfg, app.Dispatcher);
                clip.Notify += Notify;
                Log.Info("enlace iniciado: puerto {0}, ip fija {1}", Cfg.Port, fixedIp != null ? fixedIp.ToString() : "no (descubrimiento automático)");
            }
            catch (SocketException ex)
            {
                LinkError = "El puerto " + Cfg.Port + " está ocupado";
                Log.Error(ex, "link start");
            }
            catch (Exception ex)
            {
                LinkError = "Error de red: " + ex.Message;
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

        public void ShowWindow()
        {
            win.Show();
            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
            win.Activate();
            win.Topmost = true; win.Topmost = false;
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
        readonly Forms.ToolStripMenuItem status, pause;
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
            status = new Forms.ToolStripMenuItem("Buscando la otra PC…") { Enabled = false };
            menu.Items.Add(status);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(new Forms.ToolStripMenuItem("Abrir Cruce", null, (s, e) => app.ShowWindow()) { Font = new Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold) });
            pause = new Forms.ToolStripMenuItem("Pausar el cruce", null, (s, e) => { app.Engine.Paused = !app.Engine.Paused; });
            menu.Items.Add(pause);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(new Forms.ToolStripMenuItem("Salir", null, (s, e) => app.Exit()));

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
            pause.Text = paused ? "Reanudar el cruce" : "Pausar el cruce";
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
