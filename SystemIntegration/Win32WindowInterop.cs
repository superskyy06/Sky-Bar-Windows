using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SkyBar.SystemIntegration;

public record DisplayMonitorInfo(int Index, string DisplayName, Rect DipBounds, bool IsPrimary);

public static class Win32WindowInterop
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    public static void ConfigureFloatingOverlay(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public static IReadOnlyList<DisplayMonitorInfo> GetMonitors(Visual? referenceVisual = null)
    {
        double dpiScaleX = 1.0;
        double dpiScaleY = 1.0;

        if (referenceVisual != null)
        {
            var dpi = VisualTreeHelper.GetDpi(referenceVisual);
            if (dpi.DpiScaleX > 0) dpiScaleX = dpi.DpiScaleX;
            if (dpi.DpiScaleY > 0) dpiScaleY = dpi.DpiScaleY;
        }

        var rawList = new List<(RECT rect, bool isPrimary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref info))
            {
                bool primary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
                rawList.Add((info.rcMonitor, primary));
            }
            return true;
        }, IntPtr.Zero);

        // Urutkan agar Primary display selalu berada di indeks 0
        rawList.Sort((a, b) => b.isPrimary.CompareTo(a.isPrimary));

        var result = new List<DisplayMonitorInfo>();
        for (int i = 0; i < rawList.Count; i++)
        {
            var (r, isPrimary) = rawList[i];
            var dipRect = new Rect(
                r.Left / dpiScaleX,
                r.Top / dpiScaleY,
                Math.Max(1, (r.Right - r.Left) / dpiScaleX),
                Math.Max(1, (r.Bottom - r.Top) / dpiScaleY));

            string name = isPrimary
                ? "Primary display"
                : $"Display {i + 1} ({r.Right - r.Left}×{r.Bottom - r.Top})";

            result.Add(new DisplayMonitorInfo(i, name, dipRect, isPrimary));
        }

        if (result.Count == 0)
        {
            result.Add(new DisplayMonitorInfo(
                0,
                "Primary display",
                new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight),
                true));
        }

        return result;
    }
}
