using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Native-hide / tray acceptance test (node test N1/N1' + N2/N2').
// Prereq: freshly built am-engine.exe running (am stop && am run), hotkey Ctrl+0 listening.
//
// Verifies the design change: the silent pass now minimizes apps NATIVELY
// (WM_SYSCOMMAND/SC_MINIMIZE) instead of blind SW_HIDE, so a tray-capable
// app's own "minimize to tray" handler runs and its TRAY ICON is created
// and kept. Non-tray apps that end up on the taskbar are still SW_HIDDEN
// on a later poll tick.
//
// N1  native hide   : kill <app> -> `am stop && am run` -> after the silent
//                     pass, the app is alive and has NO visible, non-minimized
//                     window (it is in the tray / hidden, not on screen).
// N2  restore+tray  : hotkey opens the picker, select <app>, Enter -> the
//                     app's window comes back onto the screen (visible, not
//                     iconic). The tray icon is confirmed VISUALLY by the
//                     user (Feishu's tooltip name is unstable, so it is not
//                     asserted here).
//
// Runs N1+N2 for Feishu, then N1'+N2' for NinjaDesktop (both auto-asserted;
// icons eyeballed). Prints PASS/FAIL, mirrors to tests/bin/tray_test_result.txt,
// exit 0 = all green.
static class TrayTest
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(Delegate cb, IntPtr p);
    public delegate bool EP(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint f, IntPtr x);

    static string deploy = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "AppManager");
    static int fails = 0;
    static StringBuilder rep = new StringBuilder();
    static void R(string s) { Console.WriteLine(s); lock (rep) rep.AppendLine(s); }
    static void Pass(string n, string w) { R("PASS " + n + "  " + w); }
    static void Fail(string n, string w) { fails++; R("FAIL " + n + "  " + w); }

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

    // any visible, NON-iconic top-level window owned by proc?
    static bool HasOnScreenWindow(string proc)
    {
        var pids = Pids(proc);
        if (pids.Length == 0) return false;
        bool found = false;
        EP cb = (h, u) =>
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            int match = -1;
            for (int i = 0; i < pids.Length; i++) if (pids[i] == (int)pid) match = i;
            if (match >= 0 && IsWindowVisible(h) && !IsIconic(h)) { found = true; return false; }
            return true;
        };
        EnumWindows(cb, IntPtr.Zero);
        return found;
    }
    // any visible, non-iconic window owned by proc (poll until timeout)
    static bool WaitOnScreen(string proc, int timeoutMs)
    {
        int w = 0;
        while (w < timeoutMs) { if (HasOnScreenWindow(proc)) return true; Thread.Sleep(300); w += 300; }
        return HasOnScreenWindow(proc);
    }

    static void Am(string args)
    {
        var psi = new ProcessStartInfo(Path.Combine(deploy, "am.exe"), args) { UseShellExecute = false };
        Process p = Process.Start(psi);
        p.WaitForExit(20000);
    }

    static void Hotkey()
    {
        keybd_event(0x11, 0, 0, IntPtr.Zero); keybd_event(0x30, 0, 0, IntPtr.Zero);
        keybd_event(0x30, 0, 2, IntPtr.Zero); keybd_event(0x11, 0, 2, IntPtr.Zero);
        Thread.Sleep(2500);
    }
    static void SendKey(int vk) { keybd_event((byte)vk, 0, 0, IntPtr.Zero); Thread.Sleep(150); keybd_event((byte)vk, 0, 2, IntPtr.Zero); Thread.Sleep(400); }

    // N1: kill the app, restart engine, let the silent pass hide it natively
    static void Node1(string tag, string proc)
    {
        R("---- " + tag + ": native hide of " + proc + " ----");
        KillAll(proc);
        Thread.Sleep(2000);
        Am("stop");
        Am("run");
        // engine: silent pass runs ~30s then goes to standby
        Thread.Sleep(35000);
        bool alive = Pids(proc).Length > 0;
        bool hidden = !HasOnScreenWindow(proc);
        if (alive && hidden) Pass(tag, proc + " alive after pass and not on screen (in tray/hidden)");
        else Fail(tag, proc + " alive=" + alive + " onScreen=" + !hidden + " (expect alive & off-screen)");
    }

    static string LogTail(int lines)
    {
        string p = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "AppManager", "am.log");
        try
        {
            var all = File.ReadAllLines(p);
            var sb = new StringBuilder();
            for (int i = all.Length - lines; i < all.Length; i++) if (i >= 0) sb.AppendLine(all[i]);
            return sb.ToString();
        }
        catch { return ""; }
    }

    // N2: restore via the picker (select the item, Enter) and confirm it is back on screen.
    //
    // Deterministic navigation: the picker's selection is not reset to a known
    // point we can rely on by pressing Up (Up at the top of APPS is a no-op and
    // does not cross sections). But Down ALWAYS walks forward and stops at the
    // absolute bottom (last item of the SCRIPTS section). From that known
    // bottom, a fixed number of Ups lands on any target. So: press Down
    // `toBottom` times (overshoot is harmless) to reach the bottom, then Up
    // `upsFromBottom` times to reach the target.
    //
    // For this config the top-to-bottom order is: APPS[Feishu, NinjaDesktop],
    // SCRIPTS[WinRemap] (total 3, bottom index 2). So:
    //   Feishu      -> upsFromBottom = 2   (2 - 0)
    //   NinjaDesktop-> upsFromBottom = 1   (2 - 1)
    static void Node2(string tag, string proc, int toBottom, int upsFromBottom, string expectedName)
    {
        R("---- " + tag + ": restore " + proc + " via picker ----");
        for (int i = 0; i < 2; i++) { SendKey(0x1B); Thread.Sleep(800); } // clear any leftover picker
        Hotkey();                                                          // fresh picker
        Thread.Sleep(800);
        for (int i = 0; i < toBottom; i++) SendKey(0x28); // Down to absolute bottom
        for (int i = 0; i < upsFromBottom; i++) SendKey(0x26); // Up to the target
        SendKey(0x0D); // Enter -> open
        bool back = WaitOnScreen(proc, 12000);
        string opened = LogTail(10);
        bool rightItem = opened.Contains("opening '" + expectedName + "'");
        if (back && rightItem) Pass(tag, proc + " window back on screen after Enter; picker opened '" + expectedName + "' (tray icon: eyeball)");
        else Fail(tag, proc + " onScreen=" + back + " opened='" + expectedName + "'?=" + rightItem + " | log: " + opened.Replace("\n", " | "));
        SendKey(0x1B);
        Thread.Sleep(1000);
    }

    static int Main()
    {
        int[] eng = Pids("am-engine");
        if (eng.Length == 0)
        { Fail("pre", "am-engine not running; run 'am stop && am run' first"); Dump(); return 1; }
        if (eng.Length > 1)
        {
            Fail("pre", eng.Length + " am-engine instances running (pids " + string.Join(",", eng) +
                "); a second engine's picker interferes with key events. 'am stop && am run' (single instance) and keep the machine idle during the test");
            Dump(); return 1;
        }
        R("pre: am-engine pid " + eng[0]);

        Node1("N1", "Feishu");
        Node2("N2", "Feishu", 5, 2, "Feishu");
        Node1("N1'", "NinjaDesktop");
        Node2("N2'", "NinjaDesktop", 5, 1, "NinjaDesktop");

        R("---- results: " + (fails == 0 ? "ALL PASS" : fails + " failure(s)") + " ----");
        R("NOTE: tray icons are NOT asserted (unstable tooltip); confirm Feishu + NinjaDesktop icons visually.");
        Dump();
        return fails == 0 ? 0 : 1;
    }
    static void Dump()
    {
        lock (rep) File.WriteAllText(@"C:\project\AppManager\tests\bin\tray_test_result.txt", rep.ToString());
        R("report -> " + @"C:\project\AppManager\tests\bin\tray_test_result.txt");
    }
}
