using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using AppManager.Shared;

// Restore-accuracy test (node T2): hide/restore round-trip on a stand-in
// managed program (win_probe, see tests/win_probe.cs — built with the same
// WinForms refs), asserting the window comes back in the EXACT state it left:
// maximized -> still maximized (not the app's stale small normal rect),
// normal  -> same size + position as before the hide.
//
// Build the probe first:
//   MSYS_NO_PATHCONV=1 "/c/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe" /nologo /target:exe \
//     "/out:tests/bin/win_probe.exe" \
//     "/r:C:\Windows\Microsoft.NET\Framework\v4.0.30319\System.Windows.Forms.dll" \
//     "/r:C:\Windows\Microsoft.NET\Framework\v4.0.30319\System.Drawing.dll" tests/win_probe.cs
// Build + run this test (links the live am_shared.cs):
//   MSYS_NO_PATHCONV=1 "/c/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe" /nologo /target:exe \
//     "/out:tests/bin/restore_test.exe" \
//     "/r:C:\Windows\Microsoft.NET\Framework\v4.0.30319\System.Windows.Forms.dll" \
//     "/r:C:\Windows\Microsoft.NET\Framework\v4.0.30319\System.Drawing.dll" \
//     "/r:C:\Windows\Microsoft.NET\Framework\v4.0.30319\System.dll" \
//     "/r:C:\Windows\Microsoft.NET\Framework\v4.0.30319\System.Core.dll" \
//     "/r:C:\Windows\Microsoft.NET\Framework\v4.0.30319\System.Runtime.Serialization.dll" \
//     src/shared/am_shared.cs tests/restore_test.cs
// Run: tests/bin/restore_test.exe  (scratch am-winstate.json lands in tests/bin/)
static class RestoreTest
{
    [DllImport("user32.dll")] static extern bool EnumWindows(Delegate cb, IntPtr p);
    public delegate bool EP(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder s, int max);

    static string dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
    static int fails = 0;
    static void Pass(string n, string w) { Console.WriteLine("PASS " + n + "  " + w); }
    static void Fail(string n, string w) { fails++; Console.WriteLine("FAIL " + n + "  " + w); }
    static void Check(string n, bool ok, string w) { if (ok) Pass(n, w); else Fail(n, w); }

    static void KillProbe()
    {
        foreach (var p in Process.GetProcessesByName("win_probe")) { try { p.Kill(); } catch { } }
        Thread.Sleep(300);
    }

    static Process StartProbe(string args)
    {
        KillProbe();
        var psi = new ProcessStartInfo(Path.Combine(dir, "win_probe.exe"), args) { UseShellExecute = false, RedirectStandardOutput = true };
        var p = Process.Start(psi);
        string line = p.StandardOutput.ReadLine();
        if (line != null) Console.WriteLine("  " + line);
        return p;
    }

    // the probe's main window: visible top-level window with text
    static IntPtr ProbeMain(Process p)
    {
        uint pid = (uint)p.Id;
        IntPtr found = IntPtr.Zero;
        for (int t = 0; t < 40 && found == IntPtr.Zero; t++)
        {
            EP cb = (h, u) =>
            {
                uint pid2; GetWindowThreadProcessId(h, out pid2);
                if (pid2 == pid && IsWindowVisible(h))
                {
                    var sb = new StringBuilder(128);
                    GetWindowText(h, sb, sb.Capacity);
                    if (sb.Length > 0) found = h;
                }
                return true;
            };
            EnumWindows(cb, IntPtr.Zero);
            if (found == IntPtr.Zero) Thread.Sleep(250);
        }
        return found;
    }

    static string RectStr(P.RECT r) { return r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top); }

    static void Main()
    {
        string probe = Path.Combine(dir, "win_probe.exe");
        if (!File.Exists(probe))
        {
            Console.WriteLine("SKIP T2: win_probe.exe not built; build it per the header comment first");
            Environment.Exit(0);
        }
        try { File.Delete(Core.WinStatePath); } catch { }

        // ---- T2-max: maximized stand-in -> hide -> restore stays maximized ----
        var pm = StartProbe("--max");
        IntPtr mh = ProbeMain(pm);
        if (mh == IntPtr.Zero) { Fail("T2max", "maximized probe main window not found"); }
        else
        {
            Check("T2max-pre", IsZoomed(mh), "probe started maximized");
            Core.HideWindows(new Item { name = "probe-max", processName = "win_probe" });
            Thread.Sleep(400);
            P.RECT rc;
            bool wasMax = Core.WasMaximized("probe-max", out rc);
            Check("T2max-rec", wasMax, "hide recorded maximized=true (rc=" + RectStr(rc) + ")");
            Check("T2max-hid", P.IsIconic(mh) || !IsWindowVisible(mh), "probe window off-screen (iconic or hidden) after HideWindows");
            int shown = Core.ShowWindows("win_probe", "", "probe-max");
            Thread.Sleep(500);
            P.RECT after; P.GetWindowRect(mh, out after);
            Check("T2max-res", IsZoomed(mh) && shown == 1, "restore kept the window MAXIMIZED (shown=" + shown + " after=" + RectStr(after) + ")");
        }
        pm.Kill();

        // ---- T2-norm: fixed 640x400 stand-in -> hide -> restore same rect ----
        var pn = StartProbe("--norm 640 400");
        IntPtr nh = ProbeMain(pn);
        if (nh == IntPtr.Zero) { Fail("T2norm", "normal probe main window not found"); }
        else
        {
            P.RECT before; P.GetWindowRect(nh, out before);
            Core.HideWindows(new Item { name = "probe-norm", processName = "win_probe" });
            Thread.Sleep(400);
            P.RECT rec;
            bool wasMax = Core.WasMaximized("probe-norm", out rec);
            Check("T2norm-rec", !wasMax, "hide recorded maximized=false (rc=" + RectStr(rec) + ")");
            Check("T2norm-hid", !Core.WindowOnScreen(nh), "probe window off-screen after HideWindows (iconic is the non-tray path; a later poll tick SW_HIDES it)");
            Core.ShowWindows("win_probe", "", "probe-norm");
            Thread.Sleep(500);
            P.RECT after; P.GetWindowRect(nh, out after);
            int dx = Math.Abs((after.Right - after.Left) - (before.Right - before.Left));
            int dy = Math.Abs((after.Bottom - after.Top) - (before.Bottom - before.Top));
            int dl = Math.Abs(after.Left - before.Left), dt = Math.Abs(after.Top - before.Top);
            Check("T2norm-res", dx <= 2 && dy <= 2 && dl <= 2 && dt <= 2 && !IsZoomed(nh),
                "restore matched pre-hide rect " + RectStr(before) + " -> " + RectStr(after) + " (maximized=" + IsZoomed(nh) + ")");
        }
        pn.Kill();
        try { File.Delete(Core.WinStatePath); } catch { }

        Console.WriteLine(fails == 0 ? "ALL PASS" : (fails + " FAILED"));
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
