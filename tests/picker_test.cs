using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Final acceptance test for the hotkey picker (test list A/B/C/D).
// Prereq: freshly built am-engine.exe running, silent pass finished, hotkey Ctrl+0 listening.
// A  render      : picker shows white text (screenshot + bright-pixel count)
// B  z-order     : B1 right after open the picker carries WS_EX_TOPMOST;
//                  B2 after the ~1.5s grace window the flag is released, so
//                  ordinary apps can cover it again (asserted on the picker's
//                  own handle — no external app is launched)
// C  interactive : drag caption moves it; drag bottom-right corner resizes it
// D  logic       : hotkey toggles (open/close/open); arrows+Enter opens the
//                  selected item; Esc closes; down-arrow from the last app
//                  crosses into the SCRIPTS section
// Prints PASS/FAIL lines, mirrors them to tests/bin/picker_test_result.txt,
// exit code 0 = all green.
static class PickerTest
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, uint extra);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    const uint KEYUP = 0x2;
    const uint M_LDOWN = 0x2, M_LUP = 0x4;
    const string OUT = @"C:\project\AppManager\tests\bin\picker_test_result.txt";
    const int SETTLE = 2500;   // > the engine's 1.5s topmost grace window

    static int fails = 0;
    static StringBuilder report = new StringBuilder();
    static void R(string s)
    {
        Console.WriteLine(s);
        lock (report) report.AppendLine(s);
    }
    static void Pass(string node, string what) { R("PASS " + node + "  " + what); }
    static void Fail(string node, string what) { fails++; R("FAIL " + node + "  " + what); }

    static IntPtr FindPicker()
    {
        // the picker is a WinForms borderless dark window titled "AppManager"
        // owned by am-engine. Match the ENGINE pid, not just the title: when
        // the engine dies the DWM keeps a "Ghost" window that still carries
        // the title+class but belongs to dwm — it must not count as "open".
        IntPtr found = IntPtr.Zero;
        int eng = EnginePid();
        EnumWindows((h, u) =>
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
        }, IntPtr.Zero);
        return found;
    }

    // pid of the running engine; -1 when the pid file is missing, in which
    // case FindPicker falls back to title+class matching only
    static int EnginePid()
    {
        try
        {
            int pid;
            if (int.TryParse(File.ReadAllText(Path.Combine(
                Environment.GetEnvironmentVariable("LOCALAPPDATA"), "AppManager", "am-engine.pid")).Trim(), out pid))
                return pid;
        }
        catch { }
        return -1;
    }

    // wait until the picker matches the wanted open/closed state (poll 200ms)
    static bool WaitPicker(bool wantOpen, int timeoutMs)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            if ((FindPicker() != IntPtr.Zero) == wantOpen) return true;
            Thread.Sleep(200);
            waited += 200;
        }
        return (FindPicker() != IntPtr.Zero) == wantOpen;
    }
    static void EnsureClosed()
    {
        if (FindPicker() != IntPtr.Zero) { Hotkey(); WaitPicker(false, 8000); }
    }

    // a TOPMOST window lives in a separate z stack above every non-topmost
    // window, so the assertions check the flag itself, not relative order.
    static bool Topmost(IntPtr h) { return (GetWindowLong(h, -20) & 0x00000008) != 0; }

    static void Press() // Ctrl+0 keystroke only, no waiting
    {
        keybd_event(0x11, 0, 0, IntPtr.Zero);
        keybd_event(0x30, 0, 0, IntPtr.Zero);
        keybd_event(0x30, 0, KEYUP, IntPtr.Zero);
        keybd_event(0x11, 0, KEYUP, IntPtr.Zero);
    }
    static void Hotkey() { Press(); Thread.Sleep(SETTLE); }
    // fire the hotkey, wait (up to 1.5s) for the picker to appear WITHOUT
    // sleeping past the engine's 1.5s topmost grace window
    static bool OpenFast()
    {
        Press();
        for (int waited = 0; waited < 1500; waited += 150)
        {
            if (FindPicker() != IntPtr.Zero) return true;
            Thread.Sleep(150);
        }
        return FindPicker() != IntPtr.Zero;
    }
    static void SendKey(int vk)
    {
        keybd_event((byte)vk, 0, 0, IntPtr.Zero);
        Thread.Sleep(100);
        keybd_event((byte)vk, 0, KEYUP, IntPtr.Zero);
        Thread.Sleep(350);
    }

    // deterministic mouse: SetCursorPos for moves + button events without
    // M_ABS (mouse_event's M_ABS flag mis-scales raw-pixel args)
    static void MoveAbs(int x, int y)
    {
        SetCursorPos(x, y);
    }
    static void Drag(int x0, int y0, int x1, int y1)
    {
        MoveAbs(x0, y0); Thread.Sleep(80);
        mouse_event(M_LDOWN, 0, 0, 0, 0); Thread.Sleep(120);
        MoveAbs(x1, y1); Thread.Sleep(120);
        mouse_event(M_LUP, 0, 0, 0, 0); Thread.Sleep(150);
    }

    static string LogTail(int lines)
    {
        string p = System.IO.Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "AppManager", "am.log");
        try
        {
            var all = File.ReadAllLines(p);
            var sb = new StringBuilder();
            for (int i = all.Length - lines; i < all.Length; i++) if (i >= 0) sb.AppendLine(all[i]);
            return sb.ToString();
        }
        catch { return ""; }
    }

    static int Main()
    {
        var eng = Process.GetProcessesByName("am-engine");
        if (eng.Length == 0) { Fail("pre", "am-engine not running; run 'am stop && am run' and wait for the pass first"); DumpReport(); return 1; }
        R("pre: am-engine pid " + eng[0].Id);
        EnsureClosed();

        // ---- A: open the picker, check the text actually painted ----
        IntPtr p = IntPtr.Zero;
        Hotkey();
        if (!WaitPicker(true, 8000))
        {
            Fail("A", "picker window not found after hotkey (check am.log)");
            R("---- results: " + fails + " failure(s) ----");
            DumpReport(); return 1;
        }
        p = FindPicker();
        R("picker hwnd=" + p);
        {
            RECT r; GetWindowRect(p, out r);
            int w = r.r - r.l, h = r.b - r.t;
            // the layered bitmap can lag the window becoming visible by a
            // frame or two; retry the screenshot until the text is painted
            // (up to ~2s) so a cold DWM first-paint does not flake the test.
            int bright = 0;
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            for (int attempt = 0; attempt < 10; attempt++)
            {
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.l, r.t, 0, 0, bmp.Size);
                bright = 0;
                for (int yy = 40; yy < h - 8; yy++)
                    for (int xx = 8; xx < w - 8; xx++)
                    {
                        var px = bmp.GetPixel(xx, yy);
                        if (px.R > 140 && px.G > 140 && px.B > 140) bright++;
                    }
                if (bright >= 100) break;
                Thread.Sleep(200);
                p = FindPicker(); // window may have been recreated (topmost release)
                if (p == IntPtr.Zero) break;
                GetWindowRect(p, out r); w = r.r - r.l; h = r.b - r.t;
                if (bmp.Width != w || bmp.Height != h) bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            }
            bmp.Save(@"C:\project\AppManager\tests\bin\picker_shot.png", ImageFormat.Png);
            R("   picker rect=(" + r.l + "," + r.t + "," + r.r + "," + r.b + ") exstyle=0x" + GetWindowLong(p, -20).ToString("X8"));
            if (bright >= 100) Pass("A", "text visible: " + bright + " bright pixels in client area");
            else Fail("A", "client area is black: only " + bright + " bright pixels (<100). shot -> tests/bin/picker_shot.png");
        }

        // ---- B1: within the topmost grace window, a freshly opened picker
        // must carry WS_EX_TOPMOST. The flag lives only ~1.5s, so press the
        // hotkey and poll IMMEDIATELY (no settle sleep — a 2.5s settle would
        // blow past the grace before the check runs); grab the handle at the
        // instant it first appears and check the flag on that exact handle.
        IntPtr freshPicker = IntPtr.Zero;
        {
            Hotkey(); // close the test-A picker
            WaitPicker(false, 8000);
            Press(); // re-open: the new picker window is TOPMOST on creation
            int waited = 0;
            while (waited < 8000)
            {
                IntPtr h = FindPicker();
                if (h != IntPtr.Zero) { freshPicker = h; break; }
                Thread.Sleep(80);
                waited += 80;
            }
            if (freshPicker == IntPtr.Zero) { Fail("B1", "picker did not open for the z-order check"); }
            else
            {
                bool b1 = Topmost(freshPicker);
                if (b1) Pass("B1", "right after open: picker carries WS_EX_TOPMOST");
                else Fail("B1", "picker NOT topmost right after open");
            }
        }

        // ---- B2: after the grace window the picker's TOPMOST flag must be
        // gone (WinForms TopMost toggle, same hwnd), so ordinary apps can
        // cover it again. Asserted on the picker's own handle: no external
        // app is launched or activated.
        {
            Thread.Sleep(2500); // past the 1.5s topmost-release grace
            p = freshPicker != IntPtr.Zero ? freshPicker : FindPicker();
            if (p == IntPtr.Zero) Fail("B2", "picker no longer open when checking z-order");
            else
            {
                p = FindPicker(); // same hwnd; re-find guards against recreate
                bool b2 = p != IntPtr.Zero && !Topmost(p);
                if (b2) Pass("B2", "after grace: picker's WS_EX_TOPMOST released");
                else Fail("B2", "picker STILL topmost after the grace window");
            }
            Hotkey(); // close the picker before C
            WaitPicker(false, 8000);
        }

        // ---- C: drag + resize a fresh picker, done INSIDE the topmost
        // grace window. After the grace the picker is an ordinary window and
        // a foreground terminal/IDE window can geometrically cover the click
        // point, so a synthesized left click would land on whatever is on
        // top and dragging never reaches the picker (delta 0). While TOPMOST
        // the picker is the topmost window, so the synthesized click is
        // guaranteed to hit it and the result is deterministic.
        {
            if (!OpenFast()) { Fail("C", "picker not open for drag/resize"); }
            else
            {
                Thread.Sleep(600); // window up and settling, still within the 1.5s grace
                p = FindPicker();
                RECT r0; GetWindowRect(p, out r0);
                int cx = (r0.l + r0.r) / 2, cy = r0.t + 12; // on the caption
                Drag(cx, cy, cx + 120, cy + 90);
                RECT r1; GetWindowRect(p, out r1);
                int dx = r1.l - r0.l, dy = r1.t - r0.t;
                R("   drag: dl=" + dx + " dt=" + dy);
                if (Math.Abs(dx - 120) < 40 && Math.Abs(dy - 90) < 40) Pass("C1", "dragged caption: window moved (" + dx + "," + dy + ")");
                else Fail("C1", "drag did not move window (d=" + dx + "," + dy + ")");

                Drag(r1.r - 3, r1.b - 3, r1.r - 80, r1.b - 60);
                RECT r2; GetWindowRect(p, out r2);
                int dw = (r2.r - r2.l) - (r1.r - r1.l), dh = (r2.b - r2.t) - (r1.b - r1.t);
                R("   resize: dw=" + dw + " dh=" + dh);
                if (dw < -30 && dh < -30) Pass("C2", "resized via corner: size delta (" + dw + "," + dh + ")");
                else Fail("C2", "resize did not change size (d=" + dw + "," + dh + ")");

                SendKey(0x1B); // close for test D
                WaitPicker(false, 5000);
            }
        }

        // ---- D: toggle + selection logic ----
        {
            // D1: hotkey toggles: open -> close -> open
            EnsureClosed();
            Hotkey();
            bool o1 = WaitPicker(true, 8000);
            Hotkey();
            bool c1 = WaitPicker(false, 8000);
            Hotkey();
            bool o2 = WaitPicker(true, 8000);
            if (o1 && c1 && o2) Pass("D1", "hotkey toggles the picker (open/close/open)");
            else Fail("D1", "toggle failed: open1=" + o1 + " closed=" + c1 + " reopen=" + o2);

            // D2: arrows + Enter opens the selected item and closes the picker
            if (o2)
            {
                SendKey(0x28); SendKey(0x28); SendKey(0x0D); // Down x2 -> item 2, Enter
                bool gone = WaitPicker(false, 10000);
                string tail = LogTail(6);
                if (gone && tail.Contains("picker open")) Pass("D2", "Enter closed the picker and opened the selected item");
                else Fail("D2", "after Enter: picker gone=" + gone + ", log tail: " + tail.Replace("\n", " | "));
            }

            // D3: Esc closes a re-opened picker
            EnsureClosed();
            Hotkey();
            if (WaitPicker(true, 8000))
            {
                SendKey(0x1B);
                if (WaitPicker(false, 5000)) Pass("D3", "Esc closed the picker");
                else Fail("D3", "picker still open after Esc");
            }
            else Fail("D3", "picker did not reopen for the Esc test");

            // D4: down-arrow from the last APP crosses into the SCRIPTS
            // section (log: "picker key down -> section scripts sel 0")
            EnsureClosed();
            Hotkey();
            if (WaitPicker(true, 8000))
            {
                SendKey(0x28); SendKey(0x28); // Down x2 -> last app
                SendKey(0x28);                 // Down -> first script
                string tail = LogTail(4);
                SendKey(0x1B);
                bool gone = WaitPicker(false, 5000);
                if (tail.Contains("section scripts sel 0")) Pass("D4", "down-arrow crossed into the SCRIPTS section");
                else Fail("D4", "no section-cross in log tail: " + tail.Replace("\n", " | ") + " pickerGone=" + gone);
            }
            else Fail("D4", "picker did not open for the section-cross test");
        }

        R("---- results: " + (fails == 0 ? "ALL PASS" : fails + " failure(s)") + " ----");
        DumpReport();
        return fails == 0 ? 0 : 1;
    }

    static void DumpReport()
    {
        lock (report)
        {
            File.WriteAllText(OUT, report.ToString());
            R("report -> " + OUT);
        }
    }
}
