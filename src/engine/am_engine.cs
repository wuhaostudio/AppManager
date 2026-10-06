using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using AppManager.Shared;
using AppManager.Ui;

// V7 engine: am-engine.exe (GUI subsystem, /target:winexe) — NO console window.
// Spawned by the logon scheduled task or by `am run`.
// Behavior: single-instance (named mutex, held statically + GC.KeepAlive).
// On start: run one silent pass (start-if-needed + poll-hide during silent
// window), then stay resident in standby AND listen for the global hotkey
// (config `hotkey`): pressing it shows the app picker (a separate window
// module, src/ui/am_picker.cs).
//
// This file keeps ONLY the hotkey listener: a standard RegisterHotKey on a
// hidden message window in its own thread. On WM_HOTKEY it calls
// AppManager.Ui.Picker.Show(), which is NON-BLOCKING: each open picker runs
// on its own background thread with its own WinForms message loop, so the
// watcher keeps pumping and a second hotkey press re-enters Show() and
// closes the open picker (toggle).
namespace AppManager.Engine
{
    static class Program
    {
        static Mutex instLock;

        static void Main(string[] args)
        {
            // --keepalive = the watchdog start (every few minutes, e.g. from the
            //   AppManagerKeepAlive task): skip the logon pass and stay silent
            //   whenever the resident engine is already there or `am stop` asked
            //   for the engine to be off.
            // --no-pass    = skip the logon pass (for a manual start that must
            //   not relaunch items the user closed).
            bool keepalive = false, noPass = false;
            foreach (var a in args)
            {
                string t = a.TrimStart('-', '/').ToLowerInvariant();
                if (t == "keepalive") { keepalive = true; noPass = true; }
                else if (t == "no-pass") noPass = true;
            }

            // Any managed crash must leave evidence in am.log: without these a
            // dead process is indistinguishable from an external kill, and the
            // hotkey simply stops working with nothing to look at.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Core.Log("engine UNHANDLED (" + (e.IsTerminating ? "terminating" : "non-terminating") + "): " + e.ExceptionObject);
            Application.ThreadException += (s, e) =>
                Core.Log("engine UI-thread exception: " + e.Exception);

            bool createdNew;
            instLock = new Mutex(false, Core.MutexName, out createdNew);
            if (!createdNew)
            {
                // a watchdog run every few minutes must not spam the log
                if (!keepalive) Core.Log("engine: another instance already running -> exit");
                return;
            }
            GC.KeepAlive(instLock);

            // `am stop` wins over the watchdog until the next normal start
            if (keepalive && Core.StopRequested()) return;

            // a normal start means "run": drop a stale stop request, so the
            // watchdog is live again for this session
            if (!keepalive) Core.ClearStopRequest();

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Core.PidPath));
            System.IO.File.WriteAllText(Core.PidPath, System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
            Core.Log("engine started pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + (keepalive ? " (keepalive)" : ""));

            // One silent pass: start enabled items if needed + poll-hide during silent window.
            // A watchdog start skips it: replaying it mid-day would relaunch apps
            // the user closed and hide windows that are legitimately open.
            try
            {
                if (noPass) Core.Log("engine: silent pass skipped" + (keepalive ? " (keepalive)" : ""));
                else Core.DoPass(Core.Load());
            }
            catch (Exception ex)
            {
                Core.Log("engine pass error: " + ex);
            }
            Core.Log("engine: silent window ended -> resident standby + hotkey watcher");

            // Hotkey watcher on its own message-pump thread.
            Thread watcher = new Thread(HotkeyWatcher.Run) { IsBackground = true, Name = "am-hotkey" };
            try { watcher.Start(); }
            catch (Exception ex) { Core.Log("hotkey watcher failed to start: " + ex.Message); }

            // Resident standby: the CLI stops us by killing this process.
            while (true) Thread.Sleep(60000);
        }
    }

    // The global hotkey listener. Owns a hidden message window; on WM_HOTKEY
    // it blocks on the picker (Picker.Show) until the picker closes.
    static class HotkeyWatcher
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public int cbSize; public int style; public IntPtr lpfnWndProc;
            public int cbClsExtra; public int cbWndExtra;
            public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor;
            public IntPtr hbrBackground; public string lpszMenuName;
            public string lpszClassName; public IntPtr hIconSm;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool UnregisterClassW(string cls, IntPtr inst);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWindowExW")]
        static extern IntPtr CreateWindowExW(int exStyle, string cls, string title, int style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p2);
        [DllImport("user32.dll", SetLastError = true)] static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr h, int id, int mods, int vk);
        [DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);
        [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")] static extern int TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
        [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int i);
        [DllImport("kernel32.dll")] static extern IntPtr LoadLibrary(string n);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr m, string name);

        const int WS_POPUP = unchecked((int)0x80000000);
        const int WS_EX_TOOLWINDOW = 0x0080;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const uint WM_HOTKEY = 0x0312;
        const int HOTKEY_ID = 0x414D; // 'AM'

        public static void Run()
        {
            // Supervisor: the listener is the whole point of the resident
            // process, so it must never stay down. Whatever ends RunInner —
            // an exception, a failed class/window creation, a stray WM_QUIT —
            // we log it and start the listener over instead of leaving the
            // hotkey dead while the process is still alive.
            while (true)
            {
                try
                {
                    RunInner();
                    Core.Log("hotkey: listener ended unexpectedly; restarting in 2s");
                }
                catch (Exception ex)
                {
                    Core.Log("hotkey watcher CRASH: " + ex);
                }
                Thread.Sleep(2000);
            }
        }

        static void RunInner()
        {
            // hInstance = the engine exe's own module handle (NOT LoadLibrary(""),
            // which is 0 in a GUI-subsystem exe and makes RegisterClassExW fail).
            IntPtr hInst = GetModuleHandle(null);

            var wc = new WNDCLASSEX();
            wc.cbSize = Marshal.SizeOf(wc);
            wc.lpfnWndProc = DefWindowProcAddress();
            wc.hInstance = hInst;
            wc.hbrBackground = GetStockObject(4 /*BLACK_BRUSH*/);
            wc.lpszClassName = "AMEngineHidden" + Guid.NewGuid().ToString("N");
            ushort rc = RegisterClassExW(ref wc);
            if (rc == 0)
            {
                Core.Log("hotkey: RegisterClassExW failed (" + Marshal.GetLastWin32Error() + "); retrying");
                Thread.Sleep(1000);
                return;
            }

            IntPtr hidden = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, wc.lpszClassName, null,
                WS_POPUP, -32000, -32000, 1, 1, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            if (hidden == IntPtr.Zero)
            {
                Core.Log("hotkey: CreateWindowExW failed (" + Marshal.GetLastWin32Error() + "); hInst=" + hInst + " wndproc=" + DefWindowProcAddress() + "; retrying");
                Thread.Sleep(1000);
                return;
            }

            bool listening = false;
            string listeningLabel = "";

            // Everything below owns OS state that must be handed back before
            // the supervisor starts a fresh listener: a hidden window that is
            // still alive keeps the RegisterHotKey claim, so restarting
            // WITHOUT this cleanup fails forever with error 1409
            // (ERROR_HOTKEY_ALREADY_REGISTERED) — the hotkey stays dead while
            // the process looks perfectly healthy.
            try
            {
                while (true)
                {
                    // keep the hotkey registration in sync with config
                    var combo = Hotkey.Parse(Core.Load().hotkey);
                    if (!combo.Valid)
                    {
                        if (listening) { UnregisterHotKey(hidden, HOTKEY_ID); listening = false; listeningLabel = ""; Core.Log("hotkey: none configured -> listener off"); }
                        Thread.Sleep(5000);
                        continue;
                    }
                    if (!listening || listeningLabel != combo.Label)
                    {
                        if (listening) UnregisterHotKey(hidden, HOTKEY_ID);
                        bool ok = RegisterHotKey(hidden, HOTKEY_ID, Hotkey.RegisterMods(combo.Mods), combo.Vk);
                        listening = ok;
                        listeningLabel = ok ? combo.Label : "";
                        if (ok) Core.Log("hotkey: listening " + combo.Label);
                        else { Core.Log("hotkey: RegisterHotKey " + combo.Label + " failed (" + Marshal.GetLastWin32Error() + "); retrying"); Thread.Sleep(10000); continue; }
                    }

                    MSG m;
                    int gr = GetMessage(out m, IntPtr.Zero, 0, 0);
                    if (gr == 0) break;   // WM_QUIT: hand the claim back and let the supervisor restart us
                    if (gr == -1) { Core.Log("hotkey: GetMessage error " + Marshal.GetLastWin32Error()); Thread.Sleep(100); continue; }
                    if (m.message == WM_HOTKEY)
                    {
                        // non-blocking: the picker runs on its own thread;
                        // pressing the hotkey again while open is handled inside
                        // Picker.Show() (toggle-off)
                        try { Picker.Show(); }
                        catch (Exception ex) { Core.Log("picker error: " + ex); }
                    }
                    else
                    {
                        TranslateMessage(ref m);
                        DispatchMessage(ref m);
                    }
                }
            }
            finally
            {
                try { if (listening) UnregisterHotKey(hidden, HOTKEY_ID); } catch { }
                try { DestroyWindow(hidden); } catch { }
                try { UnregisterClassW(wc.lpszClassName, hInst); } catch { }
            }
        }

        // address of user32!DefWindowProcW (an unmanaged call target)
        static IntPtr DefWindowProcAddress()
        {
            IntPtr u32 = LoadLibrary("user32.dll");
            return GetProcAddress(u32, "DefWindowProcW");
        }
    }
}
