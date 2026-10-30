using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using StartManager.Cli;
using StartManager.Shared;

// update_test — the `sm update` editor (UpdateUi) and the script launcher tables.
// No console is needed: items/fields/values/validation, launcher resolution,
// level navigation (ApplyKey / Back), the option list and the line-editor feed
// are all pure. Scratch files land in the gitignored tests/bin.
//
// Compile: tests\build_update.cmd  ->  tests\bin\update_test.exe
// Run:     tests\bin\update_test.exe  ->  "N/N  ALL PASS", exit 0
static class UpdateTest
{
    static int total = 0;
    static int fails = 0;
    static string Dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    const string AHK = @"C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe";

    static void Check(bool ok, string what)
    {
        total++;
        if (!ok) { fails++; Console.WriteLine("FAIL  " + what); }
        else Console.WriteLine("ok    " + what);
    }

    static string MakeTarget(string name)
    {
        string p = Path.Combine(Dir, name);
        File.WriteAllText(p, "");
        return p;
    }

    static Item App(string name, string exe)
    {
        return new Item
        {
            name = name,
            exe = exe,
            processName = Path.GetFileNameWithoutExtension(exe),
            windowTitle = "",
            silentWindowMs = 0,
            enabled = true
        };
    }

    static Item Script(string name, string scriptPath)
    {
        return new Item
        {
            name = name,
            exe = scriptPath,
            processName = "AutoHotkey64",
            windowTitle = "",
            silentWindowMs = 0,
            enabled = true,
            script = true,
            launcher = "autohotkey",
            hostExe = AHK,
            hostArgs = "{script}"
        };
    }

    static string Label(List<UpdateUi.Field> fs, string key)
    {
        foreach (var f in fs) if (f.Key == key) return f.Label;
        return null;
    }

    static bool ReadOnly(List<UpdateUi.Field> fs, string key)
    {
        foreach (var f in fs) if (f.Key == key) return f.ReadOnly;
        return false;
    }

    static int FieldIndex(List<UpdateUi.Field> fs, string key)
    {
        for (int i = 0; i < fs.Count; i++) if (fs[i].Key == key) return i;
        return -1;
    }

    static ConsoleKeyInfo K(ConsoleKey k) { return new ConsoleKeyInfo('\0', k, false, false, false); }
    static ConsoleKeyInfo Ch(char c) { return new ConsoleKeyInfo(c, ConsoleKey.A, false, false, false); }

    static int Main()
    {
        string exeA = MakeTarget("probeA.exe");
        string exeB = MakeTarget("probeB.exe");
        string ps1 = MakeTarget("probe.ps1");
        string ahk = MakeTarget("probe.ahk");
        string lua = MakeTarget("probe.lua");

        var learned = new List<ExtLauncher>();
        learned.Add(new ExtLauncher { ext = ".lua", id = "luajit", host = @"C:\Tools\luajit.exe", args = "{script}", proc = "luajit" });

        var cfg = new Config();
        cfg.items.Add(App("ProbeA", exeA));
        cfg.items.Add(Script("ProbeS", ahk));
        cfg.extLaunchers = learned;

        // ---- level 1: items ----
        Check(UpdateUi.BuildItems(cfg, null).Count == 2, "BuildItems(null) -> both items");
        Check(UpdateUi.BuildItems(cfg, "ProbeA").Count == 1, "BuildItems(name) filters to that item");
        Check(UpdateUi.BuildItems(cfg, "nope").Count == 0, "unknown name -> nothing");

        // ---- fields (app vs script) ----
        var app = cfg.items[0];
        var scr = cfg.items[1];
        var af = UpdateUi.Fields(app);
        var sf = UpdateUi.Fields(scr);
        Check(af.Count == 8, "app has 8 fields");
        Check(sf.Count == 8, "script has 8 fields");
        Check(Label(af, "KIND") == "类型" && ReadOnly(af, "KIND"), "type is a read-only field");
        Check(Label(af, "TARGET") == "应用地址", "app target label");
        Check(Label(sf, "TARGET") == "脚本地址", "script target label");
        Check(FieldIndex(af, "LAUNCHER") < 0, "app has no launcher field");
        Check(FieldIndex(af, "TITLE") >= 0 && FieldIndex(af, "MONITOR") >= 0, "app has title + monitor");
        Check(FieldIndex(sf, "LAUNCHER") >= 0 && FieldIndex(sf, "HOST") >= 0, "script has launcher + host");
        Check(FieldIndex(sf, "TITLE") < 0 && FieldIndex(sf, "MONITOR") < 0, "script has no title/monitor");
        Check(!ReadOnly(sf, "LAUNCHER"), "launcher is editable");
        Check(ReadOnly(sf, "HOST"), "host is read-only");
        Check(ReadOnly(sf, "ARGS"), "script host args are read-only");

        // ---- values ----
        Check(UpdateUi.FieldText(app, "KIND") == "应用", "app kind text");
        Check(UpdateUi.FieldText(scr, "KIND") == "脚本", "script kind text");
        Check(UpdateUi.FieldText(app, "ARGS") == "(无)", "empty app args shown as (无)");
        Check(UpdateUi.FieldText(scr, "ARGS") == "{script}", "script args show the host template");
        Check(UpdateUi.FieldText(scr, "LAUNCHER") == "autohotkey", "script launcher text");
        Check(UpdateUi.FieldText(scr, "HOST") == AHK, "script host text");
        Check(UpdateUi.FieldText(app, "TITLE") == "(全部窗口)", "empty title shown as (全部窗口)");
        Check(UpdateUi.FieldText(app, "MONITOR") == "30s", "monitor 0 -> 30s");
        Check(UpdateUi.FieldText(app, "ON") == "是", "enabled -> 是");

        // ---- choices ----
        Check(UpdateUi.Choices(cfg, app, "LAUNCHER") == null, "app has no launcher choices");
        var opts = UpdateUi.Choices(cfg, scr, "LAUNCHER");
        Check(opts != null && opts.Contains("autohotkey") && opts.Contains("powershell") && opts.Contains("luajit"),
            "launcher choices = built-ins + learned");
        Check(UpdateUi.Choices(cfg, scr, "ON") != null, "enabled offers choices");

        // ---- launcher tables (pure) ----
        var rAhk = Launchers.ResolveFor(ahk, null, learned);
        Check(rAhk != null && rAhk.Id == "autohotkey" && rAhk.Proc == "AutoHotkey64" && rAhk.ArgsTemplate == "{script}",
            "auto-match .ahk -> autohotkey");
        var rPs = Launchers.ResolveFor(ps1, null, learned);
        Check(rPs != null && rPs.Id == "powershell" && rPs.ArgsTemplate.Contains("-File {script}"),
            "auto-match .ps1 -> powershell with -File {script}");
        var rLua = Launchers.ResolveFor(lua, null, learned);
        Check(rLua != null && rLua.Id == "luajit" && rLua.FromLearned, "learned .lua wins over built-ins");
        var rExplicit = Launchers.ResolveFor(lua, "autohotkey", learned);
        Check(rExplicit != null && rExplicit.Id == "autohotkey", "explicit --launcher wins over the extension");
        Check(Launchers.ResolveFor(MakeTarget("probe.xyz"), null, learned) == null, "unknown extension -> no launcher");
        Check(Launchers.IsBuiltInExt(ahk) && !Launchers.IsBuiltInExt(lua), "built-in extension detection");
        var ids = Launchers.Ids(learned);
        Check(ids.Contains("autohotkey") && ids.Contains("luajit"), "launcher ids include built-ins + learned");
        var teach = new List<ExtLauncher>();
        Launchers.Learn(".pl", "perl", @"C:\Tools\perl.exe", "{script}", "perl", teach);
        Check(Launchers.FindByExt(".pl", teach) != null, "learn adds a mapping");
        Launchers.Forget(".pl", teach);
        Check(Launchers.FindByExt(".pl", teach) == null, "forget removes a mapping");

        // ---- text edits ----
        Check(UpdateUi.ApplyText(cfg, app, "NAME", "") != null, "empty name rejected");
        Check(UpdateUi.ApplyText(cfg, app, "NAME", "ProbeS") != null, "duplicate name rejected");
        Check(UpdateUi.ApplyText(cfg, app, "NAME", "ProbeC") == null && app.name == "ProbeC", "rename accepted");
        Check(UpdateUi.ApplyText(cfg, app, "TARGET", Path.Combine(Dir, "missing.exe")) != null, "missing target rejected");
        Check(UpdateUi.ApplyText(cfg, app, "TARGET", ahk) != null, "app cannot point at a script");
        Check(UpdateUi.ApplyText(cfg, app, "TARGET", exeB) == null && app.processName == "probeB", "app target re-derives process name");
        Check(UpdateUi.ApplyText(cfg, app, "TARGET", exeA) == null && app.processName == "probeA", "app target restored");
        Check(UpdateUi.ApplyText(cfg, app, "ARGS", "--flag") == null && app.args == "--flag", "app args accepted");
        Check(UpdateUi.ApplyText(cfg, app, "MONITOR", "45s") == null && app.silentWindowMs == 45000, "45s accepted");
        Check(UpdateUi.ApplyText(cfg, app, "MONITOR", "bad") != null, "bad duration rejected");
        Check(UpdateUi.ApplyText(cfg, app, "TITLE", "主窗口") == null && app.windowTitle == "主窗口", "title accepted");
        Check(UpdateUi.ApplyText(cfg, app, "TITLE", "") == null && app.windowTitle == "", "empty title clears it");
        Check(UpdateUi.ApplyText(cfg, app, "LAUNCHER", "autohotkey") != null, "app launcher edit rejected");
        Check(UpdateUi.ApplyText(cfg, scr, "TARGET", exeA) != null, "script cannot point at an .exe");
        Check(UpdateUi.ApplyText(cfg, scr, "ARGS", "x") != null, "script host args are read-only");
        Check(UpdateUi.ApplyText(cfg, scr, "TITLE", "x") != null, "script has no title");
        Check(UpdateUi.ApplyText(cfg, scr, "MONITOR", "5s") != null, "script has no monitor");

        // script target edit re-resolves when the launcher is automatic
        var autoScript = Script("ProbeAuto", ahk);
        autoScript.launcher = "";
        autoScript.hostExe = "";
        autoScript.hostArgs = "";
        cfg.items.Add(autoScript);
        Check(UpdateUi.ApplyText(cfg, autoScript, "TARGET", ps1) == null && autoScript.launcher == "powershell"
            && autoScript.hostExe.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase),
            "script target edit re-resolves the launcher");
        cfg.items.Remove(autoScript);

        // ---- choice edits ----
        Check(UpdateUi.ApplyChoice(cfg, app, "ON", "否") == null && !app.enabled, "enabled -> false");
        Check(UpdateUi.ApplyChoice(cfg, app, "ON", "是") == null && app.enabled, "enabled -> true");
        Check(UpdateUi.ApplyChoice(cfg, scr, "LAUNCHER", "powershell") == null && scr.launcher == "powershell"
            && scr.hostExe.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase)
            && scr.processName == "powershell", "launcher choice rewrites host / proc");
        Check(UpdateUi.ApplyChoice(cfg, scr, "LAUNCHER", "autohotkey") == null && scr.processName == "AutoHotkey64",
            "launcher choice can go back");
        Check(UpdateUi.ApplyChoice(cfg, app, "LAUNCHER", "powershell") != null, "app cannot apply a launcher choice");

        // ---- key navigation ----
        var items = UpdateUi.BuildItems(cfg, null);
        var cur = new UpdateUi.Cursor();
        UpdateUi.ApplyKey(cfg, items, cur, ConsoleKey.DownArrow);
        Check(cur.Item == 1, "Down selects the second item");
        UpdateUi.ApplyKey(cfg, items, cur, ConsoleKey.DownArrow);
        Check(cur.Item == 0, "Down wraps to the first item");
        UpdateUi.ApplyKey(cfg, items, cur, ConsoleKey.Enter);
        Check(cur.Level == UpdateUi.LevelForm, "Enter opens the form");
        UpdateUi.ApplyKey(cfg, items, cur, ConsoleKey.DownArrow);
        Check(cur.Field == 1, "Down moves to the next field");

        // read-only field
        cur.Field = FieldIndex(UpdateUi.Fields(items[cur.Item]), "PROC");
        UpdateUi.ApplyKey(cfg, items, cur, ConsoleKey.Enter);
        Check(cur.Status.IndexOf("只读") >= 0, "Enter on a read-only field explains itself");

        // enabled option list
        cur.Field = FieldIndex(UpdateUi.Fields(items[cur.Item]), "ON");
        UpdateUi.ApplyKey(cfg, items, cur, ConsoleKey.Enter);
        Check(cur.OptionOpen, "Enter opens the enabled option list");
        UpdateUi.ApplyKey(cfg, items, cur, ConsoleKey.DownArrow);
        UpdateUi.ApplyKey(cfg, items, cur, ConsoleKey.Enter);
        Check(!items[cur.Item].enabled, "Enter applies 否");
        Check(cur.Status.StartsWith("已保存"), "status reports the save");

        // launcher option list on the script item
        cur.Item = 1;
        cur.Level = UpdateUi.LevelForm;
        cur.Field = FieldIndex(UpdateUi.Fields(items[1]), "LAUNCHER");
        UpdateUi.ApplyKey(cfg, items, cur, ConsoleKey.Enter);
        Check(cur.OptionOpen, "Enter opens the launcher option list");
        int psSel = -1;
        for (int i = 0; i < UpdateUi.Choices(cfg, items[1], "LAUNCHER").Count; i++)
            if (UpdateUi.Choices(cfg, items[1], "LAUNCHER")[i] == "powershell") psSel = i;
        while (cur.OptionSel != psSel) UpdateUi.ApplyKey(cfg, items, cur, ConsoleKey.DownArrow);
        UpdateUi.ApplyKey(cfg, items, cur, ConsoleKey.Enter);
        Check(items[1].launcher == "powershell", "launcher option list applies the choice");
        Check(cur.Status.StartsWith("已保存"), "launcher choice reports the save");

        // ---- Esc steps back ----
        cur.Level = UpdateUi.LevelForm;
        Check(UpdateUi.Back(cur, true) == false && cur.Level == UpdateUi.LevelList, "Esc from the form returns to the list");
        Check(UpdateUi.Back(cur, true) == true, "Esc from the list asks the caller to quit");
        Check(UpdateUi.Back(cur, false) == true, "single-item form (no list) quits on Esc");

        // ---- line editor ----
        var st = new UpdateUi.EditState();
        UpdateUi.Feed(st, Ch('a'));
        UpdateUi.Feed(st, Ch('b'));
        UpdateUi.Feed(st, Ch('c'));
        Check(st.Buffer == "abc" && st.Caret == 3, "typing appends");
        UpdateUi.Feed(st, K(ConsoleKey.LeftArrow));
        Check(st.Caret == 2, "Left moves the caret");
        UpdateUi.Feed(st, K(ConsoleKey.Backspace));
        Check(st.Buffer == "ac" && st.Caret == 1, "Backspace deletes before the caret");
        UpdateUi.Feed(st, K(ConsoleKey.Delete));
        Check(st.Buffer == "a", "Delete removes at the caret");
        UpdateUi.Feed(st, K(ConsoleKey.Home));
        UpdateUi.Feed(st, Ch('x'));
        Check(st.Buffer == "xa" && st.Caret == 1, "Home + insert");
        UpdateUi.Feed(st, K(ConsoleKey.End));
        Check(st.Caret == 2, "End jumps to the end");
        UpdateUi.Feed(st, new ConsoleKeyInfo('\u0003', ConsoleKey.A, false, false, false));
        Check(st.Buffer == "xa", "control characters are ignored");
        UpdateUi.Feed(st, K(ConsoleKey.Enter));
        Check(st.Done && !st.Cancelled, "Enter finishes");
        var st2 = new UpdateUi.EditState();
        UpdateUi.Feed(st2, K(ConsoleKey.Escape));
        Check(st2.Cancelled, "Esc cancels");

        // ---- persistence ----
        cfg.items[1].launcher = "autohotkey";
        cfg.items[1].hostExe = AHK;
        cfg.items[1].hostArgs = "{script}";
        cfg.items[1].processName = "AutoHotkey64";
        Core.Save(cfg);
        var loaded = Core.Load();
        Check(loaded.items.Count == 2, "both items survive save/load");
        Check(loaded.items[0].args == "--flag", "app args survive save/load");
        Check(loaded.items[1].script && loaded.items[1].launcher == "autohotkey", "script fields survive save/load");
        Check(loaded.extLaunchers.Count == 1 && loaded.extLaunchers[0].id == "luajit", "learned launchers survive save/load");

        // ---- frames / width policy ----
        var listFrame = UpdateUi.BuildListFrame(cfg, UpdateUi.BuildItems(cfg, null), 0, "");
        string listText = string.Join("\n", listFrame.Lines.ToArray());
        Check(listText.Contains("名称") && listText.Contains("类型"), "list frame has name + kind headers");
        Check(UpdateUi.SummaryLine(UpdateUi.BuildItems(cfg, null)).Contains("应用 1 · 脚本 1"), "summary counts apps + scripts");
        var formApp = UpdateUi.BuildFormFrame(cfg, cfg.items[0], 1, "", true);
        Check(string.Join("\n", formApp.Lines.ToArray()).Contains("应用地址"), "app form has the target field");
        var formScr = UpdateUi.BuildFormFrame(cfg, cfg.items[1], 1, "", true);
        string scrText = string.Join("\n", formScr.Lines.ToArray());
        Check(scrText.Contains("脚本地址") && scrText.Contains("启动器") && scrText.Contains("宿主"),
            "script form has target / launcher / host");
        Check(UpdateUi.RenderWidth(listFrame, 0, 200) == Math.Max(listFrame.Width, 46), "first frame uses its own width");
        Check(UpdateUi.RenderWidth(formApp, listFrame.Width + 10, 200) >= listFrame.Width + 10,
            "narrow frame keeps the previous width (tail cleared)");

        Console.WriteLine();
        if (fails == 0) Console.WriteLine(total + "/" + total + "  ALL PASS");
        else Console.WriteLine((total - fails) + "/" + total + "  PASS, " + fails + " FAIL");
        return fails == 0 ? 0 : 1;
    }
}
