using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using Microsoft.Win32;

// V7 shared core: config, P/Invoke, window ops, silent pass.
// Linked into both am.exe (CLI) and am-engine.exe (resident).
// Self-contained: config/log/pid live next to the running exe (project dir).
namespace AppManager.Shared
{
    [DataContract]
    public class Item
    {
        [DataMember] public string name = "";
        [DataMember] public string exe = "";             // target to launch: a .exe OR a script
        [DataMember] public string processName = "";     // process to track = host when a launcher is used
        [DataMember] public string windowTitle = "";
        [DataMember] public int silentWindowMs = 0;      // monitor window after logon; 0 = default 30000ms
        [DataMember] public string launcher = "";        // launcher id: built-in or custom
        [DataMember] public string hostExe = "";         // custom host exe path (empty for built-in)
        [DataMember] public string hostArgs = "";         // custom args template, {script} placeholder
        [DataMember] public bool script = false;         // true => launch-only at logon (no window hiding)
        [DataMember] public bool enabled = true;
        // false => "on-demand": registered only. The logon pass neither starts
        // it nor hides its windows; start it explicitly from the hotkey picker
        // (Enter) or `am start <name>`. It still shows in `am list` + picker.
        [DataMember] public bool autostart = true;

        // DataContractJsonSerializer builds items WITHOUT running field
        // initializers, so a config.json written before "autostart" existed
        // would read as false and silently turn every item into on-demand.
        // Prime the default before members are read; an explicit value in the
        // file still overwrites it.
        [OnDeserializing]
        void PrimeAutostart(StreamingContext ctx) { autostart = true; }
    }

    [DataContract]
    public class ExtLauncher
    {
        [DataMember] public string ext = "";        // ".lua" (with dot, lowercase)
        [DataMember] public string id = "";         // launcher name
        [DataMember] public string host = "";        // host exe absolute path or name
        [DataMember] public string args = "";       // args template, {script} = target path
        [DataMember] public string proc = "";       // process name to track
    }

    [DataContract]
    public class Config
    {
        [DataMember] public List<Item> items = new List<Item>();
        [DataMember] public List<ExtLauncher> extLaunchers = new List<ExtLauncher>();
        [DataMember] public string hotkey = "";   // global AppManager hotkey (e.g. "Ctrl+0"); empty = off
    }

    // ---------- global hotkey: parse / format ----------
    // A hotkey is a modifier set (Ctrl/Alt/Shift/Win) + one non-modifier key
    // (letter, digit, or F1..F24). Canonical label: "Ctrl+Alt+Shift+Win+X".
    public static class Hotkey
    {
        public const int MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;
        public const int MOD_NOREPEAT = 0x4000;

        // Virtual-key codes of the modifier keys. They are NOT the MOD_* flags
        // above: VK_CONTROL is 0x11 while MOD_CONTROL is 0x2, VK_MENU (Alt) is
        // 0x12 while MOD_ALT is 0x1. Confusing the two silently swaps Ctrl and
        // Alt in every captured combo.
        public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
        public const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;

        public class Combo
        {
            public bool Valid;
            public int Mods;      // MOD_* bitmask
            public int Vk;        // VK code of the key (0 when invalid)
            public string Label;  // canonical, e.g. "Ctrl+0"
        }

        // parse "Ctrl+0" / "alt+f1" / "a" (case-insensitive, tolerant of spaces)
        public static Combo Parse(string s)
        {
            var c = new Combo { Label = "" };
            if (string.IsNullOrWhiteSpace(s)) return c;
            var toks = s.Trim().ToUpperInvariant().Split('+');
            int mods = 0, vk = 0, keyToks = 0;
            foreach (var t0 in toks)
            {
                string t = t0.Trim();
                int m = ModWord(t);
                if (m != 0) { if ((mods & m) != 0) return c; mods |= m; continue; }
                int k = KeyToken(t);
                if (k != 0) { if (++keyToks > 1) return c; vk = k; continue; }
                return c; // unknown token
            }
            if (vk == 0) return c; // modifiers only (or nothing) is not a hotkey
            c.Valid = true; c.Mods = mods; c.Vk = vk;
            c.Label = FormatCombo(mods, vk);
            return c;
        }

        static int ModWord(string t)
        {
            switch (t)
            {
                case "CTRL": case "CONTROL": return MOD_CONTROL;
                case "ALT": case "ALTERNATE": return MOD_ALT;
                case "SHIFT": return MOD_SHIFT;
                case "WIN": case "WINDOWS": case "META": return MOD_WIN;
                default: return 0;
            }
        }

        // single key token -> VK code (0 when not a key). ASCII letters/digits double as their VK codes.
        static int KeyToken(string t)
        {
            if (t.Length == 1)
            {
                char ch = t[0];
                if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')) return ch;
                return 0;
            }
            if (t.Length >= 2 && t[0] == 'F')
            {
                int n;
                if (int.TryParse(t.Substring(1), out n) && n >= 1 && n <= 24) return 0x70 + n - 1;
            }
            return 0;
        }

        // canonical label from a MOD_* mask + VK
        public static string FormatCombo(int mods, int vk)
        {
            var parts = new List<string>();
            if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((mods & MOD_ALT) != 0) parts.Add("Alt");
            if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
            if ((mods & MOD_WIN) != 0) parts.Add("Win");
            parts.Add(KeyLabel(vk));
            return string.Join("+", parts);
        }

        public static string KeyLabel(int vk)
        {
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);
            return "?";
        }

        // MOD_* values double as the RegisterHotKey modifier flags
        // (MOD_ALT=0x1, MOD_CONTROL=0x2, MOD_SHIFT=0x4, MOD_WIN=0x8).
        public static int RegisterMods(int mods)
        {
            int r = 0;
            if ((mods & MOD_CONTROL) != 0) r |= 0x2;
            if ((mods & MOD_ALT) != 0) r |= 0x1;
            if ((mods & MOD_SHIFT) != 0) r |= 0x4;
            if ((mods & MOD_WIN) != 0) r |= 0x8;
            return r | MOD_NOREPEAT;
        }

        // live modifier state, for the CLI's interactive capture (no keyboard
        // hooks: GetAsyncKeyState polling). Lives here — next to the VK/MOD
        // constants — because the two are easy to mix up.
        public static int ModifiersDown()
        {
            int m = 0;
            if ((P.GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) m |= MOD_CONTROL;
            if ((P.GetAsyncKeyState(VK_MENU) & 0x8000) != 0) m |= MOD_ALT;
            if ((P.GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) m |= MOD_SHIFT;
            if ((P.GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 ||
                (P.GetAsyncKeyState(VK_RWIN) & 0x8000) != 0) m |= MOD_WIN;
            return m;
        }
    }

    public static class P
    {
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool EnumWindows(Delegate cb, IntPtr p);
        public delegate bool EP(IntPtr h, IntPtr p);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    }

    // ---------- script launchers: host interpreter + how to build the command line ----------
    // A "launcher" is the host process that actually runs a script. The item's exe field
    // is the target (script path); the launcher decides host exe + args + tracked process.
    public static class Launchers
    {
        public class Def
        {
            public string Id;
            public string[] Exts;
            public string Host;            // absolute host exe (may be just a name for PATH-resolved)
            public string ProcName;        // process name to track after launch
            public Func<string, List<string>> Build;
            public bool UseAbsoluteHost;   // if true, do a File.Exists check before start
            public Def(string id, string[] exts, string host, string proc, bool useAbs, Func<string, List<string>> build)
            { Id = id; Exts = exts; Host = host; ProcName = proc; UseAbsoluteHost = useAbs; Build = build; }
        }

        // built-in launchers. System ones use absolute paths so File.Exists check works.
        static readonly List<Def> defs = new List<Def>
        {
            new Def("autohotkey", new[] { ".ahk" },
                @"C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe", "AutoHotkey64", true,
                s => new List<string> { s }),
            new Def("powershell", new[] { ".ps1", ".psm1" },
                @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "powershell", true,
                s => new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", s }),
            new Def("cmd", new[] { ".bat", ".cmd" },
                @"C:\Windows\System32\cmd.exe", "cmd", true,
                s => new List<string> { "/d", "/c", s }),
            new Def("wscript", new[] { ".vbs", ".js", ".jse" },
                @"C:\Windows\System32\wscript.exe", "wscript", true,
                s => new List<string> { s }),
            new Def("python", new[] { ".py", ".pyw" },
                "python.exe", "python", false,
                s => new List<string> { "-B", s }),
        };

        // does the host exe for this launcher+script actually exist on disk?
        public static bool HostExists(string launcher, string scriptPath)
        {
            var d = Resolve(launcher, scriptPath);
            if (d == null) return false;
            try { return !d.UseAbsoluteHost || File.Exists(d.Host); }
            catch { return false; }
        }

        // list built-in launcher ids + extensions, for help / am launchers
        public static string KnownList()
        {
            var sb = new StringBuilder();
            foreach (var d in defs)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append("  ").Append(d.Id).Append("  (");
                string joined = "";
                foreach (var e in d.Exts) joined += (joined.Length > 0 ? ", " : "") + e;
                sb.Append(joined);
                sb.Append(")  host=");
                string h = d.UseAbsoluteHost ? System.IO.Path.GetFileName(d.Host) : d.Host;
                sb.Append(h);
            }
            return sb.ToString().Trim();
        }

        // launcher ids for the interactive editor: built-ins first, then learned ones
        public static List<string> Ids(List<ExtLauncher> learned)
        {
            var ids = new List<string>();
            foreach (var d in defs) if (!ids.Contains(d.Id)) ids.Add(d.Id);
            if (learned != null)
                foreach (var e in learned)
                    if (e != null && !string.IsNullOrEmpty(e.id) && !ids.Contains(e.id)) ids.Add(e.id);
            return ids;
        }

        // is this extension covered by a built-in launcher?
        public static bool IsBuiltInExt(string scriptPath)
        {
            string ext = System.IO.Path.GetExtension(scriptPath).ToLowerInvariant();
            foreach (var d in defs)
                foreach (var e in d.Exts)
                    if (e.Equals(ext, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Resolve a launcher by explicit name, or auto-pick from the script extension.
        // Returns null when no launcher can handle it (a bare .exe needs none).
        public static Def Resolve(string launcher, string scriptPath)
        {
            string ext = System.IO.Path.GetExtension(scriptPath).ToLowerInvariant();
            Def auto = null;
            foreach (var d in defs)
            {
                bool owns = false;
                foreach (var e in d.Exts) if (e.Equals(ext, System.StringComparison.OrdinalIgnoreCase)) { owns = true; break; }
                if (owns && auto == null) auto = d;
                if (!string.IsNullOrEmpty(launcher) && d.Id.Equals(launcher.Trim(), System.StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            if (string.IsNullOrEmpty(launcher)) return auto; // auto-pick by extension
            return auto; // explicit launcher given but unknown -> fall back to extension match
        }

        // Look up a custom extension launcher from the learned list.
        // Returns the matching ExtLauncher, or null.
        public static ExtLauncher GetCustom(string ext, List<ExtLauncher> list)
        {
            if (list == null) return null;
            string key = ext.ToLowerInvariant();
            foreach (var e in list)
                if (e.ext.Equals(key, System.StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        // turn a built-in def into a raw args template string ("{script}" = target path)
        public static string ToArgsTemplate(Def d)
        {
            var parts = d.Build("{script}");
            var sb = new StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                string tok = parts[i];
                if (i > 0) sb.Append(" ");
                if (tok != "{script}") sb.Append(Core.Quote(tok));
                else sb.Append("{script}");
            }
            return sb.ToString();
        }

        // ---------- unified resolution: learned > builtin > custom host ----------
        public class Resolved
        {
            public string Id;             // launcher id (display)
            public string Host;           // host exe (path or bare name)
            public string ArgsTemplate;   // e.g. "-NoProfile -ExecutionPolicy Bypass -File {script}"
            public string Proc;           // process name to track
            public bool FromLearned;
            public bool FromBuiltin;
            public bool FromCustom;
        }

        // resolve the effective launcher for a script path.
        // Priority: explicit host > explicit launcher name > learned ext > built-in ext.
        // Returns null if nothing matches (caller should reject).
        public static Resolved ResolveFor(string scriptPath, string launcherName, string hostOverride, List<ExtLauncher> learned)
        {
            string ext = System.IO.Path.GetExtension(scriptPath);
            ext = ext == null ? "" : ext.ToLowerInvariant();

            // 1. explicit host override (most specific)
            if (!string.IsNullOrEmpty(hostOverride))
            {
                string id = string.IsNullOrEmpty(launcherName)
                    ? System.IO.Path.GetFileNameWithoutExtension(hostOverride)
                    : launcherName.Trim();
                string proc = id;
                return new Resolved {
                    Id = id, Host = hostOverride, ArgsTemplate = "{script}",
                    Proc = proc, FromCustom = true
                };
            }

            // 2. by launcher name (built-in or learned)
            if (!string.IsNullOrEmpty(launcherName))
            {
                string ln = launcherName.Trim();
                // built-in
                foreach (var d in defs)
                    if (d.Id.Equals(ln, System.StringComparison.OrdinalIgnoreCase))
                    {
                        return new Resolved {
                            Id = d.Id, Host = d.Host, ArgsTemplate = ToArgsTemplate(d),
                            Proc = d.ProcName, FromBuiltin = true
                        };
                    }
                // learned
                foreach (var e in learned)
                    if (e != null && e.id.Equals(ln, System.StringComparison.OrdinalIgnoreCase))
                    {
                        return new Resolved {
                            Id = e.id, Host = e.host, ArgsTemplate = e.args, Proc = e.proc,
                            FromLearned = true
                        };
                    }
                return null; // name not found anywhere
            }

            // 3. by extension: learned first, then built-in
            if (!string.IsNullOrEmpty(ext))
            {
                // learned
                foreach (var e in learned)
                    if (e != null && e.ext.Equals(ext, System.StringComparison.OrdinalIgnoreCase))
                    {
                        return new Resolved {
                            Id = e.id, Host = e.host, ArgsTemplate = e.args,
                            Proc = e.proc, FromLearned = true
                        };
                    }
                // built-in
                foreach (var d in defs)
                    foreach (var e in d.Exts)
                        if (e.Equals(ext, System.StringComparison.OrdinalIgnoreCase))
                        {
                            return new Resolved {
                                Id = d.Id, Host = d.Host, ArgsTemplate = ToArgsTemplate(d),
                                Proc = d.ProcName, FromBuiltin = true
                            };
                        }
            }
            return null;
        }

        // record a learned mapping (auto or manual); replaces any existing entry for ext
        public static void Learn(string ext, string id, string host, string args, string proc, List<ExtLauncher> list)
        {
            if (list == null) list = new List<ExtLauncher>();
            ext = ext.ToLowerInvariant();
            // remove existing entry for this ext, then add
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i] != null && list[i].ext.Equals(ext, System.StringComparison.OrdinalIgnoreCase))
                    list.RemoveAt(i);
            list.Add(new ExtLauncher { ext = ext, id = id, host = host, args = args, proc = proc });
        }

        // find a learned entry by extension (case-insensitive)
        public static ExtLauncher FindByExt(string ext, List<ExtLauncher> list)
        {
            if (list == null) return null;
            ext = ext.ToLowerInvariant();
            foreach (var e in list)
                if (e != null && e.ext.Equals(ext, System.StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        // remove a learned entry by extension (case-insensitive)
        public static void Forget(string ext, List<ExtLauncher> list)
        {
            if (list == null) return;
            ext = ext.ToLowerInvariant();
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i] != null && list[i].ext.Equals(ext, System.StringComparison.OrdinalIgnoreCase))
                    list.RemoveAt(i);
        }
    }

    public static class Core
    {
        public const int SW_HIDE = 0, SW_SHOW = 5, SW_RESTORE = 9;
        public const string EngineProcName = "am-engine";
        public const string EngineExeName = "am-engine.exe";
        public const string MutexName = "AppManagerResident";

        // Self-contained: config/log/pid live next to the running exe (project dir).
        static string Dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
        public static string CfgPath = Path.Combine(Dir, "config.json");
        public static string LogPath = Path.Combine(Dir, "am.log");
        public static string PidPath = Path.Combine(Dir, "am-engine.pid");
        public static string StopPath = Path.Combine(Dir, "am-engine.stop.txt");

        // ---------- config io ----------
        public static Config Load()
        {
            Config c = null;
            try
            {
                if (File.Exists(CfgPath))
                {
                    using (var fs = File.OpenRead(CfgPath))
                    {
                        var ser = new DataContractJsonSerializer(typeof(Config));
                        c = (Config)ser.ReadObject(fs);
                    }
                }
            }
            catch { c = null; }
            if (c == null) c = new Config();
            // DataContractJsonSerializer leaves absent collection members as null; normalize
            if (c.extLaunchers == null) c.extLaunchers = new List<ExtLauncher>();
            if (c.items == null) c.items = new List<Item>();
            if (c.hotkey == null) c.hotkey = "";
            return c;
        }

        public static void Save(Config c)
        {
            Directory.CreateDirectory(Dir);
            using (var fs = File.Create(CfgPath))
            {
                var ser = new DataContractJsonSerializer(typeof(Config));
                ser.WriteObject(fs, c);
            }
        }

        public static void Log(string m)
        {
            try { Directory.CreateDirectory(Dir); File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss") + "  " + m + Environment.NewLine); }
            catch { }
        }

        // ---------- "stop" request marker ----------
        // `am stop` writes it, any NORMAL engine start (the logon task or
        // `am run`) clears it, and a `--keepalive` start refuses to run while
        // it exists. Without it the keep-alive watchdog would fight `am stop`
        // and resurrect the engine in the middle of a build.
        public static bool StopRequested()
        {
            try { return File.Exists(StopPath); }
            catch { return false; }
        }

        public static void ClearStopRequest()
        {
            try { if (File.Exists(StopPath)) File.Delete(StopPath); }
            catch { }
        }

        // ---------- process / window helpers ----------
        public static int ProcCount(string proc)
        {
            try { var a = Process.GetProcessesByName(proc); int n = a.Length; foreach (var p in a) p.Dispose(); return n; }
            catch { return 0; }
        }

        public static bool StartApp(Item it)
        {
            string hostExe = it.exe;
            string argsTemplate = "";
            string procOverride = "";
            if (it.script)
            {
                // try custom host first (from learned ext mapping or item-level)
                if (!string.IsNullOrEmpty(it.hostExe))
                {
                    hostExe = it.hostExe;
                    argsTemplate = it.hostArgs; // may contain {script}
                }
                else
                {
                    var def = Launchers.Resolve(it.launcher, it.exe);
                    if (def == null) { Log("no launcher for '" + it.name + "' (" + it.exe + ")"); return false; }
                    hostExe = def.Host;
                    if (def.UseAbsoluteHost && !File.Exists(hostExe))
                    { Log("host missing for '" + it.name + "': " + hostExe); return false; }
                    var built = def.Build(it.exe);
                    var sb = new StringBuilder();
                    for (int i = 0; i < built.Count; i++)
                        sb.Append((i == 0 ? "" : " ") + Core.Quote(built[i]));
                    argsTemplate = sb.ToString();
                    procOverride = def.ProcName;
                }
                if (string.IsNullOrEmpty(procOverride))
                    procOverride = it.processName; // fall back to item's own processName
                it.processName = procOverride;
            }
            if (string.IsNullOrEmpty(it.hostExe) && !it.script)
            {
                // plain exe: check it exists
                if (!File.Exists(hostExe)) { Log("exe missing for '" + it.name + "': " + it.exe); return false; }
            }
            try
            {
                string args = "";
                if (!string.IsNullOrEmpty(argsTemplate))
                    args = argsTemplate.Replace("{script}", Core.Quote(it.exe));
                var psi = new ProcessStartInfo(hostExe) { UseShellExecute = false };
                psi.Arguments = args;
                Process.Start(psi);
                Log(string.Format("started '{0}' -> {1} {2}", it.name, hostExe, args));
                return true;
            }
            catch (Exception ex) { Log("start failed '" + it.name + "': " + ex.Message); return false; }
        }

        // quote a single argument token for a raw command line
        public static string Quote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            if (s.IndexOf(' ') < 0 && s.IndexOf('"') < 0) return s;
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        }

        public static List<IntPtr> CollectWindows(string proc, string title)
        {
            var res = new List<IntPtr>();
            var pids = new HashSet<uint>();
            try
            {
                var ps = Process.GetProcessesByName(proc);
                foreach (var p in ps) { pids.Add((uint)p.Id); p.Dispose(); }
            }
            catch { }
            if (pids.Count == 0) return res;
            P.EP cb = delegate(IntPtr h, IntPtr x)
            {
                uint pid;
                P.GetWindowThreadProcessId(h, out pid);
                if (pids.Contains(pid))
                {
                    if (string.IsNullOrEmpty(title)) { res.Add(h); }
                    else
                    {
                        var sb = new StringBuilder(512);
                        P.GetWindowText(h, sb, sb.Capacity);
                        string t = sb.ToString();
                        if (t.Contains(title)) res.Add(h);
                    }
                }
                return true;
            };
            P.EnumWindows(cb, IntPtr.Zero);
            return res;
        }

        // Top-level windows that plausibly belong to the app's own UI.
        // EnumWindows hands back *every* top-level HWND of the process, and a
        // modern app (Chromium/Electron, IME, crash reporter, tray host) owns
        // a pile of them: renderer hosts, zero-sized message windows, tool
        // windows and owned popups. Restoring that whole set is what made the
        // picker pop up unrelated windows/pages, so keep only "real" windows:
        // unowned, non-tool, non-empty rectangles. Titled windows come first
        // (helpers are usually titleless), each group in EnumWindows z-order.
        public static List<IntPtr> CollectAppWindows(string proc, string title)
        {
            var titled = new List<IntPtr>();
            var rest = new List<IntPtr>();
            foreach (var h in CollectWindows(proc, title))
            {
                if (P.GetWindow(h, 4 /*GW_OWNER*/) != IntPtr.Zero) continue;      // owned = dialog/popup of another window
                int ex = P.GetWindowLong(h, -20 /*GWL_EXSTYLE*/);
                if ((ex & 0x80 /*WS_EX_TOOLWINDOW*/) != 0 && (ex & 0x40000 /*WS_EX_APPWINDOW*/) == 0) continue;
                P.RECT r;
                if (!P.GetWindowRect(h, out r)) continue;
                if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) continue;     // message-only / zero-size helper
                var sb = new StringBuilder(512);
                P.GetWindowText(h, sb, sb.Capacity);
                (sb.Length > 0 ? titled : rest).Add(h);
            }
            titled.AddRange(rest);
            return titled;
        }

        // The single window the picker / `am start <name>` should bring back: the
        // largest titled app window (or, when nothing is titled, the largest
        // app window). Ties keep the top-most in z-order.
        public static IntPtr FindMainWindow(string proc, string title)
        {
            IntPtr bestTitled = IntPtr.Zero, bestAny = IntPtr.Zero;
            long areaTitled = -1, areaAny = -1;
            var name = new StringBuilder(512);
            foreach (var h in CollectAppWindows(proc, title))
            {
                P.RECT r;
                if (!P.GetWindowRect(h, out r)) continue;
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > areaAny) { areaAny = area; bestAny = h; }
                name.Length = 0;
                P.GetWindowText(h, name, name.Capacity);
                if (name.Length > 0 && area > areaTitled) { areaTitled = area; bestTitled = h; }
            }
            // A titled window is the app's real UI; untitled ones are hidden
            // helpers (Chromium's big offscreen render hosts) even when their
            // rectangle is larger.
            return bestTitled != IntPtr.Zero ? bestTitled : bestAny;
        }

        public static int HideWindows(string proc, string title)
        {
            int n = 0;
            // Only the app's real windows: minimizing every visible top-level
            // HWND also hit the app's own helper windows (Chromium's power /
            // tray / renderer hosts), which is how a "hide" could disturb the
            // app's tray state instead of just putting its window away.
            foreach (var h in CollectAppWindows(proc, title))
            {
                if (!P.IsWindowVisible(h)) continue;
                if (P.IsIconic(h))
                {
                    // minimized to the taskbar -> the app has no tray-minimize
                    // path; hide it fully. Tray icons (if any) live in the
                    // app's own NotifyIcon and are not affected by this.
                    if (P.ShowWindow(h, SW_HIDE)) n++;
                }
                else
                {
                    // normally visible -> let the app minimize natively: its
                    // own "minimize to tray" handler runs, which is what
                    // creates/keeps its tray icon. Apps without tray support
                    // end up iconic on the taskbar and the SW_HIDE branch
                    // catches them on a later poll tick.
                    P.SendMessage(h, 0x0112 /*WM_SYSCOMMAND*/, new IntPtr(0xF020 /*SC_MINIMIZE*/), IntPtr.Zero);
                    n++;
                }
            }
            return n;
        }

        // Bring an app back on screen: restore its main window only. Works
        // for all three states a managed app can be in — minimized on the
        // taskbar (iconic), hidden by its own "minimize to tray" handler
        // (Chromium/Electron clients hide the window outright), or simply
        // behind other windows.
        public static int ShowWindows(string proc, string title)
        {
            IntPtr h = FindMainWindow(proc, title);
            if (h == IntPtr.Zero) return 0;
            P.ShowWindow(h, SW_RESTORE);
            // SW_RESTORE does not always un-hide a window the app hid itself
            // (ShowWindow(SW_HIDE) from its tray code never sets the iconic
            // bit), so force a plain show as well.
            if (!P.IsWindowVisible(h)) P.ShowWindow(h, SW_SHOW);
            P.SetForegroundWindow(h);
            return 1;
        }

                // ---------- one pass: start-if-needed + continuous poll-hide for T seconds ----------
        // Simple model: T = the entire monitoring window (--time per item, default 30s).
        // During T, every 250ms we scan for visible windows and hide them.
        // No distinction between "already running" vs "cold-started" - the window
        // may pop up at any time during T, and we catch it on the next tick.
        public static void DoPass(Config cfg)
        {
            // on-demand items (autostart=false) are registered only: this pass
            // neither launches nor hides them. The picker (Enter) or
            // `am start <name>` is what starts them.
            var enabled = new List<Item>();
            foreach (var i in cfg.items) if (i.enabled && i.autostart) enabled.Add(i);
            if (enabled.Count == 0) { Log("pass: no auto-start items"); return; }

            const int iv = 250;        // poll interval
            const int defT = 30000;   // default monitor window = 30s

            // 1. Launch items that are not running yet
            foreach (var it in enabled)
            {
                if (it.script)
                {
                    if (ProcCount(it.processName) == 0)
                    {
                        bool ok = StartApp(it);
                        Log(string.Format("pass: '{0}' launched={1} (script, launch-only)", it.name, ok));
                    }
                    else
                        Log(string.Format("pass: '{0}' already running (script, launch-only)", it.name));
                }
                else if (ProcCount(it.processName) == 0)
                {
                    bool ok = StartApp(it);
                    Log(string.Format("pass: started '{0}' -> {1}", it.name, ok ? "ok" : "failed"));
                }
            }

            // 2. Determine max T across app items
            int maxT = 0;
            foreach (var it in enabled)
            {
                if (it.script) continue;
                int t = it.silentWindowMs > 0 ? it.silentWindowMs : defT;
                if (t > maxT) maxT = t;
            }

            // 3. For maxT duration, every 250ms hide visible windows of app items
            var sw = Stopwatch.StartNew();
            int hides = 0;
            while (sw.ElapsedMilliseconds < maxT)
            {
                foreach (var it in enabled)
                {
                    if (it.script) continue;
                    hides += HideWindows(it.processName, it.windowTitle);
                }
                Thread.Sleep(iv);
            }
            Log(string.Format("pass done: {0} item(s), {1} hide-op(s), monitor {2}ms", enabled.Count, hides, maxT));
        }
public static Item FindItem(Config cfg, string name)
        {
            foreach (var i in cfg.items) if (string.Equals(i.name, name, StringComparison.OrdinalIgnoreCase)) return i;
            return null;
        }

        // path of the engine exe (same directory as this assembly's deploy folder)
        public static string EngineExe()
        {
            string d = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(d, EngineExeName);
        }

        // is the engine alive? returns pid or 0
        public static int EnginePid()
        {
            try
            {
                if (File.Exists(PidPath))
                {
                    int pid = int.Parse(File.ReadAllText(PidPath).Trim());
                    var p = Process.GetProcessById(pid);
                    if (p.ProcessName.IndexOf(EngineProcName, StringComparison.OrdinalIgnoreCase) >= 0)
                        return pid;
                }
            }
            catch { }
            // fallback: by process name (first one)
            try
            {
                var ps = Process.GetProcessesByName(EngineProcName);
                int pid = 0;
                foreach (var p in ps) { pid = p.Id; break; }
                if (pid > 0) return pid;
            }
            catch { }
            return 0;
        }
    }
}
