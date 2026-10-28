using System;
using System.Threading;
using AppManager.Shared;

// T3 helper: operate on a managed app's main window across process
// boundaries (the public ShowWindow/SetWindowPos APIs are callable
// cross-process). Subcommands:
//   main <proc>            -> print main window handle/rect/zoomed
//   max <proc>             -> maximize (the pre-hide state for the max test)
//   set <proc> X Y W H     -> un-maximize + move/resize to a fixed normal rect
//   hide-via-pass: not here — that is the engine's silent pass
static class WinStateProbe
{
    static void Main(string[] a)
    {
        if (a.Length < 2) { Console.WriteLine("usage: " + Environment.GetCommandLineArgs()[0] + " main|max|set|hide <proc> [X Y W H]"); Environment.Exit(2); }
        string cmd = a[0], proc = a[1];
        IntPtr h = Core.FindMainWindow(proc, "");
        for (int t = 0; t < 40 && h == IntPtr.Zero; t++) { Thread.Sleep(250); h = Core.FindMainWindow(proc, ""); }
        if (h == IntPtr.Zero) { Console.WriteLine("no main window for '" + proc + "' (proc running=" + Core.ProcCount(proc) + ")"); Environment.Exit(3); }

        P.RECT r; P.GetWindowRect(h, out r);
        switch (cmd)
        {
            case "hide":
                // the exact production hide path (engine pass / picker row-toggle):
                // records the pre-hide state, then native-minimizes the real windows
                int n = Core.HideWindows(new Item { name = proc, processName = proc });
                Thread.Sleep(600);
                Console.WriteLine("hide issued (ops=" + n + "); onScreen now=" + Core.WindowOnScreen(h) + " iconic=" + P.IsIconic(h));
                break;
            case "main":
                Console.WriteLine(string.Format("hwnd={0} rect=({1},{2}) {3}x{4} zoomed={5} visible={6} onScreen={7}",
                    h.ToInt64(), r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, P.IsZoomed(h), P.IsWindowVisible(h), Core.WindowOnScreen(h)));
                break;
            case "max":
                P.ShowWindow(h, Core.SW_SHOWMAXIMIZED);
                Thread.Sleep(500);
                P.GetWindowRect(h, out r);
                Console.WriteLine("maximized zoomed=" + P.IsZoomed(h) + " rect=" + r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top));
                break;
            case "set":
                int x = int.Parse(a[2]), y = int.Parse(a[3]), w = int.Parse(a[4]), hh = int.Parse(a[5]);
                if (P.IsZoomed(h)) P.ShowWindow(h, Core.SW_RESTORE);
                Thread.Sleep(300);
                P.SetWindowPos(h, IntPtr.Zero, x, y, w, hh, 0x0001 | 0x0004);
                Thread.Sleep(300);
                P.GetWindowRect(h, out r);
                Console.WriteLine(string.Format("set {0}x{1}@({2},{3}) -> zoomed={4} rect=({5},{6}) {7}x{8}",
                    w, hh, x, y, P.IsZoomed(h), r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
                break;
        }
    }
}
