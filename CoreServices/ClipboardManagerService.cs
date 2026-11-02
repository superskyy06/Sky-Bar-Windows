using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SkyBar.SystemIntegration;

namespace SkyBar.CoreServices;

public enum ClipboardEntryType { Text, Image }

public class ClipboardEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public ClipboardEntryType EntryType { get; init; }
    public string PreviewText { get; init; } = string.Empty;
    public string? FullText { get; init; }
    public string? LocalImagePath { get; init; }
    public DateTime CopiedAt { get; init; } = DateTime.Now;
}

public sealed class ClipboardManagerService : IDisposable
{
    private const int MaxHistoryCount = 20;
    private readonly string _storageDir;
    private readonly string _historyJsonPath;
    private HwndSource? _hwndSource;
    private IntPtr _lastExternalForegroundHwnd;
    private IntPtr _ownNotchHwnd;
    private bool _isInternalClipboardSet;

    public ObservableCollection<ClipboardEntry> Items { get; } = new();

    public ClipboardManagerService()
    {
        _storageDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sky Bar", "ClipboardCache");
        Directory.CreateDirectory(_storageDir);
        _historyJsonPath = Path.Combine(_storageDir, "clipboard_history.json");
        LoadHistoryFromDisk();
    }

    public void AttachToWindow(Window notchWindow)
    {
        _ownNotchHwnd = new WindowInteropHelper(notchWindow).EnsureHandle();
        _hwndSource = HwndSource.FromHwnd(_ownNotchHwnd);
        _hwndSource?.AddHook(WndProc);
        Win32SystemHooks.AddClipboardFormatListener(_ownNotchHwnd);
    }

    public void RecordLastActiveWindow()
    {
        IntPtr fg = Win32SystemHooks.GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != _ownNotchHwnd) _lastExternalForegroundHwnd = fg;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32SystemHooks.WM_CLIPBOARDUPDATE && !_isInternalClipboardSet) CaptureClipboardSnapshot();
        return IntPtr.Zero;
    }

    private void CaptureClipboardSnapshot()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText().Trim();
                if (string.IsNullOrEmpty(text) || Items.FirstOrDefault()?.FullText == text) return;
                InsertEntry(new ClipboardEntry
                {
                    EntryType = ClipboardEntryType.Text,
                    FullText = text,
                    PreviewText = text.Length > 45 ? text[..45].Replace("\r\n", " ") + "…" : text.Replace("\r\n", " ")
                });
            }
        }
        catch { }
    }

    private void InsertEntry(ClipboardEntry entry)
    {
        Items.Insert(0, entry);
        while (Items.Count > MaxHistoryCount) Items.RemoveAt(Items.Count - 1);
        SaveHistoryToDisk();
    }

    public async Task AutoPasteEntryAsync(ClipboardEntry entry)
    {
        try
        {
            _isInternalClipboardSet = true;
            if (entry.EntryType == ClipboardEntryType.Text && entry.FullText != null)
                Clipboard.SetText(entry.FullText);
            await Task.Delay(40);
            Win32SystemHooks.SendCtrlVToWindow(_lastExternalForegroundHwnd);
        }
        finally
        {
            await Task.Delay(80);
            _isInternalClipboardSet = false;
        }
    }

    private void SaveHistoryToDisk()
    {
        try { File.WriteAllText(_historyJsonPath, JsonSerializer.Serialize(Items)); } catch { }
    }

    private void LoadHistoryFromDisk()
    {
        try
        {
            if (!File.Exists(_historyJsonPath)) return;
            var loaded = JsonSerializer.Deserialize<ClipboardEntry[]>(File.ReadAllText(_historyJsonPath));
            if (loaded != null) foreach (var item in loaded) Items.Add(item);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_ownNotchHwnd != IntPtr.Zero) Win32SystemHooks.RemoveClipboardFormatListener(_ownNotchHwnd);
        _hwndSource?.RemoveHook(WndProc);
    }
}
