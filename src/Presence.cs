using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;

namespace Cruce
{
    /// <summary>
    /// Publishes who has the keyboard/mouse so other local apps (e.g. an activity tracker) can tell
    /// "working on this PC" from "this PC is only driving the other one".
    ///
    /// Contract — HKCU\Software\Cruce\Presence:
    ///   Mode    REG_SZ    "Local" | "Remote" | "Controlled"
    ///   Peer    REG_SZ    other PC's name ("" when not linked)
    ///   Since   REG_QWORD Unix ms (UTC) when the current mode started
    ///   Pid     REG_DWORD this process id (readers ignore the value if the process is gone)
    ///   Version REG_DWORD 1
    ///
    /// Nothing here runs on the input path: a low-priority thread samples the engine's mode every
    /// 100 ms and writes only when it changes (rapid flips coalesce to the latest state).
    /// </summary>
    public static class Presence
    {
        public static string KeyPath = @"Software\Cruce\Presence";

        static volatile Mode wantMode = Mode.Local;
        static volatile string wantPeer = "";
        static long wantSince = NowMs();
        static int dirty = 1;
        static bool errorLogged;
        static Mode lastLogged = (Mode)(-1);
        static Thread worker;
        static volatile bool running;
        static readonly int pid = Process.GetCurrentProcess().Id;

        static long NowMs() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }

        /// <summary>Records the desired state; cheap and non-blocking (the write happens elsewhere).</summary>
        public static void Set(Mode mode, string peer)
        {
            peer = peer ?? "";
            if (mode == wantMode && peer == wantPeer) return;
            if (mode != wantMode) Interlocked.Exchange(ref wantSince, NowMs());
            wantMode = mode;
            wantPeer = peer;
            Interlocked.Exchange(ref dirty, 1);
        }

        /// <summary>Starts the watcher. <paramref name="mode"/>/<paramref name="peer"/> are read every 100 ms.</summary>
        public static void Start(Func<Mode> mode, Func<string> peer)
        {
            Set(Mode.Local, "");
            Interlocked.Exchange(ref wantSince, NowMs());
            Flush();
            running = true;
            worker = new Thread(() =>
            {
                int tick = 0;
                while (running)
                {
                    Thread.Sleep(100);
                    try
                    {
                        Set(mode(), peer() ?? "");
                        if (++tick % 20 == 0 && !OwnsKey()) Interlocked.Exchange(ref dirty, 1); // self-heal every 2 s
                        if (Interlocked.Exchange(ref dirty, 0) == 1) Write();
                    }
                    catch (Exception ex) { LogOnce(ex); }
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "cruce-presence" };
            worker.Start();
        }

        /// <summary>Writes the pending state now (used at startup, on exit and by the self-test).</summary>
        public static void Flush()
        {
            Interlocked.Exchange(ref dirty, 0);
            Write();
        }

        /// <summary>On normal exit or update: leave "Local" behind.</summary>
        public static void Stop()
        {
            running = false;
            Set(Mode.Local, "");
            Flush();
        }

        static bool OwnsKey()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (k == null) return false;
                    var v = k.GetValue("Pid");
                    return v is int && (int)v == pid;
                }
            }
            catch { return true; } // can't read: don't spin on rewrites
        }

        static void Write()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    k.SetValue("Mode", wantMode.ToString(), RegistryValueKind.String);
                    k.SetValue("Peer", wantPeer ?? "", RegistryValueKind.String);
                    k.SetValue("Since", Interlocked.Read(ref wantSince), RegistryValueKind.QWord);
                    k.SetValue("Pid", pid, RegistryValueKind.DWord);
                    k.SetValue("Version", 1, RegistryValueKind.DWord);
                }
                if (wantMode != lastLogged) { lastLogged = wantMode; Log.Info("presencia: {0}{1}", wantMode, string.IsNullOrEmpty(wantPeer) ? "" : " (otra PC: " + wantPeer + ")"); }
            }
            catch (Exception ex) { LogOnce(ex); }
        }

        static void LogOnce(Exception ex)
        {
            if (errorLogged) return;
            errorLogged = true;
            Log.Info("presencia: no pude escribir en el registro ({0}); sigo sin publicarla", ex.Message);
        }
    }
}
