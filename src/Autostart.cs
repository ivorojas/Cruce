using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace Cruce
{
    /// <summary>
    /// Start with Windows through a Task Scheduler logon task that runs with the highest
    /// privileges, so Cruce can also control administrator windows on this PC, without a
    /// UAC prompt at every login. The task is created with no time limit, allowed on
    /// battery, and at normal priority (plain schtasks tasks default to below-normal
    /// priority, a 72 h limit and "AC power only", which would all hurt a laptop).
    /// </summary>
    public static class Autostart
    {
        const string TaskName = "Cruce";
        public static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Cruce");

        public static bool IsAdmin()
        {
            using (var id = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        static int Run(string args, bool elevated)
        {
            var psi = new ProcessStartInfo("schtasks.exe", args);
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.CreateNoWindow = true;
            if (elevated && !IsAdmin()) { psi.UseShellExecute = true; psi.Verb = "runas"; }
            else { psi.UseShellExecute = false; }
            using (var p = Process.Start(psi))
            {
                p.WaitForExit(20000);
                return p.HasExited ? p.ExitCode : -1;
            }
        }

        public static bool IsEnabled()
        {
            try { return Run("/Query /TN \"" + TaskName + "\"", false) == 0; } catch { return false; }
        }

        /// <summary>Copies the exe to a stable folder and registers the logon task. Returns an error text or null.</summary>
        public static string Enable()
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                string target = Path.Combine(InstallDir, "Cruce.exe");
                if (!string.Equals(Path.GetFullPath(exe), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.CreateDirectory(InstallDir); File.Copy(exe, target, true); }
                    catch { target = exe; } // installed copy is running/locked: point at this one
                }
                string user = WindowsIdentity.GetCurrent().Name;
                var xml = new StringBuilder();
                xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
                xml.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
                xml.AppendLine("  <RegistrationInfo><Description>Cruce: un mouse y un teclado para tus PCs</Description></RegistrationInfo>");
                xml.AppendLine("  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + SecurityElement.Escape(user) + "</UserId><Delay>PT2S</Delay></LogonTrigger></Triggers>");
                xml.AppendLine("  <Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>");
                xml.AppendLine("  <Settings>");
                xml.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
                xml.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
                xml.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
                xml.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
                xml.AppendLine("    <StartWhenAvailable>false</StartWhenAvailable>");
                xml.AppendLine("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>");
                xml.AppendLine("    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>");
                xml.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
                xml.AppendLine("    <Enabled>true</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle>");
                xml.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
                xml.AppendLine("    <Priority>4</Priority>");
                xml.AppendLine("  </Settings>");
                xml.AppendLine("  <Actions Context=\"Author\"><Exec><Command>\"" + SecurityElement.Escape(target) + "\"</Command><Arguments>--tray</Arguments></Exec></Actions>");
                xml.AppendLine("</Task>");
                string tmp = Path.Combine(Path.GetTempPath(), "cruce-task.xml");
                File.WriteAllText(tmp, xml.ToString(), Encoding.Unicode);
                int code = Run("/Create /TN \"" + TaskName + "\" /XML \"" + tmp + "\" /F", true);
                try { File.Delete(tmp); } catch { }
                return code == 0 ? null : L.F("No se pudo crear la tarea de inicio (código {0}).", code);
            }
            catch (System.ComponentModel.Win32Exception) { return "Cancelado."; }
            catch (Exception ex) { Log.Error(ex, "autostart"); return ex.Message; }
        }

        /// <summary>
        /// The logon task starts the copy in InstallDir. If Cruce is running from somewhere else (and updating
        /// itself there), that copy goes stale and the next login would start an old version. Keep it identical.
        /// </summary>
        public static void SyncInstalled()
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                string target = Path.Combine(InstallDir, "Cruce.exe");
                if (string.Equals(Path.GetFullPath(exe), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return;
                if (!File.Exists(target) || !IsEnabled()) return;
                byte[] a = File.ReadAllBytes(exe), b = File.ReadAllBytes(target);
                if (a.Length == b.Length && System.Linq.Enumerable.SequenceEqual(a, b)) return;
                // Only ever upgrade it: an old copy started by hand must not overwrite a newer installed one.
                Version mine = new Version(AppController.Version), theirs = new Version(0, 0);
                try { var fv = FileVersionInfo.GetVersionInfo(target).FileVersion; if (!string.IsNullOrEmpty(fv)) theirs = new Version(fv); } catch { }
                if (theirs.Major > 0 && new Version(theirs.Major, theirs.Minor) >= mine) return;
                File.Copy(exe, target, true);
                Log.Info("inicio con Windows: la copia instalada ({0}) estaba vieja; la actualicé a esta versión", target);
            }
            catch (Exception ex) { Log.Info("inicio con Windows: no pude actualizar la copia instalada ({0})", ex.Message); }
        }

        public static string Disable()
        {
            try
            {
                int code = Run("/Delete /TN \"" + TaskName + "\" /F", true);
                return code == 0 ? null : L.F("No se pudo quitar la tarea (código {0}).", code);
            }
            catch (System.ComponentModel.Win32Exception) { return "Cancelado."; }
            catch (Exception ex) { return ex.Message; }
        }
    }
}
