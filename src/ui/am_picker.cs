using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Drawing;

using AppManager.Shared;


// AppManager picker UI module (built into am-engine.exe, /target:winexe, no console).
//
// The picker is a dark, semi-transparent WinForms window listing the managed
// items in two sections: APPS on top, SCRIPTS on the bottom (each section
// gets a header row; an empty section is hidden). Shown by the engine's
// hotkey watcher via Picker.Show(); pressing the hotkey again while open
// closes it (toggle), Esc also closes.
//
// Look & feel (verified interactively with tests/window_probe.cs):
//   - borderless dark window (Opacity 0.7 when idle), custom dark title bar
//     with minimize / maximize / close buttons at the right edge
//   - self-drawn move (drag the title bar) and self-drawn resize (drag the
//     6px edges/corners): the picker captures the mouse and follows the
//     cursor itself. The system's native NC drag loop cannot be driven by
//     synthesized input, which made the behaviour untestable; opacity goes to
//     1.0 while a move/resize is in flight (DWM composites semi-transparent
//     windows slowly while they move) and back to 0.7 on release
//   - TOPMOST on open, released ~1.5s later (WinForms TopMost toggle, no
//     window recreation needed) so ordinary apps can come forward normally
//
// Keyboard: global GetAsyncKeyState polling (works regardless of focus).
// Up/Down/Left/Right move the selection, crossing section boundaries when a
// list edge is reached (apps edge -> first script, scripts edge -> last
// app); Enter opens the selected item's app window, Esc closes. The hotkey
// itself (RegisterHotKey on the engine's hidden window) toggles the picker
// open/closed.
//
// Threading: Picker.Show() does NOT block the caller. Each open picker runs
// on its own background thread with its own WinForms message loop; the
// engine's watcher thread keeps pumping so a second hotkey press (WM_HOTKEY
// on the hidden window) re-enters Show() and closes the open picker.
namespace AppManager.Ui

{

    static class Picker

    {

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idFrom, uint idTo, bool fAttach);

        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();



        // one open picker at a time; toggle-Show closes it
        static object gate = new object();

        static Form current;



        // Open the picker, or toggle-close the one that is open. Non-blocking:
        // returns immediately.
        public static void Show()

        {

            var cfg = Core.Load();

            var items = new List<Item>();

            foreach (var it in cfg.items) if (it.enabled) items.Add(it);



            lock (gate)

            {

                if (current != null)

                {

                    var f = current;

                    current = null;

                    Core.Log("picker hotkey -> closing (toggle)");

                    // close OUTSIDE the gate: FormClosed takes this same lock
                    // (to clear `current`), so holding it while we wait on the
                    // cross-thread Close() deadlocks watcher vs UI thread —
                    // from which point every further hotkey press is dropped
                    // and the picker handle leaks.
                    try

                    {

                        if (f.IsHandleCreated) f.BeginInvoke(new MethodInvoker(f.Close));

                        else f.Close();

                    }

                    catch { try { f.Close(); } catch { } }

                    return;

                }

                var form = new PickerForm(items);

                current = form;

                form.FormClosed += (s, e) =>

                {

                    Core.Log("picker closed");

                    lock (gate) { if (current == form) current = null; }

                };

                form.Shown += (s, e) => Core.Log("picker shown (" + items.Count + " item(s))");

                try

                {

                    new Thread(() =>

                    {

                        Application.Run(form);

                        Core.Log("picker UI thread exited");

                    }) { IsBackground = true, Name = "am-picker" }.Start();

                }

                catch (Exception ex)

                {

                    Core.Log("picker thread failed to start: " + ex.Message);

                    current = null;

                    form.Dispose();

                }

            }

        }



        // ---------- the picker window ----------
        class PickerForm : Form

        {

            const int GRIP = 6;           // edge/corner grab zone for native resize
            const double OPACITY = 0.7;   // idle semi-transparency
            const int ROW_H = 26;
            const int HDR_H = 18;         // section header height
            const int BTN_H = 34;
            const int BTN_W = 36;
            const int BTN_IW = 32;
            const int BTNY = 4;



            readonly List<Item> appItems = new List<Item>();
            readonly List<Item> scriptItems = new List<Item>();

            int selA = 0, selS = 0;
            bool inScripts;              // which section the selection lives in

            List<string> appText = new List<string>();
            List<string> scriptText = new List<string>();

            Panel body;
            Label hdrA, hdrS, status, title;
            ListBox listA, listS;
            Button minBtn, maxBtn, closeBtn;
            bool inNativeOp;           // a self-drawn move/resize is in flight
            Point opCursor, opWin;     // cursor + window position when it started
            Size opSize;               // window size when it started
            int opHt;                  // 0 = move, else the grabbed edge (HT code)
            Control opCap;             // control holding the mouse capture
            bool syncing;               // guard: list handlers vs programmatic selects

            System.Windows.Forms.Timer poller;
            System.Windows.Forms.Timer stateTimer;
            System.Windows.Forms.Timer grace;



            public PickerForm(List<Item> items)

            {

                foreach (var it in items) (it.script ? scriptItems : appItems).Add(it);
                inScripts = appItems.Count == 0;



                int bodyH = 0;

                if (appItems.Count > 0) bodyH += HDR_H + appItems.Count * ROW_H;

                if (scriptItems.Count > 0) bodyH += HDR_H + scriptItems.Count * ROW_H;

                int H = BTN_H + Math.Max(bodyH, 2 * ROW_H) + 30;

                int W = 520;

                int sw = Screen.PrimaryScreen.Bounds.Width, sh = Screen.PrimaryScreen.Bounds.Height;

                Location = new Point(Math.Max(0, (sw - W) / 2), Math.Max(0, (sh - H) / 4));

                Size = new Size(W, H);

                MinimumSize = new Size(320, 140);

                Text = "AppManager";

                BackColor = Color.FromArgb(30, 30, 32);

                Opacity = OPACITY;

                FormBorderStyle = FormBorderStyle.None;

                StartPosition = FormStartPosition.Manual;

                TopMost = true;

                ShowInTaskbar = true;

                AutoScaleMode = AutoScaleMode.None;



                // ---- custom dark title bar ----
                var tb = new Panel { BackColor = Color.FromArgb(24, 24, 28), Dock = DockStyle.Top, Height = BTN_H };

                title = new Label

                {

                    Text = "AppManager    Up/Down select    Enter open    Esc close",

                    ForeColor = Color.FromArgb(220, 220, 220),

                    BackColor = Color.Transparent,

                    Font = new Font("Segoe UI", 10f, FontStyle.Bold),

                    AutoSize = false,

                    TextAlign = ContentAlignment.MiddleLeft,

                    Location = new Point(8, 4),

                };

                minBtn = TitleBtn("—");

                maxBtn = TitleBtn("□");

                closeBtn = TitleBtn("×");

                closeBtn.Click += (s, e) => Close();

                minBtn.Click += (s, e) => WindowState = FormWindowState.Minimized;

                maxBtn.Click += (s, e) => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

                tb.Controls.Add(title);

                tb.Controls.Add(minBtn);

                tb.Controls.Add(maxBtn);

                tb.Controls.Add(closeBtn);

                PositionButtons();



                // ---- two-section body: APPS on top, SCRIPTS below ----
                body = new Panel { BackColor = BackColor, Dock = DockStyle.Fill };

                if (appItems.Count > 0)

                {

                    hdrA = SectionHeader("APPS");

                    listA = NewList();

                    FillList(listA, appItems, appText);

                    body.Controls.Add(hdrA);

                    body.Controls.Add(listA);

                }

                if (scriptItems.Count > 0)

                {

                    hdrS = SectionHeader("SCRIPTS");

                    listS = NewList();

                    FillList(listS, scriptItems, scriptText);

                    body.Controls.Add(hdrS);

                    body.Controls.Add(listS);

                }

                status = new Label

                {

                    ForeColor = Color.FromArgb(255, 200, 80),

                    BackColor = Color.FromArgb(30, 30, 32),

                    Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),

                    Dock = DockStyle.Bottom,

                    Height = 30,

                    TextAlign = ContentAlignment.MiddleLeft,

                };

                Controls.Add(status);

                Controls.Add(body);

                Controls.Add(tb);

                body.BringToFront();

                status.BringToFront();

                LayoutBody();

                body.SizeChanged += (s, e) => LayoutBody();



                // initial selection + status
                if (!inScripts && appItems.Count > 0) SelectApp(0);
                else if (scriptItems.Count > 0) SelectScript(0);



                // ---- self-drawn move (title bar) + resize (edges/corners) ----
                // The system's native NC drag loop (ReleaseCapture +
                // WM_NCLBUTTONDOWN) cannot be driven by synthesized mouse
                // input, so the picker moves itself: grab the mouse, follow
                // the cursor, write Location/Bounds. Same feel for the user,
                // scriptable for the acceptance test.
                HookCaption(tb);
                HookCaption(title);   // the caption label covers most of the bar

                // The docked panels and list boxes cover the client area, so
                // the 6px grab border is detected on them (screen space).
                HookGrip(body);
                HookGrip(status);
                if (listA != null) HookGrip(listA);
                if (listS != null) HookGrip(listS);



                // ---- focus: steal it for this picker's lifetime so that
                //   (a) the picker's own GetAsyncKeyState polls see a
                //        consistent foreground for its toggle, and no child
                //        control competes for the arrow/Enter keys (they are
                //        all non-selectable, so the form keeps focus);
                //   (b) keystrokes never land in the app that had the
                //        keyboard before the hotkey fired.
                // The hotkey fired globally, so we have no input of our own:
                // attach to the current foreground thread to be allowed to
                // activate.
                Shown += (s, e) =>
                {
                    try
                    {
                        IntPtr fg = GetForegroundWindow();
                        uint fgPid;
                        uint fgTid = GetWindowThreadProcessId(fg, out fgPid);
                        uint myTid = GetCurrentThreadId();
                        bool attached = false;
                        if (fgTid != 0 && fgTid != myTid)
                            attached = AttachThreadInput(myTid, fgTid, true);
                        SetForegroundWindow(this.Handle);
                        if (attached) AttachThreadInput(myTid, fgTid, false);
                    }
                    catch { }
                };


                // ---- release topmost ~1.5s after open: the picker becomes a

                // normal window ordinary apps can cover ----
                grace = new System.Windows.Forms.Timer { Interval = 1500 };

                grace.Tick += (s, e) =>

                {

                    grace.Stop();

                    TopMost = false;

                    Core.Log("picker: topmost released");

                };

                grace.Start();



                // ---- refresh running/stopped state once a second ----
                stateTimer = new System.Windows.Forms.Timer { Interval = 1000 };

                stateTimer.Tick += (s, e) => RefreshStates();

                stateTimer.Start();



                // ---- keyboard: global polling (works with or without focus) ----
                int[] watch = { 0x1B, 0x0D, 0x26, 0x28, 0x25, 0x27 }; // Esc Enter Up Down Left Right
                short[] prev = new short[watch.Length];

                for (int i = 0; i < watch.Length; i++) prev[i] = GetAsyncKeyState(watch[i]);

                poller = new System.Windows.Forms.Timer { Interval = 30 };

                poller.Tick += (s, e) => Poll(watch, prev);

                poller.Start();

            }



            Label SectionHeader(string t)

            {

                return new Label

                {

                    Text = "  " + t,

                    ForeColor = Color.FromArgb(120, 160, 220),

                    BackColor = Color.FromArgb(34, 34, 40),

                    Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),

                    AutoSize = false,

                };

            }



            // The lists must never take focus: keyboard is handled ONLY by the

            // global GetAsyncKeyState poller below. A focusable ListBox also

            // processes the arrow keys natively, so a single physical Down was

            // applied twice (native row move + poller) and rows were skipped.

            class NonSelectList : ListBox

            {

                public NonSelectList() { SetStyle(ControlStyles.Selectable, false); TabStop = false; }

            }



            ListBox NewList()

            {

                var lb = new NonSelectList

                {

                    BackColor = Color.FromArgb(38, 38, 42),

                    ForeColor = Color.White,

                    BorderStyle = BorderStyle.None,

                    Font = new Font("Consolas", 12f, FontStyle.Regular),

                    ItemHeight = ROW_H,

                };

                lb.DoubleClick += (s, e) => OpenSelected();

                lb.SelectedIndexChanged += (s, e) =>

                {

                    if (syncing || lb.SelectedIndex < 0) return;

                    // mouse click in one list moves the selection there
                    if (lb == listA) { selA = lb.SelectedIndex; inScripts = false; UpdateStatus(); }
                    else { selS = lb.SelectedIndex; inScripts = true; UpdateStatus(); }

                };

                return lb;

            }



            void FillList(ListBox lb, List<Item> its, List<string> textOut)

            {

                for (int i = 0; i < its.Count; i++)

                {

                    string t = RowText(its[i]);

                    textOut.Add(t);

                    lb.Items.Add(t);

                }

            }



            // stack the visible sections vertically; leftover vertical space

            // below stays empty (rows are fixed-height)
            void LayoutBody()

            {

                int w = body.ClientSize.Width;

                int y = 0;

                if (listA != null)

                {

                    hdrA.Location = new Point(0, y);

                    hdrA.Size = new Size(w, HDR_H);

                    y += HDR_H;

                    listA.Location = new Point(0, y);

                    listA.Size = new Size(w, appItems.Count * ROW_H);

                    y += listA.Height;

                }

                if (listS != null)

                {

                    hdrS.Location = new Point(0, y);

                    hdrS.Size = new Size(w, HDR_H);

                    y += HDR_H;

                    listS.Location = new Point(0, y);

                    listS.Size = new Size(w, scriptItems.Count * ROW_H);

                }

            }



            Item SelectedItem()

            {

                if (inScripts) return scriptItems.Count > 0 ? scriptItems[selS] : null;

                return appItems.Count > 0 ? appItems[selA] : null;

            }



            void SelectApp(int i)

            {

                syncing = true;

                selA = i;

                inScripts = false;

                if (listA != null) listA.SelectedIndex = i;

                if (listS != null) listS.SelectedIndex = -1;

                syncing = false;

                UpdateStatus();

            }



            void SelectScript(int i)

            {

                syncing = true;

                selS = i;

                inScripts = true;

                if (listS != null) listS.SelectedIndex = i;

                if (listA != null) listA.SelectedIndex = -1;

                syncing = false;

                UpdateStatus();

            }



            void UpdateStatus()

            {

                var it = SelectedItem();

                status.Text = "Selected: " + (it != null ? it.name : "(no enabled items - add one with 'am add')");

            }



            void PositionButtons()

            {

                int w = ClientSize.Width;

                minBtn.Location = new Point(w - 3 * BTN_W, BTNY);

                maxBtn.Location = new Point(w - 2 * BTN_W, BTNY);

                closeBtn.Location = new Point(w - BTN_W, BTNY);

                maxBtn.Text = WindowState == FormWindowState.Maximized ? "❐" : "□";

                // keep the hint text from running into the caption buttons
                title.Width = Math.Max(60, w - 8 - 3 * BTN_W - 4);

            }



            // Title-bar buttons must not take focus either: once the lists are

            // non-selectable WinForms hands focus to the first Button, and a

            // plain Enter press would "click" it (closing the picker) instead

            // of reaching the poller's Enter handling.

            class NonSelectButton : Button

            {

                public NonSelectButton() { SetStyle(ControlStyles.Selectable, false); TabStop = false; }

            }



            static Button TitleBtn(string t)

            {

                var b = new NonSelectButton

                {

                    Text = t,

                    ForeColor = Color.FromArgb(220, 220, 220),

                    BackColor = Color.FromArgb(48, 48, 52),

                    FlatStyle = FlatStyle.Flat,

                    Font = new Font("Segoe UI", 11f, FontStyle.Bold),

                    Size = new Size(BTN_IW, 24),

                };

                b.FlatAppearance.BorderSize = 0;

                return b;

            }



            // ---------- self-drawn move / resize ----------
            void BeginOp(Control src, int ht, Point cursor)
            {
                opHt = ht;
                opCursor = cursor;
                opWin = Location;
                opSize = Size;
                inNativeOp = true;
                Opacity = 1.0;
                opCap = src;
                try { src.Capture = true; } catch { }
            }

            // dragging anywhere on the caption (the bar itself or its label)
            // moves the window
            void HookCaption(Control c)
            {
                c.MouseDown += (s, e) =>
                {
                    if (e.Button != MouseButtons.Left) return;
                    BeginOp(c, 0, Cursor.Position);
                };
                c.MouseMove += (s, e) => { if (inNativeOp) FollowOp(); };
                c.MouseUp += (s, e) => EndOp();
            }

            // 6px grab border on a child control: the docked panels cover the
            // form's own client area, so the edge test runs in screen space
            void HookGrip(Control c)
            {
                c.MouseDown += (s, e) =>
                {
                    if (e.Button != MouseButtons.Left) return;
                    Point sp = c.PointToScreen(e.Location);
                    int ht = HitCode(sp.X - Left, sp.Y - Top);
                    if (ht == 0) return;
                    BeginOp(c, ht, Cursor.Position);
                };
                c.MouseMove += (s, e) =>
                {
                    if (inNativeOp) { FollowOp(); return; }
                    Point sp = c.PointToScreen(e.Location);
                    Cursor = CursorFor(HitCode(sp.X - Left, sp.Y - Top));
                };
                c.MouseUp += (s, e) => EndOp();
            }

            void FollowOp()
            {
                Point cur = Cursor.Position;
                int dx = cur.X - opCursor.X, dy = cur.Y - opCursor.Y;
                if (opHt == 0) { Location = new Point(opWin.X + dx, opWin.Y + dy); return; }
                bool left = opHt == 2 || opHt == 5 || opHt == 8;
                bool right = opHt == 3 || opHt == 6 || opHt == 9;
                bool top = opHt == 4 || opHt == 5 || opHt == 6;
                bool bottom = opHt == 7 || opHt == 8 || opHt == 9;
                int l = opWin.X, t = opWin.Y, w = opSize.Width, h = opSize.Height;
                if (left) { l += dx; w -= dx; }
                if (right) w += dx;
                if (top) { t += dy; h -= dy; }
                if (bottom) h += dy;
                if (w < MinimumSize.Width) { if (left) l = opWin.X + opSize.Width - MinimumSize.Width; w = MinimumSize.Width; }
                if (h < MinimumSize.Height) { if (top) t = opWin.Y + opSize.Height - MinimumSize.Height; h = MinimumSize.Height; }
                Bounds = new Rectangle(l, t, w, h);
            }

            void EndOp()
            {
                if (!inNativeOp) return;
                inNativeOp = false;
                Opacity = OPACITY;
                if (opCap != null) { try { opCap.Capture = false; } catch { } opCap = null; }
            }

            // 0 = no grab zone; else HT code: 2=left 3=right 4=top 5=tl 6=tr 7=bottom 8=bl 9=br
            int HitCode(int x, int y)

            {

                bool l = x <= GRIP, r = x >= Width - GRIP;

                bool t = y <= GRIP, b = y >= Height - GRIP;

                if (t && l) return 5; if (t && r) return 6;

                if (b && l) return 8; if (b && r) return 9;

                if (l) return 2; if (r) return 3;

                if (t) return 4; if (b) return 7;

                return 0;

            }



            static Cursor CursorFor(int ht)

            {

                switch (ht)

                {

                    case 2: case 3: return Cursors.SizeWE;

                    case 4: case 7: return Cursors.SizeNS;

                    case 5: case 9: return Cursors.SizeNWSE;

                    case 6: case 8: return Cursors.SizeNESW;

                    default: return Cursors.Default;

                }

            }



            string RowText(Item it)

            {

                // on-demand items (autostart=false) are registered only: nothing
                // starts them at logon, so label them as waiting-to-be-launched
                // rather than "stopped" (which reads like a failure).
                string state = Core.ProcCount(it.processName) > 0
                    ? "running"
                    : (it.autostart ? "stopped" : "on-demand");

                return PadR(it.name, 24) + " " + state;

            }



            static string PadR(string s, int w)

            {

                s = s ?? "";

                if (s.Length > w) s = s.Substring(0, w - 1) + ".";

                while (s.Length < w) s += " ";

                return s;

            }



            void RefreshStates()

            {

                UpdateListTexts(listA, appItems, appText);

                UpdateListTexts(listS, scriptItems, scriptText);

            }



            void UpdateListTexts(ListBox lb, List<Item> its, List<string> textOut)

            {

                if (lb == null) return;

                for (int i = 0; i < its.Count; i++)

                {

                    string t = RowText(its[i]);

                    if (t == textOut[i]) continue;

                    textOut[i] = t;

                    if (lb.SelectedIndex != i) lb.Items[i] = t;

                }

            }



            // cross-section navigation: at a list edge the direction key jumps

            // into the neighbouring section (down from the last app -> first
            // script; up from the first script -> last app); empty sections

            // are skipped
            void MoveSel(int dir)

            {

                if (dir > 0)

                {

                    if (!inScripts)

                    {

                        if (selA < appItems.Count - 1) { SelectApp(selA + 1); Core.Log("picker key down -> sel " + selA); }
                        else if (scriptItems.Count > 0) { SelectScript(0); Core.Log("picker key down -> section scripts sel 0"); }

                    }

                    else

                    {

                        if (selS < scriptItems.Count - 1) { SelectScript(selS + 1); Core.Log("picker key down -> scripts sel " + selS); }

                    }

                }

                else

                {

                    if (inScripts)

                    {

                        if (selS > 0) { SelectScript(selS - 1); Core.Log("picker key up -> scripts sel " + selS); }
                        else if (appItems.Count > 0) { SelectApp(appItems.Count - 1); Core.Log("picker key up -> section apps sel " + selA); }

                    }

                    else

                    {

                        if (selA > 0) { SelectApp(selA - 1); Core.Log("picker key up -> sel " + selA); }

                    }

                }

            }



            void Poll(int[] watch, short[] prev)

            {

                for (int i = 0; i < watch.Length; i++)

                {

                    short cur = GetAsyncKeyState(watch[i]);

                    bool downNow = (cur & 0x8000) != 0;

                    bool wasDown = (prev[i] & 0x8000) != 0;

                    prev[i] = cur;

                    // our own up->down edge (the OS 0x4000 transition bit is

                    // missed when a key tap lasts less than the poll interval)

                    if (!(downNow && !wasDown)) continue;

                    switch (watch[i])

                    {

                        case 0x26: // Up
                        case 0x25: // Left
                            MoveSel(-1);
                            break;

                        case 0x28: // Down
                        case 0x27: // Right
                            MoveSel(+1);
                            break;

                        case 0x0D: // Enter -> open the selected item
                            if (SelectedItem() != null)

                            {

                                Core.Log("picker key enter -> opening '" + SelectedItem().name + "'");

                                OpenSelected();

                            }

                            break;

                        case 0x1B: // Esc -> close
                            Core.Log("picker key esc -> closing");
                            Close();
                            break;

                    }

                }

            }



            // open the selected item: start it if not running, then restore /
            // foreground its windows (plain apps); scripts are launch-only.
            void OpenSelected()

            {

                var it = SelectedItem();

                if (it == null) { Close(); return; }

                Core.Log("picker open '" + it.name + "'");

                bool started = false;

                if (Core.ProcCount(it.processName) == 0) started = Core.StartApp(it);



                if (!it.script)

                {

                    // restore exactly ONE window: the app's own main window.

                    // Showing every top-level HWND of the process (renderer

                    // hosts, tray/IME helpers, zero-sized message windows) is

                    // what littered the screen with unrelated windows.

                    IntPtr main = Core.FindMainWindow(it.processName, it.windowTitle);

                    int tries = 0;

                    while (main == IntPtr.Zero && tries < 20)

                    {

                        Thread.Sleep(250);

                        main = Core.FindMainWindow(it.processName, it.windowTitle);

                        tries++;

                    }

                    if (main != IntPtr.Zero)

                    {

                        P.ShowWindow(main, Core.SW_RESTORE);

                        if (!P.IsWindowVisible(main)) P.ShowWindow(main, Core.SW_SHOW);

                        // bring the target app's window to the front. The
                        // picker holds the foreground for its whole life, so
                        // SetForegroundWindow needs the attach-to-foreground
                        // thread dance (the picker's Shown handler uses it too).
                        try

                        {

                            IntPtr fg = GetForegroundWindow();

                            uint fgPid;

                            uint fgTid = GetWindowThreadProcessId(fg, out fgPid);

                            uint myTid = GetCurrentThreadId();

                            bool attached = false;

                            if (fgTid != 0 && fgTid != myTid)

                                attached = AttachThreadInput(myTid, fgTid, true);

                            P.SetForegroundWindow(main);

                            if (attached) AttachThreadInput(myTid, fgTid, false);

                        }

                        catch { }

                    }

                    else if (started)

                        Core.Log("picker: no window found for '" + it.name + "' after start (background app?)");

                }

                Core.Log("picker: '" + it.name + "' " + (started ? "started" : "already running") +

                    (it.script ? " (script, launch-only)" : " window(s) shown"));

                Close();

            }



            protected override void OnFormClosed(FormClosedEventArgs e)

            {

                try { if (poller != null) poller.Stop(); } catch { }

                try { if (stateTimer != null) stateTimer.Stop(); } catch { }

                try { if (grace != null) grace.Stop(); } catch { }

                base.OnFormClosed(e);

                // destroy the window handle even when the close came from a
                // foreign thread (hotkey toggle closes us from the engine's
                // watcher thread): without this the HWND stays alive and the
                // UI thread's message loop keeps pumping, so observers see a
                // "closed" picker window that is actually still open.
                // DestroyHandle() from the form itself is what the handle
                // lifetime probe (tests/handle_probe.cs) verified to release
                // the HWND; ExitThread posted from a foreign thread did NOT.
                try { DestroyHandle(); } catch { }

            }

        }

    }

}
