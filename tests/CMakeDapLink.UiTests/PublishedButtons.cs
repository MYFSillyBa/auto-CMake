using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CMakeDapLink.UiTests;

internal static class PublishedButtons
{
    public static int Run(string[] args)
    {
        using var process = Process.Start(new ProcessStartInfo(Path.GetFullPath(args[0])) { UseShellExecute = true })!;
        try
        {
            process.WaitForInputIdle(10000); process.Refresh();
            var readyDeadline = DateTime.UtcNow.AddSeconds(15);
            while (process.MainWindowHandle == IntPtr.Zero && !process.HasExited && DateTime.UtcNow < readyDeadline)
            { Application.DoEvents(); Thread.Sleep(50); process.Refresh(); }
            var window = process.MainWindowHandle;
            if (window == IntPtr.Zero) throw new Exception("发布版主窗口尚未创建。");
            // Make this owned verification window visible without depending on foreground permission.
            SetWindowPos(window, new IntPtr(-1), 100, 80, 1730, 1100, 0x0040);
            var until = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(20); }
            GetWindowRect(window, out var bounds);
            using var screenshot = new Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
            using (var graphics = Graphics.FromImage(screenshot)) graphics.CopyFromScreen(new(bounds.Left, bounds.Top), Point.Empty, screenshot.Size);
            screenshot.Save(Path.GetFullPath(args[1]));
            var checkedButtons = 0; var blackPixels = 0;
            EnumChildWindows(window, (child, _) => {
                var name = new StringBuilder(256); GetClassName(child, name, name.Capacity);
                if (!IsWindowVisible(child) || !name.ToString().Contains("BUTTON", StringComparison.OrdinalIgnoreCase)) return true;
                GetWindowRect(child, out var rect); var black = 0;
                for (var y = rect.Top; y < rect.Bottom; y++) for (var x = rect.Left; x < rect.Right; x++)
                {
                    if (x - rect.Left > 2 && rect.Right - 1 - x > 2 && y - rect.Top > 2 && rect.Bottom - 1 - y > 2) continue;
                    var color = screenshot.GetPixel(x - bounds.Left, y - bounds.Top);
                    if (color.R < 35 && color.G < 35 && color.B < 35) black++;
                }
                checkedButtons++; blackPixels += black;
                Console.WriteLine($"发布版按钮 {checkedButtons}: 黑色边缘像素 {black}"); return true;
            }, IntPtr.Zero);
            if (checkedButtons < 10 || blackPixels > 0) throw new Exception($"真实窗口检查：按钮={checkedButtons}，黑色边缘像素={blackPixels}");
            Console.WriteLine($"PASS 发布版真实窗口，{checkedButtons} 个可见按钮均无黑色边框。"); return 0;
        }
        finally
        {
            if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(5000)) process.Kill(true); }
        }
    }
    private delegate bool EnumWindow(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out WindowRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
