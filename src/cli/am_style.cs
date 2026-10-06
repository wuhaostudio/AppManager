using System;
using System.Collections.Generic;
using System.Text;

// Console presentation helpers for the CLI: colours, and padding that respects
// display width (CJK glyphs occupy two columns, so string.Length is wrong).
//
// Colour writes go through Console.ForegroundColor/BackgroundColor, which .NET
// silently ignores when the stream is redirected — piped or logged output stays
// plain text with no escape sequences.
namespace AppManager.Cli
{
    static class Style
    {
        // ---------- colours ----------
        public static void Set(ConsoleColor? fg, ConsoleColor? bg)
        {
            try
            {
                if (bg.HasValue) Console.BackgroundColor = bg.Value;
                if (fg.HasValue) Console.ForegroundColor = fg.Value;
            }
            catch { }
        }

        public static void Reset()
        {
            try { Console.ResetColor(); } catch { }
        }

        public static void Line(string s, ConsoleColor? fg)
        {
            Set(fg, null);
            Console.WriteLine(s);
            Reset();
        }

        public static void Ok(string s) { Line(s, ConsoleColor.Green); }
        public static void Warn(string s) { Line(s, ConsoleColor.Yellow); }
        public static void Err(string s) { Line(s, ConsoleColor.Red); }
        public static void Info(string s) { Line(s, ConsoleColor.Cyan); }
        public static void Dim(string s) { Line(s, ConsoleColor.DarkGray); }

        // partial writes, for lines that mix colours
        public static void Write(string s, ConsoleColor? fg)
        {
            Set(fg, null);
            Console.Write(s);
            Reset();
        }
        public static void Bar(string s, int width) { Set(ConsoleColor.White, ConsoleColor.DarkBlue); Console.WriteLine(Fit(s, width, false)); Reset(); }
        public static void Rule(int width) { Dim(new string('-', Math.Max(1, width))); }

        // ---------- display width ----------
        public static bool IsWide(char c)
        {
            return (c >= 0x1100 && c <= 0x115F) || (c >= 0x2E80 && c <= 0xA4CF)
                || (c >= 0xAC00 && c <= 0xD7A3) || (c >= 0xF900 && c <= 0xFAFF)
                || (c >= 0xFE30 && c <= 0xFE6F) || (c >= 0xFF00 && c <= 0xFF60)
                || (c >= 0xFFE0 && c <= 0xFFE6);
        }

        public static int DispWidth(string s)
        {
            if (s == null) return 0;
            int w = 0;
            foreach (char c in s) w += IsWide(c) ? 2 : 1;
            return w;
        }

        // pad (or clip, with a trailing "..") to an exact display width
        public static string Fit(string s, int width, bool right)
        {
            s = s == null ? "" : s;
            if (DispWidth(s) > width)
            {
                var sb = new StringBuilder();
                int w = 0;
                foreach (char c in s)
                {
                    int cw = IsWide(c) ? 2 : 1;
                    if (w + cw > width - 2) break;
                    sb.Append(c);
                    w += cw;
                }
                s = sb.ToString() + "..";
            }
            int pad = width - DispWidth(s);
            if (pad <= 0) return s;
            return right ? new string(' ', pad) + s : s + new string(' ', pad);
        }

        // ---------- tables ----------
        // rows may be ragged; rightAlign/cellColor may be null.
        public static void Table(string[] headers, List<string[]> rows, bool[] rightAlign, Func<int, int, ConsoleColor?> cellColor)
        {
            int cols = headers.Length;
            var w = new int[cols];
            for (int c = 0; c < cols; c++) w[c] = DispWidth(headers[c]);
            if (rows != null)
                foreach (var r in rows)
                    for (int c = 0; c < cols; c++)
                        if (c < r.Length && DispWidth(r[c]) > w[c]) w[c] = DispWidth(r[c]);

            const int gap = 2;
            int total = gap * (cols - 1);
            for (int c = 0; c < cols; c++) total += w[c];

            Set(ConsoleColor.Cyan, null);
            for (int c = 0; c < cols; c++)
                Console.Write(c < cols - 1 ? Fit(headers[c], w[c] + gap, false) : headers[c]);
            Console.WriteLine();
            Reset();
            Rule(total);

            if (rows == null) return;
            for (int r = 0; r < rows.Count; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    bool right = rightAlign != null && c < rightAlign.Length && rightAlign[c];
                    string cell = Fit(c < rows[r].Length ? rows[r][c] : "", w[c], right);
                    ConsoleColor? col = cellColor == null ? null : cellColor(r, c);
                    if (col.HasValue) Set(col, null);
                    Console.Write(c < cols - 1 ? cell + new string(' ', gap) : cell);
                    if (col.HasValue) Reset();
                }
                Console.WriteLine();
            }
        }

        public static void Table(string[] headers, List<string[]> rows)
        {
            Table(headers, rows, null, null);
        }
    }
}
