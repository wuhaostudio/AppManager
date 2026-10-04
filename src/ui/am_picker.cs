using System;

using System.Collections.Generic;

using System.Runtime.InteropServices;

using System.Text;

using System.Threading;

using AppManager.Shared;



// AppManager picker UI module (built into am-engine.exe, /target:winexe, no console).

//

// The picker is a borderless-looking, transparent layered popup listing the

// managed items: arrows move the selection, Enter opens the selected item's

// app window, Esc closes. It is shown by the engine's hotkey watcher via

// Picker.Show(); it is a top-level window module with no engine dependencies.

//

// Rendering: the window is WS_EX_LAYERED and its pixels are a 32bpp alpha DIB

// pushed with UpdateLayeredWindow. DefWindowProc paints the caption into the

// window, so after any caption repaint we refresh the snapshot with a fresh

// push (WM_EXITSIZEMOVE / a posted WM_PICKER_SYNC). Moving/resizing sends

// WM_MOVING/WM_SIZING, which we answer with a push while they happen.

//

// Foreground: shown TOPMOST once at open, then TOPMOST is released ~1.5s

// later so ordinary apps can come forward normally (click-to-activate).

// Keyboard: global GetAsyncKeyState polling while the picker is open; the

// hotkey itself keeps listening on the engine's hidden window, and pressing

// it while open closes the picker (toggle).

namespace AppManager.Ui

{

    static class Picker

    {

        // ---------- Win32 ----------

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



        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int l, t, r, b; }

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential)] public struct BLENDFUNCTION { public byte alpha, flags, color1, color2; }

        [StructLayout(LayoutKind.Sequential)] struct BITMAPINFO

        {

            public int size, width, height;

            public int planes, bitCount;

            public int compression, imageSize, xPels, yPels;

            public int colorsUsed, colorsImportant;

            public int redMask, greenMask, blueMask, alphaMask; // 32bpp BI_MASKS

        }



        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]

        static extern ushort RegisterClassExW(ref WNDCLASSEX wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWindowExW")]

        static extern IntPtr CreateWindowExW(int exStyle, string cls, string title, int style,

            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p2);

        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);

        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

        [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr h);

        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);

        [DllImport("user32.dll")] static extern int PeekMessage(out MSG m, IntPtr h, uint min, uint max, uint rm);

        [DllImport("user32.dll")] static extern int TranslateMessage(ref MSG m);

        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

        [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);

        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int idx, int val);
        [DllImport("user32.dll", EntryPoint="SetWindowLongPtrW")] static extern long SetWindowLongPtr(IntPtr h, int idx, long val);

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);

        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT rc);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT rc);

        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);

        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idFrom, uint idTo, bool attach);

        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DrawTextW(IntPtr hdc, string s, int len, ref RECT rc, int fmt);

        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pSrc,

            ref SIZE sz, IntPtr hdcSrc, ref POINT pDst, uint crKey, ref BLENDFUNCTION blend, uint flags);

        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bi,

            int usage, out IntPtr data, IntPtr shared, int offset);

        [DllImport("gdi32.dll")] static extern int SetBkColor(IntPtr hdc, int color);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]

        static extern IntPtr CreateFontW(int h, int w, int exp1, int exp2, int weight,

            byte italic, byte underline, byte strikeout, int charset, int outType,

            int clipType, int quality, int pitch, string face);

        [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int i);

        [DllImport("gdi32.dll")] static extern int DeleteObject(IntPtr o);

        [DllImport("gdi32.dll")] static extern int SetTextColor(IntPtr hdc, int color);

        [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr hdc, int mode);

        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr o);

        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);

        [DllImport("kernel32.dll")] static extern IntPtr LoadLibrary(string n);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr m, string name);



        const int WS_POPUP = unchecked((int)0x80000000);

        const int WS_CAPTION = 0x00C00000;

        const int WS_THICKFRAME = 0x00040000;

        const int WS_SYSMENU = 0x00080000;

        const int WS_EX_LAYERED = 0x00080000;

        const int WS_EX_TOPMOST = 0x00000008;

        const int GWL_EXSTYLE = -20;

        const uint SWP_SHOWWINDOW = 0x0040, SWP_NOACTIVATE = 0x0010;

        const uint WM_HOTKEY = 0x0312;

        const uint WM_ERASEBKGND = 0x0032;

        const uint WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232, WM_MOVING = 0x0234, WM_SIZING = 0x0233;

        const uint WM_USER = 0x0400;

        const uint WM_PICKER_SYNC = WM_USER + 1; // posted to self: re-push the layered bitmap

        const uint WM_PICKER_RELEASE = WM_USER + 2; // posted to self: release topmost on the owning thread



        const int ROW_H = 20;

        const int PICKER_W = 520;



        // ---------- offscreen layered-buffer state (one picker at a time) ----------

        static IntPtr pickerDc;           // offscreen DC holding the bitmap

        static IntPtr pickerBitmap;       // 32bpp BI_MASKS DIB section

        static IntPtr pickerBitmapData;   // locked pointer to the DIB pixels

        static int pickerW = 0, pickerH = 0;

        static string pickerText = "";

        static IntPtr pickerFont;

        static int exTopmost;             // exstyle captured while TOPMOST



        // ---------- public entry: open the picker on the calling thread ----------

        public static void Show()

        {

            var cfg = Core.Load();

            var items = new List<Item>();

            foreach (var it in cfg.items) if (it.enabled) items.Add(it);



            int n = items.Count;

            int headerH = 30;

            int bodyH = Math.Max(n, 1) * ROW_H;

            int H = headerH + bodyH + 8;



            int screenW = GetSystemMetrics(0), screenH = GetSystemMetrics(1);

            int x = Math.Max(0, (screenW - PICKER_W) / 2);

            int y = Math.Max(0, (screenH - H) / 4);



            var wc = new WNDCLASSEX();

            wc.cbSize = Marshal.SizeOf(wc);

            wc.lpfnWndProc = DefWindowProcAddress();

            wc.hInstance = GetModuleHandle(null);

            wc.hbrBackground = GetStockObject(4 /*BLACK_BRUSH*/);

            wc.lpszClassName = "AMPicker" + Guid.NewGuid().ToString("N");

            if (RegisterClassExW(ref wc) == 0) { Core.Log("picker: RegisterClassExW failed (" + Marshal.GetLastWin32Error() + ")"); return; }



            // caption + thick frame => native drag/resize; the layered bitmap

            // takes over the client area so it still looks like a dark popup.

            IntPtr hwnd = CreateWindowExW(0, wc.lpszClassName, "AppManager",

                WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU,

                x, y, PICKER_W, H, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            if (hwnd == IntPtr.Zero) { Core.Log("picker: create failed (" + Marshal.GetLastWin32Error() + ")"); return; }



            // transparent layered: content is a 32bpp alpha bitmap pushed via

            // UpdateLayeredWindow; the caption is painted by DefWindowProc and

            // included in the snapshot.

            exTopmost = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_LAYERED | WS_EX_TOPMOST;

            SetWindowLong(hwnd, GWL_EXSTYLE, exTopmost);



            pickerFont = CreateFontW(0, 0, 0, 0, 400, 0, 0, 0, 131 /*ANSI*/, 0, 0, 4 /*CLEARTYPE_QUALITY*/, 1 /*DEFAULT_PITCH*/, "Consolas");

            pickerText = "";



            RepaintText(items, n, 0);

            CreatePickerBuffer(PICKER_W, H);

            PaintBitmap();

            PushLayered(hwnd);



            // open on top of every window, without stealing activation

            SetWindowPos(hwnd, new IntPtr(-1), x, y, PICKER_W, H, SWP_NOACTIVATE | SWP_SHOWWINDOW);

            Core.Log("picker shown (" + n + " item(s))");



            // keyboard focus: the picker is spawned from a background engine

            // thread, so SetForegroundWindow can be refused — fall back to

            // attaching the input thread of the current foreground window.

            if (!SetForegroundWindow(hwnd))

            {

                IntPtr fg = GetForegroundWindow();

                uint fgPid;

                uint fgTid = P.GetWindowThreadProcessId(fg, out fgPid);

                uint myTid = GetCurrentThreadId();

                if (fgTid != 0 && fgTid != myTid)

                {

                    if (AttachThreadInput(myTid, fgTid, true))

                    {

                        SetForegroundWindow(hwnd);

                        SetFocus(hwnd);

                        AttachThreadInput(myTid, fgTid, false);

                    }

                }

            }



            // release topmost ~1.5s after opening: the picker stays a normal

            // window the user can cover by activating other apps.

            // release topmost ~1.5s after opening, ON THE WINDOW'S OWNING

            // THREAD (the input pump below). Applied from a background thread

            // the release does not stick, so post it to the owning thread.

            Thread grace = new Thread(() =>

            {

                Thread.Sleep(1500);

                if (!IsWindow(hwnd)) return;

                bool posted = PostMessage(hwnd, WM_PICKER_RELEASE, IntPtr.Zero, IntPtr.Zero);

                Core.Log("picker: topmost release posted=" + posted);

            }) { IsBackground = true, Name = "am-topmost-grace" };

            try { grace.Start(); } catch (Exception ex) { Core.Log("picker grace thread: " + ex.Message); }



            // input: global GetAsyncKeyState polling (works regardless of focus)

            int[] watch = new int[] { 0x1B, 0x0D, 0x26, 0x28, 0x25, 0x27 }; // Esc Enter Up Down Left Right

            for (int i = 0; i < watch.Length; i++) GetAsyncKeyState(watch[i]);



            int sel = 0;

            int lastSel = -1;

            bool open = true;

            // deterministic up->down edge detection: the OS transition bit of

            // GetAsyncKeyState (0x4000) is unreliable (key taps shorter than a

            // poll interval clear it), so we track each key's previous state.

            short[] prev = new short[watch.Length];

            for (int i = 0; i < watch.Length; i++) prev[i] = GetAsyncKeyState(watch[i]);

            while (open)

            {

                // pump our own messages so move/resize stay live. Painting is

                // the layered bitmap, never BeginPaint (DefWindowProc owns the

                // caption; we snapshot it instead).

                MSG pm;

                bool skipDispatch;

                while (PeekMessage(out pm, IntPtr.Zero, 0, 0, 0x0001 /*PM_REMOVE*/) != 0)

                {

                    skipDispatch = false;

                    if (pm.message == WM_HOTKEY && IsWindow(hwnd)) { open = false; } // hotkey toggles the picker closed

                    else if (pm.message == WM_ERASEBKGND && pm.hwnd == hwnd) { skipDispatch = true; }

                    else if ((pm.message == WM_MOVING || pm.message == WM_SIZING) && pm.hwnd == hwnd)

                    { skipDispatch = true; if (PaintBitmap() == 0) PushLayered(hwnd); }

                    else if (pm.message == WM_ENTERSIZEMOVE && pm.hwnd == hwnd)

                    { skipDispatch = true; }

                    else if (pm.message == WM_EXITSIZEMOVE && pm.hwnd == hwnd)

                    { skipDispatch = true; PushLayered(hwnd); }

                    else if (pm.message == WM_PICKER_SYNC && pm.hwnd == hwnd) { skipDispatch = true; }

                    else if (pm.message == WM_PICKER_RELEASE && pm.hwnd == hwnd)
                    {
                        skipDispatch = true;
                        // this OS only honors the TOPMOST tier from the
                        // exstyle supplied at window CREATION; clearing the
                        // flag or SetWindowPos(HWND_NOTOPMOST) afterwards does
                        // not demote it. Release = destroy + recreate the
                        // window WITHOUT TOPMOST at the same rect.
                        RECT fr;
                        GetWindowRect(hwnd, out fr);
                        int fx = fr.l, fy = fr.t, fw = fr.r - fr.l, fh = fr.b - fr.t;
                        DestroyWindow(hwnd);
                        hwnd = CreateWindowExW(WS_EX_LAYERED, wc.lpszClassName, "AppManager",
                            WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU,
                            fx, fy, fw, fh, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
                        if (hwnd != IntPtr.Zero)
                        {
                            P.ShowWindow(hwnd, 5 /*SW_SHOW*/);
                            // redraw the content into the fresh snapshot
                            RepaintText(items, n, sel);
                            CreatePickerBuffer(fw, fh);
                            PaintBitmap();
                            PushLayered(hwnd);
                        }
                        Core.Log("picker: topmost released (window recreated without TOPMOST, hwnd=0x" + hwnd.ToString("X") + ")");
                    }

                    if (skipDispatch) continue;

                    TranslateMessage(ref pm);

                    DispatchMessage(ref pm);

                }

                for (int i = 0; i < watch.Length; i++)

                {

                    short cur = GetAsyncKeyState(watch[i]);

                    bool downNow = (cur & 0x8000) != 0;

                    bool wasDown = (prev[i] & 0x8000) != 0;

                    prev[i] = cur;

                    // our own up->down edge (reliable); the OS 0x4000 transition

                    // bit is missed when a key tap lasts < poll interval

                    if (downNow && !wasDown)

                    {

                        switch (watch[i])

                        {

                            case 0x26: // Up

                            case 0x25: // Left

                                if (n > 0) sel = (sel - 1 + n) % n;

                                Core.Log("picker key up/left -> sel " + sel);

                                break;

                            case 0x28: // Down

                            case 0x27: // Right

                                if (n > 0) sel = (sel + 1) % n;

                                Core.Log("picker key down/right -> sel " + sel);

                                break;

                            case 0x0D: // Enter -> open the selected item

                                Core.Log("picker key enter -> opening item " + Math.Min(sel, n - 1));

                                if (n > 0) OpenItem(items[sel]);

                                open = false;

                                break;

                            case 0x1B: // Esc -> close

                                Core.Log("picker key esc -> closing");

                                open = false;

                                break;

                        }

                    }

                }

                if (open && sel != lastSel)

                {

                    RepaintText(items, n, sel);

                    if (PaintBitmap() == 0) PushLayered(hwnd);

                    lastSel = sel;

                }

                Thread.Sleep(25);

            }



            DestroyWindow(hwnd);

            DeleteObject(pickerFont);

            pickerFont = IntPtr.Zero;

            pickerText = "";

            Core.Log("picker closed");

        }



        // ---------- layered-buffer plumbing ----------

        static void CreatePickerBuffer(int w, int h)

        {

            if (pickerDc != IntPtr.Zero)

            {

                DeleteObject(pickerBitmap);

                DeleteDC(pickerDc);

            }

            pickerBitmap = IntPtr.Zero;

            pickerBitmapData = IntPtr.Zero;

            pickerW = w; pickerH = h;

            pickerDc = CreateCompatibleDC(IntPtr.Zero);

            if (pickerDc == IntPtr.Zero) return;

            var bi = new BITMAPINFO();

            bi.size = Marshal.SizeOf(typeof(BITMAPINFO));

            bi.width = w; bi.height = -h;      // top-down

            bi.planes = 1; bi.bitCount = 32;

            bi.compression = 11;               // BI_MASKS

            bi.redMask = 0x00FF0000; bi.greenMask = 0x0000FF00;

            bi.blueMask = 0x000000FF; bi.alphaMask = unchecked((int)0xFF000000);

            IntPtr data;

            pickerBitmap = CreateDIBSection(pickerDc, ref bi, 0, out data, IntPtr.Zero, 0);

            if (pickerBitmap == IntPtr.Zero) { pickerDc = IntPtr.Zero; return; }

            pickerBitmapData = data;

            IntPtr old = SelectObject(pickerDc, pickerBitmap);

            if (old != IntPtr.Zero) DeleteObject(old);

        }



        // paint text into the DIB: opaque black base + white Consolas lines

        static int PaintBitmap()

        {

            if (pickerBitmap == IntPtr.Zero || pickerBitmapData == IntPtr.Zero || pickerW <= 0) return 1;

            for (int i = 0; i < pickerW * pickerH; i++)

                Marshal.WriteInt32(pickerBitmapData, i * 4, unchecked((int)0xFF000000));

            if (pickerText.Length > 0)

            {

                SetBkColor(pickerDc, unchecked((int)0xFF000000));

                SetTextColor(pickerDc, 0x00FFFFFF); // white (0x00BBGGRR)

                SetBkMode(pickerDc, 0);              // OPAQUE; alpha is per-pixel in the DIB

                IntPtr oldFont = pickerFont != IntPtr.Zero ? SelectObject(pickerDc, pickerFont) : IntPtr.Zero;

                RECT full = new RECT { l = 0, t = 0, r = pickerW, b = pickerH };

                DrawTextW(pickerDc, pickerText, -1, ref full, 0x03 /*DT_LEFT|DT_TOP*/);

                if (oldFont != IntPtr.Zero) SelectObject(pickerDc, oldFont);

            }

            return 0;

        }



        static bool PushLayered(IntPtr hwnd)

        {

            if (pickerBitmap == IntPtr.Zero) return false;

            RECT rc;

            GetClientRect(hwnd, out rc);

            POINT src = new POINT { x = 0, y = 0 };

            SIZE sz = new SIZE { cx = rc.r, cy = rc.b };

            POINT dst = new POINT();

            ClientToScreen(hwnd, ref dst);

            BLENDFUNCTION bf = new BLENDFUNCTION { flags = 0 /*AC_SRC_ALPHA*/, alpha = 0xFF, color1 = 0 };

            bool ok = UpdateLayeredWindow(hwnd, IntPtr.Zero, ref src, ref sz,

                IntPtr.Zero, ref dst, 0, ref bf, 0x0002 /*ULW_OPAQUE*/);

            // the window moved/resized: rebuild the snapshot to match, then repaint

            if (rc.r != pickerW || rc.b != pickerH)

            {

                CreatePickerBuffer(rc.r, rc.b);

                PaintBitmap();

            }

            return ok;

        }



        // ---------- content ----------

        static void RepaintText(List<Item> items, int n, int sel)

        {

            var sb = new StringBuilder();

            sb.Append(" AppManager");

            if (n > 0) sb.Append("    [Up/Down/Left/Right] move   [Enter] open   [Esc] close");

            else sb.Append("    [Esc] close");

            sb.Append('\n');

            if (n == 0)

            {

                sb.Append(" (no enabled items - add one with 'am add')");

            }

            else

            {

                for (int i = 0; i < n; i++)

                {

                    var it = items[i];

                    int pc = Core.ProcCount(it.processName);

                    string state = pc > 0 ? "running" : "stopped";

                    string mark = i == sel ? ">>>" : "   ";

                    sb.Append(mark).Append(' ');

                    sb.Append(PadR(it.name, 24)).Append(' ');

                    sb.Append(PadR(it.script ? "script" : "app", 7)).Append(' ');

                    sb.Append(state);

                    if (i < n - 1) sb.Append('\n');

                }

            }

            pickerText = sb.ToString();

        }



        static string PadR(string s, int w)

        {

            s = s ?? "";

            if (s.Length > w) s = s.Substring(0, w - 1) + ".";

            while (s.Length < w) s += " ";

            return s;

        }



        // open the selected item: start it if not running, then restore/

        // foreground its windows (plain apps); scripts are launch-only.

        static void OpenItem(Item it)

        {

            Core.Log("picker open '" + it.name + "'");

            bool started = false;

            if (Core.ProcCount(it.processName) == 0)

                started = Core.StartApp(it);



            if (!it.script)

            {

                int tries = 0;

                var wins = new List<IntPtr>();

                while (tries < 20 && wins.Count == 0)

                {

                    Thread.Sleep(250);

                    wins = Core.CollectWindows(it.processName, it.windowTitle);

                    tries++;

                }

                foreach (var h in wins)

                {

                    P.ShowWindow(h, Core.SW_SHOW);

                    P.ShowWindow(h, 9 /*SW_RESTORE*/);

                }

                if (wins.Count > 0)

                    P.SetForegroundWindow(wins[0]);

                else if (started)

                    Core.Log("picker: no window found for '" + it.name + "' after start (background app?)");

            }

            Core.Log("picker: '" + it.name + "' " + (started ? "started" : "already running") +

                (it.script ? " (script, launch-only)" : " window(s) shown"));

        }



        // address of user32!DefWindowProcW (unmanaged default wndproc)

        static IntPtr DefWindowProcAddress()

        {

            IntPtr u32 = LoadLibrary("user32.dll");

            return GetProcAddress(u32, "DefWindowProcW");

        }

    }

}

