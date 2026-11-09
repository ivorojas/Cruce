using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Cruce
{
    // ---------------------------------------------------------------------------------------------------------------
    // Audio over the link. The sending PC records what it is playing (WASAPI loopback of the default output), mutes
    // its own speakers while it does, and streams 48 kHz stereo 16-bit PCM in 5 ms packets over the encrypted UDP
    // link. The other PC plays them on its default output (e.g. the headphones) with a small jitter buffer.
    // ---------------------------------------------------------------------------------------------------------------

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr h);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float db, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    static class Wasapi
    {
        public const int Render = 0, Multimedia = 1, Shared = 0, LoopbackFlag = 0x00020000, ClsCtxAll = 23;
        public static Guid IidAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        public static Guid IidCapture = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        public static Guid IidRender = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
        public static Guid IidVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
        static readonly Guid IeeeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");

        public sealed class Fmt { public int Rate, Channels, Bits, Block; public bool Float; }

        public static Fmt Read(IntPtr p)
        {
            var f = new Fmt();
            int tag = (ushort)Marshal.ReadInt16(p, 0);
            f.Channels = (ushort)Marshal.ReadInt16(p, 2);
            f.Rate = Marshal.ReadInt32(p, 4);
            f.Block = (ushort)Marshal.ReadInt16(p, 12);
            f.Bits = (ushort)Marshal.ReadInt16(p, 14);
            f.Float = tag == 3;
            if (tag == 0xFFFE) f.Float = (Guid)Marshal.PtrToStructure(p + 24, typeof(Guid)) == IeeeFloat;
            return f;
        }

        public static IMMDevice DefaultOutput()
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
            IMMDevice dev;
            Check(en.GetDefaultAudioEndpoint(Render, Multimedia, out dev), "dispositivo de salida");
            return dev;
        }

        public static T Activate<T>(IMMDevice dev, Guid iid) where T : class
        {
            object o;
            Check(dev.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out o), "activar " + typeof(T).Name);
            return (T)o;
        }

        public static void Check(int hr, string what) { if (hr < 0) throw new COMException(what + " (0x" + hr.ToString("X8") + ")", hr); }

        /// <summary>One sample of channel ch at frame i of a device buffer, as -1..1.</summary>
        public static unsafe float Sample(IntPtr buf, Fmt f, int frame, int ch)
        {
            IntPtr p = buf + frame * f.Block + ch * (f.Bits / 8);
            if (f.Float) return f.Bits == 64 ? (float)*(double*)p : *(float*)p; // no per-sample allocations
            switch (f.Bits)
            {
                case 16: return Marshal.ReadInt16(p) / 32768f;
                case 24: return ((Marshal.ReadByte(p) | (Marshal.ReadByte(p, 1) << 8) | ((sbyte)Marshal.ReadByte(p, 2) << 16))) / 8388608f;
                case 32: return Marshal.ReadInt32(p) / 2147483648f;
            }
            return 0;
        }
    }

    // ------------------------------------------------------------------------------------------------- sender

    /// <summary>Records this PC's output (loopback), mutes its speakers meanwhile, and hands 5 ms packets to send.</summary>
    public sealed class AudioSender
    {
        public const int Rate = 48000, PacketFrames = 240; // 5 ms of 48 kHz stereo 16-bit = 960 bytes
        readonly Action<byte[], int> send;
        Thread thread;
        volatile bool run;
        public volatile bool Paused;      // e.g. while this PC is itself playing the other PC's audio (no echo loop)
        public volatile string State = "";

        public AudioSender(Action<byte[], int> send) { this.send = send; }

        public bool Running { get { return run; } }

        public void Start()
        {
            if (run) return;
            run = true;
            thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "cruce-audio-out" };
            thread.Start();
        }

        public void Stop() { run = false; }

        /// <summary>Stops and waits (up to ms) so the speakers are unmuted before returning.</summary>
        public void StopAndWait(int ms)
        {
            run = false;
            var t = thread;
            if (t != null) try { t.Join(ms); } catch { }
        }

        static readonly string MuteMark = Path.Combine(Config.Dir, "audio-silenciado-por-cruce");

        /// <summary>If Cruce died while it had muted the speakers, give them back at the next start.</summary>
        public static void RestoreMuteIfLeft()
        {
            if (!File.Exists(MuteMark)) return;
            try
            {
                var vol = Wasapi.Activate<IAudioEndpointVolume>(Wasapi.DefaultOutput(), Wasapi.IidVolume);
                var ctx = Guid.Empty;
                vol.SetMute(false, ref ctx);
                Log.Info("audio: devuelvo el sonido de esta PC (había quedado silenciada por Cruce)");
            }
            catch (Exception ex) { Log.Info("audio: no pude devolver el sonido: {0}", ex.Message); }
            try { File.Delete(MuteMark); } catch { }
        }

        /// <summary>Mute this PC's speakers while sending. Off when the other PC sends its audio here too: then this PC
        /// may be the one with the headphones, and the user mutes whichever PC they like.</summary>
        public volatile bool MuteLocal = true;

        /// <summary>True when capturing "everything except Cruce" (Windows 10 2004+): the other PC's sound that Cruce plays
        /// here is not sent back, so both PCs can send at the same time without an echo loop.</summary>
        public volatile bool ExcludesOwnPlayback;

        void Loop()
        {
            while (run)
            {
                IAudioClient client = null;
                IAudioEndpointVolume vol = null;
                bool mutedByUs = false;
                var evt = new AutoResetEvent(false);
                try
                {
                    Wasapi.Fmt f;
                    client = ProcessLoopback.Open(evt, out f);
                    ExcludesOwnPlayback = client != null;
                    if (client == null)
                    {
                        // Older Windows: whole-device loopback (it would also record what Cruce plays: echo guard = Paused)
                        var dev0 = Wasapi.DefaultOutput();
                        client = Wasapi.Activate<IAudioClient>(dev0, Wasapi.IidAudioClient);
                        IntPtr pf;
                        Wasapi.Check(client.GetMixFormat(out pf), "formato");
                        f = Wasapi.Read(pf);
                        Wasapi.Check(client.Initialize(Wasapi.Shared, Wasapi.LoopbackFlag, 2000000, 0, pf, IntPtr.Zero), "iniciar captura");
                        Marshal.FreeCoTaskMem(pf);
                    }
                    object o;
                    var iid = Wasapi.IidCapture;
                    Wasapi.Check(client.GetService(ref iid, out o), "captura");
                    var cap = (IAudioCaptureClient)o;
                    vol = Wasapi.Activate<IAudioEndpointVolume>(Wasapi.DefaultOutput(), Wasapi.IidVolume);

                    Wasapi.Check(client.Start(), "arrancar captura");
                    Log.Info("audio: mando el sonido de esta PC a la otra ({0} Hz, {1} canales, {2} bits{3}; {4})", f.Rate, f.Channels, f.Bits, f.Float ? " float" : "",
                        ExcludesOwnPlayback ? "todo menos lo que reproduce Cruce: se puede mandar desde las dos PCs a la vez" : "toda la salida");
                    State = "on";
                    double step = (double)f.Rate / Rate, pos = 0;   // linear resampling to 48 kHz
                    float lastL = 0, lastR = 0;
                    var outBuf = new byte[6 + PacketFrames * 4];
                    int outFrames = 0;
                    uint seq = 0;
                    Native.timeBeginPeriod(1);
                    try
                    {
                        while (run)
                        {
                            // speakers: muted only while this is a one-way send (see MuteLocal)
                            bool wantMute = MuteLocal;
                            if (wantMute && !mutedByUs)
                            {
                                bool already; vol.GetMute(out already);
                                if (!already) { var ctx = Guid.Empty; vol.SetMute(true, ref ctx); mutedByUs = true; try { File.WriteAllText(MuteMark, DateTime.Now.ToString("o")); } catch { } Log.Info("audio: silencio los parlantes de esta PC"); }
                            }
                            else if (!wantMute && mutedByUs)
                            {
                                var ctx = Guid.Empty; vol.SetMute(false, ref ctx); mutedByUs = false;
                                try { File.Delete(MuteMark); } catch { }
                                Log.Info("audio: la otra PC también manda su sonido: los parlantes de esta PC vuelven a sonar (silenciá a mano la que no uses)");
                            }

                            if (ExcludesOwnPlayback) evt.WaitOne(20); else Thread.Sleep(3);
                            uint n;
                            while (run && cap.GetNextPacketSize(out n) == 0 && n > 0)
                            {
                                IntPtr data; uint frames, flags; ulong dp, qp;
                                if (cap.GetBuffer(out data, out frames, out flags, out dp, out qp) < 0) break;
                                bool silent = (flags & 0x2) != 0;
                                for (int i = 0; i < frames; i++)
                                {
                                    float l = silent ? 0 : Wasapi.Sample(data, f, i, 0);
                                    float r = silent ? 0 : f.Channels > 1 ? Wasapi.Sample(data, f, i, 1) : l;
                                    // emit output frames that fall between the previous input frame and this one
                                    while (pos <= 1.0)
                                    {
                                        float ol = lastL + (l - lastL) * (float)pos, or = lastR + (r - lastR) * (float)pos;
                                        int o2 = 6 + outFrames * 4;
                                        short sl = (short)Math.Max(-32768, Math.Min(32767, ol * 32767f)), sr = (short)Math.Max(-32768, Math.Min(32767, or * 32767f));
                                        outBuf[o2] = (byte)sl; outBuf[o2 + 1] = (byte)(sl >> 8); outBuf[o2 + 2] = (byte)sr; outBuf[o2 + 3] = (byte)(sr >> 8);
                                        if (++outFrames == PacketFrames)
                                        {
                                            outBuf[0] = (byte)seq; outBuf[1] = (byte)(seq >> 8); outBuf[2] = (byte)(seq >> 16); outBuf[3] = (byte)(seq >> 24);
                                            outBuf[4] = (byte)PacketFrames; outBuf[5] = (byte)(PacketFrames >> 8);
                                            seq++;
                                            if (!Paused || ExcludesOwnPlayback) send(outBuf, outBuf.Length);
                                            outFrames = 0;
                                        }
                                        pos += step;
                                    }
                                    pos -= 1.0;
                                    lastL = l; lastR = r;
                                }
                                cap.ReleaseBuffer(frames);
                            }
                        }
                    }
                    finally { Native.timeEndPeriod(1); }
                    client.Stop();
                }
                catch (Exception ex)
                {
                    State = "error";
                    Log.Info("audio: la captura se cortó ({0}); reintento en 2 s", ex.Message);
                }
                finally
                {
                    if (mutedByUs && vol != null)
                    {
                        try { var ctx = Guid.Empty; vol.SetMute(false, ref ctx); } catch { }
                        try { File.Delete(MuteMark); } catch { }
                    }
                    if (client != null) try { Marshal.ReleaseComObject(client); } catch { }
                    evt.Dispose();
                }
                if (run) Thread.Sleep(2000);
            }
            State = "";
            Log.Info("audio: dejé de mandar el sonido de esta PC");
        }
    }

    // ------------------------------------------------------------------------------------------------- process loopback

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    /// <summary>
    /// "Everything this PC plays except Cruce" (Windows 10 2004+ process loopback, excluding our own process tree). Cruce
    /// plays the other PC's sound itself, so with this both PCs can send at once without sending that sound back.
    /// Returns null when Windows doesn't support it.
    /// </summary>
    static class ProcessLoopback
    {
        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        sealed class Handler : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            public IActivateAudioInterfaceAsyncOperation Op;
            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) { Op = operation; Done.Set(); }
        }

        [DllImport("Mmdevapi.dll", ExactSpelling = true)]
        static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            IntPtr activationParams, IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

        static bool unsupported;

        public static IAudioClient Open(AutoResetEvent evt, out Wasapi.Fmt fmt)
        {
            fmt = new Wasapi.Fmt { Rate = AudioSender.Rate, Channels = 2, Bits = 16, Block = 4, Float = false };
            if (unsupported) return null;
            IntPtr prm = Marshal.AllocHGlobal(12), pv = Marshal.AllocHGlobal(24), wf = Marshal.AllocHGlobal(18);
            try
            {
                Marshal.WriteInt32(prm, 0, 1);                                          // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
                Marshal.WriteInt32(prm, 4, System.Diagnostics.Process.GetCurrentProcess().Id);
                Marshal.WriteInt32(prm, 8, 1);                                          // PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE
                for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
                Marshal.WriteInt16(pv, 0, 65);                                          // VT_BLOB
                Marshal.WriteInt32(pv, 8, 12);
                Marshal.WriteIntPtr(pv, 16, prm);
                var h = new Handler();
                IActivateAudioInterfaceAsyncOperation op;
                int hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", Wasapi.IidAudioClient, pv, h, out op);
                if (hr < 0 || !h.Done.WaitOne(5000)) { unsupported = true; Log.Info("audio: este Windows no permite excluir a Cruce de la captura (0x{0:X8}); uso la salida entera", hr); return null; }
                int ar; object o;
                h.Op.GetActivateResult(out ar, out o);
                if (ar < 0 || o == null) { unsupported = true; Log.Info("audio: este Windows no permite excluir a Cruce de la captura (0x{0:X8}); uso la salida entera", ar); return null; }
                var client = (IAudioClient)o;
                // 48 kHz stereo 16-bit PCM: exactly what goes on the wire, Windows converts for us
                Marshal.WriteInt16(wf, 0, 1); Marshal.WriteInt16(wf, 2, 2); Marshal.WriteInt32(wf, 4, AudioSender.Rate);
                Marshal.WriteInt32(wf, 8, AudioSender.Rate * 4); Marshal.WriteInt16(wf, 12, 4); Marshal.WriteInt16(wf, 14, 16); Marshal.WriteInt16(wf, 16, 0);
                const int flags = 0x00020000 | 0x00040000 | unchecked((int)0x80000000) | 0x08000000; // LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY
                Wasapi.Check(client.Initialize(Wasapi.Shared, flags, 200000, 0, wf, IntPtr.Zero), "iniciar captura de procesos");
                Wasapi.Check(client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle()), "evento de captura");
                return client;
            }
            catch (Exception ex) { unsupported = true; Log.Info("audio: no pude excluir a Cruce de la captura ({0}); uso la salida entera", ex.Message); return null; }
            finally { Marshal.FreeHGlobal(prm); Marshal.FreeHGlobal(pv); Marshal.FreeHGlobal(wf); }
        }
    }

    // ------------------------------------------------------------------------------------------------- player

    /// <summary>Plays the other PC's audio on this PC's default output. Opens the device on the first packet and
    /// releases it after 2 s without audio. ~40 ms jitter buffer; drift between the two PCs' clocks is absorbed by
    /// skipping ahead when the buffer grows and re-priming when it runs dry.</summary>
    public sealed class AudioPlayer
    {
        const int Rate = AudioSender.Rate, Cap = Rate * 2;           // ring: 2 s of stereo frames
        readonly short[] ring = new short[Cap * 2];
        readonly object gate = new object();
        long writeFrames, readFrames;                                 // absolute frame counters
        volatile int lastPacketTick, havePackets;
        Thread thread;
        volatile bool run;

        public bool Active { get { return havePackets != 0 && Environment.TickCount - lastPacketTick < 600; } }

        /// <summary>From the link's receive thread: append one packet (seq u32, frames u16, PCM).</summary>
        public void Push(byte[] p, int off)
        {
            if (p.Length - off < 6) return;
            int frames = p[off + 4] | (p[off + 5] << 8);
            if (frames <= 0 || p.Length - off - 6 < frames * 4) return;
            lock (gate)
            {
                for (int i = 0; i < frames; i++)
                {
                    int o = off + 6 + i * 4;
                    long w = (writeFrames + i) % Cap;
                    ring[w * 2] = (short)(p[o] | (p[o + 1] << 8));
                    ring[w * 2 + 1] = (short)(p[o + 2] | (p[o + 3] << 8));
                }
                writeFrames += frames;
                if (writeFrames - readFrames > Cap) readFrames = writeFrames - Rate / 10; // way behind: jump close to live
            }
            lastPacketTick = Environment.TickCount; havePackets = 1;
            if (!run) { run = true; thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "cruce-audio-in" }; thread.Start(); }
        }

        void Loop()
        {
            IAudioClient client = null;
            try
            {
                var dev = Wasapi.DefaultOutput();
                client = Wasapi.Activate<IAudioClient>(dev, Wasapi.IidAudioClient);
                IntPtr pf;
                Wasapi.Check(client.GetMixFormat(out pf), "formato");
                var f = Wasapi.Read(pf);
                Wasapi.Check(client.Initialize(Wasapi.Shared, 0, 500000, 0, pf, IntPtr.Zero), "iniciar reproducción"); // 50 ms device buffer
                Marshal.FreeCoTaskMem(pf);
                uint bufFrames;
                client.GetBufferSize(out bufFrames);
                object o;
                var iid = Wasapi.IidRender;
                Wasapi.Check(client.GetService(ref iid, out o), "reproducción");
                var ren = (IAudioRenderClient)o;
                Wasapi.Check(client.Start(), "arrancar reproducción");
                Log.Info("audio: suena acá el audio de la otra PC ({0} Hz, {1} canales)", f.Rate, f.Channels);
                double step = (double)Rate / f.Rate, frac = 0;
                bool primed = false;
                long target = Rate * 40 / 1000, high = Rate * 150 / 1000;
                Native.timeBeginPeriod(1);
                try
                {
                    while (true)
                    {
                        Thread.Sleep(3);
                        if (Environment.TickCount - lastPacketTick > 2000) break; // the other side stopped
                        uint pad;
                        if (client.GetCurrentPadding(out pad) < 0) break;
                        int free = (int)(bufFrames - pad);
                        if (free <= 0) continue;
                        IntPtr data;
                        if (ren.GetBuffer((uint)free, out data) < 0) break;
                        lock (gate)
                        {
                            long have = writeFrames - readFrames;
                            if (!primed && have >= target) primed = true;
                            if (have > high) { readFrames = writeFrames - target; frac = 0; }  // too far behind: catch up
                            for (int i = 0; i < free; i++)
                            {
                                float l = 0, r = 0;
                                if (primed && writeFrames - readFrames >= 2)
                                {
                                    long a = readFrames % Cap, b = (readFrames + 1) % Cap;
                                    float t = (float)frac;
                                    l = (ring[a * 2] + (ring[b * 2] - ring[a * 2]) * t) / 32768f;
                                    r = (ring[a * 2 + 1] + (ring[b * 2 + 1] - ring[a * 2 + 1]) * t) / 32768f;
                                    frac += step;
                                    while (frac >= 1) { frac -= 1; readFrames++; }
                                }
                                else if (primed) primed = false; // ran dry: wait for ~40 ms again instead of crackling
                                Write(data, f, i, l, r);
                            }
                        }
                        ren.ReleaseBuffer((uint)free, 0);
                    }
                }
                finally { Native.timeEndPeriod(1); }
                client.Stop();
                Log.Info("audio: la otra PC dejó de mandar sonido");
            }
            catch (Exception ex) { Log.Info("audio: no pude reproducir el sonido de la otra PC ({0})", ex.Message); Thread.Sleep(1000); }
            finally
            {
                if (client != null) try { Marshal.ReleaseComObject(client); } catch { }
                run = false;
            }
        }

        static unsafe void Write(IntPtr buf, Wasapi.Fmt f, int frame, float l, float r)
        {
            for (int ch = 0; ch < f.Channels; ch++)
            {
                float v = ch == 0 ? l : ch == 1 ? r : 0;
                IntPtr p = buf + frame * f.Block + ch * (f.Bits / 8);
                if (f.Float && f.Bits == 32) *(float*)p = v;
                else if (f.Bits == 16) Marshal.WriteInt16(p, (short)Math.Max(-32768, Math.Min(32767, v * 32767f)));
                else if (f.Bits == 24) { int s = (int)Math.Max(-8388608, Math.Min(8388607, v * 8388607f)); Marshal.WriteByte(p, (byte)s); Marshal.WriteByte(p, 1, (byte)(s >> 8)); Marshal.WriteByte(p, 2, (byte)(s >> 16)); }
                else if (f.Bits == 32) Marshal.WriteInt32(p, (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, v * 2147483647.0)));
            }
        }
    }
}
