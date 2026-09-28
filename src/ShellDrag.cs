using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Cruce
{
    /// <summary>
    /// Everything that talks to Windows Explorer for cross-PC drag and drop:
    ///  - is a shell file drag in progress? (Explorer shows its "SysDragImage" window while dragging)
    ///  - which files are being dragged (the selection of the window where the drag started)
    ///  - where a drop lands: the desktop, an Explorer window's folder, or a sub-folder icon under the cursor.
    /// Shell calls must run on the STA UI thread; callers marshal through the dispatcher.
    /// </summary>
    public static class ShellDrag
    {
        [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(PT p);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);

        public static bool DragActive()
        {
            var h = FindWindow("SysDragImage", null);
            return h != IntPtr.Zero && IsWindowVisible(h);
        }

        public static IntPtr RootAt(int x, int y)
        {
            return GetAncestor(WindowFromPoint(new PT { X = x, Y = y }), 2 /* GA_ROOT */);
        }

        public static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            return sb.ToString();
        }

        static bool IsDesktop(string cls) { return cls == "Progman" || cls == "WorkerW"; }

        static dynamic Shell() { return Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")); }

        static dynamic DesktopView(dynamic shell)
        {
            object loc = 0, root = null; int hwnd;
            return shell.Windows().FindWindowSW(ref loc, ref root, 8 /* SWC_DESKTOP */, out hwnd, 1 /* SWFO_NEEDDISPATCH */);
        }

        static dynamic ExplorerView(dynamic shell, IntPtr root)
        {
            foreach (dynamic w in shell.Windows())
            {
                try { if (new IntPtr((long)w.HWND) == root) return w; } catch { }
            }
            return null;
        }

        /// <summary>Paths selected in the window where the drag started (= what is being dragged). STA only.</summary>
        public static string[] Selection(IntPtr sourceRoot)
        {
            try
            {
                dynamic shell = Shell();
                dynamic view = IsDesktop(ClassOf(sourceRoot)) ? DesktopView(shell) : ExplorerView(shell, sourceRoot);
                if (view == null) return new string[0];
                var list = new List<string>();
                foreach (dynamic it in view.Document.SelectedItems())
                {
                    string p = (string)it.Path;
                    if (!string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p))) list.Add(p);
                }
                return list.ToArray();
            }
            catch (Exception ex) { Log.Info("arrastre: no pude leer la selección: {0}", ex.Message); return new string[0]; }
        }

        public sealed class Target { public string Folder, Kind; }

        /// <summary>Folder a drop at (x, y) should land in. STA only.</summary>
        public static Target Resolve(int x, int y)
        {
            var t = new Target();
            try
            {
                IntPtr root = RootAt(x, y);
                string cls = ClassOf(root);
                dynamic shell = Shell();
                if (IsDesktop(cls))
                {
                    try { t.Folder = (string)DesktopView(shell).Document.Folder.Self.Path; } catch { }
                    if (string.IsNullOrEmpty(t.Folder)) t.Folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    t.Kind = "escritorio";
                }
                else if (cls == "CabinetWClass" || cls == "ExploreWClass")
                {
                    dynamic w = ExplorerView(shell, root);
                    if (w != null) { t.Folder = (string)w.Document.Folder.Self.Path; t.Kind = "carpeta"; }
                }
                if (t.Folder != null && !Directory.Exists(t.Folder)) t.Folder = null; // e.g. "This PC", libraries
                if (t.Folder != null)
                {
                    // Dropped right on a folder icon? Then it goes inside that folder, like Explorer does.
                    try
                    {
                        var el = AutomationElement.FromPoint(new Point(x, y));
                        for (var e = el; e != null; e = TreeWalker.ControlViewWalker.GetParent(e))
                        {
                            if (e.Current.ControlType == ControlType.ListItem)
                            {
                                var sub = Path.Combine(t.Folder, e.Current.Name);
                                if (Directory.Exists(sub)) { t.Folder = sub; t.Kind = "subcarpeta"; }
                                break;
                            }
                            if (e.Current.ControlType == ControlType.Window) break;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log.Info("arrastre: no pude resolver el destino: {0}", ex.Message); }
            if (t.Folder == null)
            {
                t.Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                t.Kind = "descargas";
            }
            return t;
        }
    }

    /// <summary>Small click-through floating windows: the "carrying N files" tag and the progress card.</summary>
    public static class DropUi
    {
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);

        static Dispatcher ui;
        static Window ghost;
        static TextBlock ghostText;
        static DispatcherTimer ghostTimer;
        static readonly Dictionary<uint, Card> cards = new Dictionary<uint, Card>();

        public static void Init(Dispatcher d) { ui = d; }

        static readonly SolidColorBrush Bg = Frozen(Color.FromArgb(0xF0, 0x1A, 0x1D, 0x26));
        static readonly SolidColorBrush Line = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        static readonly SolidColorBrush Txt = Frozen(Color.FromRgb(0xEC, 0xEE, 0xF4));
        static readonly SolidColorBrush Mut = Frozen(Color.FromRgb(0x90, 0x97, 0xAA));
        static readonly SolidColorBrush Acc = Frozen(Color.FromRgb(0x5B, 0x8C, 0xFF));
        static readonly SolidColorBrush Ok = Frozen(Color.FromRgb(0x3D, 0xDC, 0x97));
        static readonly SolidColorBrush Bad = Frozen(Color.FromRgb(0xFF, 0x6B, 0x6B));
        static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        static Window Floating(UIElement content)
        {
            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
                ShowActivated = false, ShowInTaskbar = false, SizeToContent = SizeToContent.WidthAndHeight, Content = content,
                Left = -10000, Top = -10000
            };
            w.SourceInitialized += (s, e) =>
            {
                var h = new WindowInteropHelper(w).Handle;
                SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x20 | 0x80000 | 0x80 | 0x08000000); // transparent, layered, toolwindow, noactivate
            };
            return w;
        }

        static void MoveTo(Window w, int x, int y)
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h != IntPtr.Zero) SetWindowPos(h, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010 | 0x0040); // NOSIZE | NOACTIVATE | SHOWWINDOW
        }

        public static string Size(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1024 * 1024) return (b / 1024.0).ToString("0") + " KB";
            if (b < 1024L * 1024 * 1024) return (b / 1048576.0).ToString("0.0") + " MB";
            return (b / 1073741824.0).ToString("0.00") + " GB";
        }

        // ------------------------------------------------------------ ghost that follows the cursor while carrying files

        public static void ShowGhost(string text)
        {
            if (ui == null) return;
            ui.BeginInvoke(new Action(() =>
            {
                if (ghost == null)
                {
                    ghostText = new TextBlock { Foreground = Txt, FontSize = 12.5, FontFamily = new FontFamily("Segoe UI") };
                    var b = new Border { Background = Bg, BorderBrush = Acc, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 5, 10, 5), Child = ghostText };
                    ghost = Floating(b);
                    ghostTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
                    ghostTimer.Tick += (s, e) => { POINT p; Native.GetCursorPos(out p); MoveTo(ghost, p.X + 18, p.Y + 22); };
                }
                ghostText.Text = "↪  " + text;
                ghost.Show();
                ghostTimer.Start();
            }));
        }

        public static void HideGhost()
        {
            if (ui == null) return;
            ui.BeginInvoke(new Action(() => { if (ghost != null) { ghostTimer.Stop(); ghost.Hide(); } }));
        }

        // ------------------------------------------------------------ progress card at the drop point

        sealed class Card
        {
            public Window W; public TextBlock Title, Sub; public Border Fill; public Grid Track;
        }

        public static void Progress(uint id, int x, int y, string title, string sub, double frac, int state /* 0 running, 1 done, 2 error */)
        {
            if (ui == null) return;
            ui.BeginInvoke(new Action(() =>
            {
                Card c;
                if (!cards.TryGetValue(id, out c))
                {
                    c = new Card();
                    c.Title = new TextBlock { Foreground = Txt, FontSize = 13, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 };
                    c.Sub = new TextBlock { Foreground = Mut, FontSize = 11.5, FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 6, 0, 0) };
                    c.Fill = new Border { Background = Acc, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
                    c.Track = new Grid { Height = 6, Width = 300, Margin = new Thickness(0, 8, 0, 0) };
                    c.Track.Children.Add(new Border { Background = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(3) });
                    c.Track.Children.Add(c.Fill);
                    var sp = new StackPanel();
                    sp.Children.Add(c.Title); sp.Children.Add(c.Track); sp.Children.Add(c.Sub);
                    var b = new Border { Background = Bg, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 12), Child = sp };
                    c.W = Floating(b);
                    cards[id] = c;
                    c.W.Show();
                    MoveTo(c.W, x + 14, y + 14);
                }
                c.Title.Text = title;
                c.Sub.Text = sub;
                c.Fill.Width = Math.Max(0, Math.Min(1, frac)) * 300;
                c.Fill.Background = state == 1 ? Ok : state == 2 ? Bad : Acc;
                if (state != 0)
                {
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(state == 1 ? 2.5 : 6) };
                    t.Tick += (s, e) => { t.Stop(); c.W.Close(); cards.Remove(id); };
                    t.Start();
                }
            }));
        }
    }
}
