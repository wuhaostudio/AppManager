using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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
                case "update": DoUpdate(args); break;
                case "remove": DoRemove(args); break;
                case "list": DoList(); break;
                case "start": DoStart(args); break;
                case "run": DoRun(); break;
                case "stop": DoStop(); break;
                case "launchers": DoLaunchers(args); break;
                case "hotkey": DoHotkey(args); break;
                default: Help(); break;
            }
            return 0;
        }

        // ---------- table rendering: delegates to the shared Style renderer
        // (colour + CJK-aware padding; colours vanish when output is redirected)
        static void PrintTable(string[] headers, List<string[]> rows)
        {
            Style.Table(headers, rows);
        }

        static void PrintTable(string[] headers, List<string[]> rows, Func<int, int, ConsoleColor?> cellColor)
        {
            Style.Table(headers, rows, null, cellColor);
        }

        // STATE / AT LOGON cells: green = as intended, yellow = look here, grey = idle
        static ConsoleColor? StateColor(string st)
        {
            if (st == "HIDDEN" || st == "RUNNING") return ConsoleColor.Green;
            if (st == "visible") return ConsoleColor.Yellow;
            if (st == "off" || st == "no-window" || st == "not-started") return ConsoleColor.DarkGray;
            return null;
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

            Style.Info(string.Format("Found {0} installed program(s){1}. Filter='{2}'",
                filtered.Count, filtered.Count == 1 ? "" : "s", kw));
            var table = new List<string[]>();
            foreach (var r in filtered) table.Add(new string[] { r[0], r[1] });
            PrintTable(new[] { "PROGRAM", "EXE" }, table);
            Console.WriteLine();
            Style.Dim("To manage one:  am add <exePath>   (or just 'am add' for interactive)");
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
            bool autostart = true;
            for (int i = 1; i < a.Length; i++)
            {
                string t = a[i].ToLowerInvariant();
                if (t == "--name") { if (i + 1 < a.Length) name = a[++i]; }
                else if (t == "--title") { if (i + 1 < a.Length) title = a[++i]; }
                else if (t == "--time") { if (i + 1 < a.Length) { window = ParseSec(a[++i], 30000); hasWindow = true; } }
                else if (t == "--launcher") { if (i + 1 < a.Length) launcher = a[++i]; }
                else if (t == "--no-autostart") autostart = false;
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

                // logon auto-start question (last). Default yes, so pressing
                // Enter keeps the classic behaviour; "n" registers the item as
                // on-demand (listed + in the picker, never started at logon).
                Console.Write((isScript ? "5" : "4") + ") start it silently at logon? [Y/n]: ");
                p = Console.ReadLine();
                string ap = p.Trim().ToLowerInvariant();
                autostart = !(ap == "n" || ap == "no");

                // interactive mode: no default-poll-time question — app items default to
                // 30s in the engine; script items are launch-only (no window hiding).
                Console.WriteLine();
            }

            // ---- resolve launcher using learned map + built-ins + explicit host ----
            bool isScriptItem = IsScriptExt(exe);
            if (isScriptItem && hasWindow)
            {
                Style.Warn("[warn] --time is ignored for script items (launch-only, no window hiding)");
                window = 0;
            }
            var cfg = Core.Load();
            var it = new Item(); // shared item object

            // target must exist
            if (!File.Exists(exe))
            {
                Style.Err("[error] target not found, NOT added: " + exe);
                return;
            }

            if (isScriptItem)
            {
                var resolved = Launchers.ResolveFor(exe, launcher, null, cfg.extLaunchers);
                if (resolved == null)
                {
                    Style.Err("[error] no launcher/interpreter available for '" + exe + "', NOT added");
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
                    Style.Warn("[warn] host '" + resolved.Host + "' not found on disk (added anyway)");
                }
                else if (!string.IsNullOrEmpty(resolved.Host) && resolved.Host.IndexOf('\\') >= 0 && !File.Exists(resolved.Host))
                {
                    Style.Err("[error] host not found: " + resolved.Host + ", NOT added");
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
            it.autostart = autostart;

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
            string logon = it.autostart ? "" : ", logon=on-demand";
            string extra = isScriptItem
                ? "  (host=" + it.hostExe + ", launch-only, no window hiding" + logon + ")"
                : "  (silent=" + SilentLabel(it.silentWindowMs) + logon + ")";
            Core.Log("cli " + mode + " '" + name + "' -> " + exe + " " + extra);
            Style.Ok(mode + " '" + name + "' -> " + exe + "  " + extra);
            if (it.autostart)
                Style.Dim("Takes effect at next logon (or run 'am stop && am run' now).");
            else
            {
                Style.Warn("On-demand item: the logon pass never starts it and never hides its windows.");
                Style.Dim("Start it from the hotkey picker (Enter) or with 'am start " + name + "'.");
            }
        }

        // detect known script extensions (shared with the `am update` table editor)
        internal static bool IsScriptExt(string path)
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
            if (nm == null) { Style.Warn("usage: am remove <name>"); return; }
            var cfg = Core.Load();
            int idx = -1;
            for (int i = 0; i < cfg.items.Count; i++)
                if (string.Equals(cfg.items[i].name, nm, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
            if (idx < 0) { Style.Err("no item named '" + nm + "'"); return; }
            var removed = cfg.items[idx];
            cfg.items.RemoveAt(idx);
            Core.Save(cfg);
            Core.Log("cli remove '" + nm + "'");
            Style.Ok("removed '" + nm + "' (" + removed.exe + ")");
            Style.Dim("Takes effect at next logon (the running engine already did its pass).");
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
                    scriptRows.Add(new string[] { it.name, running, it.launcher == "" ? "auto" : it.launcher, it.autostart ? "silent" : "on-demand", st, it.exe });
                }
                else
                {
                    var wins = Core.CollectAppWindows(it.processName, it.windowTitle);
                    int vis = 0, hid = 0;
                    foreach (var h in wins) { if (P.IsWindowVisible(h)) vis++; else hid++; }
                    string win = "(" + vis + "/" + hid + ")";
                    string st = it.enabled ? (hid > 0 ? "HIDDEN" : (vis > 0 ? "visible" : "no-window")) : "off";
                    appRows.Add(new string[] { it.name, running, win, SilentLabel(it.silentWindowMs), it.autostart ? "silent" : "on-demand", st, it.exe });
                    if (!string.IsNullOrEmpty(it.windowTitle))
                        notes.Add(it.name + " hides only windows whose title contains \"" + it.windowTitle + "\"");
                }
            }

            Console.WriteLine("APPS (launch + hide windows)");
            if (appRows.Count == 0) Console.WriteLine("  (none)");
            else
            {
                Func<int, int, ConsoleColor?> appColors = delegate(int r, int c)
                {
                    if (c == 4) return appRows[r][4] == "on-demand" ? ConsoleColor.Yellow : (ConsoleColor?)null;
                    if (c == 5) return StateColor(appRows[r][5]);
                    if (c == 1) return appRows[r][1] == "-" ? ConsoleColor.DarkGray : (ConsoleColor?)null;
                    return null;
                };
                PrintTable(new[] { "NAME", "RUNNING", "WINDOWS(v/h)", "MONITOR", "AT LOGON", "STATE", "TARGET" }, appRows, appColors);
                Style.Dim("  MONITOR = poll-hide window after logon (default 30s; per-item, set via --time).");
            }
            Console.WriteLine();
            Console.WriteLine("SCRIPTS (launch-only at logon, no window hiding)");
            if (scriptRows.Count == 0) Console.WriteLine("  (none)");
            else
            {
                Func<int, int, ConsoleColor?> scriptColors = delegate(int r, int c)
                {
                    if (c == 3) return scriptRows[r][3] == "on-demand" ? ConsoleColor.Yellow : (ConsoleColor?)null;
                    if (c == 4) return StateColor(scriptRows[r][4]);
                    if (c == 1) return scriptRows[r][1] == "-" ? ConsoleColor.DarkGray : (ConsoleColor?)null;
                    return null;
                };
                PrintTable(new[] { "NAME", "RUNNING", "LAUNCHER", "AT LOGON", "STATE", "TARGET" }, scriptRows, scriptColors);
            }
            Console.WriteLine();
            foreach (var t in notes) Style.Dim("  " + t);
            bool anyOnDemand = false;
            foreach (var it in cfg.items) if (it.enabled && !it.autostart) anyOnDemand = true;
            if (anyOnDemand)
            {
                Style.Dim("  AT LOGON=silent: the engine pass starts it at logon and hides its windows.");
                Style.Warn("  AT LOGON=on-demand: registered only — never started by the pass;");
                Style.Warn("    launch it from the hotkey picker (Enter) or with 'am start <name>'.");
            }
            int e = Core.EnginePid();
            string hk = Hotkey.Parse(cfg.hotkey).Valid ? cfg.hotkey : "";
            Console.Write(string.Format("  {0} item(s). engine: ", cfg.items.Count));
            Style.Write(e > 0 ? "running (pid " + e + ")" : "not running", e > 0 ? ConsoleColor.Green : ConsoleColor.DarkGray);
            if (e <= 0 && Core.StopRequested())
                Style.Warn("   stopped by request ('am run' resumes; the keep-alive watchdog stays off until then)");
            Style.Dim("   config: " + Core.CfgPath);
            if (hk == "") Style.Dim("  HOTKEY: (none) — set one with 'am hotkey'");
            else
            {
                Console.Write("  HOTKEY: ");
                Style.Write(hk, ConsoleColor.Cyan);
                Style.Dim("  (global app-picker shortcut; effective from next engine start)");
                Style.Dim("  (press " + hk + " -> semi-transparent picker window: arrows select, Enter opens, Esc closes)");
            }
        }

        // ---------- update: interactive config table ----------
        // `am update`         -> every item (apps + scripts) in an editable table
        // `am update <name>`  -> just that item
        static void DoUpdate(string[] a)
        {
            string nm = a.Length >= 2 ? a[1] : null;
            var cfg = Core.Load();
            if (nm != null && Core.FindItem(cfg, nm) == null)
            {
                Style.Err("no item named '" + nm + "'");
                return;
            }
            Core.Log("cli update" + (nm != null ? " '" + nm + "'" : "") + " (table editor)");
            UpdateUi.Run(cfg, nm);
        }

        // ---------- start ----------
        // `am start`         -> one-shot pass, same semantics as logon (on-demand items untouched)
        // `am start <name>`  -> start that item if needed, then bring its main window back —
        //                       exactly what Enter does in the hotkey picker (absorbed `am show`)
        static void DoStart(string[] a)
        {
            string nm = a.Length >= 2 ? a[1] : null;
            if (nm != null) { DoStartOne(nm); return; }

            Core.Log("cli start (one-shot)");
            Core.DoPass(Core.Load());
            Style.Ok("one-shot pass complete. 'am list' to verify.");
            Style.Dim("(on-demand items are not part of this pass — start one with 'am start <name>')");
        }

        static void DoStartOne(string nm)
        {
            var cfg = Core.Load();
            var it = Core.FindItem(cfg, nm);
            if (it == null) { Style.Err("no item named '" + nm + "'"); return; }
            if (!it.enabled) Style.Warn("(note: '" + it.name + "' is disabled in config)");

            bool started = false;
            if (Core.ProcCount(it.processName) == 0)
            {
                started = Core.StartApp(it);
                if (!started) { Style.Err("failed to start '" + it.name + "' (target/host missing? see am.log)"); return; }
            }

            if (it.script)
            {
                Core.Log("cli start '" + it.name + "': launched=" + started + " (script, launch-only)");
                Style.Ok((started ? "started '" : "already running '") + it.name + "' (script, launch-only; no window to restore)");
                return;
            }

            // cold starts need a moment before their main window exists
            int n = 0;
            for (int i = 0; i < 20 && n == 0; i++)
            {
                n = Core.ShowWindows(it.processName, it.windowTitle);
                if (n == 0) Thread.Sleep(250);
            }
            Core.Log("cli start '" + it.name + "': started=" + started + " restored=" + n);
            if (n > 0)
                Style.Ok((started ? "started '" : "restored '") + it.name + "' -> " + it.exe);
            else
                Style.Warn("'" + it.name + "' is running but has no main window yet; try again in a moment");
        }

        // ---------- run ----------
        static void DoRun()
        {
            int existing = Core.EnginePid();
            if (existing > 0)
            {
                Style.Warn("engine already running (pid " + existing + "); not starting another");
                return;
            }
            string exe = Core.EngineExe();
            if (!File.Exists(exe))
            {
                Style.Err("engine not found: " + exe);
                Style.Dim("run build.ps1 first (or place am-engine.exe next to am.exe)");
                return;
            }

            // an explicit start overrides an earlier stop: the keep-alive
            // watchdog may revive the engine again from here on
            Core.ClearStopRequest();

            // Prefer the installed logon task as the parent. An engine spawned
            // directly by this CLI is a child of whatever shell/agent started
            // us: if that process tree is torn down (a job-object kill), the
            // engine dies with it — silently, leaving the hotkey dead with no
            // log line. Started through the task, the engine's parent is the
            // Task Scheduler service, so it survives the CLI and its caller.
            if (StartViaTask())
            {
                int pid = WaitEnginePid(8000);
                if (pid > 0)
                {
                    Core.Log("cli run: started engine pid=" + pid + " via task '" + TaskName + "'");
                    Style.Ok("engine started (pid " + pid + ", via task '" + TaskName + "'); silent window in progress, CLI exiting");
                    return;
                }
                Style.Dim("task '" + TaskName + "' did not produce an engine within 8s; starting directly");
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
                    Style.Ok("engine started (pid " + p.Id + "); silent window in progress, CLI exiting");
                }
            }
            catch (Exception ex)
            {
                Style.Err("failed to start engine: " + ex.Message);
            }
        }

        const string TaskName = "AppManager";

        // ask Task Scheduler to run the installed task; false when the task is
        // missing (portable use) or the request fails
        static bool StartViaTask()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/run /tn \"" + TaskName + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(5000);
                    if (p.ExitCode == 0) return true;
                    Core.Log("cli run: task start refused (" + outp.Trim() + ")");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Core.Log("cli run: task start failed: " + ex.Message);
                return false;
            }
        }

        // the engine writes am-engine.pid at startup: poll for it
        static int WaitEnginePid(int timeoutMs)
        {
            for (int waited = 0; waited < timeoutMs; waited += 250)
            {
                int pid = Core.EnginePid();
                if (pid > 0) return pid;
                Thread.Sleep(250);
            }
            return 0;
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
            if (killed) Style.Ok("stop: " + msg); else Style.Dim("stop: " + msg);
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

        // ---------- hotkey: capture the global AppManager hotkey ----------
        // Interactive capture: the user presses Enter to arm a round, then the
        // hotkey combo anywhere on the keyboard; the round is confirmed with a
        // second Enter. Two rounds must agree before config.json is written.
        // No keyboard hooks: we poll GetAsyncKeyState and watch for the first
        // up->down transition of an allowed key while snapshotting modifiers.
        static void DoHotkey(string[] a)
        {
            string sub = a.Length >= 2 ? a[1].ToLowerInvariant() : "set";
            if (sub == "clear")
            {
                var cfg = Core.Load();
                string old = cfg.hotkey;
                cfg.hotkey = "";
                Core.Save(cfg);
                Core.Log("cli hotkey clear" + (old != "" ? " (was " + old + ")" : ""));
                Console.WriteLine(old == "" ? "no hotkey was configured" : "cleared hotkey '" + old + "'");
                Console.WriteLine("Takes effect at next engine start (am stop && am run).");
                return;
            }
            if (sub != "set")
            {
                Console.WriteLine("usage: am hotkey [set|clear]");
                Console.WriteLine("  set    interactive capture (press the combo on your keyboard, two verifications)");
                Console.WriteLine("  clear  remove the configured hotkey");
                return;
            }
            RunHotkeyCapture();
        }

        static void RunHotkeyCapture()
        {
            var current = Core.Load();

            Console.WriteLine();
            Console.WriteLine("=== hotkey capture (round 1 of 2) ===");
            if (!string.IsNullOrEmpty(current.hotkey))
                Console.WriteLine("current hotkey: " + current.hotkey + " (will be replaced on success)");
            Console.WriteLine("1) Press ENTER to start capturing");
            Console.Write("   >> ");
            Console.ReadLine();
            Console.WriteLine("   Now press the desired hotkey combo on the keyboard (modifiers + one key, e.g. Ctrl+0).");
            Console.WriteLine("   Allowed keys: letters A-Z, digits 0-9, F1-F24. Esc cancels the round. 60s timeout.");
            var r1 = CaptureKeyCombo();
            if (r1 == null) { Console.WriteLine("round 1 cancelled; nothing written."); return; }
            Console.WriteLine("   captured: " + r1.Label);
            Console.Write("   2) Press ENTER to confirm this combo: ");
            Console.ReadLine();

            Console.WriteLine();
            Console.WriteLine("=== hotkey capture (round 2 of 2 — must match round 1) ===");
            Console.WriteLine("   match target: " + r1.Label);
            Console.Write("   1) Press ENTER to start capturing: ");
            Console.ReadLine();
            Console.WriteLine("   Now press the SAME combo again (e.g. " + r1.Label + "). Esc cancels the round.");
            var r2 = CaptureKeyCombo();
            if (r2 == null) { Console.WriteLine("round 2 cancelled; nothing written."); return; }
            Console.WriteLine("   captured: " + r2.Label);
            if (r2.Label != r1.Label)
            {
                Console.WriteLine("rounds disagree (" + r1.Label + " vs " + r2.Label + ") — nothing written.");
                Console.WriteLine("retry with: am hotkey");
                return;
            }
            Console.Write("   2) Press ENTER to confirm: ");
            Console.ReadLine();

            var cfg = Core.Load();
            cfg.hotkey = r1.Label;
            Core.Save(cfg);
            Core.Log("cli hotkey set " + r1.Label);
            Console.WriteLine("hotkey set to " + r1.Label);
            Console.WriteLine("Takes effect at next engine start (am stop && am run).");
            Console.WriteLine("The engine will listen with " + r1.Label + " and open the app picker window.");
        }

        // Polls the global keyboard state (~5ms cadence) for the first
        // up->down transition of an allowed key, snapshotting the modifier
        // state at that instant. Edge detection is previous-vs-current state
        // per key (GetAsyncKeyState's transition bit is unreliable). Esc
        // cancels; 60s timeout. A bare letter/digit is rejected: a global
        // hotkey must carry a modifier (or be a bare F-key).
        static Hotkey.Combo CaptureKeyCombo()
        {
            int[] keys = new int[10 + 26 + 24]; // 0-9, A-Z, F1-F24
            int n = 0;
            for (int c = '0'; c <= '9'; c++) keys[n++] = c;
            for (int c = 'A'; c <= 'Z'; c++) keys[n++] = c;
            for (int f = 0x70; f <= 0x87; f++) keys[n++] = f;

            short[] prev = new short[keys.Length];
            for (int i = 0; i < prev.Length; i++) prev[i] = KeyNative.GetAsyncKeyState(keys[i]);
            prevEsc = (KeyNative.GetAsyncKeyState(0x1B) & 0x8000) != 0 ? 1 : 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 60000)
            {
                // Esc cancels
                if ((KeyNative.GetAsyncKeyState(0x1B) & 0x8000) != 0 && prevEsc == 0)
                {
                    Console.WriteLine("   Esc pressed - round cancelled.");
                    FlushConsoleInput();
                    return null;
                }
                prevEsc = (KeyNative.GetAsyncKeyState(0x1B) & 0x8000) != 0 ? 1 : 0;

                for (int i = 0; i < keys.Length; i++)
                {
                    short cur = KeyNative.GetAsyncKeyState(keys[i]);
                    bool downNow = (cur & 0x8000) != 0;
                    bool wasDown = (prev[i] & 0x8000) != 0;
                    if (downNow && !wasDown)
                    {
                        int mods = ModSnapshot();
                        var combo = Hotkey.Parse(Hotkey.FormatCombo(mods, keys[i]));
                        bool isF = keys[i] >= 0x70 && keys[i] <= 0x87;
                        if (combo.Valid && (mods != 0 || isF))
                        {
                            FlushConsoleInput();
                            return combo;
                        }
                        // bare letter/digit without modifier: reject, keep capturing
                        Console.WriteLine("   '" + Hotkey.KeyLabel(keys[i]) + "' without a modifier is not a usable global hotkey.");
                        Console.WriteLine("   Use modifier+key (e.g. Ctrl+0) or a bare F-key. Esc cancels. Press the combo again:");
                        prev[i] = 0; // consume the press; wait for the next one
                    }
                    prev[i] = cur;
                }
                Thread.Sleep(5);
            }
            Console.WriteLine("   60s timeout - no key captured; round cancelled.");
            return null;
        }

        static int prevEsc;

        // Modifier snapshot at capture time. The VK/MOD mapping lives in
        // Hotkey.ModifiersDown (src/shared) so it stays next to the constants
        // and can be exercised directly: VK_CONTROL (0x11) -> MOD_CONTROL,
        // VK_MENU/Alt (0x12) -> MOD_ALT.
        static int ModSnapshot()
        {
            return Hotkey.ModifiersDown();
        }

        // drain any characters the capture keys buffered into the console, so
        // the following "press ENTER" prompt is not pre-filled
        static void FlushConsoleInput()
        {
            try
            {
                while (Console.KeyAvailable)
                {
                    try { Console.ReadKey(true); }
                    catch { break; }
                }
            }
            catch { }
        }

        static class KeyNative
        {
            [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        }

        // ---------- help ----------
        static void Help()
        {
            Style.Info("am — AppManager V7 CLI (console). Engine = am-engine.exe (no window).");
            Console.WriteLine();
            Style.Info("Commands:");
            Style.Info("  -- manage (config) --");
            Console.WriteLine("  scan [keyword]   list installed programs (registry Uninstall) with guessed exe");
            Console.WriteLine("  add <exe|script> [--name n] [--title t] [--time 15s|30|5000ms] [--launcher L] [--no-autostart]");
            Console.WriteLine("                   add a managed item (same target path updates it in place).");
            Console.WriteLine("                   L = host/interpreter name (powershell, cmd, wscript, autohotkey, python, or a learned id); auto-picked by extension when omitted");
            Console.WriteLine("                   --no-autostart = register it as ON-DEMAND: listed in 'am list' and the hotkey picker,");
            Console.WriteLine("                   but never started (and never window-hidden) by the logon pass");
            Console.WriteLine("  add             interactive mode: ask path, name, title, launcher (if script), logon auto-start");
            Console.WriteLine("  update [name]    config editor, two levels: pick an item from the list (↑↓, Enter), then edit");
            Console.WriteLine("                   its fields (↑↓ pick a field, Enter edits — inline line editor for text fields,");
            Console.WriteLine("                   option list for logon auto-start / enabled / launcher).");
            Console.WriteLine("                   Esc always steps one level back, q quits; every accepted edit is saved immediately");
            Console.WriteLine("  remove <name>    remove a managed item");
            Console.WriteLine();
            Style.Info("  -- look --");
            Console.WriteLine("  list             all managed items with live state: APPS (running, windows, monitor, AT LOGON, state) + SCRIPTS (running, launcher, AT LOGON, state) + engine + hotkey");
            Console.WriteLine("  launchers [list|learn|forget]   view / add / remove extension→interpreter mappings");
            Console.WriteLine();
            Style.Info("  -- run --");
            Console.WriteLine("  start             one-shot pass (start-if-needed + hide), then exit — on-demand items are skipped");
            Console.WriteLine("  start <name>      start that item if needed, then bring its main window back");
            Console.WriteLine("  run               start the resident engine (via the 'AppManager' task, so it outlives this shell), then CLI exits");
            Console.WriteLine("  stop              stop the engine and hold it off: the keep-alive watchdog stays quiet until the next 'am run' / logon");
            Console.WriteLine();
            Style.Info("  -- hotkey --");
            Console.WriteLine("  hotkey [set|clear] set the global AppManager hotkey (interactive capture, two verifications);");
            Console.WriteLine("                   the hotkey pops a semi-transparent picker window in the engine:");
            Console.WriteLine("                   arrow keys select an app, Enter opens its window, Esc closes");
            Console.WriteLine();
            Style.Dim("Apps (.exe): launched + window hidden. Scripts (.ahk/.ps1/.bat/.vbs/.py/...): launch-only via host, no window hiding.");
            Style.Dim("MONITOR window: one fixed poll-hide time per app item (default 30s); scripts are launch-only.");
            Style.Dim("Config: " + Core.CfgPath);
            Style.Dim("Log:    " + Core.LogPath);
        }
    }
}
