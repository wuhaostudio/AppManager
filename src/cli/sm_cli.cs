using System;
using System.Collections.Generic;
using System.IO;
using StartManager.Shared;

// StartManager CLI: sm.exe (console subsystem, /target:exe).
// Configuration only: add / update / remove / list the items that the logon
// engine starts silently. There is no runtime control (no start / stop / run,
// no window restore, no hotkey, no process guard).
namespace StartManager.Cli
{
    static class Program
    {
        static int Main(string[] args)
        {
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            switch (cmd)
            {
                case "add": DoAdd(args); break;
                case "update": DoUpdate(args); break;
                case "remove": DoRemove(args); break;
                case "list": DoList(); break;
                case "launchers": DoLaunchers(args); break;
                default: Help(); break;
            }
            return 0;
        }

        // human label for the monitor window — concrete values only
        static string SilentLabel(int ms)
        {
            int t = ms > 0 ? ms : 30000; // default (unset) = 30s
            return (t % 1000 == 0) ? (t / 1000) + "s" : t + "ms";
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

        // ---------- add ----------
        static void DoAdd(string[] a)
        {
            string exe = null, name = null, title = null, argLine = null, launcher = null;
            int window = 0;
            bool hasWindow = false;
            for (int i = 1; i < a.Length; i++)
            {
                string t = a[i].ToLowerInvariant();
                if (t == "--name") { if (i + 1 < a.Length) name = a[++i]; }
                else if (t == "--args") { if (i + 1 < a.Length) argLine = a[++i]; }
                else if (t == "--title") { if (i + 1 < a.Length) title = a[++i]; }
                else if (t == "--launcher") { if (i + 1 < a.Length) launcher = a[++i]; }
                else if (t == "--time") { if (i + 1 < a.Length) { window = ParseSec(a[++i], 30000); hasWindow = true; } }
                else if (exe == null) exe = a[i];
            }

            if (exe == null)
            {
                Style.Warn("usage: sm add <exe|script> [--name n] [--args s] [--title t] [--time 15s|30|5000ms] [--launcher L]");
                return;
            }
            exe = exe.Trim().Trim('"');
            if (!File.Exists(exe))
            {
                Style.Err("[error] target not found, NOT added: " + exe);
                return;
            }
            string full = Path.GetFullPath(exe);
            if (name == null || name.Trim().Length == 0) name = Path.GetFileNameWithoutExtension(full);

            bool isExe = full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            bool isScript = !isExe;
            if (isExe && launcher != null)
            {
                Style.Err("[error] --launcher applies to scripts only; " + full + " is an .exe");
                return;
            }
            if (isScript && !IsScriptExt(full) && launcher == null)
            {
                Style.Err("[error] unknown script type '" + Path.GetExtension(full) + "', NOT added");
                Console.WriteLine("  use --launcher <id> to pick a host, or teach one first:");
                Console.WriteLine("    sm launchers learn " + Path.GetExtension(full) + " <id> \"<hostExe>\"");
                Console.WriteLine("  built-in: " + Launchers.KnownList().Replace("\n", "\n  "));
                return;
            }

            var cfg = Core.Load();
            var it = new Item { name = name.Trim(), exe = full, enabled = true };

            if (isScript)
            {
                var resolved = Launchers.ResolveFor(full, launcher, cfg.extLaunchers);
                if (resolved == null)
                {
                    Style.Err("[error] no launcher for '" + full + "', NOT added");
                    Console.WriteLine("  built-in: " + Launchers.KnownList().Replace("\n", "\n  "));
                    if (cfg.extLaunchers.Count > 0)
                    {
                        Console.WriteLine("  learned:");
                        foreach (var e in cfg.extLaunchers)
                            if (e != null) Console.WriteLine("    " + e.ext + " -> " + e.id + " (" + e.host + ")");
                    }
                    Console.WriteLine("  specify one with --launcher <id>, or teach it with:");
                    Console.WriteLine("    sm launchers learn " + Path.GetExtension(full) + " <id> \"<hostExe>\"");
                    return;
                }
                bool hostIsPath = resolved.Host.IndexOf('\\') >= 0 || resolved.Host.IndexOf('/') >= 0;
                if (hostIsPath && !File.Exists(resolved.Host))
                {
                    Style.Err("[error] host not found: " + resolved.Host + ", NOT added");
                    return;
                }
                it.script = true;
                it.launcher = resolved.Id;
                it.hostExe = resolved.Host;
                it.hostArgs = resolved.ArgsTemplate;
                it.processName = resolved.Proc;
                it.args = "";
                it.windowTitle = "";
                it.silentWindowMs = 0;
                if (hasWindow) Style.Warn("[warn] --time is ignored for script items (launch-only, no window hiding)");
                if (title != null && title.Trim().Length > 0) Style.Warn("[warn] --title is ignored for script items (launch-only)");
                if (argLine != null) Style.Warn("[warn] --args is ignored for script items (the host args come from the launcher)");
            }
            else
            {
                it.script = false;
                it.args = argLine == null ? "" : argLine;
                it.processName = Path.GetFileNameWithoutExtension(full);
                it.windowTitle = title == null ? "" : title.Trim();
                it.silentWindowMs = hasWindow ? window : 0;
            }

            int idx = -1;
            string norm = full.ToLowerInvariant();
            // update by NAME first: several scripts may share one host exe
            // (AutoHotkey64.exe), so the target path is not a unique key.
            for (int i = 0; i < cfg.items.Count; i++)
                if (string.Equals(cfg.items[i].name, it.name, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
            if (idx < 0)
                for (int i = 0; i < cfg.items.Count; i++)
                {
                    string existing = Path.GetFullPath(cfg.items[i].exe).ToLowerInvariant();
                    if (existing == norm && string.Equals(cfg.items[i].args, it.args, StringComparison.Ordinal))
                    { idx = i; break; }
                }

            string mode = idx >= 0 ? "updated" : "added";
            if (idx >= 0) cfg.items[idx] = it; else cfg.items.Add(it);
            Core.Save(cfg);
            if (it.script)
            {
                Core.Log("cli " + mode + " '" + it.name + "' script " + it.launcher + " -> " + it.hostExe + " " + it.hostArgs);
                Style.Ok(mode + " '" + it.name + "' -> " + it.exe + "  (script via " + it.launcher + ", launch-only)");
                Style.Dim("Starts at next logon; scripts are launched but their windows are NOT hidden.");
            }
            else
            {
                Core.Log("cli " + mode + " '" + it.name + "' -> " + it.exe + (it.args == "" ? "" : " " + it.args));
                Style.Ok(mode + " '" + it.name + "' -> " + it.exe + (it.args == "" ? "" : "  " + it.args));
                Style.Dim("Starts silently at next logon (monitor " + SilentLabel(it.silentWindowMs) + ").");
            }
        }

        // known script extensions (used for type detection in add / update)
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

        // ---------- remove ----------
        static void DoRemove(string[] a)
        {
            string nm = a.Length >= 2 ? a[1] : null;
            if (nm == null) { Style.Warn("usage: sm remove <name>"); return; }
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
            Style.Dim("Takes effect at next logon.");
        }

        // ---------- list: the configured items only ----------
        static void DoList()
        {
            var cfg = Core.Load();
            if (cfg.items.Count == 0)
            {
                Console.WriteLine("(no items configured. use: sm add <exe|script>  or  sm update)");
                return;
            }
            var rows = new List<string[]>();
            foreach (var it in cfg.items)
                rows.Add(new string[] {
                    it.name,
                    it.script ? "脚本" : "应用",
                    it.enabled ? "是" : "否",
                    it.script ? "-" : SilentLabel(it.silentWindowMs),
                    it.script ? "-" : (string.IsNullOrEmpty(it.windowTitle) ? "(全部窗口)" : it.windowTitle),
                    it.exe,
                    it.script ? (string.IsNullOrEmpty(it.launcher) ? "auto" : it.launcher) : (string.IsNullOrEmpty(it.args) ? "-" : it.args)
                });
            Func<int, int, ConsoleColor?> colors = delegate(int r, int c)
            {
                return rows[r][2] == "否" ? ConsoleColor.DarkGray : (ConsoleColor?)null;
            };
            Style.Table(new[] { "NAME", "TYPE", "ENABLED", "MONITOR", "WINDOW TITLE", "TARGET", "ARGS/LAUNCHER" }, rows, null, colors);
            Console.WriteLine();
            Style.Dim("  At logon: start each enabled item, hide APP windows for MONITOR, then exit.");
            Style.Dim("  Scripts are launch-only (no window hiding); ARGS/LAUNCHER shows the launcher id.");
            Style.Dim("  config: " + Core.CfgPath);
        }

        // ---------- launchers: built-in + learned script hosts ----------
        static void DoLaunchers(string[] a)
        {
            string sub = a.Length >= 2 ? a[1].ToLowerInvariant() : "list";
            var cfg = Core.Load();

            if (sub == "list" || sub == "")
            {
                Console.WriteLine("Built-in launchers:");
                foreach (var line in Launchers.KnownList().Split('\n'))
                    Console.WriteLine(line.TrimEnd('\r'));
                Console.WriteLine();
                if (cfg.extLaunchers.Count > 0)
                {
                    Console.WriteLine("Learned mappings:");
                    var rows = new List<string[]>();
                    foreach (var e in cfg.extLaunchers)
                        if (e != null) rows.Add(new string[] { e.ext, e.id, e.host, e.args, e.proc });
                    Style.Table(new[] { "EXT", "ID", "HOST", "ARGS", "PROC" }, rows);
                }
                else
                {
                    Console.WriteLine("Learned mappings: (none yet)");
                }
                Console.WriteLine();
                Console.WriteLine("To learn a new mapping:");
                Console.WriteLine("  sm launchers learn .lua luajit \"C:\\Tools\\luajit.exe\"");
                Console.WriteLine("  (the next 'sm add x.lua' will auto-match without --launcher)");
                return;
            }

            if (sub == "forget")
            {
                string ext = a.Length >= 3 ? a[2].ToLowerInvariant() : null;
                if (ext == null) { Style.Warn("usage: sm launchers forget <ext>  e.g. sm launchers forget .lua"); return; }
                if (!ext.StartsWith(".")) ext = "." + ext;
                if (Launchers.FindByExt(ext, cfg.extLaunchers) != null)
                {
                    Launchers.Forget(ext, cfg.extLaunchers);
                    Core.Save(cfg);
                    Core.Log("cli launchers forget " + ext);
                    Style.Ok("forgotten " + ext);
                }
                else if (Launchers.IsBuiltInExt(ext))
                    Style.Warn(ext + " is a built-in launcher; learn a mapping for it to override the built-in");
                else
                    Style.Warn(ext + " is not in the learned map");
                return;
            }

            if (sub == "learn")
            {
                if (a.Length < 5)
                {
                    Style.Warn("usage: sm launchers learn <ext> <id> <host> [args] [proc]");
                    Console.WriteLine("  ext   = .lua (with dot; a leading dot is added if missing)");
                    Console.WriteLine("  id    = name to display / use with --launcher");
                    Console.WriteLine("  host  = full path to the interpreter exe");
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
                    Style.Err("[error] host not found: " + hostPath);
                    return;
                }
                Launchers.Learn(ext, id, hostPath, args, proc, cfg.extLaunchers);
                Core.Save(cfg);
                Core.Log("cli launchers learn " + ext + " -> " + id + " (" + hostPath + ")");
                Style.Ok("learned: " + ext + " -> " + id + " (host=" + hostPath + ", proc=" + proc + ")");
                Console.WriteLine("Next time you 'sm add' a " + ext + " file, it will auto-match this launcher.");
                return;
            }

            Style.Warn("usage: sm launchers [list|learn|forget]");
        }

        // ---------- update: interactive config table ----------
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

        // ---------- help ----------
        static void Help()
        {
            Style.Info("sm — StartManager CLI (configuration only). Engine = sm-engine.exe (logon pass, no window).");
            Console.WriteLine();
            Style.Info("Commands:");
            Console.WriteLine("  add <exe|script> [--name n] [--args s] [--title t] [--time 15s|30|5000ms] [--launcher L]");
            Console.WriteLine("                   add or update a startup item (same name updates it in place).");
            Console.WriteLine("                   scripts auto-match a host by extension (built-in or learned);");
            Console.WriteLine("                   use --launcher <id> to override; --args/--title/--time apply to .exe items");
            Console.WriteLine("  update [name]    interactive config editor: pick an item, then edit its fields");
            Console.WriteLine("  remove <name>    remove an item");
            Console.WriteLine("  list             show the configured items");
            Console.WriteLine("  launchers [list|learn|forget]   view / teach / remove script host mappings");
            Console.WriteLine();
            Style.Dim("At logon StartManager starts every enabled item, hides APP windows for the monitor");
            Style.Dim("window, then exits. Scripts are launch-only (their host windows are never hidden).");
            Style.Dim("Nothing else runs: no resident process, no window restore, no process guard, no hotkey.");
            Style.Dim("Config: " + Core.CfgPath);
            Style.Dim("Log:    " + Core.LogPath);
        }
    }
}
