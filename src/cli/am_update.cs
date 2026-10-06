using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AppManager.Shared;

// `am update` — interactive config editor, two levels:
//
//   level 1  item list    one row per managed app/script (name / kind / logon / enabled /
//                         monitor / target).  ↑↓ pick a row, Enter opens it.
//   level 2  field form   the fields of that single item, one per row
//                         (名称 / 应用地址 / 窗口标题 / 监控时长 / 登录自启 / 启用 …).
//                         ↑↓ pick a field, Enter edits it — a line editor for text
//                         fields, an option list for enum fields.
//
// Esc always goes one level up (form -> list -> quit; option list / line editor ->
// their caller), so nothing traps the user. Accepted edits are saved immediately.
//
// Split for testability: every model operation (items / fields / values / validation /
// ApplyKey / the line-editor feed) is console-free; only Run() and the Draw* helpers
// touch the terminal.
namespace AppManager.Cli
{
    static class UpdateUi
    {
        // ---------- field keys ----------
        const string F_NAME = "NAME";
        const string F_KIND = "KIND";
        const string F_TARGET = "TARGET";
        const string F_TITLE = "TITLE";
        const string F_MONITOR = "MONITOR";
        const string F_LAUNCHER = "LAUNCHER";
        const string F_HOST = "HOST";
        const string F_ARGS = "ARGS";
        const string F_ATLOGON = "ATLOGON";
        const string F_ON = "ON";
        const string F_PROC = "PROC";

        const string V_SILENT = "静默启动";
        const string V_DEMAND = "按需";
        const string V_YES = "是";
        const string V_NO = "否";
        const string V_ALL = "(全部窗口)";

        // ---------- fields ----------
        public class Field
        {
            public string Key;
            public string Label;
            public bool ReadOnly;
            public Field(string key, string label, bool ro) { Key = key; Label = label; ReadOnly = ro; }
        }

        public static List<Field> Fields(Item it)
        {
            var l = new List<Field>();
            l.Add(new Field(F_NAME, "名称", false));
            l.Add(new Field(F_KIND, "类型", true));
            l.Add(new Field(F_TARGET, it.script ? "脚本地址" : "应用地址", false));
            if (it.script)
            {
                l.Add(new Field(F_LAUNCHER, "启动器", false));
                l.Add(new Field(F_HOST, "宿主", true));
                l.Add(new Field(F_ARGS, "启动参数", true));
            }
            else
            {
                l.Add(new Field(F_TITLE, "窗口标题", false));
                l.Add(new Field(F_MONITOR, "监控时长", false));
            }
            l.Add(new Field(F_ATLOGON, "登录自启", false));
            l.Add(new Field(F_ON, "启用", false));
            l.Add(new Field(F_PROC, "进程名", true));
            return l;
        }

        // ---------- level 1: the items ----------
        public static List<Item> BuildItems(Config cfg, string only)
        {
            var items = new List<Item>();
            foreach (var it in cfg.items)
            {
                if (it == null) continue;
                if (!string.IsNullOrEmpty(only) && !string.Equals(it.name, only, StringComparison.OrdinalIgnoreCase)) continue;
                items.Add(it);
            }
            return items;
        }

        public static string KindText(Item it)
        {
            return it.script ? "脚本" : "应用";
        }

        // ---------- field values ----------
        static string Silent(int ms)
        {
            int t = ms > 0 ? ms : 30000;
            return (t % 1000 == 0) ? (t / 1000) + "s" : t + "ms";
        }

        public static string FieldText(Item it, string key)
        {
            if (key == F_NAME) return it.name;
            if (key == F_KIND) return it.script ? "脚本（登录时只启动、不藏窗）" : "应用（登录时启动 + 藏窗）";
            if (key == F_TARGET) return it.exe;
            if (key == F_TITLE) return string.IsNullOrEmpty(it.windowTitle) ? V_ALL : it.windowTitle;
            if (key == F_MONITOR) return Silent(it.silentWindowMs);
            if (key == F_LAUNCHER) return string.IsNullOrEmpty(it.launcher) ? "auto" : it.launcher;
            if (key == F_HOST) return string.IsNullOrEmpty(it.hostExe) ? "(内置/自动)" : it.hostExe;
            if (key == F_ARGS) return string.IsNullOrEmpty(it.hostArgs) ? "(无)" : it.hostArgs;
            if (key == F_ATLOGON) return it.autostart ? V_SILENT : V_DEMAND;
            if (key == F_ON) return it.enabled ? V_YES : V_NO;
            if (key == F_PROC) return it.processName;
            return "";
        }

        // null => text field; otherwise the options this field offers
        public static List<string> Choices(Config cfg, Item it, string key)
        {
            var l = new List<string>();
            if (key == F_ATLOGON) { l.Add(V_SILENT); l.Add(V_DEMAND); return l; }
            if (key == F_ON) { l.Add(V_YES); l.Add(V_NO); return l; }
            if (key == F_LAUNCHER) return Launchers.Ids(cfg.extLaunchers);
            return null;
        }

        // the value the option list pre-selects (handles the "auto" label)
        public static string CurrentChoice(Config cfg, Item it, string key)
        {
            if (key != F_LAUNCHER) return FieldText(it, key);
            if (!string.IsNullOrEmpty(it.launcher)) return it.launcher;
            var r = Launchers.ResolveFor(it.exe, null, null, cfg.extLaunchers);
            return r != null && !string.IsNullOrEmpty(r.Id) ? r.Id : "";
        }

        // ---------- edits: null = applied, else the message to show ----------
        public static string ApplyText(Config cfg, Item it, string key, string input)
        {
            input = input == null ? "" : input.Trim();
            if (key == F_NAME)
            {
                if (input.Length == 0) return "名称不能为空（Esc 取消编辑）";
                foreach (var o in cfg.items)
                    if (!object.ReferenceEquals(o, it) && string.Equals(o.name, input, StringComparison.OrdinalIgnoreCase))
                        return "已有同名条目：" + input;
                it.name = input;
                return null;
            }
            if (key == F_TITLE) { it.windowTitle = input; return null; }
            if (key == F_MONITOR)
            {
                if (it.script) return "脚本条目没有监控时长（只启动、不藏窗）";
                int ms;
                if (!TryParseMs(input, out ms)) return "时长格式不对，示例：30s / 30 / 5000ms / 0";
                it.silentWindowMs = ms;
                return null;
            }
            if (key == F_TARGET) return ApplyTarget(cfg, it, input);
            return "该字段不可编辑";
        }

        static string ApplyTarget(Config cfg, Item it, string path)
        {
            path = path.Trim().Trim('"');
            if (path.Length == 0) return "地址不能为空（Esc 取消编辑）";
            bool isScript = Program.IsScriptExt(path);
            if (it.script && !isScript) return "脚本条目不能指向 .exe（要管 .exe 请用 am add 新建）";
            if (!it.script && isScript) return ".exe 条目不能指向脚本（要管脚本请用 am add 新建）";
            if (!File.Exists(path)) return "目标不存在：" + path;

            if (it.script)
            {
                var r = Launchers.ResolveFor(path, it.launcher, null, cfg.extLaunchers);
                if (r == null) return "这个后缀没有可用的启动器，先 am launchers learn";
                it.exe = path;
                it.hostExe = r.Host;
                it.hostArgs = r.ArgsTemplate;
                it.processName = r.Proc;
                if (!string.IsNullOrEmpty(r.Id)) it.launcher = r.Id;
                return null;
            }
            it.exe = path;
            it.processName = Path.GetFileNameWithoutExtension(path);
            return null;
        }

        public static string ApplyChoice(Config cfg, Item it, string key, string value)
        {
            if (key == F_ATLOGON) { it.autostart = (value == V_SILENT); return null; }
            if (key == F_ON) { it.enabled = (value == V_YES); return null; }
            if (key == F_LAUNCHER)
            {
                var r = Launchers.ResolveFor(it.exe, value, null, cfg.extLaunchers);
                if (r == null) return "无法解析启动器：" + value;
                it.launcher = value;
                it.hostExe = r.Host;
                it.hostArgs = r.ArgsTemplate;
                it.processName = r.Proc;
                return null;
            }
            return "该字段不可编辑";
        }

        static bool TryParseMs(string s, out int ms)
        {
            ms = 0;
            s = (s == null ? "" : s).Trim().ToLowerInvariant();
            if (s.Length == 0) return false;
            if (s.EndsWith("ms"))
            {
                int v;
                if (!int.TryParse(s.Substring(0, s.Length - 2), out v) || v < 0) return false;
                ms = v;
                return true;
            }
            if (s.EndsWith("s"))
            {
                int v;
                if (!int.TryParse(s.Substring(0, s.Length - 1), out v) || v < 0) return false;
                ms = v * 1000;
                return true;
            }
            int n;
            if (!int.TryParse(s, out n) || n < 0) return false;
            ms = (n < 1000) ? n * 1000 : n;
            return true;
        }

        // ---------- line editor (pure) ----------
        public class EditState
        {
            public string Buffer = "";
            public int Caret;          // char index into Buffer
            public bool Done;          // Enter
            public bool Cancelled;     // Esc
        }

        // feed one key; returns true when the edit is over
        public static bool Feed(EditState st, ConsoleKeyInfo k)
        {
            if (k.Key == ConsoleKey.Escape) { st.Cancelled = true; return true; }
            if (k.Key == ConsoleKey.Enter) { st.Done = true; return true; }
            if (st.Caret > st.Buffer.Length) st.Caret = st.Buffer.Length;
            if (st.Caret < 0) st.Caret = 0;

            if (k.Key == ConsoleKey.Backspace)
            {
                if (st.Caret > 0) { st.Buffer = st.Buffer.Remove(st.Caret - 1, 1); st.Caret--; }
                return false;
            }
            if (k.Key == ConsoleKey.Delete)
            {
                if (st.Caret < st.Buffer.Length) st.Buffer = st.Buffer.Remove(st.Caret, 1);
                return false;
            }
            if (k.Key == ConsoleKey.LeftArrow) { if (st.Caret > 0) st.Caret--; return false; }
            if (k.Key == ConsoleKey.RightArrow) { if (st.Caret < st.Buffer.Length) st.Caret++; return false; }
            if (k.Key == ConsoleKey.Home) { st.Caret = 0; return false; }
            if (k.Key == ConsoleKey.End) { st.Caret = st.Buffer.Length; return false; }
            if (k.KeyChar != '\0' && !char.IsControl(k.KeyChar))
            {
                st.Buffer = st.Buffer.Insert(st.Caret, k.KeyChar.ToString());
                st.Caret++;
            }
            return false;
        }

        // the slice of the buffer that fits into `width` columns (caret kept visible)
        public static string Visible(string buf, int caret, int width, out int caretCol)
        {
            if (width < 6) width = 6;
            if (caret > buf.Length) caret = buf.Length;
            string prefix = "";
            int start = 0;
            while (start < caret && Style.DispWidth(buf.Substring(start, caret - start)) + prefix.Length > width - 1)
            {
                start++;
                prefix = "..";
            }
            string tail = buf.Substring(start);
            while (tail.Length > 0 && Style.DispWidth(prefix + tail) > width) tail = tail.Substring(0, tail.Length - 1);
            caretCol = prefix.Length + Style.DispWidth(buf.Substring(start, caret - start));
            return prefix + tail;
        }

        // ---------- frame (no console I/O) ----------
        public struct LineStyle
        {
            public ConsoleColor? Fg;
            public ConsoleColor? Bg;
            public bool Fill;
        }

        public class Frame
        {
            public List<string> Lines = new List<string>();
            public List<LineStyle> Styles = new List<LineStyle>();
            public int Width;
            public int HiLine = -1;
            public int HiCol = -1;
            public int HiLen = 0;

            public void Add(string text, ConsoleColor? fg)
            {
                Add(text, fg, null, false);
            }

            public void Add(string text, ConsoleColor? fg, ConsoleColor? bg, bool fill)
            {
                var s = new LineStyle();
                s.Fg = fg;
                s.Bg = bg;
                s.Fill = fill;
                Lines.Add(text);
                Styles.Add(s);
            }
        }

        static void Title(Frame f, string subtitle)
        {
            f.Add(" am update · " + subtitle + " ", ConsoleColor.White, ConsoleColor.DarkBlue, true);
            f.Add(" config: " + Core.CfgPath, ConsoleColor.DarkGray);
            f.Add("", null);
        }

        // level 1: one row per item
        public static Frame BuildListFrame(Config cfg, List<Item> items, int sel, string status)
        {
            var f = new Frame();
            var headers = new[] { "名称", "类型", "登录自启", "启用", "监控时长", "目标" };
            var rows = new List<string[]>();
            foreach (var it in items)
                rows.Add(new string[] { it.name, KindText(it), FieldText(it, F_ATLOGON), FieldText(it, F_ON),
                    it.script ? "-" : FieldText(it, F_MONITOR), it.exe });

            var w = new int[headers.Length];
            for (int c = 0; c < headers.Length; c++) w[c] = Style.DispWidth(headers[c]);
            foreach (var r in rows)
                for (int c = 0; c < headers.Length; c++)
                    if (Style.DispWidth(r[c]) > w[c]) w[c] = Style.DispWidth(r[c]);
            int capTarget = 40;
            if (w[5] > capTarget) w[5] = capTarget;
            int total = (headers.Length - 1) * 3;
            for (int c = 0; c < headers.Length; c++) total += w[c];
            f.Width = Math.Max(total, 46);

            Title(f, "选择要修改的条目");
            var head = new StringBuilder();
            for (int c = 0; c < headers.Length; c++)
            {
                if (c > 0) head.Append(" | ");
                head.Append(Style.Fit(headers[c], w[c], false));
            }
            f.Add(head.ToString(), ConsoleColor.Cyan);
            f.Add(new string('-', Math.Min(f.Width, 100)), ConsoleColor.DarkGray);

            for (int r = 0; r < rows.Count; r++)
            {
                var sb = new StringBuilder();
                for (int c = 0; c < headers.Length; c++)
                {
                    if (c > 0) sb.Append(" | ");
                    sb.Append(Style.Fit(rows[r][c], w[c], false));
                }
                string txt = sb.ToString();
                if (r == sel)
                {
                    f.HiLine = f.Lines.Count;
                    f.HiCol = 0;
                    f.HiLen = txt.Length;
                }
                f.Add(txt, r == sel ? ConsoleColor.White : (ConsoleColor?)null);
            }

            f.Add("", null);
            f.Add(SummaryLine(items), ConsoleColor.DarkGray);
            f.Add("↑↓ 选条目    Enter 编辑    q/Esc 退出", ConsoleColor.DarkGray);
            if (status.Length > 0) f.Add(status, StatusColor(status));
            return f;
        }

        // level 2: the fields of one item
        public static Frame BuildFormFrame(Config cfg, Item it, int sel, string status, bool canGoBack)
        {
            var f = new Frame();
            var fields = Fields(it);
            int lw = 0;
            foreach (var fld in fields) if (Style.DispWidth(fld.Label) > lw) lw = Style.DispWidth(fld.Label);
            int vw = 20;
            foreach (var fld in fields)
            {
                int x = Style.DispWidth(FieldText(it, fld.Key));
                if (x > vw) vw = x;
            }
            // a form row owns the whole line, so long paths may stay intact (Draw clips to the terminal)
            if (vw > 100) vw = 100;
            f.Width = Math.Max(lw + vw + 3, 52);

            Title(f, it.name + " · " + KindText(it));
            f.Add(Style.Fit("字段", lw, false) + " | 值", ConsoleColor.Cyan);
            f.Add(new string('-', Math.Min(f.Width, 100)), ConsoleColor.DarkGray);

            for (int i = 0; i < fields.Count; i++)
            {
                var fld = fields[i];
                string label = Style.Fit(fld.Label, lw, false);
                string val = Style.Fit(FieldText(it, fld.Key), vw, false);
                string txt = label + " | " + val;
                if (i == sel)
                {
                    // offsets are character based (labels are CJK: 4 chars but 8 columns)
                    f.HiLine = f.Lines.Count;
                    f.HiCol = label.Length + 3;
                    f.HiLen = val.Length;
                }
                f.Add(txt, fld.ReadOnly ? ConsoleColor.DarkGray : (ConsoleColor?)null);
            }

            f.Add("", null);
            f.Add("↑↓ 选字段    Enter 编辑" + (canGoBack ? "    Esc 返回条目列表" : "") + "    q 退出", ConsoleColor.DarkGray);
            if (status.Length > 0) f.Add(status, StatusColor(status));
            return f;
        }

        static ConsoleColor? StatusColor(string s)
        {
            if (s.StartsWith("已保存")) return ConsoleColor.Green;
            if (s.StartsWith("取消")) return ConsoleColor.DarkGray;
            return ConsoleColor.Yellow;
        }

        public static string SummaryLine(List<Item> items)
        {
            int apps = 0, scripts = 0;
            foreach (var it in items) { if (it.script) scripts++; else apps++; }
            int pid = Core.EnginePid();
            return "共 " + items.Count + " 项（应用 " + apps + " · 脚本 " + scripts + "）    engine: "
                + (pid > 0 ? "running (pid " + pid + ")" : "not running");
        }

        // ---------- cursor / key handling (pure) ----------
        public const int LevelList = 0;
        public const int LevelForm = 1;

        public class Cursor
        {
            public int Level = LevelList;
            public int Item;           // index into items
            public int Field;          // index into Fields(items[Item])
            public int OptionSel;
            public bool OptionOpen;
            public string Status = "";
        }

        public static bool IsChoiceField(Config cfg, List<Item> items, Cursor c)
        {
            var it = items[c.Item];
            var fields = Fields(it);
            if (c.Field < 0 || c.Field >= fields.Count) return false;
            return Choices(cfg, it, fields[c.Field].Key) != null;
        }

        // arrow navigation + the option list. Enter on a *text* field is the caller's
        // job (it needs terminal input); everything else lands here.
        public static void ApplyKey(Config cfg, List<Item> items, Cursor c, ConsoleKey key)
        {
            if (items.Count == 0) return;
            if (c.Item >= items.Count) c.Item = items.Count - 1;
            if (c.Item < 0) c.Item = 0;
            var it = items[c.Item];
            var fields = Fields(it);

            if (c.OptionOpen)
            {
                var opts = Choices(cfg, it, fields[c.Field].Key);
                if (opts == null || opts.Count == 0) { c.OptionOpen = false; return; }
                if (key == ConsoleKey.Escape) { c.OptionOpen = false; c.Status = "取消修改（原值不变）"; return; }
                if (key == ConsoleKey.LeftArrow || key == ConsoleKey.UpArrow) { c.OptionSel = (c.OptionSel + opts.Count - 1) % opts.Count; return; }
                if (key == ConsoleKey.RightArrow || key == ConsoleKey.DownArrow) { c.OptionSel = (c.OptionSel + 1) % opts.Count; return; }
                if (key == ConsoleKey.Enter)
                {
                    string v = opts[c.OptionSel];
                    string cur = CurrentChoice(cfg, it, fields[c.Field].Key);
                    c.OptionOpen = false;
                    if (v == cur) { c.Status = ""; return; }
                    string err = ApplyChoice(cfg, it, fields[c.Field].Key, v);
                    if (err != null) { c.Status = err; return; }
                    Save(cfg, it, fields[c.Field]);
                    c.Status = SavedMessage(it, fields[c.Field]);
                }
                return;
            }

            if (c.Level == LevelList)
            {
                if (key == ConsoleKey.UpArrow) { c.Item = (c.Item + items.Count - 1) % items.Count; c.Status = ""; }
                else if (key == ConsoleKey.DownArrow) { c.Item = (c.Item + 1) % items.Count; c.Status = ""; }
                else if (key == ConsoleKey.Enter) { c.Level = LevelForm; c.Field = 0; c.Status = ""; }
                return;
            }

            // form level
            if (key == ConsoleKey.UpArrow) { c.Field = (c.Field + fields.Count - 1) % fields.Count; c.Status = ""; }
            else if (key == ConsoleKey.DownArrow) { c.Field = (c.Field + 1) % fields.Count; c.Status = ""; }
            else if (key == ConsoleKey.Enter)
            {
                if (fields[c.Field].ReadOnly) { c.Status = "“" + fields[c.Field].Label + "”只读（由启动器/路径推导）"; return; }
                var opts = Choices(cfg, it, fields[c.Field].Key);
                if (opts != null)
                {
                    string cur = CurrentChoice(cfg, it, fields[c.Field].Key);
                    int sel = opts.IndexOf(cur);
                    c.OptionSel = sel < 0 ? 0 : sel;
                    c.OptionOpen = true;
                    c.Status = "";
                }
            }
        }

        // Esc: step exactly one level up. Returns true when there is nothing above
        // (the caller quits). Used by Run so the behaviour is testable.
        public static bool Back(Cursor c, bool hasList)
        {
            if (c.Level == LevelForm && hasList)
            {
                c.Level = LevelList;
                c.Status = "取消修改（原值不变）";
                return false;
            }
            return true;
        }

        // persist one accepted change (used by ApplyKey; Run uses it too)
        public static void Save(Config cfg, Item it, Field fld)
        {
            Core.Save(cfg);
            Core.Log("cli update '" + it.name + "' " + fld.Key + " -> " + FieldText(it, fld.Key));
        }

        public static string SavedMessage(Item it, Field fld)
        {
            return "已保存：" + fld.Label + " = " + FieldText(it, fld.Key) + "（下次登录或 am stop && am run 生效）";
        }

        // ---------- console half ----------
        const string NL = "\r\n";

        static int ConsoleWidth()
        {
            try { return Math.Max(40, Console.WindowWidth - 1); }
            catch { return 120; }
        }

        static int ConsoleHeight()
        {
            try { return Math.Max(8, Console.WindowHeight - 1); }
            catch { return 40; }
        }

        // character span of the highlighted cell inside a padded frame
        public static void HighlightSpan(List<string> lines, int hiLine, int hiCol, int hiLen, out int start, out int len)
        {
            start = -1;
            len = 0;
            if (hiLine < 0 || hiLine >= lines.Count || hiCol < 0) return;
            int off = 0;
            for (int i = 0; i < hiLine; i++) off += lines[i].Length + NL.Length;
            start = off + Math.Min(hiCol, lines[hiLine].Length);
            len = Math.Max(0, Math.Min(hiLen, lines[hiLine].Length - (start - off)));
        }

        // what the previous frame occupied, so a narrower frame can clear its tail
        class Screen
        {
            public int Lines;
            public int Width;
        }

        // How wide a frame must be rendered. It may never shrink below the previous
        // frame's width, otherwise the previous (wider) frame's tail stays on screen
        // — that is what smeared the item list's target column over the field form.
        public static int RenderWidth(Frame f, int prevWidth, int consoleWidth)
        {
            int w = Math.Max(f.Width, 46);
            if (consoleWidth > 0 && w > consoleWidth) w = consoleWidth;
            int floor = consoleWidth > 0 ? Math.Min(prevWidth, consoleWidth) : prevWidth;
            return Math.Max(w, floor);
        }

        static void Draw(Frame f, Screen sc)
        {
            int cw = ConsoleWidth();
            int pad = RenderWidth(f, sc.Width, cw);

            var lines = new List<string>();
            foreach (var l in f.Lines) lines.Add(Style.Fit(l, pad, false));

            int hiLine = f.HiLine;
            int viewStart = 0;
            int avail = ConsoleHeight();
            if (lines.Count > avail)
            {
                viewStart = hiLine >= 0 ? Math.Max(0, hiLine - avail + 4) : 0;
                if (viewStart + avail > lines.Count) viewStart = lines.Count - avail;
                if (viewStart < 0) viewStart = 0;
                lines = lines.GetRange(viewStart, Math.Min(avail, lines.Count - viewStart));
                if (hiLine >= 0) hiLine -= viewStart;
            }

            int hiStart, hiLen;
            HighlightSpan(lines, hiLine, f.HiCol, f.HiLen, out hiStart, out hiLen);

            try
            {
                Console.CursorVisible = false;
                Console.SetCursorPosition(0, 0);
            }
            catch { }

            int offset = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                int src = viewStart + i;
                LineStyle st = (src < f.Styles.Count) ? f.Styles[src] : new LineStyle();
                Style.Set(st.Fg, st.Bg);

                int hs = -1, hl = 0;
                if (i == hiLine && hiStart >= 0)
                {
                    hs = hiStart - offset;
                    hl = Math.Min(hiLen, Math.Max(0, lines[i].Length - hs));
                }
                if (hs >= 0 && hs <= lines[i].Length)
                {
                    Console.Write(lines[i].Substring(0, hs));
                    Style.Set(ConsoleColor.Black, ConsoleColor.Gray);
                    Console.Write(lines[i].Substring(hs, hl));
                    Style.Set(st.Fg, st.Bg);
                    Console.Write(lines[i].Substring(hs + hl));
                }
                else Console.Write(lines[i]);

                if (i < lines.Count - 1) Console.Write(NL);
                offset += lines[i].Length + NL.Length;
                Style.Reset();
            }
            for (int i = lines.Count; i < sc.Lines; i++) Console.Write(NL + new string(' ', pad));
            Style.Reset();

            sc.Lines = lines.Count;
            sc.Width = pad;
        }

        static void ClearRegion(int lines)
        {
            try
            {
                Console.SetCursorPosition(0, 0);
                var sb = new StringBuilder();
                for (int i = 0; i < lines; i++) sb.Append(new string(' ', ConsoleWidth())).Append(NL);
                Console.Write(sb.ToString());
                Console.SetCursorPosition(0, 0);
            }
            catch { }
        }

        // the line editor, drawn under the current frame; null = cancelled (Esc)
        static string EditTextInline(Item it, Field fld, Frame frame, Screen sc)
        {
            var st = new EditState();
            st.Buffer = FieldText(it, fld.Key);
            st.Caret = st.Buffer.Length;
            int width = Math.Max(frame.Width, 46);
            int cw = ConsoleWidth();
            if (width > cw) width = cw;

            try { Console.CursorVisible = true; } catch { }
            try
            {
                while (true)
                {
                    var f = new Frame();
                    f.Width = frame.Width;
                    f.Lines.AddRange(frame.Lines);
                    f.Styles.AddRange(frame.Styles);
                    f.Add("", null);
                    int caretCol;
                    int budget = Math.Max(10, width - Style.DispWidth(fld.Label) - 6);
                    string vis = Visible(st.Buffer, st.Caret, budget, out caretCol);
                    f.Add(fld.Label + "  " + vis, ConsoleColor.White);
                    f.Add("Enter 保存    Esc 取消    ←→ 移动光标    Backspace 删除", ConsoleColor.DarkGray);
                    Draw(f, sc);
                    try
                    {
                        Console.CursorVisible = true;
                        Console.SetCursorPosition(Math.Min(width - 1, Style.DispWidth(fld.Label) + 2 + caretCol), f.Lines.Count - 2);
                    }
                    catch { }
                    ConsoleKeyInfo k = Console.ReadKey(true);
                    if (Feed(st, k)) break;
                }
            }
            finally { try { Console.CursorVisible = false; } catch { } }

            return st.Cancelled ? null : st.Buffer;
        }

        // ---------- entry point ----------
        public static void Run(Config cfg, string only)
        {
            var items = BuildItems(cfg, only);
            if (items.Count == 0)
            {
                if (only == null) Style.Warn("(no items configured — add one with: am add <exePath>)");
                else Style.Err("no item named '" + only + "'");
                return;
            }

            bool interactive;
            try { interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected; }
            catch { interactive = false; }

            if (!interactive)
            {
                var dump = BuildListFrame(cfg, items, -1, "");
                foreach (var l in dump.Lines) Console.WriteLine(l);
                Console.WriteLine();
                foreach (var it in items)
                {
                    Style.Info("  " + it.name + "  (" + KindText(it) + ")");
                    foreach (var fld in Fields(it))
                        Console.WriteLine("    " + Style.Fit(fld.Label, 10, false) + " " + FieldText(it, fld.Key));
                    Console.WriteLine();
                }
                Style.Dim("(piped output: the editable UI needs a real console — run `am update` there)");
                return;
            }

            var cur = new Cursor();
            cur.Level = (only != null) ? LevelForm : LevelList;   // a single item opens straight into its form
            var sc = new Screen();
            try
            {
                Console.CursorVisible = false;
                while (true)
                {
                    var it = items[cur.Item];
                    var fields = Fields(it);
                    Frame f;
                    if (cur.Level == LevelList)
                    {
                        f = BuildListFrame(cfg, items, cur.Item, cur.Status);
                    }
                    else
                    {
                        f = BuildFormFrame(cfg, it, cur.Field, cur.Status, only == null);
                        if (cur.OptionOpen)
                        {
                            var opts = Choices(cfg, it, fields[cur.Field].Key);
                            string curVal = CurrentChoice(cfg, it, fields[cur.Field].Key);
                            f.Add(fields[cur.Field].Label + "： ←→ 或 ↑↓ 选择，Enter 应用，Esc 取消", ConsoleColor.Yellow);
                            for (int i = 0; i < opts.Count; i++)
                            {
                                bool on = (i == cur.OptionSel);
                                string tail = (opts[i] == curVal) ? "   (当前)" : "";
                                f.Add((on ? "  > " : "    ") + opts[i] + tail, on ? ConsoleColor.White : ConsoleColor.DarkGray);
                            }
                        }
                    }
                    Draw(f, sc);

                    ConsoleKeyInfo k = Console.ReadKey(true);

                    // Esc always steps one level up; q always leaves
                    if (!cur.OptionOpen && k.Key == ConsoleKey.Q) break;
                    if (!cur.OptionOpen && k.Key == ConsoleKey.Escape)
                    {
                        if (Back(cur, only == null)) break;
                        continue;
                    }

                    // Enter on a text field: the caller owns the terminal editor
                    if (cur.Level == LevelForm && !cur.OptionOpen && k.Key == ConsoleKey.Enter && !IsChoiceField(cfg, items, cur))
                    {
                        var fld = fields[cur.Field];
                        if (fld.ReadOnly) { cur.Status = "“" + fld.Label + "”只读（由启动器/路径推导）"; continue; }
                        string typed = EditTextInline(it, fld, f, sc);
                        if (typed == null) { cur.Status = "取消修改（原值不变）"; continue; }
                        string err = ApplyText(cfg, it, fld.Key, typed);
                        if (err != null) { cur.Status = err; continue; }
                        Save(cfg, it, fld);
                        cur.Status = SavedMessage(it, fld);
                        continue;
                    }

                    ApplyKey(cfg, items, cur, k.Key);
                }
            }
            finally
            {
                ClearRegion(sc.Lines + 1);
                try { Console.CursorVisible = true; } catch { }
            }
            Style.Dim("am update: 退出（已保存的改动即时生效：下次登录，或 am stop && am run）");
        }
    }
}
