using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AppManager.Shared;

// V7 engine: am-engine.exe (GUI subsystem, /target:winexe) — NO console window.
// Spawned by the logon scheduled task or by `am run`.
// Behavior: single-instance (named mutex, held statically + GC.KeepAlive).
// On start: run one silent pass (start-if-needed + poll-hide during silent
// window), then stay resident in standby. The CLI stops it via pidfile /
// process name.
namespace AppManager.Engine
{
    static class Program
    {
        static Mutex instLock;

        static void Main()
        {
            bool createdNew;
            instLock = new Mutex(false, Core.MutexName, out createdNew);
            if (!createdNew)
            {
                Core.Log("engine: another instance already running -> exit");
                return;
            }
            GC.KeepAlive(instLock);

            Directory.CreateDirectory(Path.GetDirectoryName(Core.PidPath));
            File.WriteAllText(Core.PidPath, Process.GetCurrentProcess().Id.ToString());
            Core.Log("engine started pid=" + Process.GetCurrentProcess().Id);

            // One silent pass: start enabled items if needed + poll-hide during silent window.
            try
            {
                Core.DoPass(Core.Load());
            }
            catch (Exception ex)
            {
                Core.Log("engine pass error: " + ex);
            }
            Core.Log("engine: silent window ended -> resident standby");

            // Resident standby: sleep loop, no auto-hide after the pass window.
            while (true) Thread.Sleep(60000);
        }
    }
}
