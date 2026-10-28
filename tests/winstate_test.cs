using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AppManager.Shared;

// Win-state acceptance test (node T1): pre-hide state persistence + restore
// decision. Links the live src/shared/am_shared.cs so the real WinState io,
// IsZoomed/GetWindowPlacement reads, and RestoreAppWindow are under test.
//
// Build:
//   MSYS_NO_PATHCONV=1 "/c/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe" /nologo /target:exe \
//     "/out:tests/bin/winstate_test.exe" \
//     "/r:C:\Windows\Microsoft.NET\Framework\v4.0.30319\System.Windows.Forms.dll" \
//     "/r:C:\Windows\Microsoft.NET\Framework\v4.0.30319\System.Drawing.dll" \
//     "/r:C:\Windows\Microsoft.NET\Framework\v4.0.30319\System.dll" \
//     "/r:C:\Windows\Microsoft.NET\Framework\v4.0.30319\System.Core.dll" \
//     "/r:C:\Windows\Microsoft.NET\Framework\v4.0.30319\System.Runtime.Serialization.dll" \
//     src/shared/am_shared.cs tests/winstate_test.cs
// Run: tests/bin/winstate_test.exe   (scratch am-winstate.json lands in tests/bin/)
//
//   T1a-T1d  winstate.json round-trip: record/overwrite/case-insensitive/unknown
//   T2       hidden maximized form + recorded-max -> RestoreAppWindow -> IsZoomed
//   T3       hidden normal form + recorded-normal -> restore -> normal, same rect
//   T4       no record -> restore keeps the normal rect
//   T5       a VISIBLE window -> restore is a no-op (on-screen state kept)
static class WinStateTest
{
    static int fails = 0;
    static void Pass(string n, string w) { Console.WriteLine("PASS " + n + "  " + w); }
    static void Fail(string n, string w) { fails++; Console.WriteLine("FAIL " + n + "  " + w); }
    static void Check(string n, bool ok, string w) { if (ok) Pass(n, w); else Fail(n, w); }
    static string RectStr(P.RECT r) { return r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top); }

    static bool IsMaximizedP(IntPtr h)
    {
        if (P.IsZoomed(h)) return true;
        P.WINDOWPLACEMENT wp = default(P.WINDOWPLACEMENT);
        if (!P.GetWindowPlacement(h, out wp)) return false;
        return wp.showCmd == Core.SW_SHOWMAXIMIZED || wp.state != 0;
    }

    static void Main()
    {
        try { File.Delete(Core.WinStatePath); } catch { }
        P.RECT zeroRect = default(P.RECT);

        // ---- ① winstate.json round-trip ----
        Core.RecordHiddenState("Feishu", true, zeroRect);
        P.RECT dummy;
        Check("T1a", Core.WasMaximized("Feishu", out dummy), "record true -> WasMaximized true");
        Core.RecordHiddenState("Feishu", false, zeroRect);
        Check("T1b", !Core.WasMaximized("Feishu", out dummy), "re-record false overwrites");
        Core.RecordHiddenState("Ninja", true, zeroRect);
        Check("T1c", Core.WasMaximized("ninja", out dummy) && Core.WasMaximized("NINJA", out dummy), "lookup is case-insensitive");
        Check("T1d", !Core.WasMaximized("Ghost", out dummy), "unknown item -> false (plain show path)");

        // ---- ② hidden maximized form -> restore keeps it maximized ----
        var f = new Form { Visible = false };
        f.WindowState = FormWindowState.Maximized;
        Check("T2a", IsMaximizedP(f.Handle), "premise: hidden form IS maximized");
        var wp = new P.WINDOWPLACEMENT();
        P.GetWindowPlacement(f.Handle, out wp);
        P.RECT maxRec = wp.rcNormal;
        Core.RecordHiddenState("Feishu", true, maxRec);
        Check("T2b", Core.RestoreAppWindow(new Item { name = "Feishu" }, f.Handle), "hidden + recorded-max -> RestoreAppWindow ok");
        Check("T2c", P.IsZoomed(f.Handle), "after restore the window is still MAXIMIZED (pre-fix: SW_RESTORE dropped it to the small normal rect)");
        Check("T2d", Core.WindowOnScreen(f.Handle), "maximized window is on screen");

        // ---- ③ hidden normal form -> restore back to the same rect ----
        var f2 = new Form { Visible = false, Size = new Size(640, 400) };
        P.RECT before2; P.GetWindowRect(f2.Handle, out before2);
        Core.RecordHiddenState("Feishu", false, before2);
        Core.RestoreAppWindow(new Item { name = "Feishu" }, f2.Handle);
        P.RECT after2; P.GetWindowRect(f2.Handle, out after2);
        bool sizeOk = Math.Abs((after2.Right - after2.Left) - (before2.Right - before2.Left)) <= 2
                   && Math.Abs((after2.Bottom - after2.Top) - (before2.Bottom - before2.Top)) <= 2;
        bool posOk = Math.Abs(after2.Left - before2.Left) <= 2 && Math.Abs(after2.Top - before2.Top) <= 2;
        Check("T3", sizeOk && posOk && !P.IsZoomed(f2.Handle),
            "hidden + recorded-normal -> same rect " + RectStr(before2) + " -> " + RectStr(after2) + " (maximized=" + P.IsZoomed(f2.Handle) + ")");

        // ---- ④ no record -> plain show, recorded rc stays irrelevant ----
        var f4 = new Form { Visible = false, Size = new Size(300, 200) };
        P.RECT before4; P.GetWindowRect(f4.Handle, out before4);
        Core.SaveWinState(new Core.WinState());
        Core.RestoreAppWindow(new Item { name = "Ghost" }, f4.Handle);
        P.RECT after4; P.GetWindowRect(f4.Handle, out after4);
        Check("T4", P.IsWindowVisible(f4.Handle) && !P.IsZoomed(f4.Handle)
              && Math.Abs((after4.Right - after4.Left) - (before4.Right - before4.Left)) <= 2,
            "no record -> plain SW_SHOW, rect kept " + RectStr(before4) + " -> " + RectStr(after4));

        // ---- ⑤ visible window -> restore must be a no-op ----
        var f5 = new Form { Size = new Size(300, 200), StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
        f5.Show();
        P.RECT before5; P.GetWindowRect(f5.Handle, out before5);
        Core.RecordHiddenState("Feishu", false, before5); // recorded state, but window is LIVE now
        bool noop = Core.RestoreAppWindow(new Item { name = "Feishu" }, f5.Handle);
        P.RECT after5; P.GetWindowRect(f5.Handle, out after5);
        Check("T5", noop && P.IsWindowVisible(f5.Handle)
              && Math.Abs(after5.Left - before5.Left) <= 2 && Math.Abs((after5.Right - after5.Left) - (before5.Right - before5.Left)) <= 2,
            "visible window -> no-op (rect " + RectStr(before5) + " unchanged, still not maximized=" + P.IsZoomed(f5.Handle) + ")");
        f5.Close();

        f.Dispose();
        f2.Dispose();
        f4.Dispose();
        try { File.Delete(Core.WinStatePath); } catch { }

        Console.WriteLine(fails == 0 ? "ALL PASS" : (fails + " FAILED"));
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
