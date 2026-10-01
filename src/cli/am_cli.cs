using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using AppManager.Shared;
using Microsoft.Win32;

// V7 CLI: am.exe (console subsystem, /target:exe).
// Stateless local operations only: config.json next to the exe, direct OS
// process/window enumeration, spawn/kill the engine. No IPC with the engine.
namespace AppManager.Cli
{
    static class Program
    {
        static int Main(string[] args)
        {
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            switch (cmd)
            {
                case "scan": DoScan(args); break;
                case "add": DoAdd(args); break;
                case "remove": DoRemove(args); break;
                case "list": DoList(); break;
                case "show": DoShow(args); break;
                case "start": DoStart(); break;
                case "run": DoRun(); break;
                case "stop": DoStop(); break;
                case "launchers": DoLaunchers(args); break;
                default: Help(); break;
            }
            return 0;
        }

        // ---------- table rendering: left-aligned cols, full-width separator ----------
        static void PrintTable(string[] headers, List<string[]> rows)
        {
            int cols = headers.Length;
            int[] w = new int[cols];
            for (int c = 0; c < cols; c++) w[c] = headers[c].Length;
            foreach (var r in rows)
                for (int c = 0; c < cols; c++)
                    if (r[c] != null && r[c].Length > w[c]) w[c] = r[c].Length;
            const int gap = 2;
            int total = 0;
            for (int c = 0; c < cols; c++) total += w[c] + gap;
            total -= gap;

            string h = "";
            for (int c = 0; c < cols; c++) h += (c < cols - 1) ? headers[c].PadRight(w[c] + gap) : headers[c];
            Console.WriteLine(h);
            Console.WriteLine(new string('-', total));
            foreach (var r in rows)
            {
                string line = "";
                for (int c = 0; c < cols; c++) line += (c < cols - 1) ? (r[c] ?? "").PadRight(w[c] + gap) : (r[c] ?? "");
                Console.WriteLine(line);
            }
        }

        // human label for the unified monitor window — concrete values only
        static string SilentLabel(int ms)
        {
            int t = ms > 0 ? ms : 30000; // default (unset) = 30s
            return (t % 1000 == 0) ? (t / 1000) + "s" : t + "ms";
        }

        // ---------- scan ----------
        static void DoScan(string[] a)
        {
            string kw = a.Length >= 2 ? a[1] : "";
            var seen = new HashSet<string>();
            var rows = new List<string[]>();
            ScanRoot(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", seen, rows);
            ScanRoot(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", seen, rows);
            ScanRoot(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", seen, rows);

            var filtered = new List<string[]>();
            foreach (var r in rows)
                if (kw.Length == 0 || r[0].IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) filtered.Add(r);

            Console.WriteLine(string.Format("Found {0} installed program(s){1}. Filter='{2}'",
                filtered.Count, filtered.Count == 1 ? "" : "s", kw));
            var table = new List<string[]>();
            foreach (var r in filtered) table.Add(new string[] { r[0], r[1] });
            PrintTable(new[] { "PROGRAM", "EXE" }, table);
            Console.WriteLine();
            Console.WriteLine("To manage one:  am add <exePath>   (or just 'am add' for interactive)");
        }

        static void ScanRoot(RegistryKey root, string sub, HashSet<string> seen, List<string[]> rows)
        {
            try
            {
                using (var baseKey = root.OpenSubKey(sub))
                {
                    if (baseKey == null) return;
                    foreach (var sn in baseKey.GetSubKeyNames())
                    {
                        using (var k = baseKey.OpenSubKey(sn))
                        {
                            if (k == null) continue;
                            var disp = (k.GetValue("DisplayName") ?? "") as string;
                            var icon = (k.GetValue("DisplayIcon") ?? "") as string;
                            var loc = (k.GetValue("InstallLocation") ?? "") as string;
                            if (string.IsNullOrWhiteSpace(disp)) continue;
                            string exe = ResolveExe(icon, loc);
                            if (exe == null) continue;
                            if (!File.Exists(exe)) continue;
                            string key = exe.ToLowerInvariant();
                            if (!seen.Add(key)) continue;
                            rows.Add(new string[] { disp, exe, Path.GetFileNameWithoutExtension(exe) });
                        }
                    }
                }
            }
            catch { }
        }

        static string ResolveExe(string icon, string loc)
        {
            if (!string.IsNullOrWhiteSpace(icon))
            {
                string s = icon.Trim().Trim('"');
                int comma = s.IndexOf(',');
                if (comma >= 0) s = s.Substring(0, comma).Trim().Trim('"');
                if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(s)) return s;
            }
            if (!string.IsNullOrWhiteSpace(loc) && Directory.Exists(loc))
            {
                try
                {
                    var exes = Directory.GetFiles(loc, "*.exe");
                    if (exes.Length > 0) return exes[0];
                }
                catch { }
            }
            return null;
        }

        // ---------- add ----------
        static void DoAdd(string[] a)
        {
            string exe = null, name = null, title = null, launcher = null;
            int window = 0;
            bool hasWindow = false;
            for (int i = 1; i < a.Length; i++)
            {
                string t = a[i].ToLowerInvariant();
                if (t == "--name") { if (i + 1 < a.Length) name = a[++i]; }
                else if (t == "--title") { if (i + 1 < a.Length) title = a[++i]; }
                else if (t == "--time") { if (i + 1 < a.Length) { window = ParseSec(a[++i], 30000); hasWindow = true; } }
                else if (t == "--launcher") { if (i + 1 < a.Length) launcher = a[++i]; }
                else if (exe == null) exe = a[i];
            }

            // Interactive mode: `am add` with no exe argument -> step-by-step prompts.
            if (exe == null)
            {
                Console.WriteLine("am add (interactive) — press Enter to accept the [default]");
                Console.Write("1) program path (exe or script)  [Enter to cancel]: ");
                string p = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(p)) { Console.WriteLine("cancelled."); return; }
                exe = p.Trim().Trim('"');

                string defName = Path.GetFileNameWithoutExtension(exe);
                Console.Write("2) name [" + defName + "]: ");
                p = Console.ReadLine();
                name = string.IsNullOrWhiteSpace(p) ? defName : p.Trim();

                Console.Write("3) window title keyword [empty = hide ALL windows]: ");
                p = Console.ReadLine();
                title = p.Trim();

                // auto-detect script extension
                bool isScript = IsScriptExt(exe);
                if (isScript)
                {
                    var cfg0 = Core.Load();
                    var rDef = Launchers.ResolveFor(exe, null, null, cfg0.extLaunchers);
                    string autoL = rDef != null ? rDef.Id : "(none known)";
                    Console.Write("4) launcher [" + autoL + "]: ");
                    p = Console.ReadLine();
                    launcher = string.IsNullOrWhiteSpace(p) ? autoL : p.Trim();
                    if (launcher == "(none known)") launcher = "";
                }

                // interactive mode: no default-poll-time question — app items default to
                // 30s in the engine; script items are launch-only (no window hiding).
                Console.WriteLine();
            }

            // ---- resolve launcher using learned map + built-ins + explicit host ----
            bool isScriptItem = IsScriptExt(exe);
            if (isScriptItem && hasWindow)
            {
                Console.WriteLine("[warn] --time is ignored for script items (launch-only, no window hiding)");
                window = 0;
            }
            var cfg = Core.Load();
            var it = new Item(); // shared item object

            // target must exist
            if (!File.Exists(exe))
            {
                Console.WriteLine("[error] target not found, NOT added: " + exe);
                return;
            }

            if (isScriptItem)
            {
                var resolved = Launchers.ResolveFor(exe, launcher, null, cfg.extLaunchers);
                if (resolved == null)
                {
                    Console.WriteLine("[error] no launcher/interpreter available for '" + exe + "', NOT added");
                    Console.WriteLine("  built-in: " + Launchers.KnownList().Replace("\n", "\n  "));
                    if (cfg.extLaunchers.Count > 0)
                    {
                        Console.WriteLine("  learned:");
                        foreach (var e in cfg.extLaunchers)
                            if (e != null) Console.WriteLine("    " + e.ext + " -> " + e.id + " (" + e.host + ")");
                    }
                    Console.WriteLine("  specify one with:  --launcher <id>");
                    Console.WriteLine("  or define a new mapping first:  am launchers learn <ext> <id> <hostExe>");
                    Console.WriteLine("  or install a matching interpreter and retry.");
                    return;
                }

                // verify host exe exists
                if (!string.IsNullOrEmpty(resolved.Host) && !File.Exists(resolved.Host)
                    && !resolved.Host.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("[warn] host '" + resolved.Host + "' not found on disk (added anyway)");
                }
                else if (!string.IsNullOrEmpty(resolved.Host) && resolved.Host.IndexOf('\\') >= 0 && !File.Exists(resolved.Host))
                {
                    Console.WriteLine("[error] host not found: " + resolved.Host + ", NOT added");
                    return;
                }

                it.script = true;
                it.launcher = resolved.Id;
                it.hostExe = resolved.Host;
                it.hostArgs = resolved.ArgsTemplate;
                it.processName = resolved.Proc;
            }
            else
            {
                it.script = false;
                it.processName = Path.GetFileNameWithoutExtension(exe);
            }

            if (name == null) name = Path.GetFileNameWithoutExtension(exe);
            it.name = name;
            it.exe = exe;
            it.windowTitle = title == null ? "" : title;
            it.silentWindowMs = isScriptItem ? 0 : window; // scripts: launch-only, no poll-time
            it.enabled = true;

            // dedupe by TARGET PATH first, then NAME
            int idx = -1;
            string norm = System.IO.Path.GetFullPath(exe).ToLowerInvariant();
            for (int i = 0; i < cfg.items.Count; i++)
            {
                string existing = System.IO.Path.GetFullPath(cfg.items[i].exe).ToLowerInvariant();
                if (existing == norm) { idx = i; break; }
            }
            if (idx < 0)
                for (int i = 0; i < cfg.items.Count; i++)
                    if (string.Equals(cfg.items[i].name, name, System.StringComparison.OrdinalIgnoreCase)) { idx = i; break; }

            string mode;
            if (idx >= 0) { mode = "updated"; cfg.items[idx] = it; }
            else { mode = "added"; cfg.items.Add(it); }
            Core.Save(cfg);
            string extra = isScriptItem
                ? "  (host=" + it.hostExe + ", launch-only, no window hiding)"
                : "  (silent=" + SilentLabel(it.silentWindowMs) + ")";
            Core.Log("cli " + mode + " '" + name + "' -> " + exe + " " + extra);
            Console.WriteLine(mode + " '" + name + "' -> " + exe + "  " + extra);
            Console.WriteLine("Takes effect at next logon (or run 'am stop && am run' now).");
        }

        // detect known script extensions
        static bool IsScriptExt(string path)
        {
            string ext = Path.GetExtension(path);
            ext = ext == null ? "" : ext.ToLowerInvariant();
            switch (ext)
            {
                case ".ahk": case ".ps1": case ".psm1": case ".psd1":
                case ".bat": case ".cmd": case ".vbs": case ".js": case ".jse":
                case ".py": case ".pyw": case ".lua": case ".pl":
                case ".sh": case ".rb":
                    return true;
                default: return false;
            }
        }

        // accept "10" / "10s" / "10000"; fallback to defMs on parse failure
        static int ParseSec(string s, int defMs)
        {
            s = s.Trim().ToLowerInvariant();
            int ms;
            if (s.EndsWith("s") && !s.EndsWith("ms"))
            {
                int v; if (int.TryParse(s.Substring(0, s.Length - 1), out v)) ms = v * 1000; else return defMs;
            }
            else if (s.EndsWith("ms"))
            {
                int v; if (int.TryParse(s.Substring(0, s.Length - 2), out v)) ms = v; else return defMs;
            }
            else
            {
                int v; if (int.TryParse(s, out v)) ms = (v < 1000) ? v * 1000 : v; else return defMs;
            }
            return ms;
        }

        // ---------- remove ----------
        static void DoRemove(string[] a)
        {
            string nm = a.Length >= 2 ? a[1] : null;
            if (nm == null) { Console.WriteLine("usage: am remove <name>"); return; }
            var cfg = Core.Load();
            int idx = -1;
            for (int i = 0; i < cfg.items.Count; i++)
                if (string.Equals(cfg.items[i].name, nm, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
            if (idx < 0) { Console.WriteLine("no item named '" + nm + "'"); return; }
            var removed = cfg.items[idx];
            cfg.items.RemoveAt(idx);
            Core.Save(cfg);
            Core.Log("cli remove '" + nm + "'");
            Console.WriteLine("removed '" + nm + "' (" + removed.exe + ")");
            Console.WriteLine("Takes effect at next logon (the running engine already did its pass).");
        }

        // ---------- list: configured items split into APPS / SCRIPTS, with live state ----------
        static void DoList()
        {
            var cfg = Core.Load();
            if (cfg.items.Count == 0) { Console.WriteLine("(no items configured. use: am add <exePath>  or  just 'am add')"); return; }

            var appRows = new List<string[]>();
            var scriptRows = new List<string[]>();
            var notes = new List<string>();
            foreach (var it in cfg.items)
            {
                int procs = Core.ProcCount(it.processName);
                string running = procs > 0 ? procs + " proc" : "-";
                if (it.script)
                {
                    string st = it.enabled ? (procs > 0 ? "RUNNING" : "not-started") : "off";
                    scriptRows.Add(new string[] { it.name, running, it.launcher == "" ? "auto" : it.launcher, st, it.exe });
                }
                else
                {
                    var wins = Core.CollectWindows(it.processName, it.windowTitle);
                    int vis = 0, hid = 0;
                    foreach (var h in wins) { if (P.IsWindowVisible(h)) vis++; else hid++; }
                    string win = "(" + vis + "/" + hid + ")";
                    string st = it.enabled ? (hid > 0 ? "HIDDEN" : (vis > 0 ? "visible" : "no-window")) : "off";
                    appRows.Add(new string[] { it.name, running, win, SilentLabel(it.silentWindowMs), st, it.exe });
                    if (!string.IsNullOrEmpty(it.windowTitle))
                        notes.Add(it.name + " hides only windows whose title contains \"" + it.windowTitle + "\"");
                }
            }

            Console.WriteLine("APPS (launch + hide windows)");
            if (appRows.Count == 0) Console.WriteLine("  (none)");
            else
            {
                PrintTable(new[] { "NAME", "RUNNING", "WINDOWS(v/h)", "MONITOR", "STATE", "TARGET" }, appRows);
                Console.WriteLine("  MONITOR = poll-hide window after logon (default 30s; per-item, set via --time).");
            }
            Console.WriteLine();
            Console.WriteLine("SCRIPTS (launch-only at logon, no window hiding)");
            if (scriptRows.Count == 0) Console.WriteLine("  (none)");
            else
                PrintTable(new[] { "NAME", "RUNNING", "LAUNCHER", "STATE", "TARGET" }, scriptRows);
            Console.WriteLine();
            foreach (var t in notes) Console.WriteLine("  " + t);
            int e = Core.EnginePid();
            Console.WriteLine(string.Format("  {0} item(s). engine: {1}   config: {2}",
                cfg.items.Count, e > 0 ? "running (pid " + e + ")" : "not running", Core.CfgPath));
        }

        // ---------- show ----------
        static void DoShow(string[] a)
        {
            string nm = a.Length >= 2 ? a[1] : null;
            if (nm == null) { Console.WriteLine("usage: am show <name>"); return; }
            var cfg = Core.Load();
            var it = Core.FindItem(cfg, nm);
            if (it == null) { Console.WriteLine("no item named '" + nm + "'"); return; }
            int n = Core.ShowWindows(it.processName, it.windowTitle);
            Core.Log("cli show '" + nm + "': restored " + n + " window(s)");
            Console.WriteLine("restored " + n + " window(s) for '" + nm + "'");
        }

        // ---------- start ----------
        static void DoStart()
        {
            Core.Log("cli start (one-shot)");
            Core.DoPass(Core.Load());
            Console.WriteLine("one-shot pass complete. 'am list' to verify.");
        }

        // ---------- run ----------
        static void DoRun()
        {
            int existing = Core.EnginePid();
            if (existing > 0)
            {
                Console.WriteLine("engine already running (pid " + existing + "); not starting another");
                return;
            }
            string exe = Core.EngineExe();
            if (!File.Exists(exe))
            {
                Console.WriteLine("engine not found: " + exe);
                Console.WriteLine("run build.ps1 first (or place am-engine.exe next to am.exe)");
                return;
            }
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(exe)
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForInputIdle(2000);
                    Core.Log("cli run: spawned engine pid=" + p.Id);
                    Console.WriteLine("engine started (pid " + p.Id + "); silent window in progress, CLI exiting");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("failed to start engine: " + ex.Message);
            }
        }

        // ---------- stop ----------
        static void DoStop()
        {
            string msg = "";
            bool killed = false;
            try
            {
                int pid = Core.EnginePid();
                if (pid > 0)
                {
                    Process.GetProcessById(pid).Kill();
                    killed = true;
                    msg = "stopped engine pid " + pid;
                }
            }
            catch { }
            try
            {
                foreach (var p in Process.GetProcessesByName(Core.EngineProcName))
                {
                    try { p.Kill(); killed = true; msg += " killed-by-name " + p.Id; } catch { }
                }
            }
            catch { }
            if (!killed && msg == "") msg = "engine not running";
            try { Directory.CreateDirectory(Path.GetDirectoryName(Core.StopPath)); File.WriteAllText(Core.StopPath, DateTime.Now + "  " + msg + "\n"); } catch { }
            Core.Log("cli stop: " + msg);
            Console.WriteLine("stop: " + msg);
        }

        // ---------- launchers ----------
        static void DoLaunchers(string[] a)
        {
            string sub = a.Length >= 2 ? a[1].ToLowerInvariant() : "list";
            var cfg = Core.Load();

            if (sub == "list" || sub == "")
            {
                Console.WriteLine("Built-in launchers:");
                foreach (var line in Launchers.KnownList().Split('\n'))
                    Console.WriteLine(line);
                Console.WriteLine();
                if (cfg.extLaunchers.Count > 0)
                {
                    Console.WriteLine("Learned mappings:");
                    var rows = new List<string[]>();
                    foreach (var e in cfg.extLaunchers)
                        if (e != null) rows.Add(new string[] { e.ext, e.id, e.host, e.args, e.proc });
                    PrintTable(new[] { "EXT", "ID", "HOST", "ARGS", "PROC" }, rows);
                }
                else
                {
                    Console.WriteLine("Learned mappings: (none yet)");
                }
                Console.WriteLine();
                Console.WriteLine("To learn a new mapping:");
                Console.WriteLine("  am launchers learn .lua luajit \"C:\\Tools\\luajit.exe\"");
                Console.WriteLine("  (next time you 'am add' a .lua, it will auto-match without --launcher)");
                return;
            }

            if (sub == "forget")
            {
                string ext = a.Length >= 3 ? a[2].ToLowerInvariant() : null;
                if (ext == null) { Console.WriteLine("usage: am launchers forget <ext>  e.g. am launchers forget .lua"); return; }
                if (!ext.StartsWith(".")) ext = "." + ext;
                if (Launchers.FindByExt(ext, cfg.extLaunchers) != null)
                {
                    Launchers.Forget(ext, cfg.extLaunchers);
                    Core.Save(cfg);
                    Console.WriteLine("forgotten " + ext);
                }
                else Console.WriteLine(ext + " not in learned map (or is built-in; cannot forget built-in)");
                return;
            }

            // am launchers learn <ext> <id> <host> [args] [proc]
            if (sub == "learn")
            {
                if (a.Length < 5)
                {
                    Console.WriteLine("usage: am launchers learn <ext> <id> <host> [args] [proc]");
                    Console.WriteLine("  ext   = .lua (with dot)");
                    Console.WriteLine("  id    = name to display / use with --launcher");
                    Console.WriteLine("  host  = full path to interpreter exe");
                    Console.WriteLine("  args  = optional args template ({script} = target path), default '{script}'");
                    Console.WriteLine("  proc  = optional process name to track, default = id");
                    return;
                }
                string ext = a[2].StartsWith(".") ? a[2] : "." + a[2];
                string id = a[3];
                string hostPath = a[4];
                string args = a.Length >= 6 ? a[5] : "{script}";
                string proc = a.Length >= 7 ? a[6] : id;
                if (!File.Exists(hostPath))
                {
                    Console.WriteLine("[error] host not found: " + hostPath);
                    return;
                }
                Launchers.Learn(ext, id, hostPath, args, proc, cfg.extLaunchers);
                Core.Save(cfg);
                Core.Log("cli launchers learn " + ext + " -> " + id + " (" + hostPath + ")");
                Console.WriteLine("learned: " + ext + " -> " + id + " (host=" + hostPath + ", proc=" + proc + ")");
                Console.WriteLine("Next time you am add a " + ext + " file, it will auto-match this launcher.");
                return;
            }

            Console.WriteLine("usage: am launchers [list|learn|forget]");
        }

        // ---------- help ----------
        static void Help()
        {
            Console.WriteLine("am — AppManager V7 CLI (console). Engine = am-engine.exe (no window).");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  scan [keyword]   list installed programs (registry Uninstall) with guessed exe");
            Console.WriteLine("  add <exe|script> [--name n] [--title t] [--time 15s|30|5000ms] [--launcher L]");
            Console.WriteLine("                   add/update a managed item.");
            Console.WriteLine("                   L = host/interpreter name (powershell, cmd, wscript, autohotkey, python, or a learned id); auto-picked by extension when omitted");
            Console.WriteLine("                   duplicate target path updates in place; broken config is rejected, not saved");
            Console.WriteLine("  add             interactive mode: ask path, name, title, launcher (if script)");
            Console.WriteLine("  remove <name>    remove a managed item");
            Console.WriteLine("  list             all managed items with live state: APPS (running, windows, monitor, state) + SCRIPTS (running, launcher, state) + engine");
            Console.WriteLine("  show <name>       restore a hidden window (apps only)");
            Console.WriteLine("  start             one-shot pass (start-if-needed + hide), then exit");
            Console.WriteLine("  run               spawn the resident engine (detached), then CLI exits");
            Console.WriteLine("  stop              stop the engine");
            Console.WriteLine("  launchers [list|learn|forget]   view / add / remove extension→interpreter mappings");
            Console.WriteLine();
            Console.WriteLine("Apps (.exe): launched + window hidden. Scripts (.ahk/.ps1/.bat/.vbs/.py/...): launch-only via host, no window hiding.");
            Console.WriteLine("MONITOR window: one fixed poll-hide time per app item (default 30s); scripts are launch-only.");
            Console.WriteLine("Config: " + Core.CfgPath);
            Console.WriteLine("Log:    " + Core.LogPath);
        }
    }
}
