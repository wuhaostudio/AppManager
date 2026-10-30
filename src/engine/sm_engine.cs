using System;
using System.Diagnostics;
using StartManager.Shared;

// StartManager engine: sm-engine.exe (GUI subsystem, /target:winexe) — NO console window.
// Started by the logon scheduled task 'StartManager'.
//
// One job only: run the logon pass (start every enabled item, poll-hide its
// windows for the configured monitor window) and EXIT. There is no resident
// process, no process guard, no keep-alive, no window restore and no hotkey.
namespace StartManager.Engine
{
    static class Program
    {
        static void Main(string[] args)
        {
            int pid = Process.GetCurrentProcess().Id;
            Core.Log("engine started pid=" + pid);
            try
            {
                Core.DoPass(Core.Load());
            }
            catch (Exception ex)
            {
                Core.Log("engine pass error: " + ex);
            }
            Core.Log("engine done");
        }
    }
}
