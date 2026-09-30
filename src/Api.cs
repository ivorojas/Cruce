using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Cruce
{
    /// <summary>
    /// Local API for other apps on this PC (e.g. Dictado): named pipe \\.\pipe\Cruce.Api, one JSON object per line.
    ///   {"cmd":"state"}                         → {"mode","peer","connected","self","version"}
    ///   {"cmd":"send","app":"x","data":{...}}   → relayed, encrypted, to the same app on the other PC → {"ok":true|false,"error"?}
    ///   {"cmd":"subscribe","app":"x"}           → {"ok":true}, then one line per message: {"from":"<pc>","data":{...}}
    ///   {"cmd":"subscribe","app":"_state"}      → state pushed on every change: {"from":"cruce","data":{state}}
    /// Only the current Windows user can connect. Small messages ride the low-latency reliable channel,
    /// big ones the encrypted TCP stream. Deliveries to clients run on their own thread, never on the
    /// input or network threads.
    /// </summary>
    public static class Api
    {
        public static string PipeName = "Cruce.Api"; // tests use another name
        const int SmallLimit = 1000;

        sealed class Client
        {
            public NamedPipeServerStream Pipe;
            public StreamWriter W;
            public readonly HashSet<string> Subs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly object Gate = new object();
            public volatile bool Dead;
            // Each client has its own outbox and writer thread: a client that stops reading (hung, or mid-update)
            // can never stall deliveries to the others. If its outbox fills up, it is dropped.
            public readonly BlockingCollection<string> Out = new BlockingCollection<string>(500);
        }

        static Engine engine;
        static Config cfg;
        static Func<ClipSync> clip;
        static readonly List<Client> clients = new List<Client>();
        static readonly BlockingCollection<Tuple<string, string, string>> inbox = new BlockingCollection<Tuple<string, string, string>>(2000); // app, from, json
        static volatile bool running;
        public static volatile bool DictadoConnected;

        static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 8 << 20 }; }

        public static void Start(Engine e, Config c, Func<ClipSync> cl)
        {
            engine = e; cfg = c; clip = cl;
            running = true;
            // Two listeners: several apps reconnecting at once (e.g. right after their own updates) never find the pipe busy.
            for (int i = 0; i < 2; i++) new Thread(AcceptLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "cruce-api" }.Start();
            new Thread(DeliverLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "cruce-api-out" }.Start();
        }

        static PipeSecurity Security()
        {
            // Only this user (and SYSTEM). No explicit integrity label: the pipe gets the default (medium),
            // so a non-admin app can still talk to an admin Cruce.
            var ps = new PipeSecurity();
            string sid = WindowsIdentity.GetCurrent().User.Value;
            ps.SetSecurityDescriptorSddlForm("D:P(A;;GA;;;" + sid + ")(A;;GA;;;SY)");
            return ps;
        }

        static void AcceptLoop()
        {
            PipeSecurity sec = null;
            try { sec = Security(); } catch (Exception ex) { Log.Info("api: seguridad del pipe: {0}", ex.Message); }
            int errors = 0;
            while (running)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, sec);
                    pipe.WaitForConnection();
                    var p = pipe;
                    new Thread(() => Serve(p)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "cruce-api-client" }.Start();
                    errors = 0;
                }
                catch (Exception ex)
                {
                    if (pipe != null) try { pipe.Dispose(); } catch { }
                    if (errors++ < 3) Log.Info("api: error aceptando cliente: {0}", ex.Message);
                    Thread.Sleep(Math.Min(10000, 500 * errors));
                }
            }
        }

        static void Serve(NamedPipeServerStream pipe)
        {
            var c = new Client { Pipe = pipe, W = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" } };
            lock (clients) clients.Add(c);
            new Thread(() => WriteLoop(c)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "cruce-api-write" }.Start();
            try
            {
                using (var r = new StreamReader(pipe, Encoding.UTF8))
                {
                    string line;
                    while (running && (line = r.ReadLine()) != null)
                    {
                        if (line.Trim().Length == 0) continue;
                        string reply;
                        try { reply = Handle(c, line); }
                        catch (Exception ex) { reply = Json().Serialize(new Dictionary<string, object> { { "ok", false }, { "error", ex.Message } }); }
                        if (reply != null) Write(c, reply);
                    }
                }
            }
            catch { }
            finally { Drop(c); }
        }

        static void Drop(Client c)
        {
            c.Dead = true;
            lock (clients) clients.Remove(c);
            try { c.Out.CompleteAdding(); } catch { }
            try { c.Pipe.Dispose(); } catch { } // also unblocks a writer stuck on a client that stopped reading
            RefreshDictado();
        }

        static void Write(Client c, string line)
        {
            if (c.Dead) return;
            bool queued;
            try { queued = c.Out.TryAdd(line); } catch (InvalidOperationException) { return; }
            if (!queued)
            {
                Log.Info("api: un programa dejó de leer sus mensajes ({0} pendientes); lo desconecto para no frenar a los demás", c.Out.Count);
                Drop(c);
            }
        }

        static void WriteLoop(Client c)
        {
            try
            {
                foreach (var line in c.Out.GetConsumingEnumerable())
                {
                    if (c.Dead) break;
                    c.W.WriteLine(line);
                }
            }
            catch { }
            if (!c.Dead) Drop(c);
        }

        static Dictionary<string, object> State()
        {
            var p = engine != null ? engine.Peer : null;
            return new Dictionary<string, object>
            {
                { "mode", engine != null ? engine.Mode.ToString() : "Local" },
                { "peer", p != null ? p.Name : "" },
                { "connected", p != null },
                { "self", cfg != null ? cfg.Name : Environment.MachineName },
                { "version", AppController.Version }
            };
        }

        static string Handle(Client c, string line)
        {
            var js = Json();
            var msg = js.DeserializeObject(line) as Dictionary<string, object>;
            if (msg == null) return js.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", "se esperaba un objeto JSON" } });
            object cmdObj; msg.TryGetValue("cmd", out cmdObj);
            string cmd = cmdObj as string ?? "";
            object appObj; msg.TryGetValue("app", out appObj);
            string app = (appObj as string ?? "").Trim();
            switch (cmd)
            {
                case "state":
                    return js.Serialize(State());
                case "subscribe":
                    if (app.Length == 0) return js.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", "falta app" } });
                    lock (c.Gate) c.Subs.Add(app);
                    RefreshDictado();
                    Log.Info("api: un programa se suscribió a '{0}'", app);
                    Write(c, js.Serialize(new Dictionary<string, object> { { "ok", true }, { "subscribed", app } }));
                    if (app == "_state") Write(c, js.Serialize(new Dictionary<string, object> { { "from", "cruce" }, { "data", State() } }));
                    return null;
                case "send":
                    {
                        if (app.Length == 0 || app.StartsWith("_")) return js.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", "app inválida" } });
                        object data; msg.TryGetValue("data", out data);
                        if (engine == null || engine.Peer == null) return js.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", "peer desconectado" } });
                        string payload = js.Serialize(data);
                        bool ok = Relay(app, payload);
                        return js.Serialize(ok ? new Dictionary<string, object> { { "ok", true } } : new Dictionary<string, object> { { "ok", false }, { "error", "no se pudo enviar" } });
                    }
                default:
                    return js.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", "comando desconocido: " + cmd } });
            }
        }

        static bool Subscribed(Client c, string app) { lock (c.Gate) return c.Subs.Contains(app); }

        static void RefreshDictado()
        {
            bool any;
            lock (clients) any = clients.Any(x => !x.Dead && Subscribed(x, "dictado"));
            if (any != DictadoConnected) Log.Info("api: Dictado {0} (F9 {1})", any ? "conectado" : "desconectado", any ? "lo maneja Dictado: ya no se retiene en esta PC" : "vuelve a la regla de teclas locales");
            DictadoConnected = any;
        }

        /// <summary>Small → reliable low-latency channel; big → encrypted TCP.</summary>
        static bool Relay(string app, string payloadJson)
        {
            var body = Encoding.UTF8.GetBytes(payloadJson);
            if (body.Length + app.Length <= SmallLimit && engine != null) return engine.SendAppMsg(app, payloadJson);
            var c = clip != null ? clip() : null;
            if (c == null) return false;
            c.SendAppMsg(app, payloadJson);
            return true;
        }

        /// <summary>A message for local app <paramref name="app"/> arrived from the other PC (any thread; non-blocking).</summary>
        public static void FromPeer(string app, string payloadJson)
        {
            var p = engine != null ? engine.Peer : null;
            inbox.TryAdd(Tuple.Create(app, p != null ? p.Name : "", payloadJson));
        }

        /// <summary>Mode/peer changed: push to "_state" subscribers.</summary>
        public static void PushState()
        {
            inbox.TryAdd(Tuple.Create("_state", "cruce", (string)null));
        }

        static void DeliverLoop()
        {
            foreach (var m in inbox.GetConsumingEnumerable())
            {
                try
                {
                    var js = Json();
                    object data = m.Item3 == null ? (object)State() : js.DeserializeObject(m.Item3);
                    string line = js.Serialize(new Dictionary<string, object> { { "from", m.Item2 }, { "data", data } });
                    List<Client> targets;
                    lock (clients) targets = clients.Where(x => !x.Dead && Subscribed(x, m.Item1)).ToList();
                    foreach (var t in targets) Write(t, line);
                }
                catch (Exception ex) { Log.Info("api: no pude entregar un mensaje: {0}", ex.Message); }
            }
        }
    }
}
