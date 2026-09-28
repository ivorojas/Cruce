using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Cruce
{
    public sealed class MainWindow : Window
    {
        static readonly Color CAccent = Color.FromRgb(0x5B, 0x8C, 0xFF);
        static readonly Color CViolet = Color.FromRgb(0x9B, 0x7B, 0xFF);
        static readonly Color CGreen = Color.FromRgb(0x3D, 0xDC, 0x97);
        static readonly Color CAmber = Color.FromRgb(0xFF, 0xB5, 0x47);
        static readonly Color CRed = Color.FromRgb(0xFF, 0x6B, 0x6B);
        static readonly Color CGray = Color.FromRgb(0x8C, 0x93, 0xA6);

        readonly AppController app;
        readonly FrameworkElement root;
        readonly Canvas map, spark;
        readonly TextBlock pillText, modeText, sideHint, statRtt, statRttSub, statP95, statMax, statLoss, statPps, statQos, sparkScale, speedText, adminText, footer, secretHint;
        readonly Ellipse pillDot;
        readonly Border pill;
        readonly PasswordBox secret;
        readonly TextBox secretPlain, peerIp;
        readonly Button showSecret, adminBtn;
        readonly Slider speed;
        readonly CheckBox clipSw, filesSw, autoSw;
        readonly RadioButton[] sides;
        readonly DispatcherTimer tick, debounce, saveLater;
        readonly List<double> hist = new List<double>();
        bool loading, autoBusy;
        int tickCount;
        DateTime lastSample = DateTime.MinValue;
        string mapSig = "";
        string appliedSecret, appliedIp;
        public bool ReallyClose;

        // live-map transform
        double mSc, mTx, mTy, mOx, mOy;
        Ellipse dot;

        T F<T>(string name) where T : class { return (T)root.FindName(name); }

        public MainWindow(AppController app)
        {
            this.app = app;
            Title = "Cruce";
            Width = 880;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.CanMinimize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(0x13, 0x15, 0x1B));
            UseLayoutRounding = true;
            Icon = IconArt.Wpf(64);

            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Cruce.ui.xaml"))
                root = (FrameworkElement)XamlReader.Load(s);
            Content = root;

            map = F<Canvas>("Map"); spark = F<Canvas>("Spark");
            pillText = F<TextBlock>("PillText"); modeText = F<TextBlock>("ModeText"); sideHint = F<TextBlock>("SideHint");
            statRtt = F<TextBlock>("StatRtt"); statRttSub = F<TextBlock>("StatRttSub"); statP95 = F<TextBlock>("StatP95");
            statMax = F<TextBlock>("StatMax"); statLoss = F<TextBlock>("StatLoss"); statPps = F<TextBlock>("StatPps");
            statQos = F<TextBlock>("StatQos"); sparkScale = F<TextBlock>("SparkScale"); speedText = F<TextBlock>("SpeedText");
            adminText = F<TextBlock>("AdminText"); footer = F<TextBlock>("Footer"); secretHint = F<TextBlock>("SecretHint");
            pillDot = F<Ellipse>("PillDot"); pill = F<Border>("Pill");
            secret = F<PasswordBox>("Secret"); secretPlain = F<TextBox>("SecretPlain"); peerIp = F<TextBox>("PeerIp");
            showSecret = F<Button>("ShowSecret"); adminBtn = F<Button>("AdminBtn");
            speed = F<Slider>("Speed");
            clipSw = F<CheckBox>("ClipSw"); filesSw = F<CheckBox>("FilesSw"); autoSw = F<CheckBox>("AutoSw");
            sides = new[] { F<RadioButton>("SideLeft"), F<RadioButton>("SideRight"), F<RadioButton>("SideTop"), F<RadioButton>("SideBottom") };
            F<Image>("Logo").Source = IconArt.Wpf(96);

            debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            debounce.Tick += (s, e) => { debounce.Stop(); ApplyConnection(); };
            saveLater = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            saveLater.Tick += (s, e) => { saveLater.Stop(); app.Cfg.Save(); };

            LoadValues();
            Wire();

            tick = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (s, e) => Refresh(), Dispatcher);
            tick.Start();
            SourceInitialized += (s, e) => { Backdrop(); FitToScreen(); };
            DpiChanged += (s, e) => Dispatcher.BeginInvoke(new Action(FitToScreen));
            IsVisibleChanged += (s, e) => { if (IsVisible) Dispatcher.BeginInvoke(new Action(FitToScreen), DispatcherPriority.Loaded); };
            Closing += (s, e) => { if (!ReallyClose) { e.Cancel = true; Hide(); } };
            map.SizeChanged += (s, e) => mapSig = "";
            // Animate the live pointer dot only while the window is on screen: an attached
            // Rendering handler keeps WPF drawing at 60 fps even when hidden.
            EventHandler render = (s, e) => MoveDot();
            IsVisibleChanged += (s, e) =>
            {
                CompositionTarget.Rendering -= render;
                if (IsVisible) CompositionTarget.Rendering += render;
                tick.Interval = TimeSpan.FromMilliseconds(IsVisible ? 250 : 1000);
            };
            Refresh();
        }

        // ------------------------------------------------------------ window chrome

        static int OsBuild()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    return int.Parse((string)k.GetValue("CurrentBuildNumber"));
            }
            catch { return 0; }
        }

        void Backdrop()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int on = 1;
            Native.DwmSetWindowAttribute(hwnd, 20, ref on, 4); // dark title bar
            if (OsBuild() >= 22621)
            {
                // Windows 11 Mica behind the whole window
                Background = Brushes.Transparent;
                var src = HwndSource.FromHwnd(hwnd);
                if (src != null && src.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
                var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                Native.DwmExtendFrameIntoClientArea(hwnd, ref m);
                int mica = 2;
                Native.DwmSetWindowAttribute(hwnd, 38, ref mica, 4);
            }
        }

        /// <summary>Keeps the whole window inside the work area of the monitor it is on.</summary>
        void FitToScreen()
        {
            var h = new WindowInteropHelper(this).Handle;
            var src = PresentationSource.FromVisual(this);
            if (h == IntPtr.Zero || src == null || src.CompositionTarget == null) return;
            var wa = System.Windows.Forms.Screen.FromHandle(h).WorkingArea;
            var t = src.CompositionTarget.TransformFromDevice;
            var tl = t.Transform(new Point(wa.Left, wa.Top));
            var br = t.Transform(new Point(wa.Right, wa.Bottom));
            MaxHeight = br.Y - tl.Y;
            UpdateLayout();
            double w = ActualWidth > 0 ? ActualWidth : Width, hh = ActualHeight > 0 ? ActualHeight : MaxHeight;
            Left = Math.Max(tl.X, Math.Min(Left, br.X - w));
            Top = Math.Max(tl.Y, Math.Min(Top, br.Y - hh));
        }

        // ------------------------------------------------------------ settings

        void LoadValues()
        {
            loading = true;
            var c = app.Cfg;
            secret.Password = c.Secret;
            peerIp.Text = c.PeerIp;
            speed.Value = c.Speed;
            speedText.Text = c.Speed.ToString("0.00") + "×";
            clipSw.IsChecked = c.Clipboard;
            filesSw.IsChecked = c.Files;
            filesSw.IsEnabled = c.Clipboard;
            SetSide(c.Side);
            appliedSecret = c.Secret;
            appliedIp = c.PeerIp;
            loading = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool on = Autostart.IsEnabled();
                Dispatcher.BeginInvoke(new Action(() => { loading = true; autoSw.IsChecked = on; loading = false; }));
            });
        }

        void SetSide(Side s)
        {
            for (int i = 0; i < 4; i++) sides[i].IsChecked = i == (int)s;
        }

        void Wire()
        {
            secret.PasswordChanged += (s, e) =>
            {
                if (secretPlain.Visibility != Visibility.Visible) secretPlain.Text = secret.Password;
                if (!loading) { debounce.Stop(); debounce.Start(); }
            };
            secretPlain.TextChanged += (s, e) =>
            {
                if (secretPlain.Visibility == Visibility.Visible && secret.Password != secretPlain.Text) secret.Password = secretPlain.Text;
            };
            showSecret.Click += (s, e) =>
            {
                bool show = secretPlain.Visibility != Visibility.Visible;
                secretPlain.Text = secret.Password;
                secretPlain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                secret.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
                showSecret.Content = show ? "Ocultar" : "Ver";
            };
            peerIp.TextChanged += (s, e) => { if (!loading) { debounce.Stop(); debounce.Start(); } };
            speed.ValueChanged += (s, e) =>
            {
                double v = Math.Round(speed.Value / 0.05) * 0.05;
                speedText.Text = v.ToString("0.00") + "×";
                if (loading) return;
                app.Cfg.Speed = v;
                saveLater.Stop(); saveLater.Start();
            };
            clipSw.Click += (s, e) => { app.Cfg.Clipboard = clipSw.IsChecked == true; filesSw.IsEnabled = app.Cfg.Clipboard; app.Cfg.Save(); };
            filesSw.Click += (s, e) => { app.Cfg.Files = filesSw.IsChecked == true; app.Cfg.Save(); };
            autoSw.Click += (s, e) => ToggleAutostart();
            adminBtn.Click += (s, e) => app.RestartElevated();
            F<Button>("UpdBtn").Click += (s, e) => app.CheckUpdates(true);
            F<Button>("LogBtn").Click += (s, e) => { try { System.Diagnostics.Process.Start("notepad.exe", "\"" + Log.PathName + "\""); } catch { } };
            for (int i = 0; i < 4; i++)
            {
                var side = (Side)i;
                sides[i].Checked += (s, e) =>
                {
                    if (loading || app.Cfg.Side == side) return;
                    app.Cfg.Side = side;
                    app.Cfg.SideStamp = DateTime.UtcNow.Ticks;
                    app.Cfg.Save();
                    app.Engine.InvalidateEdges();
                    var l = app.Link;
                    if (l != null) l.AnnounceNow();
                    mapSig = "";
                };
            }
        }

        void ApplyConnection()
        {
            string sec = secret.Password;
            string ip = peerIp.Text.Trim();
            if (sec == appliedSecret && ip == appliedIp) return;
            app.Cfg.Secret = sec;
            app.Cfg.PeerIp = ip;
            app.Cfg.Save();
            appliedSecret = sec;
            appliedIp = ip;
            app.RestartLink();
            mapSig = "";
        }

        void ToggleAutostart()
        {
            if (autoBusy) return;
            autoBusy = true;
            bool want = autoSw.IsChecked == true;
            autoSw.IsEnabled = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = want ? Autostart.Enable() : Autostart.Disable();
                bool now = Autostart.IsEnabled();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    loading = true; autoSw.IsChecked = now; loading = false;
                    autoSw.IsEnabled = true;
                    autoBusy = false;
                    if (err != null && err != "Cancelado.") adminText.Text = err;
                }));
            });
        }

        // ------------------------------------------------------------ refresh

        static SolidColorBrush B(Color c, byte a = 255) { var b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)); b.Freeze(); return b; }
        static string Ms(double v) { return v < 10 ? v.ToString("0.0") : v.ToString("0"); }

        void Refresh()
        {
            tickCount++;
            var e = app.Engine;
            var p = e.Peer;
            var l = app.Link;
            var st = l != null ? l.Stats : null;
            bool connected = p != null && l != null;

            string text;
            Color col;
            int trayState;
            if (app.LinkError != null) { text = app.LinkError; col = CRed; trayState = 3; }
            else if (e.Paused) { text = "En pausa"; col = CGray; trayState = 2; }
            else if (connected) { text = "Conectado con " + p.Name; col = CGreen; trayState = 0; }
            else { text = "Buscando la otra PC…"; col = CAmber; trayState = 1; }
            pillText.Text = text;
            pillDot.Fill = B(col);
            pill.Background = B(col, 0x26);
            pill.BorderBrush = B(col, 0x40);
            app.UpdateTray(text, trayState);

            string act = app.Clip != null ? app.Clip.Activity : "";
            if (!string.IsNullOrEmpty(act)) modeText.Text = act;
            else if (!connected) modeText.Text = "Abrí Cruce en la otra PC con la misma clave";
            else if (e.Mode == Mode.Remote) modeText.Text = "Estás usando " + p.Name;
            else if (e.Mode == Mode.Controlled) modeText.Text = p.Name + " está usando esta PC";
            else modeText.Text = "Estás usando esta PC";

            if (connected && !p.Elevated)
                sideHint.Text = p.Name + " no tiene permisos de admin: ahí no vas a poder usar ventanas de administrador.";
            else sideHint.Text = "Se sincroniza sola en las dos PCs.";

            if (connected && st != null && st.RttMs >= 0)
            {
                statRtt.Text = Ms(st.RttMs);
                statRttSub.Text = "ida y vuelta";
                statP95.Text = Ms(st.RttP95Ms);
                statMax.Text = "máximo " + Ms(st.RttMaxMs) + " ms";
                statLoss.Text = st.LossPct.ToString("0.0");
                statPps.Text = ((int)Math.Round(st.PpsOut + st.PpsIn)).ToString();
                statQos.Text = "prioridad de voz: " + (st.Qos ? "activa" : "no disponible");
            }
            else
            {
                statRtt.Text = "–"; statP95.Text = "–"; statLoss.Text = "–"; statPps.Text = "–";
                statMax.Text = "máximo –"; statQos.Text = "prioridad de voz: –";
            }

            // one sample per 500 ms regardless of the refresh rate (slower while hidden)
            var nowT = DateTime.UtcNow;
            if (lastSample == DateTime.MinValue) lastSample = nowT;
            bool added = false;
            for (int guard = 0; (nowT - lastSample).TotalMilliseconds >= 500 && guard < 120; guard++)
            {
                hist.Add(connected && st != null && st.RttMs >= 0 ? st.RttMs : double.NaN);
                if (hist.Count > 120) hist.RemoveAt(0);
                lastSample = lastSample.AddMilliseconds(500);
                added = true;
            }
            if ((nowT - lastSample).TotalMilliseconds >= 500) lastSample = nowT;
            if (added && IsVisible) DrawSpark();

            if (!loading && !sides[(int)app.Cfg.Side].IsChecked.GetValueOrDefault())
            {
                loading = true; SetSide(app.Cfg.Side); loading = false;
            }

            bool admin = app.IsElevated;
            adminBtn.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
            if (string.IsNullOrEmpty(adminText.Text) || adminText.Tag as string == "auto")
            {
                adminText.Text = admin ? "Corriendo con permisos de admin ✓" : "Sin permisos de admin: no controla ventanas de administrador en esta PC.";
                adminText.Tag = "auto";
            }

            secretHint.Text = string.IsNullOrEmpty(app.Cfg.Secret)
                ? "Elegí una clave y poné la misma en las dos PCs."
                : "Poné la misma clave en las dos PCs. Todo viaja cifrado con ella.";

            footer.Text = !string.IsNullOrEmpty(Updater.Status) ? Updater.Status : "Esta PC: " + app.Cfg.Name + "  ·  " + LocalIp() + "  ·  v" + AppController.Version;

            if (IsVisible)
            {
                string sig = string.Join("|", new object[] { app.Cfg.Side, e.Mode, p != null ? p.Name + p.Session + string.Join(",", p.Mons.Select(m => m.L + ":" + m.T + ":" + m.R + ":" + m.B)) : "-", string.Join(",", e.LocalMons.Select(m => m.L + ":" + m.T + ":" + m.R + ":" + m.B)), map.ActualWidth, map.ActualHeight, app.Cfg.Name });
                if (sig != mapSig) { mapSig = sig; DrawMap(); }
            }
        }

        static string cachedIp;
        static DateTime ipAt;
        static string LocalIp()
        {
            if (cachedIp != null && (DateTime.Now - ipAt).TotalSeconds < 15) return cachedIp;
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect("192.0.2.1", 9); // no packet is sent; just picks the outgoing interface
                    cachedIp = ((IPEndPoint)s.LocalEndPoint).Address.ToString();
                }
            }
            catch { cachedIp = "sin red"; }
            ipAt = DateTime.Now;
            return cachedIp;
        }

        // ------------------------------------------------------------ map

        static Rect Bbox(Mon[] ms)
        {
            if (ms == null || ms.Length == 0) return new Rect(0, 0, 1920, 1080);
            double l = ms.Min(m => m.L), t = ms.Min(m => m.T), r = ms.Max(m => m.R), b = ms.Max(m => m.B);
            return new Rect(l, t, r - l, b - t);
        }

        void DrawMap()
        {
            var e = app.Engine;
            var p = e.Peer;
            var local = e.LocalMons;
            Side s = app.Cfg.Side;
            double W = map.ActualWidth, H = map.ActualHeight;
            map.Children.Clear();
            dot = null;
            if (W < 20 || H < 20 || local == null || local.Length == 0) return;

            bool known = p != null && p.Mons.Length > 0;
            Mon[] remote = known ? p.Mons : new[] { new Mon { L = 0, T = 0, R = 1920, B = 1080, Dpi = 96 } };
            Rect lb = Bbox(local), rb0 = Bbox(remote);
            double gap = Math.Max(lb.Width, lb.Height) * 0.07;
            double ox, oy;
            switch (s)
            {
                case Side.Right: ox = lb.Right + gap - rb0.Left; oy = lb.Top + (lb.Height - rb0.Height) / 2 - rb0.Top; break;
                case Side.Left: ox = lb.Left - gap - rb0.Width - rb0.Left; oy = lb.Top + (lb.Height - rb0.Height) / 2 - rb0.Top; break;
                case Side.Top: ox = lb.Left + (lb.Width - rb0.Width) / 2 - rb0.Left; oy = lb.Top - gap - rb0.Height - rb0.Top; break;
                default: ox = lb.Left + (lb.Width - rb0.Width) / 2 - rb0.Left; oy = lb.Bottom + gap - rb0.Top; break;
            }
            var rb = new Rect(rb0.Left + ox, rb0.Top + oy, rb0.Width, rb0.Height);
            var all = Rect.Union(lb, rb);
            const double labelH = 22;
            double sc = Math.Min((W - 8) / all.Width, (H - labelH * 2 - 4) / all.Height);
            double tx = (W - all.Width * sc) / 2 - all.Left * sc;
            double ty = labelH + (H - labelH * 2 - all.Height * sc) / 2 - all.Top * sc;
            mSc = sc; mTx = tx; mTy = ty; mOx = ox; mOy = oy;

            bool hereActive = e.Mode != Mode.Remote;
            foreach (var m in local) Monitor(m.L * sc + tx, m.T * sc + ty, m.W * sc, m.H * sc, CAccent, hereActive && p != null, false, m);
            foreach (var m in remote) Monitor((m.L + ox) * sc + tx, (m.T + oy) * sc + ty, m.W * sc, m.H * sc, CViolet, !hereActive, !known, m);

            GroupLabel(lb.Left * sc + tx, lb.Top * sc + ty, lb.Width * sc, "ESTA PC", app.Cfg.Name, CAccent);
            GroupLabel(rb.Left * sc + tx, rb.Top * sc + ty, rb.Width * sc, known ? "OTRA PC" : "OTRA PC", known ? p.Name : "sin conectar", CViolet);

            // the crossing seam
            var le = Edge.Of(local, s);
            if (le != null && known)
            {
                bool h = Geo.Horiz(s);
                double mid = (s == Side.Right || s == Side.Bottom) ? le.Boundary + gap / 2 : le.Boundary - gap / 2;
                var re = Edge.Of(remote.Select(m => new Mon { L = (int)(m.L + ox), T = (int)(m.T + oy), R = (int)(m.R + ox), B = (int)(m.B + oy) }).ToArray(), Geo.Opposite(s));
                double a0 = Math.Max(le.A0, re.A0), a1 = Math.Min(le.A1, re.A1);
                if (a1 <= a0) { a0 = le.A0; a1 = le.A1; }
                var seam = new Line { StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                seam.Stroke = new LinearGradientBrush(CAccent, CViolet, h ? 90 : 0);
                if (h) { seam.X1 = seam.X2 = mid * sc + tx; seam.Y1 = a0 * sc + ty + 4; seam.Y2 = a1 * sc + ty - 4; }
                else { seam.Y1 = seam.Y2 = mid * sc + ty; seam.X1 = a0 * sc + tx + 4; seam.X2 = a1 * sc + tx - 4; }
                seam.Effect = new DropShadowEffect { Color = CAccent, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.8 };
                map.Children.Add(seam);
            }

            dot = new Ellipse { Width = 9, Height = 9, Fill = Brushes.White, IsHitTestVisible = false };
            dot.Effect = new DropShadowEffect { Color = Colors.White, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 };
            map.Children.Add(dot);
            MoveDot();
        }

        void Monitor(double x, double y, double w, double h, Color c, bool active, bool placeholder, Mon m)
        {
            var border = new Border
            {
                Width = Math.Max(4, w - 3),
                Height = Math.Max(4, h - 3),
                CornerRadius = new CornerRadius(Math.Min(8, Math.Min(w, h) / 6)),
                BorderThickness = new Thickness(1.5),
                BorderBrush = B(c, (byte)(active ? 0xE0 : 0x70)),
                Background = placeholder ? (Brush)B(c, 0x10) : new LinearGradientBrush(Color.FromArgb(0x55, c.R, c.G, c.B), Color.FromArgb(0x1C, c.R, c.G, c.B), 60),
            };
            if (placeholder)
            {
                border.BorderBrush = B(c, 0x60);
                border.Child = new TextBlock { Text = "?", Foreground = B(c, 0xA0), FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }
            else if (w > 70 && h > 36)
            {
                var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(new TextBlock { Text = m.W + " × " + m.H, Foreground = B(Color.FromRgb(0xEC, 0xEE, 0xF4), 0xD0), FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center });
                if (m.Dpi > 0 && m.Dpi != 96 && h > 50)
                    sp.Children.Add(new TextBlock { Text = Math.Round(m.Dpi * 100.0 / 96) + "%", Foreground = B(CGray), FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center });
                border.Child = sp;
            }
            if (active) border.Effect = new DropShadowEffect { Color = c, BlurRadius = 26, ShadowDepth = 0, Opacity = 0.55 };
            Canvas.SetLeft(border, x + 1.5);
            Canvas.SetTop(border, y + 1.5);
            map.Children.Add(border);
        }

        void GroupLabel(double x, double y, double w, string kicker, string name, Color c)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = kicker, Foreground = B(c), FontSize = 10.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = "  " + name, Foreground = B(Color.FromRgb(0xEC, 0xEE, 0xF4)), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            sp.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(sp, x + (w - sp.DesiredSize.Width) / 2);
            Canvas.SetTop(sp, y - 21);
            map.Children.Add(sp);
        }

        void MoveDot()
        {
            if (dot == null) return;
            var e = app.Engine;
            double x, y;
            if (e.Mode == Mode.Remote)
            {
                e.GetRemotePos(out x, out y);
                x += mOx; y += mOy;
            }
            else
            {
                POINT c;
                Native.GetCursorPos(out c);
                x = c.X; y = c.Y;
            }
            Canvas.SetLeft(dot, x * mSc + mTx - 4.5);
            Canvas.SetTop(dot, y * mSc + mTy - 4.5);
        }

        // ------------------------------------------------------------ sparkline

        void DrawSpark()
        {
            spark.Children.Clear();
            double W = spark.ActualWidth, H = spark.ActualHeight;
            var vals = hist.Where(v => !double.IsNaN(v)).ToList();
            if (W < 10 || H < 10 || vals.Count == 0) { sparkScale.Text = ""; return; }
            double top = Nice(Math.Max(4, vals.Max() * 1.25));
            sparkScale.Text = "0 – " + top.ToString("0") + " ms";

            for (int g = 1; g <= 3; g++)
            {
                var gl = new Line { X1 = 0, X2 = W, Y1 = H * g / 4.0, Y2 = H * g / 4.0, Stroke = B(Colors.White, 0x10), StrokeThickness = 1 };
                spark.Children.Add(gl);
            }

            int n = 120;
            var seg = new PointCollection();
            Action flush = () =>
            {
                if (seg.Count < 2) { seg = new PointCollection(); return; }
                var fill = new PointCollection(seg);
                fill.Add(new Point(seg[seg.Count - 1].X, H));
                fill.Add(new Point(seg[0].X, H));
                spark.Children.Add(new Polygon
                {
                    Points = fill,
                    Fill = new LinearGradientBrush(Color.FromArgb(0x55, CAccent.R, CAccent.G, CAccent.B), Color.FromArgb(0x00, CAccent.R, CAccent.G, CAccent.B), 90)
                });
                spark.Children.Add(new Polyline { Points = seg, Stroke = B(CAccent), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round });
                seg = new PointCollection();
            };
            int offset = n - hist.Count;
            for (int i = 0; i < hist.Count; i++)
            {
                double v = hist[i];
                if (double.IsNaN(v)) { flush(); continue; }
                double x = (offset + i) * W / (n - 1);
                double y = H - Math.Min(v, top) / top * (H - 2) - 1;
                seg.Add(new Point(x, y));
            }
            flush();
        }

        static double Nice(double v)
        {
            foreach (var s in new double[] { 5, 10, 20, 30, 50, 100, 200, 300, 500, 1000, 2000 }) if (v <= s) return s;
            return Math.Ceiling(v / 1000) * 1000;
        }
    }
}
