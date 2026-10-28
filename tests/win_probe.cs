using System;
using System.Drawing;
using System.Windows.Forms;

// win_probe: a plain window app used as the "managed program" stand-in for
// hide/restore-state tests.  --max: start maximized (mimics an app that
// maximizes itself on startup); --norm W H: start at a fixed normal size.
// Prints its pid, then stays up until killed.
class WinProbe
{
    [STAThread]
    static void Main(string[] args)
    {
        int w = 640, h = 400;
        bool max = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--max") max = true;
            else if (args[i] == "--norm" && i + 2 < args.Length)
            { w = int.Parse(args[i + 1]); h = int.Parse(args[i + 2]); i += 2; }
        }
        Application.EnableVisualStyles();
        var f = new Form { Text = "WinProbe", StartPosition = FormStartPosition.CenterScreen };
        if (max) f.WindowState = FormWindowState.Maximized;
        else f.Size = new Size(w, h);
        f.Show();
        Console.WriteLine("win_probe pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + (max ? " (maximized)" : " (normal " + w + "x" + h + ")"));
        Application.Run(f);
    }
}
