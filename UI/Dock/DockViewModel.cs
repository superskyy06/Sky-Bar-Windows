using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SkyBar.CoreServices;
using SkyBar.SystemIntegration;

namespace SkyBar.UI.Dock;

public partial class DockAppItem : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public ImageSource? AppIcon { get; init; }
    public bool IsPinned { get; set; }
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private IntPtr _activeWindowHandle;
    [ObservableProperty] private double _currentSize = 48.0;
}

public class DockViewModel
{
    public ObservableCollection<DockAppItem> PinnedApps { get; } = new();
    public ObservableCollection<DockAppItem> ActiveUnpinnedApps { get; } = new();
    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _processSyncTimer;

    public DockViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;
        foreach (string path in _settingsService.Current.PinnedExecutablePaths)
        {
            if (File.Exists(path))
            {
                PinnedApps.Add(new DockAppItem
                {
                    Name = Path.GetFileNameWithoutExtension(path),
                    ExecutablePath = path,
                    AppIcon = ExtractIcon(path),
                    IsPinned = true
                });
            }
        }
        _processSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _processSyncTimer.Tick += (_, _) => SyncRunningApplications();
        SyncRunningApplications();
        _processSyncTimer.Start();
    }

    public void SyncRunningApplications()
    {
        var guiProcesses = Process.GetProcesses()
            .Where(p => p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.MainWindowTitle))
            .ToList();

        var seenActivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pinned in PinnedApps)
        {
            var match = guiProcesses.FirstOrDefault(p => string.Equals(TryGetProcessPath(p), pinned.ExecutablePath, StringComparison.OrdinalIgnoreCase));
            pinned.IsRunning = match != null;
            pinned.ActiveWindowHandle = match?.MainWindowHandle ?? IntPtr.Zero;
            if (match != null) seenActivePaths.Add(pinned.ExecutablePath);
        }

        var currentUnpinned = ActiveUnpinnedApps.ToList();
        var activeUnpinnedProcesses = new List<(Process proc, string path)>();

        foreach (var proc in guiProcesses)
        {
            if (proc.ProcessName.Equals("Sky Bar", StringComparison.OrdinalIgnoreCase) ||
                proc.ProcessName.Equals("SkyBar", StringComparison.OrdinalIgnoreCase) ||
                proc.ProcessName.Equals("NotchDock", StringComparison.OrdinalIgnoreCase) ||
                proc.ProcessName.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase) ||
                proc.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) continue;

            string? path = TryGetProcessPath(proc);
            if (string.IsNullOrEmpty(path) || seenActivePaths.Contains(path)) continue;
            seenActivePaths.Add(path);
            activeUnpinnedProcesses.Add((proc, path));
        }

        foreach (var existing in currentUnpinned)
        {
            if (!activeUnpinnedProcesses.Any(x => x.path.Equals(existing.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
                ActiveUnpinnedApps.Remove(existing);
        }

        foreach (var (proc, path) in activeUnpinnedProcesses.Take(6))
        {
            var existing = ActiveUnpinnedApps.FirstOrDefault(x => x.ExecutablePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.ActiveWindowHandle = proc.MainWindowHandle;
                existing.IsRunning = true;
            }
            else
            {
                ActiveUnpinnedApps.Add(new DockAppItem
                {
                    Name = string.IsNullOrWhiteSpace(proc.MainWindowTitle) ? proc.ProcessName : proc.MainWindowTitle,
                    ExecutablePath = path,
                    AppIcon = ExtractIcon(path),
                    IsPinned = false,
                    IsRunning = true,
                    ActiveWindowHandle = proc.MainWindowHandle
                });
            }
        }
    }

    public void PinApplication(string rawFilePath)
    {
        string? exePath = ShortcutResolver.ResolveExecutablePath(rawFilePath);
        if (string.IsNullOrEmpty(exePath)) return;
        if (PinnedApps.Any(p => p.ExecutablePath.Equals(exePath, StringComparison.OrdinalIgnoreCase))) return;

        var activeMatch = ActiveUnpinnedApps.FirstOrDefault(a => a.ExecutablePath.Equals(exePath, StringComparison.OrdinalIgnoreCase));
        if (activeMatch != null)
        {
            ActiveUnpinnedApps.Remove(activeMatch);
            activeMatch.IsPinned = true;
            PinnedApps.Add(activeMatch);
        }
        else
        {
            PinnedApps.Add(new DockAppItem
            {
                Name = Path.GetFileNameWithoutExtension(exePath),
                ExecutablePath = exePath,
                AppIcon = ExtractIcon(exePath),
                IsPinned = true
            });
        }
        SavePinnedList();
        SyncRunningApplications();
    }

    public void UnpinApplication(DockAppItem item)
    {
        if (!item.IsPinned) return;
        PinnedApps.Remove(item);
        item.IsPinned = false;
        if (item.IsRunning) ActiveUnpinnedApps.Add(item);
        SavePinnedList();
    }

    public void QuitApplication(DockAppItem item)
    {
        if (!item.IsRunning || item.ActiveWindowHandle == IntPtr.Zero) return;
        foreach (var proc in Process.GetProcesses().Where(p => p.MainWindowHandle == item.ActiveWindowHandle))
        {
            try { proc.CloseMainWindow(); } catch { }
        }
    }

    public void LaunchOrFocusApp(DockAppItem item)
    {
        if (item.IsRunning && item.ActiveWindowHandle != IntPtr.Zero)
        {
            if (Win32SystemHooks.IsIconic(item.ActiveWindowHandle))
                Win32SystemHooks.ShowWindow(item.ActiveWindowHandle, Win32SystemHooks.SW_RESTORE);
            Win32SystemHooks.SetForegroundWindow(item.ActiveWindowHandle);
            return;
        }
        if (File.Exists(item.ExecutablePath))
            Process.Start(new ProcessStartInfo { FileName = item.ExecutablePath, UseShellExecute = true });
    }

    private void SavePinnedList()
    {
        _settingsService.Current.PinnedExecutablePaths = PinnedApps.Select(p => p.ExecutablePath).ToList();
        _settingsService.Save();
    }

    private static string? TryGetProcessPath(Process process)
    {
        try { return process.MainModule?.FileName; } catch { return null; }
    }

    private static ImageSource? ExtractIcon(string filePath)
    {
        try
        {
            using Icon? ico = Icon.ExtractAssociatedIcon(filePath);
            if (ico == null) return null;
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }
}
