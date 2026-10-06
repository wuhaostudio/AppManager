using System;
using System.Collections.Generic;
using System.IO;
using AppManager.Cli;
using AppManager.Shared;

// update_test — the `am update` editor (UpdateUi): model + key handling + line editor.
// No console is needed: items/fields/values/validation, the level navigation
// (ApplyKey / Back), the option list and the line-editor feed are all pure.
// Scratch files (config.json / am.log / dummy targets) land next to the exe, i.e.
// inside the gitignored tests/bin.
//
// Compile (from the repo root, MSYS path style):
//   MSYS_NO_PATHCONV=1 "/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe" /nologo \
//     /codepage:65001 /target:exe /main:UpdateTest /out:tests/bin/update_test.exe \
//     src/shared/am_shared.cs src/cli/am_cli.cs src/cli/am_update.cs src/cli/am_style.cs tests/update_test.cs
// Run: tests/bin/update_test.exe    ->  "N/N  ALL PASS", exit 0
static class UpdateTest
{
    static int fails = 0;
    static int total = 0;

    static void Check(bool ok, string what)
    {
        total++;
        if (!ok) { fails++; Console.WriteLine("FAIL  " + what); }
        else Console.WriteLine("ok    " + what);
    }

    static string Dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);

    static Item App(string name, string exe)
    {
        var it = new Item();
        it.name = name; it.exe = exe; it.processName = Path.GetFileNameWithoutExtension(exe);
        it.windowTitle = ""; it.silentWindowMs = 30000; it.script = false;
        it.enabled = true; it.autostart = true;
        return it;
    }

    static Item Script(string name, string exe)
    {
        var it = new Item();
        it.name = name; it.exe = exe; it.processName = "powershell";
        it.launcher = "powershell"; it.hostExe = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
        it.hostArgs = "-NoProfile -ExecutionPolicy Bypass -File {script}"; it.script = true;
        it.enabled = true; it.autostart = false;
        return it;
    }

    static string Label(List<UpdateUi.Field> fs, string key)
    {
        foreach (var f in fs) if (f.Key == key) return f.Label;
        return null;
    }

    static ConsoleKeyInfo K(ConsoleKey k) { return new ConsoleKeyInfo('\0', k, false, false, false); }
    static ConsoleKeyInfo Ch(char c) { return new ConsoleKeyInfo(c, ConsoleKey.A, false, false, false); }

    static int Main()
    {
        // scratch targets so the path editor has something real to validate against
        string exeA = Path.Combine(Dir, "probeA.exe");
        string exeB = Path.Combine(Dir, "probeB.exe");
        string ps1 = Path.Combine(Dir, "s.ps1");
        File.WriteAllText(exeA, "x"); File.WriteAllText(exeB, "x"); File.WriteAllText(ps1, "x");

        var cfg = new Config();
        var a = App("ProbeA", exeA);
        var s = Script("ProbeS", ps1);
        cfg.items.Add(a); cfg.items.Add(s);

        Console.WriteLine("=== items (level 1) ===");
        var items = UpdateUi.BuildItems(cfg, null);
        Check(items.Count == 2, "BuildItems(null) -> both entries");
        Check(UpdateUi.BuildItems(cfg, "ProbeS").Count == 1, "BuildItems(name) filters to that item");
        Check(UpdateUi.BuildItems(cfg, "Nope").Count == 0, "unknown name -> nothing");
        Check(UpdateUi.KindText(a) == "应用" && UpdateUi.KindText(s) == "脚本", "kind labels");

        Console.WriteLine("=== fields (level 2) ===");
        var fa = UpdateUi.Fields(a);
        var fs = UpdateUi.Fields(s);
        Check(Label(fa, "NAME") == "名称" && Label(fa, "TARGET") == "应用地址", "app field labels");
        Check(Label(fa, "TITLE") == "窗口标题" && Label(fa, "MONITOR") == "监控时长", "app has title + monitor");
        Check(Label(fa, "LAUNCHER") == null, "app has no launcher field");
        Check(Label(fs, "TARGET") == "脚本地址" && Label(fs, "LAUNCHER") == "启动器", "script field labels");
        Check(Label(fs, "MONITOR") == null && Label(fs, "TITLE") == null, "script has no monitor/title");
        bool roKind = false, roProc = false, editableName = false;
        foreach (var f in fa)
        {
            if (f.Key == "KIND" && f.ReadOnly) roKind = true;
            if (f.Key == "PROC" && f.ReadOnly) roProc = true;
            if (f.Key == "NAME" && !f.ReadOnly) editableName = true;
        }
        Check(roKind && roProc && editableName, "类型/进程名 are read-only, 名称 is editable");

        Console.WriteLine("=== field values ===");
        Check(UpdateUi.FieldText(a, "ATLOGON") == "静默启动", "autostart=true shows 静默启动");
        Check(UpdateUi.FieldText(s, "ATLOGON") == "按需", "autostart=false shows 按需");
        Check(UpdateUi.FieldText(a, "MONITOR") == "30s", "monitor 30000 -> 30s");
        Check(UpdateUi.FieldText(a, "TITLE") == "(全部窗口)", "empty title shows (全部窗口)");
        Check(UpdateUi.FieldText(a, "KIND").StartsWith("应用"), "kind text for an app");
        Check(UpdateUi.FieldText(s, "HOST").EndsWith("powershell.exe"), "host shown for a script");
        var chLogon = UpdateUi.Choices(cfg, a, "ATLOGON");
        Check(chLogon != null && chLogon.Count == 2, "登录自启 offers 2 options");
        Check(UpdateUi.Choices(cfg, a, "NAME") == null, "名称 is a text field");
        var chL = UpdateUi.Choices(cfg, s, "LAUNCHER");
        Check(chL != null && chL.Contains("powershell") && chL.Contains("cmd"), "启动器 offers built-in ids");

        Console.WriteLine("=== frames ===");
        var lf = UpdateUi.BuildListFrame(cfg, items, 0, "");
        bool head = false, summary = false;
        foreach (var l in lf.Lines)
        {
            if (l.Contains("名称") && l.Contains("类型") && l.Contains("登录自启")) head = true;
            if (l.Contains("共 2 项")) summary = true;
        }
        Check(head, "list header carries 名称/类型/登录自启");
        Check(summary, "list shows the summary line");
        Check(lf.HiLine >= 0 && lf.Lines[lf.HiLine].StartsWith("ProbeA"), "selected row is highlighted and starts with ProbeA");
        var lf2 = UpdateUi.BuildListFrame(cfg, items, 1, "");
        Check(lf2.Lines[lf2.HiLine].StartsWith("ProbeS"), "second row selectable");

        var ff = UpdateUi.BuildFormFrame(cfg, a, 5, "", true);
        Check(ff.Lines[ff.HiLine].Substring(ff.HiCol, ff.HiLen).Trim() == "静默启动", "form cursor sits on the 登录自启 value");
        int monIdx = -1, idx = 0;
        foreach (var f in fa) { if (f.Key == "MONITOR") monIdx = idx; idx++; }
        var ff2 = UpdateUi.BuildFormFrame(cfg, a, monIdx, "", true);
        Check(ff2.Lines[ff2.HiLine].Substring(ff2.HiCol, ff2.HiLen).Trim() == "30s", "form cursor on the monitor value");
        Check(ff.Lines[3].Contains("|"), "form rows are 字段 | 值");

        Console.WriteLine("=== edits: name / target ===");
        Check(UpdateUi.ApplyText(cfg, a, "NAME", "  ") != null, "empty name rejected");
        Check(UpdateUi.ApplyText(cfg, a, "NAME", "ProbeS") != null, "duplicate name rejected");
        Check(UpdateUi.ApplyText(cfg, a, "NAME", "Alpha") == null && a.name == "Alpha", "rename accepted");
        UpdateUi.ApplyText(cfg, a, "NAME", "ProbeA");
        Check(UpdateUi.ApplyText(cfg, a, "TARGET", @"C:\nope\nope.exe") != null, "missing target rejected");
        Check(UpdateUi.ApplyText(cfg, a, "TARGET", exeB) == null && a.exe == exeB && a.processName == "probeB", "target edit re-derives processName");
        Check(UpdateUi.ApplyText(cfg, a, "TARGET", ps1) != null, "app cannot point at a script");
        Check(UpdateUi.ApplyText(cfg, s, "TARGET", exeA) != null, "script cannot point at an .exe");
        UpdateUi.ApplyText(cfg, a, "TARGET", exeA);

        Console.WriteLine("=== edits: monitor / title / choices ===");
        Check(UpdateUi.ApplyText(cfg, a, "MONITOR", "45s") == null && a.silentWindowMs == 45000, "45s accepted");
        Check(UpdateUi.ApplyText(cfg, a, "MONITOR", "abc") != null, "bad duration rejected");
        Check(UpdateUi.ApplyText(cfg, s, "MONITOR", "30s") != null, "script has no monitor field");
        Check(UpdateUi.ApplyText(cfg, a, "TITLE", "主窗口") == null && a.windowTitle == "主窗口", "title accepted");
        Check(UpdateUi.ApplyText(cfg, a, "TITLE", "") == null && a.windowTitle == "", "empty title clears it (= all windows)");
        Check(UpdateUi.ApplyChoice(cfg, a, "ATLOGON", "按需") == null && !a.autostart, "ATLOGON -> 按需");
        Check(UpdateUi.ApplyChoice(cfg, a, "ATLOGON", "静默启动") == null && a.autostart, "ATLOGON -> 静默启动");
        Check(UpdateUi.ApplyChoice(cfg, a, "ON", "否") == null && !a.enabled, "ON -> 否");
        UpdateUi.ApplyChoice(cfg, a, "ON", "是");
        Check(UpdateUi.ApplyChoice(cfg, s, "LAUNCHER", "cmd") == null && s.launcher == "cmd" && s.processName == "cmd", "LAUNCHER -> cmd re-resolves host/proc");
        Check(UpdateUi.ApplyChoice(cfg, s, "LAUNCHER", "nope-not-a-launcher") != null, "unknown launcher rejected");
        UpdateUi.ApplyChoice(cfg, s, "LAUNCHER", "powershell");

        Console.WriteLine("=== keys: level 1 -> level 2 ===");
        var c = new UpdateUi.Cursor();
        UpdateUi.ApplyKey(cfg, items, c, ConsoleKey.DownArrow);
        Check(c.Item == 1, "Down picks the second item");
        UpdateUi.ApplyKey(cfg, items, c, ConsoleKey.DownArrow);
        Check(c.Item == 0, "Down wraps to the first item");
        UpdateUi.ApplyKey(cfg, items, c, ConsoleKey.UpArrow);
        Check(c.Item == 1, "Up wraps to the last item");
        UpdateUi.ApplyKey(cfg, items, c, ConsoleKey.Enter);
        Check(c.Level == UpdateUi.LevelForm && c.Field == 0, "Enter opens that item's form");

        Console.WriteLine("=== keys: form navigation + option list ===");
        var c2 = new UpdateUi.Cursor();
        c2.Level = UpdateUi.LevelForm;
        c2.Item = 0;
        int atLogonIdx = 0, n = 0;
        foreach (var f in fa) { if (f.Key == "ATLOGON") atLogonIdx = n; n++; }
        for (int i = 0; i < atLogonIdx; i++) UpdateUi.ApplyKey(cfg, items, c2, ConsoleKey.DownArrow);
        Check(c2.Field == atLogonIdx, "Down reaches the 登录自启 field");
        Check(UpdateUi.IsChoiceField(cfg, items, c2), "登录自启 is a choice field");
        UpdateUi.ApplyKey(cfg, items, c2, ConsoleKey.Enter);
        Check(c2.OptionOpen && c2.OptionSel == 0, "Enter opens the option list on 静默启动");
        UpdateUi.ApplyKey(cfg, items, c2, ConsoleKey.DownArrow);
        Check(c2.OptionSel == 1, "Down moves to 按需");
        UpdateUi.ApplyKey(cfg, items, c2, ConsoleKey.Escape);
        Check(!c2.OptionOpen && items[0].autostart, "Esc closes the list without changing the value");
        Check(c2.Status.Contains("取消"), "status says the change was cancelled");
        UpdateUi.ApplyKey(cfg, items, c2, ConsoleKey.Enter);
        UpdateUi.ApplyKey(cfg, items, c2, ConsoleKey.DownArrow);
        UpdateUi.ApplyKey(cfg, items, c2, ConsoleKey.Enter);
        Check(!c2.OptionOpen && !items[0].autostart, "Enter applies the choice -> autostart=false");
        Check(c2.Status.Contains("已保存"), "status reports the save");
        Check(!Core.FindItem(Core.Load(), "ProbeA").autostart, "the key-driven edit reached config.json");

        var c3 = new UpdateUi.Cursor();
        c3.Level = UpdateUi.LevelForm; c3.Item = 0; c3.Field = 0;
        Check(!UpdateUi.IsChoiceField(cfg, items, c3), "名称 is a text field");
        UpdateUi.ApplyKey(cfg, items, c3, ConsoleKey.Enter);
        Check(!c3.OptionOpen, "Enter on a text field opens no list (Run starts the line editor)");
        int procIdx = 0, pi = 0;
        foreach (var f in fa) { if (f.Key == "PROC") procIdx = pi; pi++; }
        c3.Field = procIdx;
        UpdateUi.ApplyKey(cfg, items, c3, ConsoleKey.Enter);
        Check(!c3.OptionOpen && c3.Status.Contains("只读"), "Enter on a read-only field just explains itself");

        Console.WriteLine("=== keys: Esc steps back ===");
        var c4 = new UpdateUi.Cursor();
        c4.Level = UpdateUi.LevelForm;
        Check(!UpdateUi.Back(c4, true) && c4.Level == UpdateUi.LevelList, "Esc from the form returns to the item list");
        Check(UpdateUi.Back(c4, true), "Esc from the list asks the caller to quit");
        var c5 = new UpdateUi.Cursor();
        c5.Level = UpdateUi.LevelForm;
        Check(UpdateUi.Back(c5, false), "single-item form (no list) quits on Esc");
        var c6 = new UpdateUi.Cursor();
        c6.Level = UpdateUi.LevelForm; c6.Item = 0; c6.Field = atLogonIdx;
        UpdateUi.ApplyKey(cfg, items, c6, ConsoleKey.Enter);
        UpdateUi.ApplyKey(cfg, items, c6, ConsoleKey.Escape);
        Check(!c6.OptionOpen && c6.Level == UpdateUi.LevelForm, "Esc in the option list stays on the form (one level at a time)");

        Console.WriteLine("=== frame width policy (no smearing when the frame shrinks) ===");
        int wList = UpdateUi.RenderWidth(lf, 0, 200);
        Check(wList == lf.Width, "first frame uses its own width");
        int wForm = UpdateUi.RenderWidth(ff, wList, 200);
        Check(wForm == wList && wForm > ff.Width, "a narrower form still renders at the list's width (tail cleared)");
        Check(UpdateUi.RenderWidth(ff, 0, 200) == ff.Width, "without history the form uses its own width");
        Check(UpdateUi.RenderWidth(lf, wList, 60) == 60, "clamped to the terminal width");
        Check(UpdateUi.RenderWidth(ff, 500, 60) == 60, "a huge previous width is clamped too");

        Console.WriteLine("=== line editor ===");
        var st = new UpdateUi.EditState();
        st.Buffer = "abc"; st.Caret = 3;
        Check(!UpdateUi.Feed(st, Ch('d')) && st.Buffer == "abcd" && st.Caret == 4, "typing appends");
        Check(!UpdateUi.Feed(st, K(ConsoleKey.LeftArrow)) && st.Caret == 3, "Left moves the caret");
        Check(!UpdateUi.Feed(st, K(ConsoleKey.Backspace)) && st.Buffer == "abd" && st.Caret == 2, "Backspace deletes before the caret");
        Check(!UpdateUi.Feed(st, K(ConsoleKey.Delete)) && st.Buffer == "ab", "Delete removes at the caret");
        Check(!UpdateUi.Feed(st, K(ConsoleKey.Home)) && st.Caret == 0, "Home jumps to the start");
        Check(!UpdateUi.Feed(st, Ch('X')) && st.Buffer == "Xab" && st.Caret == 1, "inserts at the caret");
        Check(!UpdateUi.Feed(st, K(ConsoleKey.End)) && st.Caret == 3, "End jumps to the end");
        Check(UpdateUi.Feed(st, new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)) && st.Done, "Enter finishes");
        var st2 = new UpdateUi.EditState();
        st2.Buffer = "keepme"; st2.Caret = 6;
        Check(UpdateUi.Feed(st2, K(ConsoleKey.Escape)) && st2.Cancelled && st2.Buffer == "keepme", "Esc cancels and keeps the text for the caller to discard");
        var st3 = new UpdateUi.EditState();
        st3.Buffer = "a"; st3.Caret = 1;
        UpdateUi.Feed(st3, new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false));
        Check(st3.Buffer == "a", "control characters are ignored");

        Console.WriteLine("=== line window (CJK aware) ===");
        int cc;
        UpdateUi.Visible("中文abc", 2, 40, out cc);
        Check(cc == 4, "caret column counts CJK as two columns");
        string vis = UpdateUi.Visible(new string('x', 60), 60, 20, out cc);
        Check(vis.StartsWith("..") && Style.DispWidth(vis) <= 20 && cc <= 20, "long input scrolls and keeps the caret inside");
        UpdateUi.Visible("short", 5, 20, out cc);
        Check(cc == 5, "caret at the end of a short input");

        Console.WriteLine("=== highlight math ===");
        var hl3 = new List<string> { "aaaa", "bbbbbb", "cccc" };
        int hs, hl;
        UpdateUi.HighlightSpan(hl3, 1, 2, 3, out hs, out hl);
        Check(hs == 8 && hl == 3, "span lands on line 1, offset 2, length 3");
        UpdateUi.HighlightSpan(hl3, 9, 0, 2, out hs, out hl);
        Check(hs == -1 && hl == 0, "out-of-range line -> nothing highlighted");
        UpdateUi.HighlightSpan(hl3, 2, 2, 99, out hs, out hl);
        Check(hl == 2, "length clamps to the end of the line");

        Console.WriteLine("=== persistence ===");
        Core.Save(cfg);
        var back = Core.Load();
        var rb = Core.FindItem(back, "ProbeA");
        Check(rb != null && rb.windowTitle == "" && rb.silentWindowMs == 45000 && !rb.autostart, "edits survive save/load");
        Check(Core.FindItem(back, "ProbeS").launcher == "powershell", "script item survives save/load");

        Console.WriteLine();
        Console.WriteLine("=== dump mode (what a piped `am update` prints) ===");
        UpdateUi.Run(back, null);

        Console.WriteLine();
        Console.WriteLine((total - fails) + "/" + total + (fails == 0 ? "  ALL PASS" : "  " + fails + " FAILED"));
        return fails == 0 ? 0 : 1;
    }
}
