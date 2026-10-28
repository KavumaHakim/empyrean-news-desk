using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NewsDesk.App.Services;

public class DisplayInfo
{
    public int Index { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsPrimary { get; set; }

    public string Description =>
        $"{DisplayName} ({Width}x{Height}) {(IsPrimary ? "[Primary Laptop]" : "[Projector/HDMI]")}";
}

public class DisplayManager : IDisposable
{
    public event EventHandler? DisplaysChanged;

    public DisplayManager()
    {
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        DisplaysChanged?.Invoke(this, EventArgs.Empty);
    }

    public List<DisplayInfo> GetConnectedDisplays()
    {
        var list = new List<DisplayInfo>();
        int index = 0;

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT lprcMonitor, IntPtr dwData) =>
        {
            var info = new NativeMethods.MONITORINFOEX();
            info.cbSize = Marshal.SizeOf(info);

            if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                int width = info.rcMonitor.Right - info.rcMonitor.Left;
                int height = info.rcMonitor.Bottom - info.rcMonitor.Top;
                bool isPrimary = (info.dwFlags & 1) != 0; // MONITORINFOF_PRIMARY = 1

                // Win32 monitor rects are physical pixels; WPF Left/Top/Width/Height are
                // device-independent pixels (96 DIP/inch). Scale by the monitor's effective DPI
                // so the borderless broadcast window is truly fullscreen at any scaling factor.
                var dpi = NativeMethods.GetMonitorEffectiveDpi(hMonitor);
                double scale = 96.0 / (dpi > 0 ? dpi : 96.0);

                list.Add(new DisplayInfo
                {
                    Index = index++,
                    DeviceName = new string(info.szDevice).TrimEnd('\0'),
                    DisplayName = isPrimary ? "Primary Display" : $"External Display / Projector {index}",
                    X = (int)Math.Round(info.rcMonitor.Left * scale),
                    Y = (int)Math.Round(info.rcMonitor.Top * scale),
                    Width = (int)Math.Round(width * scale),
                    Height = (int)Math.Round(height * scale),
                    IsPrimary = isPrimary
                });
            }
            return true;
        }, IntPtr.Zero);

        if (list.Count == 0)
        {
            // Fallback to primary screen bounds
            list.Add(new DisplayInfo
            {
                Index = 0,
                DisplayName = "Primary Display",
                X = 0,
                Y = 0,
                Width = (int)System.Windows.SystemParameters.PrimaryScreenWidth,
                Height = (int)System.Windows.SystemParameters.PrimaryScreenHeight,
                IsPrimary = true
            });
        }

        return list;
    }

    public DisplayInfo? GetSecondaryOrProjectorDisplay()
    {
        var displays = GetConnectedDisplays();
        return displays.FirstOrDefault(d => !d.IsPrimary) ?? displays.FirstOrDefault();
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    private static class NativeMethods
    {
        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public char[] szDevice;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        private const int MDT_EFFECTIVE_DPI = 0;

        /// <summary>
        /// Returns the monitor's effective DPI (96 = 100% scaling), or 0 when unsupported.
        /// </summary>
        public static int GetMonitorEffectiveDpi(IntPtr hMonitor)
        {
            try
            {
                var shcore = GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY);
                if (shcore == 0 && dpiX > 0)
                {
                    return (int)dpiX;
                }
            }
            catch (DllNotFoundException)
            {
                // Pre-Win10: assume 96 DPI
            }
            return 0;
        }

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
    }
}
