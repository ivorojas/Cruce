using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Cruce
{
    /// <summary>
    /// Shared clipboard over an encrypted TCP stream: text, images and files.
    /// Copy on one PC, paste on the other. Files are streamed in 1 MB chunks to a
    /// temp folder and then offered to the other PC's clipboard as a normal file copy.
    /// </summary>
    public sealed class ClipSync : IDisposable
    {
        const byte K_TEXT = 10, K_IMAGE = 11, K_FILES = 12, K_CHUNK = 13, K_END = 14, K_DROP = 15, K_PASTE = 16, K_APPMSG = 17;

        /// <summary>Api: a big app message (e.g. a dictated text) over the encrypted TCP stream.</summary>
        public void SendAppMsg(string app, string json)
        {
            var addr = peerAddr();
            if (addr == null) return;
            var w = new WBuf(64 + json.Length * 3);
            w.Str(app);
            var b = Encoding.UTF8.GetBytes(json);
            w.I32(b.Length); w.Bytes(b, 0, b.Length);
            Interlocked.Increment(ref appOut); Interlocked.Add(ref appOutBytes, w.P);
            Send(addr, s => WriteFrame(s, K_APPMSG, w.B, 0, w.P), CancellationToken.None, null);
        }

        // Apps (Focus, Dictado...) may send many messages: one summary line a minute instead of one line each.
        long appOut, appOutBytes, appIn, appInBytes;

        /// <summary>"N enviados (X KB), M recibidos (Y KB)" since the last call, or null if there was no app traffic.</summary>
        public string TakeAppStats()
        {
            long o = Interlocked.Exchange(ref appOut, 0), ob = Interlocked.Exchange(ref appOutBytes, 0);
            long i = Interlocked.Exchange(ref appIn, 0), ib = Interlocked.Exchange(ref appInBytes, 0);
            if (o == 0 && i == 0) return null;
            return string.Format("{0} enviados ({1:0} KB), {2} recibidos ({3:0} KB) por TCP", o, ob / 1024.0, i, ib / 1024.0);
        }

        /// <summary>Tests only: log pastes instead of touching this PC's clipboard and keyboard.</summary>
        public static bool TestNoInject;
        public static Action<string> TestPasted;
        public static Action<string, string> TestAppMsg;

        /// <summary>
        /// "Paste this text over there": another app on this PC (e.g. Dictalo) pasted while you were
        /// driving the other PC. The text and the paste travel in one message, so the other PC never
        /// pastes a stale clipboard.
        /// </summary>
        public void SendPaste(string text)
        {
            var addr = peerAddr();
            if (addr == null || string.IsNullOrEmpty(text)) return;
            var bytes = Encoding.UTF8.GetBytes(text);
            Send(addr, s => WriteFrame(s, K_PASTE, bytes, 0, bytes.Length), CancellationToken.None, "pegado remoto");
        }

        void PasteHere(string text)
        {
            if (TestNoInject) { var t = TestPasted; if (t != null) t(text); return; }
            ui.BeginInvoke(new Action(() =>
            {
                bool ok = false;
                for (int i = 0; i < 8 && !ok; i++)
                {
                    try { Clipboard.SetDataObject(text, true); ignoreSeq = Native.GetClipboardSequenceNumber(); ok = true; }
                    catch { Thread.Sleep(40); }
                }
                if (!ok) { Log.Info("pegado remoto: no pude escribir el portapapeles"); return; }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.Sleep(40);
                    Inject.Key(0xA2, 0x1D, false, false);   // Ctrl (real scan codes: Chromium/Electron need them)
                    Thread.Sleep(25);
                    Inject.Key(0x56, 0x2F, false, false);   // V
                    Thread.Sleep(25);
                    Inject.Key(0x56, 0x2F, false, true);
                    Thread.Sleep(25);
                    Inject.Key(0xA2, 0x1D, false, true);
                    Log.Info("pegado remoto: {0} caracteres pegados acá", text.Length);
                });
            }));
        }
        public const long MaxFilesBytes = 2L << 30; // 2 GB per copy
        const int Chunk = 1 << 20;

        readonly PacketCrypto crypto;
        readonly TcpListener listener;
        readonly int port;
        readonly Func<IPEndPoint> peerAddr;
        public readonly int ListenPort;
        readonly Func<Config> cfg;
        readonly Dispatcher ui;
        HwndSource wnd;
        volatile bool running = true;
        uint ignoreSeq;
        CancellationTokenSource sending;

        public event Action<string> Notify;
        public volatile string Activity = "";

        public ClipSync(Keys keys, int port, Func<IPEndPoint> peerAddr, Func<Config> cfg, Dispatcher ui)
        {
            crypto = new PacketCrypto(keys);
            this.port = port;
            this.peerAddr = peerAddr;
            this.cfg = cfg;
            this.ui = ui;
            TcpListener lst = null;
            for (int tp = port; tp < port + 20 && lst == null; tp++)
            {
                try { var t = new TcpListener(IPAddress.Any, tp); t.Start(); lst = t; }
                catch (SocketException ex) { Log.Info("TCP {0} no disponible ({1}), pruebo otro", tp, ex.SocketErrorCode); }
            }
            if (lst == null) { lst = new TcpListener(IPAddress.Any, 0); lst.Start(); }
            listener = lst;
            ListenPort = ((IPEndPoint)lst.LocalEndpoint).Port;
            Log.Info("portapapeles escuchando en TCP {0}", ListenPort);
            new Thread(AcceptLoop) { IsBackground = true, Name = "cruce-clip" }.Start();

            var p = new HwndSourceParameters("CruceClipboard");
            p.ParentWindow = new IntPtr(-3); // HWND_MESSAGE
            p.WindowStyle = 0;
            wnd = new HwndSource(p);
            wnd.AddHook(WndProc);
            Native.AddClipboardFormatListener(wnd.Handle);
            CleanupOld();
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == Native.WM_CLIPBOARDUPDATE) { try { OnLocalChange(); } catch (Exception ex) { Log.Error(ex, "clip read"); } }
            return IntPtr.Zero;
        }

        void Say(string s) { var n = Notify; if (n != null) n(s); }

        // ------------------------------------------------------------- send

        void OnLocalChange()
        {
            var c = cfg();
            if (!c.Clipboard) return;
            if (Native.GetClipboardSequenceNumber() == ignoreSeq) return; // our own paste from the other PC
            var addr = peerAddr();
            if (addr == null) return;

            IDataObject data = null;
            for (int i = 0; i < 5 && data == null; i++)
            {
                try { data = Clipboard.GetDataObject(); } catch { Thread.Sleep(30); }
            }
            if (data == null) return;

            if (data.GetDataPresent(DataFormats.FileDrop) && c.Files)
            {
                var paths = data.GetData(DataFormats.FileDrop) as string[];
                if (paths != null && paths.Length > 0) { StartSend(addr, s => SendFiles(s, paths)); return; }
            }
            if (data.GetDataPresent(DataFormats.UnicodeText))
            {
                var text = data.GetData(DataFormats.UnicodeText) as string;
                if (text != null && text.Length > 0 && text.Length < 8 * 1024 * 1024)
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    if (Duplicate("txt" + text)) return;
                    StartSend(addr, s => WriteFrame(s, K_TEXT, bytes, 0, bytes.Length));
                }
                return;
            }
            if (data.GetDataPresent(DataFormats.Bitmap))
            {
                BitmapSource img = null;
                try { img = Clipboard.GetImage(); } catch { }
                if (img == null) return;
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                byte[] png;
                using (var ms = new MemoryStream()) { enc.Save(ms); png = ms.ToArray(); }
                if (png.Length < 64 * 1024 * 1024 && !Duplicate("img" + png.Length + ":" + Convert.ToBase64String(SHA256.Create().ComputeHash(png)))) StartSend(addr, s => WriteFrame(s, K_IMAGE, png, 0, png.Length));
            }
        }

        string lastKey; DateTime lastKeyAt;

        bool Duplicate(string key)
        {
            bool dup = key == lastKey && (DateTime.UtcNow - lastKeyAt).TotalMilliseconds < 1500;
            lastKey = key; lastKeyAt = DateTime.UtcNow;
            return dup;
        }

        void StartSend(IPEndPoint addr, Action<Stream> body)
        {
            if (sending != null) sending.Cancel();
            var cts = new CancellationTokenSource();
            sending = cts;
            Send(addr, body, cts.Token, "portapapeles");
        }

        void Send(IPEndPoint addr, Action<Stream> body, CancellationToken token, string what)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    using (var tcp = new TcpClient())
                    {
                        tcp.NoDelay = true;
                        var ar = tcp.BeginConnect(addr.Address, addr.Port, null, null);
                        if (!ar.AsyncWaitHandle.WaitOne(3000)) { Log.Info("{0}: no pude conectar con la otra PC", what ?? "mensaje de app"); return; }
                        tcp.EndConnect(ar);
                        tcp.SendTimeout = 30000;
                        QosBackground(tcp.Client);
                        using (var s = tcp.GetStream())
                        {
                            cancel = token;
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            body(s);
                            s.Flush();
                            if (what != null) Log.Info("{0} enviado en {1} ms", what, sw.ElapsedMilliseconds); // app messages: counted instead
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Info("{0}: fallo al enviar: {1}", (what ?? "mensaje de app").ToUpperInvariant(), ex.Message); }
                finally { if (what != null) Activity = ""; } // an app message must not clear a file transfer's status
            });
        }

        // Bulk transfers are marked "background" so, on Wi-Fi, they queue behind the mouse traffic (marked "voice").
        [DllImport("qwave.dll")] static extern bool QOSCreateHandle(ref QosVer v, out IntPtr h);
        [DllImport("qwave.dll")] static extern bool QOSAddSocketToFlow(IntPtr h, IntPtr s, IntPtr dest, int type, uint flags, ref uint flow);
        [StructLayout(LayoutKind.Sequential)] struct QosVer { public ushort Major, Minor; }
        static IntPtr qosBg;

        static void QosBackground(Socket s)
        {
            try
            {
                if (qosBg == IntPtr.Zero) { var v = new QosVer { Major = 1 }; if (!QOSCreateHandle(ref v, out qosBg)) return; }
                uint flow = 0;
                QOSAddSocketToFlow(qosBg, s.Handle, IntPtr.Zero, 1 /* QOSTrafficTypeBackground */, 0, ref flow);
            }
            catch { }
        }

        // ------------------------------------------------------------- drag and drop between PCs

        public sealed class DropSpot { public string Folder, Kind; public int X, Y; }
        readonly System.Collections.Concurrent.ConcurrentDictionary<uint, DropSpot> drops = new System.Collections.Concurrent.ConcurrentDictionary<uint, DropSpot>();

        /// <summary>Remembers where the files of drag <paramref name="id"/> must land on this PC.</summary>
        public void ExpectDrop(uint id, DropSpot spot) { drops[id] = spot; }

        /// <summary>Streams the dragged files to the other PC (called on the PC where the drag started).</summary>
        public void SendDrop(uint id, string[] paths)
        {
            var addr = peerAddr();
            if (addr == null) { Log.Info("arrastre: la otra PC no está conectada"); return; }
            Send(addr, s => SendFiles(s, paths, K_DROP, id), CancellationToken.None, "arrastre");
        }

        [ThreadStatic] static CancellationToken cancel;

        void WriteFrame(Stream s, byte kind, byte[] data, int off, int len)
        {
            cancel.ThrowIfCancellationRequested();
            var plain = new byte[1 + len];
            plain[0] = kind;
            Buffer.BlockCopy(data, off, plain, 1, len);
            var sealedFrame = crypto.Seal(plain, plain.Length);
            s.Write(BitConverter.GetBytes(sealedFrame.Length), 0, 4);
            s.Write(sealedFrame, 0, sealedFrame.Length);
        }

        void SendFiles(Stream s, string[] roots) { SendFiles(s, roots, K_FILES, 0); }

        void SendFiles(Stream s, string[] roots, byte kind, uint dropId)
        {
            // Flatten folders into (relative path, full path, size)
            var items = new List<Tuple<string, string, long>>();
            foreach (var r in roots)
            {
                if (File.Exists(r)) items.Add(Tuple.Create(Path.GetFileName(r), r, new FileInfo(r).Length));
                else if (Directory.Exists(r))
                {
                    var baseDir = Path.GetDirectoryName(r.TrimEnd('\\')) ?? "";
                    foreach (var f in Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories))
                        items.Add(Tuple.Create(f.Substring(baseDir.Length).TrimStart('\\'), f, new FileInfo(f).Length));
                    if (!Directory.EnumerateFileSystemEntries(r).Any())
                        items.Add(Tuple.Create(Path.GetFileName(r.TrimEnd('\\')) + "\\", r, -1L));
                }
            }
            long total = items.Where(i => i.Item3 > 0).Sum(i => i.Item3);
            if (kind == K_FILES && total > MaxFilesBytes) { Say(L.T("Archivos demasiado grandes para el portapapeles (más de 2 GB): arrastralos a la otra pantalla.")); return; }

            var w = new WBuf(1024);
            if (kind == K_DROP) w.U32(dropId);
            w.U16(roots.Length);
            foreach (var r in roots) w.Str(Path.GetFileName(r.TrimEnd('\\')));
            w.I32(items.Count);
            foreach (var i in items) { w.Str(i.Item1); w.I64(i.Item3); }
            WriteFrame(s, kind, w.B, 0, w.P);

            long sent = 0;
            var buf = new byte[Chunk];
            foreach (var i in items)
            {
                if (i.Item3 <= 0) continue;
                using (var fs = new FileStream(i.Item2, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        WriteFrame(s, K_CHUNK, buf, 0, n);
                        sent += n;
                        Activity = L.F("Enviando archivos… {0:0}%", 100.0 * sent / Math.Max(1, total));
                    }
                }
            }
            WriteFrame(s, K_END, new byte[0], 0, 0);
        }

        // ------------------------------------------------------------- receive

        void AcceptLoop()
        {
            while (running)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch { if (!running) return; Thread.Sleep(50); continue; }
                ThreadPool.QueueUserWorkItem(_ => Serve(c));
            }
        }

        static bool ReadExact(Stream s, byte[] b, int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = s.Read(b, got, n - got);
                if (r <= 0) return false;
                got += r;
            }
            return true;
        }

        byte[] ReadFrame(Stream s)
        {
            var lb = new byte[4];
            if (!ReadExact(s, lb, 4)) return null;
            int len = BitConverter.ToInt32(lb, 0);
            if (len < PacketCrypto.Overhead || len > (80 << 20)) return null;
            var b = new byte[len];
            if (!ReadExact(s, b, len)) return null;
            uint sid; long ctr;
            return crypto.Open(b, len, out sid, out ctr);
        }

        void Serve(TcpClient c)
        {
            string tmpDir = null;
            try
            {
                using (c)
                {
                    c.ReceiveTimeout = 30000;
                    var s = c.GetStream();
                    var first = ReadFrame(s);
                    if (first == null || first.Length == 0) return;
                    if (first[0] == K_APPMSG) { Interlocked.Increment(ref appIn); Interlocked.Add(ref appInBytes, first.Length); } // summarized once a minute
                    else Log.Info("portapapeles recibido: tipo {0}, {1} bytes", first[0] == K_TEXT ? "texto" : first[0] == K_IMAGE ? "imagen" : first[0] == K_PASTE ? "pegado remoto" : first[0] == K_DROP ? "arrastre" : "archivos", first.Length - 1);
                    switch (first[0])
                    {
                        case K_TEXT:
                            {
                                var text = Encoding.UTF8.GetString(first, 1, first.Length - 1);
                                ui.BeginInvoke(new Action(() => SetClip(() => Clipboard.SetDataObject(text, true))));
                                break;
                            }
                        case K_IMAGE:
                            {
                                var png = new byte[first.Length - 1];
                                Buffer.BlockCopy(first, 1, png, 0, png.Length);
                                ui.BeginInvoke(new Action(() => SetClip(() =>
                                {
                                    var dec = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                                    Clipboard.SetImage(dec.Frames[0]);
                                })));
                                break;
                            }
                        case K_FILES:
                            tmpDir = ReceiveFiles(s, first);
                            break;
                        case K_DROP:
                            ReceiveDrop(s, first);
                            break;
                        case K_PASTE:
                            PasteHere(Encoding.UTF8.GetString(first, 1, first.Length - 1));
                            break;
                        case K_APPMSG:
                            {
                                var r = new RBuf(first, 1, first.Length - 1);
                                string app = r.Str();
                                int n = r.I32();
                                string json = Encoding.UTF8.GetString(r.Bytes(n));
                                var tam = TestAppMsg;
                                if (tam != null) tam(app, json); else Api.FromPeer(app, json);
                                break;
                            }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info("clipboard receive failed: {0}", ex.Message);
                if (tmpDir != null) try { Directory.Delete(tmpDir, true); } catch { }
            }
            finally { Activity = ""; }
        }

        static string SafeRel(string rel)
        {
            rel = rel.Replace('/', '\\');
            if (rel.Length == 0 || Path.IsPathRooted(rel) || rel.Split('\\').Any(p => p == ".." || p == ".")) throw new InvalidDataException("bad path");
            foreach (var ch in Path.GetInvalidPathChars()) if (rel.IndexOf(ch) >= 0) throw new InvalidDataException("bad path");
            return rel;
        }

        string ReceiveFiles(Stream s, byte[] header)
        {
            var r = new RBuf(header, 1, header.Length - 1);
            int rootCount = r.U16();
            var roots = new List<string>();
            for (int i = 0; i < rootCount; i++) roots.Add(SafeRel(r.Str()));
            int count = r.I32();
            var items = new List<Tuple<string, long>>();
            for (int i = 0; i < count; i++) items.Add(Tuple.Create(SafeRel(r.Str()), r.I64()));
            long total = items.Where(i => i.Item2 > 0).Sum(i => i.Item2);
            if (total > MaxFilesBytes) return null;

            var dir = Path.Combine(Path.GetTempPath(), "Cruce", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(dir);
            if (total > 20 << 20) Say(L.T("Recibiendo archivos de la otra PC…"));
            long got = 0;
            byte[] pending = null;
            int pendingOff = 0;
            foreach (var it in items)
            {
                var full = Path.Combine(dir, it.Item1);
                if (it.Item2 < 0) { Directory.CreateDirectory(full); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                using (var fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write))
                {
                    long left = it.Item2;
                    while (left > 0)
                    {
                        if (pending == null || pendingOff >= pending.Length)
                        {
                            var f = ReadFrame(s);
                            if (f == null || f[0] != K_CHUNK) throw new IOException("transfer interrupted");
                            pending = f; pendingOff = 1;
                        }
                        int n = (int)Math.Min(left, pending.Length - pendingOff);
                        fs.Write(pending, pendingOff, n);
                        pendingOff += n; left -= n; got += n;
                        Activity = L.F("Recibiendo archivos… {0:0}%", 100.0 * got / Math.Max(1, total));
                    }
                }
            }
            var end = ReadFrame(s);
            if (end == null || end[0] != K_END) throw new IOException("transfer incomplete");

            var list = new StringCollection();
            foreach (var root in roots)
            {
                var p = Path.Combine(dir, root);
                if (File.Exists(p) || Directory.Exists(p)) list.Add(p);
            }
            ui.BeginInvoke(new Action(() => SetClip(() => Clipboard.SetFileDropList(list))));
            if (total > 20 << 20) Say(L.T("Archivos listos: pegalos con Ctrl+V"));
            return dir;
        }

        static string Unique(string folder, string name, bool isDir)
        {
            if (!File.Exists(Path.Combine(folder, name)) && !Directory.Exists(Path.Combine(folder, name))) return name;
            string stem = isDir ? name : Path.GetFileNameWithoutExtension(name), ext = isDir ? "" : Path.GetExtension(name);
            for (int i = 2; ; i++)
            {
                var n = stem + " (" + i + ")" + ext;
                if (!File.Exists(Path.Combine(folder, n)) && !Directory.Exists(Path.Combine(folder, n))) return n;
            }
        }

        /// <summary>Writes dragged files straight into the folder where they were dropped, with a progress card there.</summary>
        void ReceiveDrop(Stream s, byte[] header)
        {
            var r = new RBuf(header, 1, header.Length - 1);
            uint id = r.U32();
            int rootCount = r.U16();
            var roots = new List<string>();
            for (int i = 0; i < rootCount; i++) roots.Add(SafeRel(r.Str()));
            int count = r.I32();
            var items = new List<Tuple<string, long>>();
            for (int i = 0; i < count; i++) items.Add(Tuple.Create(SafeRel(r.Str()), r.I64()));
            long total = items.Where(i => i.Item2 > 0).Sum(i => i.Item2);

            // The drop location is resolved on this PC when the button is released; it may land a moment after the data.
            DropSpot spot = null;
            for (int i = 0; i < 300 && !drops.TryRemove(id, out spot); i++) Thread.Sleep(50);
            if (spot == null)
            {
                spot = new DropSpot { Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), Kind = "descargas" };
                POINT c; Native.GetCursorPos(out c); spot.X = c.X; spot.Y = c.Y;
            }
            Directory.CreateDirectory(spot.Folder);

            // Each dragged item keeps its name; if it already exists there, "name (2)" like Windows does.
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                bool isDir = items.Any(it => it.Item1.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase));
                map[root] = Unique(spot.Folder, root, isDir);
            }
            Func<string, string> dest = rel =>
            {
                int k = rel.IndexOf('\\');
                string first = k < 0 ? rel : rel.Substring(0, k), rest = k < 0 ? "" : rel.Substring(k + 1);
                string mapped;
                if (!map.TryGetValue(first, out mapped)) mapped = first;
                return Path.Combine(spot.Folder, mapped, rest);
            };

            string label = roots.Count == 1 ? roots[0] : L.F("{0} elementos", roots.Count);
            string where = spot.Kind == "escritorio" ? L.T("el escritorio") : spot.Kind == "descargas" ? L.T("Descargas") : Path.GetFileName(spot.Folder.TrimEnd('\\'));
            Log.Info("arrastre recibido: {0} ({1}) → {2} [{3}]", label, DropUi.Size(total), spot.Folder, spot.Kind);
            var created = new List<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long got = 0, lastUi = 0;
            byte[] pending = null;
            int pendingOff = 0;
            try
            {
                DropUi.Progress(id, spot.X, spot.Y, L.F("Recibiendo {0}", label), "0% · " + DropUi.Size(total), 0, 0);
                foreach (var it in items)
                {
                    var full = dest(it.Item1);
                    if (it.Item2 < 0) { Directory.CreateDirectory(full); created.Add(full); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    string part = full + ".cruce-parcial";
                    created.Add(part);
                    using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write))
                    {
                        long left = it.Item2;
                        while (left > 0)
                        {
                            if (pending == null || pendingOff >= pending.Length)
                            {
                                var f = ReadFrame(s);
                                if (f == null || f[0] != K_CHUNK) throw new IOException("se cortó la transferencia");
                                pending = f; pendingOff = 1;
                            }
                            int n = (int)Math.Min(left, pending.Length - pendingOff);
                            fs.Write(pending, pendingOff, n);
                            pendingOff += n; left -= n; got += n;
                            long ms = sw.ElapsedMilliseconds;
                            if (ms - lastUi > 120)
                            {
                                lastUi = ms;
                                double speed = got / Math.Max(0.001, ms / 1000.0);
                                double eta = (total - got) / Math.Max(1, speed);
                                DropUi.Progress(id, spot.X, spot.Y, L.F("Recibiendo {0}", label),
                                    L.F("{0:0}% · {1} de {2} · {3}/s · faltan {4:0} s", 100.0 * got / Math.Max(1, total), DropUi.Size(got), DropUi.Size(total), DropUi.Size((long)speed), eta),
                                    (double)got / Math.Max(1, total), 0);
                            }
                        }
                    }
                    if (File.Exists(full)) full = Path.Combine(Path.GetDirectoryName(full), Unique(Path.GetDirectoryName(full), Path.GetFileName(full), false));
                    File.Move(part, full);
                    created[created.Count - 1] = full;
                }
                var end = ReadFrame(s);
                if (end == null || end[0] != K_END) throw new IOException("transferencia incompleta");
                DropUi.Progress(id, spot.X, spot.Y, L.F("Listo ✓  {0}", label), L.F("en {0} · {1} en {2} s", where, DropUi.Size(total), (sw.ElapsedMilliseconds / 1000.0).ToString("0.0")), 1, 1);
                Log.Info("arrastre completo: {0} en {1:0.0} s", DropUi.Size(total), sw.ElapsedMilliseconds / 1000.0);
            }
            catch (Exception ex)
            {
                Log.Info("ARRASTRE: falló la recepción: {0}", ex.Message);
                foreach (var p in created.Where(p => p.EndsWith(".cruce-parcial"))) try { File.Delete(p); } catch { }
                DropUi.Progress(id, spot.X, spot.Y, L.F("No se pudo copiar {0}", label), ex.Message, (double)got / Math.Max(1, total), 2);
            }
        }

        void SetClip(Action set)
        {
            if (!cfg().Clipboard) return;
            for (int i = 0; i < 8; i++)
            {
                try
                {
                    set();
                    ignoreSeq = Native.GetClipboardSequenceNumber();
                    return;
                }
                catch (Exception) { Thread.Sleep(40); }
            }
            Log.Info("PORTAPAPELES: no se pudo escribir (otra app lo tenía ocupado)");
        }

        static void CleanupOld()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var root = Path.Combine(Path.GetTempPath(), "Cruce");
                    if (!Directory.Exists(root)) return;
                    foreach (var d in Directory.GetDirectories(root))
                        if (Directory.GetCreationTime(d) < DateTime.Now.AddDays(-2)) try { Directory.Delete(d, true); } catch { }
                }
                catch { }
            });
        }

        public void Dispose()
        {
            running = false;
            try { listener.Stop(); } catch { }
            if (wnd != null)
            {
                Native.RemoveClipboardFormatListener(wnd.Handle);
                wnd.Dispose();
                wnd = null;
            }
        }
    }
}
