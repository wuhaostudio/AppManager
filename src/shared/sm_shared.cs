using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

// StartManager shared core: configuration + one logon pass.
//
// Scope: start each ENABLED item at logon (once) and hide its windows during
// the configured monitor window. The engine exits when the pass is done.
// There is no resident process, no process guard, no window restore, no
// hotkey and no UI: the only thing StartManager does after the pass is stop.
//
// Linked into both sm.exe (CLI) and sm-engine.exe (logon pass).
// Self-contained: config/log live next to the running exe (deploy dir).
namespace StartManager.Shared
{
    [DataContract]
    public class Item
    {
        [DataMember] public string name = "";
        [DataMember] public string exe = "";            // executable to launch (apps and script hosts)
        [DataMember] public string args = "";           // optional command line (e.g. the .ahk path for AutoHotkey64.exe)
        [DataMember] public string processName = "";    // process to track after launch
        [DataMember] public string windowTitle = "";    // optional title filter; empty = every real window
        [DataMember] public int silentWindowMs = 0;     // monitor window; 0 = default 30000ms
        [DataMember] public bool enabled = true;        // disabled items are not started and not hidden

        // ---- script items (launch-only) ----
        // A script is not directly executable: it is launched through a host
        // interpreter resolved from a learned/built-in launcher. Script items
        // are started at logon but their windows are never hidden (the host
        // process is usually shared with other tools).
        [DataMember] public bool script = false;
        [DataMember] public string launcher = "";       // launcher id (built-in or learned)
        [DataMember] public string hostExe = "";        // resolved host interpreter
        [DataMember] public string hostArgs = "";       // args template, {script} = the script path
    }

    [DataContract]
    public class ExtLauncher
    {
        [DataMember] public string ext = "";        // ".lua" (with dot, lowercase)
        [DataMember] public string id = "";         // launcher name
        [DataMember] public string host = "";       // host exe absolute path (or a PATH name)
        [DataMember] public string args = "";       // args template, {script} = target path
        [DataMember] public string proc = "";       // process name to track
    }

    [DataContract]
    public class Config
    {
        [DataMember] public List<Item> items = new List<Item>();
        [DataMember] public List<ExtLauncher> extLaunchers = new List<ExtLauncher>();
    }

    public static class P
    {
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool EnumWindows(Delegate cb, IntPtr p);
        public delegate bool EP(IntPtr h, IntPtr p);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    }

    // ---------- script launchers: host interpreter + command line ----------
    // A "launcher" is the host process that actually runs a script. Resolution
    // priority: learned extension map > built-in extension map. A learned entry
    // can also be selected explicitly by its id (`sm add ... --launcher id`).
    public static class Launchers
    {
        public class Def
        {
            public string Id;
            public string[] Exts;
            public string Host;            // absolute host exe (may be a bare PATH name)
            public string ProcName;        // process to track after launch
            public Func<string, List<string>> Build;
            public bool UseAbsoluteHost;   // if true, File.Exists check applies
            public Def(string id, string[] exts, string host, string proc, bool useAbs, Func<string, List<string>> build)
            { Id = id; Exts = exts; Host = host; ProcName = proc; UseAbsoluteHost = useAbs; Build = build; }
        }

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
                sb.Append(d.UseAbsoluteHost ? System.IO.Path.GetFileName(d.Host) : d.Host);
            }
            return sb.ToString().Trim();
        }

        // launcher ids for the editor: built-ins first, then learned ones
        public static List<string> Ids(List<ExtLauncher> learned)
        {
            var ids = new List<string>();
            foreach (var d in defs) if (!ids.Contains(d.Id)) ids.Add(d.Id);
            if (learned != null)
                foreach (var e in learned)
                    if (e != null && !string.IsNullOrEmpty(e.id) && !ids.Contains(e.id)) ids.Add(e.id);
            return ids;
        }

        public static bool IsBuiltInExt(string scriptPath)
        {
            string ext = System.IO.Path.GetExtension(scriptPath).ToLowerInvariant();
            foreach (var d in defs)
                foreach (var e in d.Exts)
                    if (e.Equals(ext, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Resolve by explicit launcher name (no fallback), or auto-pick by extension.
        public static Def Resolve(string launcher, string scriptPath)
        {
            string ext = System.IO.Path.GetExtension(scriptPath).ToLowerInvariant();
            Def auto = null;
            foreach (var d in defs)
            {
                bool owns = false;
                foreach (var e in d.Exts) if (e.Equals(ext, StringComparison.OrdinalIgnoreCase)) { owns = true; break; }
                if (owns && auto == null) auto = d;
                if (!string.IsNullOrEmpty(launcher) && d.Id.Equals(launcher.Trim(), StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            return auto;
        }

        public static ExtLauncher GetCustom(string ext, List<ExtLauncher> list)
        {
            if (list == null) return null;
            string key = ext.ToLowerInvariant();
            foreach (var e in list)
                if (e.ext.Equals(key, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

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

        public class Resolved
        {
            public string Id;
            public string Host;
            public string ArgsTemplate;
            public string Proc;
            public bool FromLearned;
            public bool FromBuiltin;
        }

        // resolve the effective launcher for a script path.
        // Priority: explicit launcher name > learned ext > built-in ext.
        // Returns null if nothing matches (caller should reject).
        public static Resolved ResolveFor(string scriptPath, string launcherName, List<ExtLauncher> learned)
        {
            string ext = System.IO.Path.GetExtension(scriptPath);
            ext = ext == null ? "" : ext.ToLowerInvariant();

            // 1. by launcher name (built-in or learned)
            if (!string.IsNullOrEmpty(launcherName))
            {
                string ln = launcherName.Trim();
                foreach (var d in defs)
                    if (d.Id.Equals(ln, StringComparison.OrdinalIgnoreCase))
                        return new Resolved { Id = d.Id, Host = d.Host, ArgsTemplate = ToArgsTemplate(d), Proc = d.ProcName, FromBuiltin = true };
                if (learned != null)
                    foreach (var e in learned)
                        if (e != null && e.id.Equals(ln, StringComparison.OrdinalIgnoreCase))
                            return new Resolved { Id = e.id, Host = e.host, ArgsTemplate = e.args, Proc = e.proc, FromLearned = true };
                return null;
            }

            // 2. by extension: learned first, then built-in
            if (!string.IsNullOrEmpty(ext))
            {
                if (learned != null)
                    foreach (var e in learned)
                        if (e != null && e.ext.Equals(ext, StringComparison.OrdinalIgnoreCase))
                            return new Resolved { Id = e.id, Host = e.host, ArgsTemplate = e.args, Proc = e.proc, FromLearned = true };
                foreach (var d in defs)
                    foreach (var e in d.Exts)
                        if (e.Equals(ext, StringComparison.OrdinalIgnoreCase))
                            return new Resolved { Id = d.Id, Host = d.Host, ArgsTemplate = ToArgsTemplate(d), Proc = d.ProcName, FromBuiltin = true };
            }
            return null;
        }

        // record a learned mapping (replaces any existing entry for ext)
        public static void Learn(string ext, string id, string host, string args, string proc, List<ExtLauncher> list)
        {
            ext = ext.ToLowerInvariant();
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i] != null && list[i].ext.Equals(ext, StringComparison.OrdinalIgnoreCase))
                    list.RemoveAt(i);
            list.Add(new ExtLauncher { ext = ext, id = id, host = host, args = args, proc = proc });
        }

        public static ExtLauncher FindByExt(string ext, List<ExtLauncher> list)
        {
            if (list == null) return null;
            ext = ext.ToLowerInvariant();
            foreach (var e in list)
                if (e != null && e.ext.Equals(ext, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        public static void Forget(string ext, List<ExtLauncher> list)
        {
            if (list == null) return;
            ext = ext.ToLowerInvariant();
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i] != null && list[i].ext.Equals(ext, StringComparison.OrdinalIgnoreCase))
                    list.RemoveAt(i);
        }
    }

    public static class Core
    {
        public const int SW_HIDE = 0;

        // Self-contained: config/log live next to the running exe (deploy dir).
        static string Dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
        public static string CfgPath = Path.Combine(Dir, "config.json");
        public static string LogPath = Path.Combine(Dir, "sm.log");

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
            if (c.items == null) c.items = new List<Item>();
            if (c.extLaunchers == null) c.extLaunchers = new List<ExtLauncher>();
            // normalize: derive processName; resolve a missing script host from
            // the learned / built-in launcher tables
            foreach (var it in c.items)
            {
                if (it == null) continue;
                // DataContractJsonSerializer leaves absent string members null
                if (it.name == null) it.name = "";
                if (it.exe == null) it.exe = "";
                if (it.args == null) it.args = "";
                if (it.processName == null) it.processName = "";
                if (it.windowTitle == null) it.windowTitle = "";
                if (it.launcher == null) it.launcher = "";
                if (it.hostExe == null) it.hostExe = "";
                if (it.hostArgs == null) it.hostArgs = "";
                if (it.script)
                {
                    if (string.IsNullOrEmpty(it.hostExe) || string.IsNullOrEmpty(it.hostArgs))
                    {
                        var r = Launchers.ResolveFor(it.exe, it.launcher, c.extLaunchers);
                        if (r != null)
                        {
                            it.hostExe = r.Host;
                            it.hostArgs = r.ArgsTemplate;
                            if (!string.IsNullOrEmpty(r.Proc)) it.processName = r.Proc;
                            if (string.IsNullOrEmpty(it.launcher)) it.launcher = r.Id;
                        }
                    }
                    if (string.IsNullOrEmpty(it.processName))
                        it.processName = Path.GetFileNameWithoutExtension(it.hostExe);
                }
                else if (string.IsNullOrEmpty(it.processName) && !string.IsNullOrEmpty(it.exe))
                {
                    it.processName = Path.GetFileNameWithoutExtension(it.exe);
                }
            }
            foreach (var e in c.extLaunchers)
            {
                if (e == null) continue;
                if (e.ext == null) e.ext = "";
                if (e.id == null) e.id = "";
                if (e.host == null) e.host = "";
                if (e.args == null) e.args = "";
                if (e.proc == null) e.proc = "";
            }
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

        // ---------- process / window helpers ----------
        public static int ProcCount(string proc)
        {
            try { var a = Process.GetProcessesByName(proc); int n = a.Length; foreach (var p in a) p.Dispose(); return n; }
            catch { return 0; }
        }

        public static bool StartApp(Item it)
        {
            string host = it.exe;
            string argLine = it.args;

            if (it.script)
            {
                host = it.hostExe;
                if (string.IsNullOrEmpty(host))
                {
                    var r = Launchers.ResolveFor(it.exe, it.launcher, null);
                    if (r != null) host = r.Host;
                }
                if (string.IsNullOrEmpty(host))
                {
                    Log("no host launcher for script '" + it.name + "' (" + it.exe + ")");
                    return false;
                }
                bool isPath = host.IndexOf('\\') >= 0 || host.IndexOf('/') >= 0;
                if (isPath && !File.Exists(host))
                {
                    Log("host missing for script '" + it.name + "': " + host);
                    return false;
                }
                argLine = (it.hostArgs == null ? "" : it.hostArgs).Replace("{script}", Quote(it.exe));
            }
            else if (string.IsNullOrEmpty(host) || !File.Exists(host))
            {
                Log("exe missing for '" + it.name + "': " + it.exe);
                return false;
            }

            try
            {
                var psi = new ProcessStartInfo(host) { UseShellExecute = false };
                if (!string.IsNullOrEmpty(argLine)) psi.Arguments = argLine;
                string dir = Path.GetDirectoryName(host);
                if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
                Process.Start(psi);
                Log(string.Format("started '{0}' -> {1} {2}", it.name, host, argLine));
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

        // Top-level windows of the process, optionally filtered by a title substring.
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
                        if (sb.ToString().Contains(title)) res.Add(h);
                    }
                }
                return true;
            };
            P.EnumWindows(cb, IntPtr.Zero);
            return res;
        }

        // Top-level windows that plausibly belong to the app's own UI.
        // EnumWindows hands back *every* top-level HWND of the process (renderer
        // hosts, tray hosts, IME, zero-sized message windows); hiding that whole
        // set would disturb the app's internals. Keep only "real" windows:
        // unowned, non-tool, non-empty rectangles. Titled windows come first.
        public static List<IntPtr> CollectAppWindows(string proc, string title)
        {
            var titled = new List<IntPtr>();
            var rest = new List<IntPtr>();
            foreach (var h in CollectWindows(proc, title))
            {
                if (P.GetWindow(h, 4 /*GW_OWNER*/) != IntPtr.Zero) continue;
                int ex = P.GetWindowLong(h, -20 /*GWL_EXSTYLE*/);
                if ((ex & 0x80 /*WS_EX_TOOLWINDOW*/) != 0 && (ex & 0x40000 /*WS_EX_APPWINDOW*/) == 0) continue;
                P.RECT r;
                if (!P.GetWindowRect(h, out r)) continue;
                if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) continue;
                var sb = new StringBuilder(512);
                P.GetWindowText(h, sb, sb.Capacity);
                (sb.Length > 0 ? titled : rest).Add(h);
            }
            titled.AddRange(rest);
            return titled;
        }

        // Hide the app's real windows through its own "minimize to tray" path
        // (SC_MINIMIZE), falling back to SW_HIDE for windows that stay iconic
        // (taskbar minimize; the app has no tray path).
        public static int HideWindows(Item it)
        {
            int n = 0;
            foreach (var h in CollectAppWindows(it.processName, it.windowTitle))
            {
                if (!P.IsWindowVisible(h)) continue;
                if (P.IsIconic(h))
                {
                    if (P.ShowWindow(h, SW_HIDE)) n++;
                }
                else
                {
                    P.SendMessage(h, 0x0112 /*WM_SYSCOMMAND*/, new IntPtr(0xF020 /*SC_MINIMIZE*/), IntPtr.Zero);
                    n++;
                }
            }
            return n;
        }

        // ---------- the one logon pass: start if needed + poll-hide for T ----------
        // T = the whole monitoring window (silentWindowMs per item, default 30s).
        // During T every 250ms we hide visible windows. No distinction between
        // "already running" and "cold-started": late popups are caught too.
        public static void DoPass(Config cfg)
        {
            var enabled = new List<Item>();
            foreach (var i in cfg.items) if (i != null && i.enabled) enabled.Add(i);
            if (enabled.Count == 0) { Log("pass: no enabled items"); return; }

            const int iv = 250;      // poll interval
            const int defT = 30000;  // default monitor window = 30s

            // 1. launch items that are not running yet
            foreach (var it in enabled)
            {
                if (string.IsNullOrEmpty(it.processName))
                {
                    Log("pass: '" + it.name + "' skipped (no processName)");
                    continue;
                }
                if (ProcCount(it.processName) == 0)
                {
                    bool ok = StartApp(it);
                    Log(string.Format("pass: started '{0}' -> {1}{2}", it.name, ok ? "ok" : "failed",
                        it.script ? " (script, launch-only)" : ""));
                }
                else
                {
                    Log(string.Format("pass: '{0}' already running{1}", it.name,
                        it.script ? " (script, launch-only)" : ""));
                }
            }

            // 2. longest monitor window among APP items (scripts are launch-only)
            int maxT = 0;
            foreach (var it in enabled)
            {
                if (it.script) continue;
                int t = it.silentWindowMs > 0 ? it.silentWindowMs : defT;
                if (t > maxT) maxT = t;
            }

            // 3. hide visible windows every 250ms for maxT
            var sw = Stopwatch.StartNew();
            int hides = 0;
            while (sw.ElapsedMilliseconds < maxT)
            {
                foreach (var it in enabled)
                {
                    if (it.script) continue;
                    if (string.IsNullOrEmpty(it.processName)) continue;
                    hides += HideWindows(it);
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
    }
}
