using System.Reflection;
using CMakeDapLink.App;

namespace CMakeDapLink.UiTests;

internal static class LiveButtons
{
    public static int Run(string[] args)
    {
        using var form = new MainForm { Size = new(1730, 1100), StartPosition = FormStartPosition.CenterScreen, TopMost = true };
        form.Show(); form.Activate(); Pump(2000);
        var output = args.FirstOrDefault() ?? Path.GetFullPath("live-buttons.png");
        // Capture the actual on-screen pixels; DrawToBitmap uses a different background path.
        using var screenshot = new Bitmap(form.Width, form.Height);
        using (var graphics = Graphics.FromImage(screenshot)) graphics.CopyFromScreen(form.Location, Point.Empty, screenshot.Size);
        screenshot.Save(output);
        foreach (var name in new[] { "_navSetup", "_configure", "_autoRepair", "_browseProject" })
        {
            var button = (Button)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            var screen = button.PointToScreen(Point.Empty);
            var rect = new Rectangle(screen.X - form.Left, screen.Y - form.Top, button.Width, button.Height);
            var black = 0;
            for (var y = rect.Top; y < rect.Bottom; y++) for (var x = rect.Left; x < rect.Right; x++)
            {
                if (x - rect.Left > 2 && rect.Right - 1 - x > 2 && y - rect.Top > 2 && rect.Bottom - 1 - y > 2) continue;
                var color = screenshot.GetPixel(x, y); if (color.R < 35 && color.G < 35 && color.B < 35) black++;
            }
            var opaque = (bool)typeof(Control).GetMethod("GetStyle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, [ControlStyles.Opaque])!;
            Console.WriteLine($"SCREEN {name} Enabled={button.Enabled}, Opaque={opaque}, ParentColor={button.Parent!.BackColor}, black edge pixels={black}");
        }
        form.Close(); return 0;
    }
    private static void Pump(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(20); }
    }
}
