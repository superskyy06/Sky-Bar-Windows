using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace SkyBar.CoreServices;

public class AppSettings
{
    public bool ShowNotch { get; set; } = true;
    public bool AutoHideNotchInFullscreenGame { get; set; } = true;
    public string ThemeMode { get; set; } = "Auto";
    public string AccentColorHex { get; set; } = "#0A84FF";
    public double ScaleFactor { get; set; } = 1.0;
    public bool EnableAnimations { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;

    // Smart Notch General & Closed Notch settings
    public string NotchStyle { get; set; } = "Notch"; // "Notch", "Pill", "Bar"
    public bool Use24HourClock { get; set; } = true;
    public int ScreenEdgeDistancePx { get; set; } = 0;
    public int TargetMonitorIndex { get; set; } = 0;
    public string ClosedLeftMode { get; set; } = "Auto"; // "Auto", "Pomodoro", "SystemStats", "Nothing"
    public bool ShowMusicVisualizer { get; set; } = true;

    public List<string> PinnedExecutablePaths { get; set; } = new()
    {
        @"C:\Windows\explorer.exe",
        @"C:\Windows\System32\notepad.exe"
    };
}

public sealed class SettingsService
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRegistryName = "SkyBarForWindows";
    private readonly string _settingsFilePath;

    public AppSettings Current { get; private set; } = new();
    public event Action<AppSettings>? SettingsChanged;

    public SettingsService()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string dir = Path.Combine(localAppData, "Sky Bar");
        Directory.CreateDirectory(dir);
        _settingsFilePath = Path.Combine(dir, "settings.json");

        // Migrasi pengaturan lama jika ada
        if (!File.Exists(_settingsFilePath))
        {
            string legacyPath = Path.Combine(localAppData, "NotchDock", "settings.json");
            if (File.Exists(legacyPath))
            {
                try { File.Copy(legacyPath, _settingsFilePath, overwrite: false); } catch { }
            }
        }

        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsFilePath));
                if (loaded != null) Current = loaded;
            }
        }
        catch { }
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);
            ApplyStartupRegistry(Current.StartWithWindows);
            SettingsChanged?.Invoke(Current);
        }
        catch { }
    }

    public void NotifyAndSave()
    {
        SettingsChanged?.Invoke(Current);
        Save();
    }

    private static void ApplyStartupRegistry(bool enable)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true);
            if (key == null) return;
            if (enable && !string.IsNullOrEmpty(Environment.ProcessPath))
                key.SetValue(AppRegistryName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(AppRegistryName, throwOnMissingValue: false);
        }
        catch { }
    }
}
