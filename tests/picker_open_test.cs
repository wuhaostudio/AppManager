using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Node tests P1-P4 for the picker "open" path (start-if-needed + tray-restore
// + front-verify). Prereq: freshly built am-engine.exe running, silent pass
// done, hotkey Ctrl+0 listening.
//
// P1  cold start   : kill Feishu -> open picker -> select Feishu -> Enter ->
//                    the app process is alive again AND its main window is
//                    on screen (visible, not iconic); log says "started".
// P2  tray restore : the silent pass hid Feishu into the tray -> open picker
//                    -> select -> Enter -> the window is back on screen; log
//                    says "already running" (no relaunch).
// P3  already open : Feishu running with its window on screen -> Enter ->
//                    process count did NOT grow and the window stays on
//                    screen; log says "already running".
// P4  no UI freeze : during P1's cold-start wait window the picker can still
//                    be toggle-closed (press the hotkey while the picker is
//                    open); afterwards the restore thread still lands the
//                    window on screen (background-thread restore, not tied
//                    to the picker form).
//
// Navigation is config-driven: index of "Feishu" in the enabled item list
// (APPS section) decides how many Down presses reach it from the initial
// top selection. The selection also toggles rows (a newly selected running
// app row is shown on screen), so between nodes the test closes the picker
// and lets the silent poll re-hide.
//
// Uses only managed windows + the picker itself as reference (no external
// app is launched). Prints PASS/FAIL, mirrors to
// tests/bin/picker_open_test_result.txt, exit 0 = all green.
static class PickerOpenTest
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(Delegate cb, IntPtr p);
    public delegate bool EP(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint f, IntPtr x);

    static string deploy = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "AppManager");
    static string logPath = Path.Combine(deploy, "am.log");
    static int fails = 0;
    static StringBuilder rep = new StringBuilder();
    static void R(string s) { Console.WriteLine(s); lock (rep) rep.AppendLine(s); }
    static void Pass(string n, string w) { R("PASS " + n + "  " + w); }
    static void Fail(string n, string w) { fails++; R("FAIL " + n + "  " + w); }

    const string PROC = "Feishu";
    const string NAME = "Feishu";

    static int[] Pids(string proc)
    {
        var a = Process.GetProcessesByName(proc);
        var r = new int[a.Length];
        for (int i = 0; i < r.Length; i++) { r[i] = a[i].Id; a[i].Dispose(); }
        return r;
    }
    static void KillAll(string proc)
    {
        foreach (var p in Process.GetProcessesByName(proc))
        { try { p.Kill(); } catch { } }
    }
    static int ProcCount(string proc) { return Pids(proc).Length; }

    // the Feishu main window specifically: big unowned visible non-tool
    // window with title text, owned by some Feishu pid (helpers/renderers
    // are unowned too, but Feishu's real UI window is the largest titled
    // one — good enough: assert on ANY titled visible non-iconic window,
    // and on P1 additionally that at least one Feishu window is on screen).
    static bool FeishuOnScreen()
    {
        var pids = Pids(PROC);
        if (pids.Length == 0) return false;
        bool found = false;
        EP cb = (h, u) =>
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            bool mine = false;
            for (int i = 0; i < pids.Length; i++) if (pids[i] == (int)pid) mine = true;
            if (!mine) return true;
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, sb.Capacity);
            if (sb.Length > 0) { found = true; return false; }
            return true;
        };
        EnumWindows(cb, IntPtr.Zero);
        return found;
    }
    static bool WaitOnScreen(string proc, int timeoutMs)
    {
        int w = 0;
        while (w < timeoutMs) { if (FeishuOnScreen()) return true; Thread.Sleep(300); w += 300; }
        return FeishuOnScreen();
    }

    // After 'am run' the new engine writes its pid file a moment after the
    // task spawns; FindPicker() filters by that pid, so probe only once the
    // file points at a LIVE am-engine (otherwise a dead pid rejects the new
    // engine's picker and every node reads "did not open").
    static void WaitPidFileLive(int timeoutMs)
    {
        int w = 0;
        while (w < timeoutMs)
        {
            int p = EnginePid();
            bool live = false;
            foreach (var q in Pids("am-engine")) if (q == p) live = true;
            if (live) return;
            Thread.Sleep(200); w += 200;
        }
    }


    static int LogPos() { try { return File.Exists(logPath) ? (int)new FileInfo(logPath).Length : 0; } catch { return 0; } }
    static string LogSince(int pos)
    {
        try
        {
            var all = File.ReadAllLines(logPath);
            var sb = new StringBuilder();
            int off = 0;
            foreach (var l in all) { off += l.Length + 2; if (off > pos) sb.AppendLine(l); }
            return sb.ToString();
        }
        catch { return ""; }
    }

    // The hotkey watcher only starts AFTER the engine's ~30s silent pass
    // (DoPass blocks the Main thread). Poll the log for the most recent
    // "engine started" and then for "silent window ended" after it; until
    // that line exists the hotkey cannot open the picker.
    static void WaitWatcherLive(int timeoutMs)
    {
        int w = 0;
        while (w < timeoutMs)
        {
            string log;
            try { log = File.ReadAllText(logPath); } catch { log = ""; }
            int startedAt = log.LastIndexOf("engine started pid=", StringComparison.Ordinal);
            if (startedAt >= 0)
            {
                int endedAt = log.IndexOf("silent window ended", startedAt, StringComparison.Ordinal);
                if (endedAt > startedAt) return;
            }
            Thread.Sleep(500); w += 500;
        }
    }

    // ---- picker window probing (same pattern as picker_test) ----
    static int EnginePid()
    {
        try
        {
            int pid;
            if (int.TryParse(File.ReadAllText(Path.Combine(deploy, "am-engine.pid")).Trim(), out pid))
                return pid;
        }
        catch { }
        return -1;
    }
    static IntPtr FindPicker()
    {
        IntPtr found = IntPtr.Zero;
        int eng = EnginePid();
        EP cb = (h, u) =>
        {
            if (!IsWindowVisible(h)) return true;
            var c = new StringBuilder(128);
            GetClassName(h, c, c.Capacity);
            if (!c.ToString().StartsWith("WindowsForms", StringComparison.Ordinal)) return true;
            var t = new StringBuilder(256);
            GetWindowTextW(h, t, t.Capacity);
            if (!t.ToString().StartsWith("AppManager", StringComparison.Ordinal)) return true;
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (eng > 0 && (int)pid != eng) return true;
            found = h;
            return true;
        };
        EnumWindows(cb, IntPtr.Zero);
        return found;
    }
    static bool WaitPicker(bool wantOpen, int timeoutMs)
    {
        int w = 0;
        while (w < timeoutMs)
        {
            if ((FindPicker() != IntPtr.Zero) == wantOpen) return true;
            Thread.Sleep(200); w += 200;
        }
        return (FindPicker() != IntPtr.Zero) == wantOpen;
    }
    static void Press() // Ctrl+0
    {
        keybd_event(0x11, 0, 0, IntPtr.Zero);
        keybd_event(0x30, 0, 0, IntPtr.Zero);
        keybd_event(0x30, 0, 2, IntPtr.Zero);
        keybd_event(0x11, 0, 2, IntPtr.Zero);
    }
    static void Hotkey() { Press(); Thread.Sleep(2500); }
    static void SendKey(int vk)
    {
        keybd_event((byte)vk, 0, 0, IntPtr.Zero);
        Thread.Sleep(150);
        keybd_event((byte)vk, 0, 2, IntPtr.Zero);
        Thread.Sleep(400);
    }
    static void ClosePicker()
    {
        for (int i = 0; i < 2; i++) { SendKey(0x1B); Thread.Sleep(600); }
        if (FindPicker() != IntPtr.Zero) { Press(); WaitPicker(false, 8000); }
    }

    // number of Downs from the initial top selection to the Feishu row
    static int DownsForFeishu()
    {
        try
        {
            string cfg = File.ReadAllText(Path.Combine(deploy, "config.json"));
            int idx = 0, found = -1;
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(cfg, @"\{[^{}]*\}"))
            {
                string o = m.Value;
                if (!o.Contains("\"name\"") || !o.Contains("\"exe\"")) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(o, "\"script\"\\s*:\\s*true")) continue;
                var nm = System.Text.RegularExpressions.Regex.Match(o, "\"name\"\\s*:\\s*\"([^\"]*)\"");
                if (nm.Success && nm.Groups[1].Value == NAME) found = idx;
                idx++;
            }
            return found >= 0 ? found : 0;
        }
        catch { return 0; }
    }

    static int Main()
    {
        int[] eng = Pids("am-engine");
        if (eng.Length != 1)
        {
            Fail("pre", eng.Length + " am-engine instances (need exactly one, 'am stop && am run')");
            Dump(); return 1;
        }
        R("pre: am-engine pid " + eng[0] + ", downToFeishu=" + DownsForFeishu());
        ClosePicker();

        // ---------- P1: cold start via the picker ----------
        {
            R("---- P1: cold start " + PROC + " via picker ----");
            WaitWatcherLive(80000); // the hotkey is only live after the silent pass;
                                     // killing before it would let the pass restart+hide
            KillAll(PROC);
            Thread.Sleep(2000);
            int logPos = LogPos();
            Hotkey();
            if (!WaitPicker(true, 8000)) { Fail("P1", "picker did not open"); }
            else
            {
                for (int i = 0; i < DownsForFeishu(); i++) SendKey(0x28); // Down -> Feishu row
                SendKey(0x0D);                                            // Enter -> open
                bool gone = WaitPicker(false, 10000);
                int w = 0;
                while (w < 20000 && ProcCount(PROC) == 0) { Thread.Sleep(500); w += 500; }
                bool alive = ProcCount(PROC) > 0;
                bool back = alive && WaitOnScreen(PROC, 15000);
                string log = LogSince(logPos);
                bool startedLogged = log.Contains("started");
                if (gone && alive && back && startedLogged)
                    Pass("P1", "killed -> picker Enter: process restarted (" + ProcCount(PROC) + " procs), window on screen, log 'started'");
                else
                    Fail("P1", "gone=" + gone + " alive=" + alive + " onScreen=" + back + " startedLogged=" + startedLogged +
                        " | log: " + log.Replace("\n", " | ").Substring(0, Math.Min(600, log.Length)));
            }
        }

        // ---------- P2: tray restore (already running, window hidden) ----------
        // The engine's hotkey watcher only goes live AFTER its ~30s silent pass
        // (DoPass) returns, so restart the engine and wait out the pass — the
        // same way tray_test's N1 does. During that pass the poll hides the
        // running Feishu window into the tray; afterwards Feishu is alive but
        // off-screen, which is exactly the tray state P2 restores from.
        {
            R("---- P2: tray/hidden restore of " + PROC + " via picker ----");
            // restart the engine: stop (which blocks the keepalive watchdog),
            // wait for it to be really gone, then run a fresh normal engine
            Am("stop");
            int w = 0;
            while (Pids("am-engine").Length > 0 && w < 10000) { Thread.Sleep(500); w += 500; }
            Am("run");
            WaitPidFileLive(20000); // the new engine's pid file must point at a LIVE
                                    // engine before FindPicker's pid filter is useful
            WaitWatcherLive(80000); // silent pass done -> watcher live; the pass hides
                                    // Feishu into the tray during those 30s
            // now WAIT for the tray state: P1 left Feishu on screen, so it may
            // still be visible at the instant the watcher went live
            bool hidden = false;
            w = 0;
            while (w < 30000)
            {
                if (ProcCount(PROC) > 0 && !FeishuOnScreen()) { hidden = true; break; }
                Thread.Sleep(500); w += 500;
            }
            bool alive = ProcCount(PROC) > 0;
            int logPos = LogPos();
            R("   P2 state: alive=" + alive + " hidden=" + hidden + " engine=" + EnginePid());
            Hotkey();
            int probes = 0;
            bool opened = false;
            for (int i = 0; i < 40 && !opened; i++)
            {
                if (FindPicker() != IntPtr.Zero) { opened = true; probes = i; break; }
                Thread.Sleep(200);
            }
            R("   P2 open-probe: opened=" + opened + " after " + (probes * 200) + "ms");
            if (!opened) { Fail("P2", "picker did not open (probe 8s, " + LogSince(logPos).Replace("\n", " | ").Substring(0, Math.Min(300, LogSince(logPos).Length)) + ")"); }
            else
            {
                int ds = DownsForFeishu();
                for (int i = 0; i < ds; i++) SendKey(0x28);
                SendKey(0x0D);
                bool gone = WaitPicker(false, 10000);
                bool back = WaitOnScreen(PROC, 15000);
                string log = LogSince(logPos);
                bool noRelaunch = log.Contains("already running");
                if (hidden && gone && back && noRelaunch)
                    Pass("P2", "hidden (tray) -> picker Enter: window back on screen, log 'already running' (no relaunch)");
                else
                    Fail("P2", "hidden=" + hidden + " gone=" + gone + " onScreen=" + back + " alreadyRunning=" + noRelaunch +
                        " | log: " + log.Replace("\n", " | ").Substring(0, Math.Min(600, log.Length)));
            }
        }

        // ---------- P3: already open (on screen) -> no double start ----------
        {
            R("---- P3: Feishu on screen -> picker Enter keeps a single instance ----");
            WaitWatcherLive(80000);
            if (!FeishuOnScreen()) { Thread.Sleep(2000); } // give the poll a moment
            int before = ProcCount(PROC);
            // wait for a quiet moment: the silent poll may be between ticks
            int logPos = LogPos();
            Hotkey();
            if (!WaitPicker(true, 8000)) { Fail("P3", "picker did not open"); }
            else
            {
                int ds = DownsForFeishu();
                for (int i = 0; i < ds; i++) SendKey(0x28);
                SendKey(0x0D);
                bool gone = WaitPicker(false, 10000);
                Thread.Sleep(3000);
                int after = ProcCount(PROC);
                bool stillOn = FeishuOnScreen();
                string log = LogSince(logPos);
                bool noRelaunch = log.Contains("already running") && !log.Contains("started '" + NAME + "'");
                if (gone && after == before && stillOn && noRelaunch)
                    Pass("P3", "on-screen -> picker Enter: process count unchanged (" + after + "), window still on screen, no relaunch");
                else
                    Fail("P3", "gone=" + gone + " procsBefore=" + before + " after=" + after + " onScreen=" + stillOn +
                        " | log: " + log.Replace("\n", " | ").Substring(0, Math.Min(600, log.Length)));
            }
        }

        // ---------- P4: picker stays toggleable during the cold-start wait ----------
        {
            R("---- P4: toggle-close the picker mid cold-start wait ----");
            KillAll(PROC);
            Thread.Sleep(2000);
            int logPos = LogPos();
            Press();
            bool opened = WaitPicker(true, 6000);
            bool toggled = false;
            if (opened)
            {
                int ds = DownsForFeishu();
                for (int i = 0; i < ds; i++) SendKey(0x28);
                SendKey(0x0D); // Enter -> open + close; the restore thread now waits up to 5s
                Thread.Sleep(1200); // inside the cold-start wait window
                Press();          // toggle: a NEW picker must open. It takes a
                                  // couple of seconds to appear (new UI thread +
                                  // Application.Run), so poll rather than probe once.
                                  // Under the OLD blocking OpenSelected this toggle
                                  // deadlocked: the open loop slept on the picker's
                                  // UI thread, so the close-then-reopen never came.
                opened = WaitPicker(true, 6000);
                toggled = opened;
                if (toggled) WaitPicker(true, 4000);
                ClosePicker();
            }
            // the background restore thread must still land the window on screen
            bool alive = false;
            int w = 0;
            while (w < 25000) { if (ProcCount(PROC) > 0) { alive = true; break; } Thread.Sleep(500); w += 500; }
            bool back = alive && WaitOnScreen(PROC, 15000);
            if (toggled && alive && back)
                Pass("P4", "mid-wait toggle worked: new picker opened while the restore wait was in flight, window still landed on screen");
            else
                Fail("P4", "toggleOpened=" + toggled + " alive=" + alive + " onScreen=" + back +
                    " | log: " + LogSince(logPos).Replace("\n", " | ").Substring(0, Math.Min(600, LogSince(logPos).Length)));
        }

        R("---- results: " + (fails == 0 ? "ALL PASS" : fails + " failure(s)") + " ----");
        Dump();
        return fails == 0 ? 0 : 1;
    }

    static void Am(string args)
    {
        var psi = new ProcessStartInfo(Path.Combine(deploy, "am.exe"), args) { UseShellExecute = false };
        Process p = Process.Start(psi);
        p.WaitForExit(20000);
    }

    static void Dump()
    {
        lock (rep)
        {
            Directory.CreateDirectory(@"C:\project\AppManager\tests\bin");
            File.WriteAllText(@"C:\project\AppManager\tests\bin\picker_open_test_result.txt", rep.ToString());
        }
        R(@"report -> C:\project\AppManager\tests\bin\picker_open_test_result.txt");
    }
}
