using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace Cruce
{
    public sealed class PeerInfo
    {
        public uint Session;
        public string Name = "";
        public Mon[] Mons = new Mon[0];
        public IPEndPoint Ep;
        public Side SideOfMe;      // where *we* are, from the peer's point of view
        public long SideStamp;
        public bool Elevated;
        public int TcpPort;
    }

    public sealed class HelloInfo
    {
        public string Name;
        public Mon[] Mons;
        public Side PeerSide;
        public long SideStamp;
        public bool Elevated;
        public int TcpPort;
    }

    public interface ILinkHandler
    {
        HelloInfo GetHello();
        void OnPeerUp(PeerInfo p);
        void OnPeerInfo(PeerInfo p);
        void OnPeerDown();
        void OnReliable(byte type, byte[] data);
        void OnMove(int x, int y);
        void OnTick(long nowUs);
    }

    public sealed class LinkStats
    {
        public double RttMs = -1, RttP95Ms = -1, RttMaxMs = -1, LossPct, PpsOut, PpsIn;
        public bool Qos;
    }

    /// <summary>
    /// UDP link to the other PC.
    /// - Mouse position is sent as an absolute, sequence-numbered "latest state": a lost packet is
    ///   simply superseded by the next one, never retransmitted, so loss can't add lag or drift.
    /// - Clicks, keys, wheel and control events travel on a reliable, ordered channel. Every outgoing
    ///   packet piggybacks all still-unacknowledged events, so on lossy Wi-Fi a drop is repaired by
    ///   the very next packet (~1 ms) instead of waiting for a retransmit timeout like TCP.
    /// - While active (or the cursor is near the crossing edge) a 20 ms keepalive keeps the laptop's
    ///   Wi-Fi radio out of power-save, which is the main source of 100+ ms spikes.
    /// </summary>
    public sealed class Link : IDisposable
    {
        const byte T_HELLO = 1, T_DATA = 2, T_BYE = 3, PROTO = 1;
        const int RelBudget = 1100;
        public const int PortSpan = 20; /* if the base port is taken (in use or reserved by Windows), the next free one is used */
        public int BoundPort;
        public static bool Trace;

        sealed class Rel { public uint Seq; public byte Type; public byte[] Data; }

        readonly object gate = new object();
        readonly Socket sock;
        readonly PacketCrypto crypto;
        readonly ILinkHandler h;
        readonly int peerPort;
        readonly IPAddress fixedPeer;
        public readonly uint Session;

        PeerInfo peer;
        IPEndPoint lastKnownEp;
        long lastHeard;

        readonly List<Rel> outQ = new List<Rel>();
        uint nextRseq = 1;
        uint inDelivered;
        readonly Dictionary<uint, Rel> inBuf = new Dictionary<uint, Rel>();

        bool hasMove, movePending;
        int mx, my;
        uint moveSeq;
        long lastMoveSend;
        bool anyInMove;
        uint lastInMoveSeq;

        long lastDataSend, lastHelloSend, lastNeedHello;
        uint peerTs;
        long peerTsAt;

        public int MinMoveIntervalUs = 1000;
        public volatile bool Active;
        long fastUntil;
        public double DropRate;                 // test hook: simulated outgoing loss
        readonly Random dropRng = new Random();

        Thread rxThread, timerThread;
        volatile bool running;

        // stats
        readonly uint[] rttRing = new uint[2048];
        readonly long[] rttAt = new long[2048];
        int rttIdx;
        double rttEwma;
        uint peerSid;
        long lastCtr, winLost, winRecv, winOut, winIn, winStart;
        volatile LinkStats stats = new LinkStats();
        bool qosOn;
        IntPtr qosHandle;
        uint qosFlow;
        IPEndPoint qosEp;
        IPAddress[] bcastCache;
        long bcastAt;

        public LinkStats Stats { get { return stats; } }

        public Link(Keys keys, int port, int peerPort, IPAddress fixedPeer, ILinkHandler handler)
        {
            crypto = new PacketCrypto(keys);
            h = handler;
            this.peerPort = peerPort;
            this.fixedPeer = fixedPeer;
            var r = new byte[4];
            using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider()) rng.GetBytes(r);
            Session = BitConverter.ToUInt32(r, 0) | 1;

            sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.EnableBroadcast = true;
            sock.ReceiveBufferSize = 1 << 20;
            sock.SendBufferSize = 1 << 20;
            try { sock.IOControl(unchecked((int)0x9800000C), new byte[4], null); } catch { } // ignore ICMP "port unreachable" resets
            SocketException last = null;
            for (int p = port; p < port + PortSpan; p++)
            {
                try { sock.Bind(new IPEndPoint(IPAddress.Any, p)); BoundPort = p; last = null; break; }
                catch (SocketException ex) { last = ex; Log.Info("UDP {0} no disponible ({1}), pruebo otro", p, ex.SocketErrorCode); }
            }
            if (last != null) throw last;
            Log.Info("enlace escuchando en UDP {0}", BoundPort);
        }

        public void Start()
        {
            running = true;
            rxThread = new Thread(RxLoop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "cruce-rx" };
            timerThread = new Thread(TimerLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "cruce-timer" };
            rxThread.Start();
            timerThread.Start();
        }

        public static long NowUs()
        {
            long t = Stopwatch.GetTimestamp(), f = Stopwatch.Frequency;
            return (t / f) * 1000000 + (t % f) * 1000000 / f;
        }

        public PeerInfo Peer { get { return peer; } }

        public long SinceHeardUs(long now) { return now - Interlocked.Read(ref lastHeard); }

        public void Warm(long untilUs) { lock (gate) if (untilUs > fastUntil) fastUntil = untilUs; }

        // ---------------------------------------------------------------- outgoing API

        public bool QueueReliable(byte type, byte[] data)
        {
            lock (gate)
            {
                if (peer == null) return false;
                if (outQ.Count > 4000) outQ.RemoveAt(0);
                var q = new Rel();
                q.Seq = nextRseq++; q.Type = type; q.Data = data ?? new byte[0];
                outQ.Add(q);
                SendDataLocked(NowUs());
            }
            return true;
        }

        public void SetMove(int x, int y)
        {
            lock (gate)
            {
                if (peer == null) return;
                if (hasMove && x == mx && y == my) return;
                hasMove = true;
                moveSeq++;
                mx = x; my = y;
                long now = NowUs();
                if (now - lastMoveSend >= MinMoveIntervalUs) SendDataLocked(now);
                else movePending = true;
            }
        }

        public void ResetMove()
        {
            lock (gate) { hasMove = false; movePending = false; }
        }

        public void AnnounceNow()
        {
            IPEndPoint ep;
            lock (gate) ep = peer != null ? peer.Ep : null;
            if (ep != null) SendHello(new[] { ep }, true);
        }

        // ---------------------------------------------------------------- packets

        void SendRaw(byte[] plain, int len, IPEndPoint[] eps)
        {
            var sealedPkt = crypto.Seal(plain, len);
            Interlocked.Increment(ref winOut);
            if (DropRate > 0) { lock (dropRng) if (dropRng.NextDouble() < DropRate) return; }
            foreach (var ep in eps)
            {
                if (ep == null) continue;
                try { sock.SendTo(sealedPkt, ep); } catch (SocketException) { } catch (ObjectDisposedException) { }
            }
        }

        void SendDataLocked(long now)
        {
            if (peer == null) return;
            var w = new WBuf(96);
            w.U8(T_DATA);
            w.U32(Session);
            w.U32(peer.Session);
            w.U32(inDelivered);
            w.U32((uint)now);
            if (peerTs != 0) { w.U32(peerTs); w.U32((uint)(now - peerTsAt)); } else { w.U32(0); w.U32(0); }
            w.U8(hasMove ? 1 : 0);
            if (hasMove) { w.U32(moveSeq); w.I32(mx); w.I32(my); }
            int countPos = w.P;
            w.U8(0);
            int cnt = 0;
            foreach (var q in outQ)
            {
                if (cnt == 255 || (cnt > 0 && w.P + 7 + q.Data.Length > RelBudget)) break;
                w.U32(q.Seq); w.U8(q.Type); w.U16(q.Data.Length); w.Bytes(q.Data, 0, q.Data.Length);
                cnt++;
            }
            w.B[countPos] = (byte)cnt;
            movePending = false;
            if (hasMove) lastMoveSend = now;
            lastDataSend = now;
            SendRaw(w.B, w.P, new[] { peer.Ep });
        }

        void SendData()
        {
            lock (gate) SendDataLocked(NowUs());
        }

        void SendHello(IPEndPoint[] eps, bool reply)
        {
            var hi = h.GetHello();
            var w = new WBuf(256);
            w.U8(T_HELLO);
            w.U32(Session);
            w.U8(PROTO);
            w.U8(reply ? 1 : 0);
            w.Str(hi.Name);
            w.U8((int)hi.PeerSide);
            w.I64(hi.SideStamp);
            w.U8(hi.Elevated ? 1 : 0);
            var mons = hi.Mons ?? new Mon[0];
            w.U8(mons.Length);
            foreach (var m in mons) { w.I32(m.L); w.I32(m.T); w.I32(m.R); w.I32(m.B); w.U16(m.Dpi); }
            w.U16(hi.TcpPort);
            lock (gate) lastHelloSend = NowUs();
            if (Trace) Log.Info("[{0}] hello{1} -> {2} destinos (1ro {3})", BoundPort, reply ? "(reply)" : "", eps.Length, eps.Length > 0 ? eps[0].ToString() : "-");
            SendRaw(w.B, w.P, eps);
        }

        IPEndPoint[] DiscoveryTargets()
        {
            long now = NowUs();
            if (bcastCache == null || now - bcastAt > 10000000)
            {
                var list = new List<IPAddress> { IPAddress.Broadcast };
                try
                {
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                            var a = ua.Address.GetAddressBytes();
                            var m = ua.IPv4Mask.GetAddressBytes();
                            if (m.All(b => b == 0)) continue;
                            for (int i = 0; i < 4; i++) a[i] = (byte)(a[i] | ~m[i]);
                            list.Add(new IPAddress(a));
                        }
                    }
                }
                catch { }
                bcastCache = list.Distinct().ToArray();
                bcastAt = now;
            }
            var eps = new List<IPEndPoint>();
            for (int p = peerPort; p < peerPort + PortSpan; p++)
            {
                foreach (var a in bcastCache) eps.Add(new IPEndPoint(a, p));
                if (fixedPeer != null) eps.Add(new IPEndPoint(fixedPeer, p));
            }
            if (lastKnownEp != null) eps.Insert(0, lastKnownEp);
            return eps.ToArray();
        }

        // ---------------------------------------------------------------- receive

        void RxLoop()
        {
            Native.BoostThread();
            var buf = new byte[65536];
            while (running)
            {
                EndPoint any = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = sock.ReceiveFrom(buf, ref any); }
                catch (SocketException) { if (!running) break; continue; }
                catch (ObjectDisposedException) { break; }
                uint sid; long ctr;
                var p = crypto.Open(buf, n, out sid, out ctr);
                if (p == null || p.Length == 0) continue;
                var src = (IPEndPoint)any;
                try
                {
                    switch (p[0])
                    {
                        case T_HELLO: OnHello(p, new IPEndPoint(src.Address, src.Port), sid, ctr); break;
                        case T_DATA: OnData(p, new IPEndPoint(src.Address, src.Port), sid, ctr); break;
                        case T_BYE: OnBye(p); break;
                    }
                }
                catch (Exception ex) { Log.Error(ex, "rx"); }
            }
        }

        void CountRx(uint sid, long ctr)
        {
            // caller holds gate
            Interlocked.Increment(ref winIn);
            if (sid != peerSid) { peerSid = sid; lastCtr = ctr; winRecv++; return; }
            if (ctr <= lastCtr) return; // duplicate (same broadcast via several routes)
            if (ctr - lastCtr > 1 && ctr - lastCtr < 10000) { winLost += ctr - lastCtr - 1; minLost += ctr - lastCtr - 1; }
            winRecv++; minRecv++; minPkts++;
            lastCtr = ctr;
        }

        void ResetChannelLocked()
        {
            outQ.Clear(); nextRseq = 1;
            inDelivered = 0; inBuf.Clear();
            hasMove = false; movePending = false; moveSeq = 0;
            anyInMove = false; lastInMoveSeq = 0;
            peerTs = 0;
        }

        void OnHello(byte[] p, IPEndPoint ep, uint sid, long ctr)
        {
            var r = new RBuf(p, 1, p.Length - 1);
            uint s = r.U32();
            if (s == Session) return; // our own broadcast looping back
            if (Trace) Log.Info("[{0}] hello recibido de {1} ses {2:X}", BoundPort, ep, s);
            int proto = r.U8();
            bool reply = r.U8() == 1;
            string name = r.Str();
            var side = (Side)r.U8();
            long stamp = r.I64();
            bool elev = r.U8() == 1;
            int mc = r.U8();
            var mons = new Mon[mc];
            for (int i = 0; i < mc; i++) { mons[i].L = r.I32(); mons[i].T = r.I32(); mons[i].R = r.I32(); mons[i].B = r.I32(); mons[i].Dpi = r.U16(); }
            int tcpPort = r.Left >= 2 ? r.U16() : 0;
            if (proto != PROTO) return;

            bool isNew = false, changed = false;
            PeerInfo snap;
            lock (gate)
            {
                if (peer == null || peer.Session != s)
                {
                    ResetChannelLocked();
                    peer = new PeerInfo();
                    peer.Session = s;
                    peerSid = 0;
                    isNew = true;
                }
                else
                {
                    changed = peer.Name != name || !Geo.Same(peer.Mons, mons) || peer.SideStamp != stamp || peer.SideOfMe != side || peer.Elevated != elev;
                }
                CountRx(sid, ctr);
                peer.TcpPort = tcpPort; peer.Name = name; peer.Mons = mons; peer.SideOfMe = side; peer.SideStamp = stamp; peer.Elevated = elev;
                if (peer.Ep == null || !peer.Ep.Equals(ep)) { peer.Ep = ep; }
                lastKnownEp = ep;
                Interlocked.Exchange(ref lastHeard, NowUs());
                snap = peer;
            }
            ApplyQos(ep);
            if (!reply) SendHello(new[] { ep }, true);
            if (isNew) { Log.Info("peer up: {0} at {1}", name, ep); h.OnPeerUp(snap); }
            else if (changed) h.OnPeerInfo(snap);
        }

        void OnBye(byte[] p)
        {
            var r = new RBuf(p, 1, p.Length - 1);
            uint s = r.U32();
            bool down = false;
            lock (gate)
            {
                if (peer != null && peer.Session == s) { peer = null; ResetChannelLocked(); down = true; }
            }
            if (down) { Log.Info("peer said bye"); h.OnPeerDown(); }
        }

        void OnData(byte[] p, IPEndPoint ep, uint sid, long ctr)
        {
            var r = new RBuf(p, 1, p.Length - 1);
            uint s = r.U32();
            uint dst = r.U32();
            List<Rel> deliver = null;
            bool doMove = false, sendAck = false, needHello = false;
            int x = 0, y = 0;
            long now = NowUs();
            lock (gate)
            {
                if (peer == null || peer.Session != s || dst != Session)
                {
                    if (now - lastNeedHello > 200000) { lastNeedHello = now; needHello = true; }
                }
                else
                {
                    CountRx(sid, ctr);
                    Interlocked.Exchange(ref lastHeard, now);
                    if (!peer.Ep.Equals(ep)) peer.Ep = ep;
                    uint ack = r.U32();
                    uint ts = r.U32();
                    uint echo = r.U32();
                    uint hold = r.U32();
                    int flags = r.U8();

                    if (outQ.Count > 0) outQ.RemoveAll(q => (int)(q.Seq - ack) <= 0);

                    if (echo != 0)
                    {
                        uint rtt = unchecked((uint)now - echo - hold);
                        if (rtt < 5000000) AddRttLocked(rtt, now);
                    }
                    peerTs = ts == 0 ? 1 : ts;
                    peerTsAt = now;

                    if ((flags & 1) != 0)
                    {
                        uint ms = r.U32();
                        int px = r.I32(), py = r.I32();
                        if (!anyInMove || (int)(ms - lastInMoveSeq) > 0)
                        {
                            anyInMove = true; lastInMoveSeq = ms;
                            doMove = true; x = px; y = py;
                        }
                    }

                    int cnt = r.U8();
                    for (int i = 0; i < cnt; i++)
                    {
                        var q = new Rel();
                        q.Seq = r.U32(); q.Type = (byte)r.U8();
                        int ln = r.U16();
                        q.Data = r.Bytes(ln);
                        sendAck = true;
                        if ((int)(q.Seq - inDelivered) <= 0) continue;
                        if (q.Seq == inDelivered + 1)
                        {
                            if (deliver == null) deliver = new List<Rel>();
                            deliver.Add(q);
                            inDelivered = q.Seq;
                            Rel nx;
                            while (inBuf.TryGetValue(inDelivered + 1, out nx))
                            {
                                inBuf.Remove(inDelivered + 1);
                                deliver.Add(nx);
                                inDelivered++;
                            }
                        }
                        else if (!inBuf.ContainsKey(q.Seq) && inBuf.Count < 4000) inBuf[q.Seq] = q;
                    }
                }
            }
            if (needHello) { SendHello(new[] { ep }, false); return; }
            if (deliver != null) foreach (var q in deliver) h.OnReliable(q.Type, q.Data);
            if (doMove) h.OnMove(x, y);
            if (sendAck) SendData();
        }

        // per-minute aggregates for the diagnostic log
        readonly int[] minHist = new int[1001]; // 1 ms buckets, last = 1 s+
        long minCount, minSumUs, minMaxUs, minLost, minRecv, minPkts, minSpikes, lastSpikeLog;

        /// <summary>Returns a one-line summary of the last period and resets it (null if no samples).</summary>
        public string TakeSummary()
        {
            lock (gate)
            {
                string s = null;
                if (minCount > 0)
                {
                    long target = (long)(minCount * 0.95), acc = 0; int p95 = 0;
                    for (int i = 0; i < minHist.Length; i++) { acc += minHist[i]; if (acc > target) { p95 = i; break; } }
                    long tot = minLost + minRecv;
                    s = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "rtt avg {0:0.0} ms, p95 {1} ms, max {2:0.0} ms, picos>30ms {3}, perdida {4:0.00}% ({5}/{6}), paquetes {7}, qos {8}",
                        minSumUs / 1000.0 / minCount, p95, minMaxUs / 1000.0, minSpikes, tot > 0 ? 100.0 * minLost / tot : 0, minLost, tot, minPkts, qosOn ? "si" : "no");
                }
                Array.Clear(minHist, 0, minHist.Length);
                minCount = minSumUs = minMaxUs = minLost = minRecv = minPkts = minSpikes = 0;
                return s;
            }
        }

        void AddRttLocked(uint us, long now)
        {
            minHist[Math.Min(1000, (int)(us / 1000))]++;
            minCount++; minSumUs += us; if (us > minMaxUs) minMaxUs = us;
            if (us > 30000)
            {
                minSpikes++;
                if (now - lastSpikeLog > 2000000) { lastSpikeLog = now; Log.Info("PICO de latencia: {0:0} ms (activo={1})", us / 1000.0, Active); }
            }
            rttRing[rttIdx & 2047] = us;
            rttAt[rttIdx & 2047] = now;
            rttIdx++;
            rttEwma = rttEwma == 0 ? us : rttEwma * 0.9 + us * 0.1;
        }

        // ---------------------------------------------------------------- timer

        void TimerLoop()
        {
            // 1 ms timer resolution only while it matters (active, warming up, or with pending
            // traffic); otherwise tick slowly so an idle laptop is not kept awake on battery.
            bool hiRes = false;
            long lastBusy = 0;
            try
            {
                long lastStats = 0;
                while (running)
                {
                    bool busy;
                    lock (gate) busy = Active || movePending || outQ.Count > 0 || NowUs() < fastUntil;
                    if (busy) lastBusy = NowUs();
                    bool wantHi = busy || NowUs() - lastBusy < 2000000;
                    if (wantHi != hiRes)
                    {
                        if (wantHi) Native.timeBeginPeriod(1); else Native.timeEndPeriod(1);
                        hiRes = wantHi;
                    }
                    long before = NowUs();
                    Thread.Sleep(hiRes ? 1 : 15);
                    long now = NowUs();
                    if (now - before > 100000 && peer != null) Log.Info("FRENADO: el sistema pausó a Cruce {0:0} ms (CPU saturada, suspensión o ahorro de energía)", (now - before) / 1000.0);
                    bool bcast = false, helloPeer = false, down = false;
                    IPEndPoint peerEp = null;
                    lock (gate)
                    {
                        if (peer != null)
                        {
                            bool send = false;
                            if (movePending && now - lastMoveSend >= MinMoveIntervalUs) send = true;
                            long rto = Math.Max(6000, (long)(rttEwma * 1.5) + 2000);
                            if (outQ.Count > 0 && now - lastDataSend >= rto) send = true;
                            long ka = (Active || now < fastUntil) ? 20000 : 30000; // always keep the laptop radio awake while linked
                            if (now - lastDataSend >= ka) send = true;
                            if (send) SendDataLocked(now);
                            if (now - lastHelloSend >= 2000000) { helloPeer = true; peerEp = peer.Ep; }
                            if (now - Interlocked.Read(ref lastHeard) > 3500000) down = true;
                        }
                        else if (now - lastHelloSend >= 1000000) bcast = true;
                    }
                    if (down)
                    {
                        lock (gate)
                        {
                            down = peer != null && NowUs() - Interlocked.Read(ref lastHeard) > 3500000;
                            if (down) { peer = null; ResetChannelLocked(); }
                        }
                        if (down) { Log.Info("peer timed out"); h.OnPeerDown(); }
                    }
                    else if (helloPeer) SendHello(new[] { peerEp }, true);
                    if (bcast) SendHello(DiscoveryTargets(), false);

                    h.OnTick(now);

                    if (now - lastStats >= 500000) { lastStats = now; ComputeStats(now); }
                }
            }
            catch (Exception ex) { Log.Error(ex, "timer"); }
            finally { if (hiRes) Native.timeEndPeriod(1); }
        }

        void ComputeStats(long now)
        {
            var st = new LinkStats();
            var samples = new List<uint>();
            lock (gate)
            {
                int n = Math.Min(rttIdx, 2048);
                for (int i = 0; i < n; i++)
                {
                    int k = (rttIdx - 1 - i) & 2047;
                    if (now - rttAt[k] > 3000000) break;
                    samples.Add(rttRing[k]);
                }
                double secs = Math.Max(0.001, (now - winStart) / 1e6);
                if (winStart == 0) secs = 0.5;
                st.PpsOut = Interlocked.Read(ref winOut) / secs;
                st.PpsIn = Interlocked.Read(ref winIn) / secs;
                long tot = winRecv + winLost;
                st.LossPct = tot > 0 ? 100.0 * winLost / tot : 0;
                if (now - winStart > 3000000)
                {
                    winStart = now; winLost = 0; winRecv = 0;
                    Interlocked.Exchange(ref winOut, 0); Interlocked.Exchange(ref winIn, 0);
                }
                st.Qos = qosOn;
            }
            if (samples.Count > 0)
            {
                samples.Sort();
                st.RttMs = samples.Average(v => (double)v) / 1000.0;
                st.RttP95Ms = samples[Math.Min(samples.Count - 1, (int)(samples.Count * 0.95))] / 1000.0;
                st.RttMaxMs = samples[samples.Count - 1] / 1000.0;
            }
            else if (stats.RttMs >= 0 && peer != null)
            {
                st.RttMs = stats.RttMs; st.RttP95Ms = stats.RttP95Ms; st.RttMaxMs = stats.RttMaxMs;
            }
            stats = st;
        }

        // ---------------------------------------------------------------- QoS (Wi-Fi WMM voice queue)

        [StructLayout(LayoutKind.Sequential)]
        struct QOS_VERSION { public ushort Major, Minor; }

        [DllImport("qwave.dll", SetLastError = true)]
        static extern bool QOSCreateHandle(ref QOS_VERSION v, out IntPtr handle);
        [DllImport("qwave.dll", SetLastError = true)]
        static extern bool QOSAddSocketToFlow(IntPtr h, IntPtr socket, byte[] dest, int trafficType, uint flags, ref uint flowId);
        [DllImport("qwave.dll", SetLastError = true)]
        static extern bool QOSRemoveSocketFromFlow(IntPtr h, IntPtr socket, uint flowId, uint flags);
        [DllImport("qwave.dll", SetLastError = true)]
        static extern bool QOSCloseHandle(IntPtr h);

        void ApplyQos(IPEndPoint ep)
        {
            lock (gate)
            {
                if (qosEp != null && qosEp.Equals(ep)) return;
                qosEp = ep;
            }
            try
            {
                if (qosHandle == IntPtr.Zero)
                {
                    var v = new QOS_VERSION { Major = 1, Minor = 0 };
                    if (!QOSCreateHandle(ref v, out qosHandle)) { qosHandle = IntPtr.Zero; return; }
                }
                if (qosFlow != 0) { QOSRemoveSocketFromFlow(qosHandle, sock.Handle, qosFlow, 0); qosFlow = 0; }
                var sa = new byte[16];
                sa[0] = 2; // AF_INET
                sa[2] = (byte)(ep.Port >> 8); sa[3] = (byte)ep.Port;
                Buffer.BlockCopy(ep.Address.GetAddressBytes(), 0, sa, 4, 4);
                uint flow = 0;
                bool ok = QOSAddSocketToFlow(qosHandle, sock.Handle, sa, 4 /* QOSTrafficTypeVoice */, 2 /* NON_ADAPTIVE */, ref flow);
                qosFlow = ok ? flow : 0;
                qosOn = ok;
                if (!ok) Log.Info("QoS not applied (err {0})", Marshal.GetLastWin32Error());
            }
            catch (Exception ex) { Log.Info("QoS unavailable: {0}", ex.Message); }
        }

        public void Dispose()
        {
            IPEndPoint ep = null;
            lock (gate) if (peer != null) ep = peer.Ep;
            if (ep != null)
            {
                var w = new WBuf(8);
                w.U8(T_BYE); w.U32(Session);
                SendRaw(w.B, w.P, new[] { ep });
            }
            running = false;
            try { sock.Close(); } catch { }
            if (timerThread != null) timerThread.Join(500);
            if (rxThread != null) rxThread.Join(500);
            try { if (qosHandle != IntPtr.Zero) QOSCloseHandle(qosHandle); } catch { }
        }
    }
}
