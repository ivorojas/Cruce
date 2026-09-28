using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
        const byte K_TEXT = 10, K_IMAGE = 11, K_FILES = 12, K_CHUNK = 13, K_END = 14;
        public const long MaxFilesBytes = 2L << 30; // 2 GB per copy
        const int Chunk = 1 << 20;

        readonly PacketCrypto crypto;
        readonly TcpListener listener;
        readonly int port;
        readonly Func<IPAddress> peerAddr;
        readonly Func<Config> cfg;
        readonly Dispatcher ui;
        HwndSource wnd;
        volatile bool running = true;
        uint ignoreSeq;
        CancellationTokenSource sending;

        public event Action<string> Notify;
        public volatile string Activity = "";

        public ClipSync(Keys keys, int port, Func<IPAddress> peerAddr, Func<Config> cfg, Dispatcher ui)
        {
            crypto = new PacketCrypto(keys);
            this.port = port;
            this.peerAddr = peerAddr;
            this.cfg = cfg;
            this.ui = ui;
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
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
                if (png.Length < 64 * 1024 * 1024) StartSend(addr, s => WriteFrame(s, K_IMAGE, png, 0, png.Length));
            }
        }

        void StartSend(IPAddress addr, Action<Stream> body)
        {
            if (sending != null) sending.Cancel();
            var cts = new CancellationTokenSource();
            sending = cts;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    using (var tcp = new TcpClient())
                    {
                        tcp.NoDelay = true;
                        var ar = tcp.BeginConnect(addr, port, null, null);
                        if (!ar.AsyncWaitHandle.WaitOne(3000)) return;
                        tcp.EndConnect(ar);
                        tcp.SendTimeout = 15000;
                        using (var s = tcp.GetStream())
                        {
                            cancel = cts.Token;
                            body(s);
                            s.Flush();
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Info("clipboard send failed: {0}", ex.Message); }
                finally { Activity = ""; }
            });
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

        void SendFiles(Stream s, string[] roots)
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
            if (total > MaxFilesBytes) { Say("Archivos demasiado grandes para compartir (más de 2 GB)."); return; }

            var w = new WBuf(1024);
            w.U16(roots.Length);
            foreach (var r in roots) w.Str(Path.GetFileName(r.TrimEnd('\\')));
            w.I32(items.Count);
            foreach (var i in items) { w.Str(i.Item1); w.I64(i.Item3); }
            WriteFrame(s, K_FILES, w.B, 0, w.P);

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
                        Activity = string.Format("Enviando archivos… {0:0}%", 100.0 * sent / Math.Max(1, total));
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
            if (total > 20 << 20) Say("Recibiendo archivos de la otra PC…");
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
                        Activity = string.Format("Recibiendo archivos… {0:0}%", 100.0 * got / Math.Max(1, total));
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
            if (total > 20 << 20) Say("Archivos listos: pegalos con Ctrl+V");
            return dir;
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
            Log.Info("could not set clipboard (busy)");
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
