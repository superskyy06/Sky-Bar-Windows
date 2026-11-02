using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace SkyBar.CoreServices;

/// <summary>
/// Satu item notifikasi di Pusat Notifikasi Notch (berasal dari Windows atau dari NotchDock sendiri).
/// </summary>
public sealed class NotificationItem : INotifyPropertyChanged
{
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly FontFamily TextFont = new("SF Pro Text, Inter, Segoe UI Variable Text, Segoe UI");

    public string Key { get; init; } = Guid.NewGuid().ToString("N");
    public uint? SystemNotificationId { get; init; }
    public string AppName { get; init; } = "Sky Bar";
    public string AppUserModelId { get; init; } = string.Empty;
    public string LaunchArgs { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public ImageSource? Icon { get; set; }

    /// <summary>Glyph Segoe untuk notifikasi internal; huruf awal nama aplikasi untuk notifikasi Windows tanpa logo.</summary>
    public string GlyphText { get; init; } = string.Empty;
    public bool GlyphIsIcon { get; init; }
    public FontFamily GlyphFont => GlyphIsIcon ? IconFont : TextFont;
    public Brush AccentBrush { get; init; } = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));

    /// <summary>Aksi saat notifikasi dipencet (untuk notifikasi internal, mis. pindah ke tab Pomodoro).</summary>
    public Action? OpenAction { get; init; }

    public bool HasIcon => Icon != null;
    public bool IsSystem => SystemNotificationId.HasValue;
    public string BodyOrPlaceholder => string.IsNullOrWhiteSpace(Body) ? "Ketuk untuk membuka" : Body;

    public string TimeAgoText
    {
        get
        {
            TimeSpan span = DateTimeOffset.Now - Timestamp;
            if (span.TotalSeconds < 45) return "sekarang";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} mnt";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours} jam";
            return Timestamp.LocalDateTime.ToString("d MMM");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshTimeAgo() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeAgoText)));

    internal void NotifyIconChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasIcon)));
    }
}

/// <summary>
/// Pusat notifikasi dinamis: membaca notifikasi Windows (UserNotificationListener) secara real-time,
/// menampung notifikasi internal NotchDock, dan membuka aplikasi asal saat notifikasi dipencet.
/// </summary>
public sealed class NotificationCenterService : IDisposable
{
    private const int MaxItems = 40;

    private static readonly Color[] AccentPalette =
    {
        Color.FromRgb(0x0A, 0x84, 0xFF),
        Color.FromRgb(0x30, 0xD1, 0x58),
        Color.FromRgb(0xFF, 0x9F, 0x0A),
        Color.FromRgb(0xBF, 0x5A, 0xF2),
        Color.FromRgb(0xFF, 0x37, 0x5F),
        Color.FromRgb(0x64, 0xD2, 0xFF),
    };

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _timeAgoTimer;
    private readonly HashSet<uint> _seenSystemIds = new();
    private UserNotificationListener? _listener;
    private bool _isPolling;
    private bool _initialSyncDone;

    public ObservableCollection<NotificationItem> Items { get; } = new();

    /// <summary>Dipicu saat notifikasi BARU masuk (untuk animasi banner dinamis di Notch).</summary>
    public event Action<NotificationItem>? NotificationArrived;
    public event Action<int>? UnreadCountChanged;
    public event Action<string>? StatusChanged;

    public int UnreadCount { get; private set; }
    public bool IsSystemAccessAllowed { get; private set; }
    public string StatusText { get; private set; } = "Menghubungkan ke notifikasi Windows...";

    public NotificationCenterService()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _pollTimer.Tick += async (_, _) => await PollSystemNotificationsAsync();

        _timeAgoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timeAgoTimer.Tick += (_, _) =>
        {
            foreach (var item in Items) item.RefreshTimeAgo();
        };
        _timeAgoTimer.Start();
    }

    public async Task InitializeAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            UserNotificationListenerAccessStatus status = await _listener.RequestAccessAsync();
            IsSystemAccessAllowed = status == UserNotificationListenerAccessStatus.Allowed;

            if (IsSystemAccessAllowed)
            {
                SetStatus("Tersinkron dengan notifikasi Windows");
                await PollSystemNotificationsAsync();
                _pollTimer.Start();
            }
            else
            {
                SetStatus("Akses notifikasi Windows belum diizinkan");
            }
        }
        catch
        {
            _listener = null;
            IsSystemAccessAllowed = false;
            SetStatus("Notifikasi Windows tidak tersedia • notifikasi Sky Bar tetap aktif");
        }
    }

    public async Task RetryAccessAsync()
    {
        _pollTimer.Stop();
        await InitializeAsync();
    }

    public static void OpenWindowsNotificationPrivacySettings()
    {
        TryShellExecute("ms-settings:privacy-notifications");
    }

    // ================================================================
    //  NOTIFIKASI INTERNAL SKY BAR
    // ================================================================

    public NotificationItem PushInternal(string title, string body, string glyph, Color accent, Action? openAction, string appName = "Sky Bar")
    {
        var brush = new SolidColorBrush(accent);
        brush.Freeze();

        var item = new NotificationItem
        {
            AppName = appName,
            Title = title,
            Body = body,
            GlyphText = glyph,
            GlyphIsIcon = true,
            AccentBrush = brush,
            OpenAction = openAction,
            Timestamp = DateTimeOffset.Now
        };

        InsertNewItem(item, announce: true);
        return item;
    }

    // ================================================================
    //  MEMBACA NOTIFIKASI WINDOWS SECARA REAL-TIME
    // ================================================================

    private async Task PollSystemNotificationsAsync()
    {
        if (_listener == null || _isPolling) return;
        _isPolling = true;

        try
        {
            IReadOnlyList<UserNotification> list = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var currentIds = new HashSet<uint>();

            foreach (UserNotification notif in list.OrderBy(n => n.CreationTime))
            {
                currentIds.Add(notif.Id);
                if (_seenSystemIds.Contains(notif.Id)) continue;
                _seenSystemIds.Add(notif.Id);

                NotificationItem? item = await BuildItemFromSystemAsync(notif);
                if (item == null) continue;

                // Saat sinkron pertama, isi daftar tanpa memunculkan banner berulang kali
                InsertNewItem(item, announce: _initialSyncDone);
            }

            // Sinkronkan: hapus notifikasi yang sudah ditutup dari Action Center Windows
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                var existing = Items[i];
                if (existing.SystemNotificationId is uint id && !currentIds.Contains(id))
                {
                    Items.RemoveAt(i);
                    _seenSystemIds.Remove(id);
                }
            }

            _initialSyncDone = true;
        }
        catch
        {
            // Abaikan kegagalan sementara; polling berikutnya akan mencoba lagi
        }
        finally
        {
            _isPolling = false;
        }
    }

    private static async Task<NotificationItem?> BuildItemFromSystemAsync(UserNotification notif)
    {
        try
        {
            string appName = "Aplikasi";
            string aumid = string.Empty;
            try
            {
                appName = notif.AppInfo?.DisplayInfo?.DisplayName ?? appName;
                aumid = notif.AppInfo?.AppUserModelId ?? string.Empty;
            }
            catch { }

            string title = string.Empty;
            string subtitle = string.Empty;
            string body = string.Empty;

            NotificationBinding? binding = notif.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding != null)
            {
                var texts = binding.GetTextElements();
                if (texts.Count > 0) title = texts[0].Text ?? string.Empty;
                if (texts.Count >= 3)
                {
                    subtitle = texts[1].Text ?? string.Empty;
                    body = string.Join("  ", texts.Skip(1).Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
                }
                else if (texts.Count > 1)
                {
                    body = string.Join("  ", texts.Skip(1).Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
                }
            }

            if (string.IsNullOrWhiteSpace(title)) title = appName;

            int paletteIndex = Math.Abs(appName.GetHashCode()) % AccentPalette.Length;
            var accent = new SolidColorBrush(AccentPalette[paletteIndex]);
            accent.Freeze();

            string launchArgs = TryExtractToastLaunchArgs(notif);

            var item = new NotificationItem
            {
                SystemNotificationId = notif.Id,
                AppName = appName,
                AppUserModelId = aumid,
                LaunchArgs = launchArgs,
                Title = title.Trim(),
                Subtitle = subtitle.Trim(),
                Body = body.Trim(),
                Timestamp = notif.CreationTime,
                GlyphText = string.IsNullOrWhiteSpace(appName) ? "?" : appName.Trim()[..1].ToUpperInvariant(),
                GlyphIsIcon = false,
                AccentBrush = accent
            };

            item.Icon = await TryLoadAppLogoAsync(notif);
            return item;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ImageSource?> TryLoadAppLogoAsync(UserNotification notif)
    {
        try
        {
            var logoRef = notif.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(64, 64));
            if (logoRef == null) return null;

            using var winStream = await logoRef.OpenReadAsync();
            using Stream netStream = winStream.AsStreamForRead();
            var buffer = new MemoryStream();
            await netStream.CopyToAsync(buffer);
            if (buffer.Length == 0) return null;
            buffer.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 64;
            bmp.StreamSource = buffer;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void InsertNewItem(NotificationItem item, bool announce)
    {
        void Apply()
        {
            Items.Insert(0, item);
            while (Items.Count > MaxItems) Items.RemoveAt(Items.Count - 1);

            if (announce)
            {
                UnreadCount++;
                UnreadCountChanged?.Invoke(UnreadCount);
                NotificationArrived?.Invoke(item);
            }
        }

        if (_dispatcher.CheckAccess()) Apply();
        else _dispatcher.InvokeAsync(Apply);
    }

    // ================================================================
    //  AKSI: BUKA ASAL, TUTUP, BERSIHKAN
    // ================================================================

    public void MarkAllRead()
    {
        if (UnreadCount == 0) return;
        UnreadCount = 0;
        UnreadCountChanged?.Invoke(0);
    }

    public void Dismiss(NotificationItem item)
    {
        Items.Remove(item);
        if (item.SystemNotificationId is uint id && _listener != null)
        {
            try { _listener.RemoveNotification(id); } catch { }
        }
    }

    public void ClearAll()
    {
        foreach (var item in Items.ToList())
        {
            if (item.SystemNotificationId is uint id && _listener != null)
            {
                try { _listener.RemoveNotification(id); } catch { }
            }
        }
        Items.Clear();
        MarkAllRead();
    }

    /// <summary>
    /// Membuka sumber notifikasi: aksi internal, jendela aplikasi yang sedang berjalan,
    /// serta untuk aplikasi pesan (seperti WhatsApp) langsung mengarahkan ke ruang chat pengirim.
    /// </summary>
    public void OpenOrigin(NotificationItem item)
    {
        _ = OpenOriginAsync(item);
    }

    private async Task OpenOriginAsync(NotificationItem item)
    {
        if (item.OpenAction != null)
        {
            item.OpenAction();
            return;
        }

        bool isWhatsApp = item.AppName.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase) ||
                          item.AppUserModelId.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase);

        // 1. Jika LaunchArgs berformat URI langsung (mis. whatsapp://, tg://, discord://), jalankan URI tersebut
        if (!string.IsNullOrWhiteSpace(item.LaunchArgs) && IsDirectProtocolUri(item.LaunchArgs))
        {
            TryShellExecute(item.LaunchArgs);
            if (!isWhatsApp) return;
        }

        // 2. Untuk aplikasi selain WhatsApp yang memiliki LaunchArgs, aktifkan via IApplicationActivationManager.
        //    (Jangan meneruskan raw toast LaunchArgs ke OnLaunched WhatsApp karena membuat WhatsApp membuka "Chat Saya Sendiri").
        if (!isWhatsApp && !string.IsNullOrWhiteSpace(item.AppUserModelId) && !string.IsNullOrWhiteSpace(item.LaunchArgs))
        {
            TryActivatePackagedAppWithArgs(item.AppUserModelId, item.LaunchArgs);
        }

        // 3. Aktifkan jendela aplikasi yang sedang berjalan (atau luncurkan jika belum terbuka)
        if (!TryActivateRunningWindow(item.AppName, item.AppUserModelId, out IntPtr targetHwnd))
        {
            if (!string.IsNullOrWhiteSpace(item.AppUserModelId))
            {
                if (!TryActivatePackagedAppWithArgs(item.AppUserModelId, isWhatsApp ? null : item.LaunchArgs))
                {
                    TryShellExecute("explorer.exe", $"shell:AppsFolder\\{item.AppUserModelId}");
                }

                // Tunggu sejenak hingga jendela muncul
                for (int i = 0; i < 8 && targetHwnd == IntPtr.Zero; i++)
                {
                    await Task.Delay(160);
                    TryActivateRunningWindow(item.AppName, item.AppUserModelId, out targetHwnd);
                }
            }
        }

        // 4. Khusus WhatsApp: arahkan langsung ke ruang chat teman/grup berdasarkan judul notifikasi
        if (isWhatsApp && targetHwnd != IntPtr.Zero && !string.IsNullOrWhiteSpace(item.Title))
        {
            string senderTitle = item.Title;
            string senderSubtitle = item.Subtitle;
            string senderBody = item.Body;
            await Task.Run(() => NavigateWhatsAppToSenderChat(targetHwnd, senderTitle, senderSubtitle, senderBody));
        }
    }

    private static string TryExtractToastLaunchArgs(UserNotification notif)
    {
        try
        {
            string dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Notifications", "wpndatabase.db");

            if (!File.Exists(dbPath)) return string.Empty;

            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Cache=Shared");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CAST(Payload AS TEXT) FROM Notification WHERE Id = $id LIMIT 1";
            cmd.Parameters.AddWithValue("$id", (long)notif.Id);
            if (cmd.ExecuteScalar() is string payload)
            {
                return ParseLaunchFromToastXml(payload);
            }
        }
        catch { }

        return string.Empty;
    }

    private static string ParseLaunchFromToastXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return string.Empty;
        Match m = Regex.Match(xml, @"<toast\b[^>]*?\blaunch\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (!m.Success) return string.Empty;
        return System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
    }

    private static bool IsDirectProtocolUri(string arg)
    {
        return arg.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase) ||
               arg.StartsWith("tg:", StringComparison.OrdinalIgnoreCase) ||
               arg.StartsWith("discord:", StringComparison.OrdinalIgnoreCase) ||
               arg.StartsWith("ms-", StringComparison.OrdinalIgnoreCase) ||
               arg.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               arg.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    // ----------------------------------------------------------------
    //  COM IApplicationActivationManager (Aktivasi UWP / WinUI dengan LaunchArgs)
    // ----------------------------------------------------------------

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            uint options,
            out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }

    private static bool TryActivatePackagedAppWithArgs(string aumid, string? launchArgs)
    {
        try
        {
            var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
            int hr = mgr.ActivateApplication(
                aumid,
                string.IsNullOrWhiteSpace(launchArgs) ? null : launchArgs,
                0,
                out _);
            return hr >= 0;
        }
        catch
        {
            return false;
        }
    }

    // ----------------------------------------------------------------
    //  Navigasi Otomatis ke Ruang Chat Pengirim di WhatsApp Desktop
    // ----------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_F = 0x46;
    private const byte VK_A = 0x41;
    private const byte VK_V = 0x56;
    private const byte VK_DOWN = 0x28;
    private const byte VK_RETURN = 0x0D;
    private const byte VK_ESCAPE = 0x1B;

    private void NavigateWhatsAppToSenderChat(IntPtr hwnd, string notificationTitle, string notificationSubtitle, string notificationBody)
    {
        try
        {
            var targetCandidates = ExtractWhatsAppChatTargets(notificationTitle, notificationSubtitle, notificationBody);
            if (targetCandidates.Count == 0) return;

            string primaryTarget = targetCandidates[0];

            // Beri waktu jendela WhatsApp selesai dirender di depan layar
            Thread.Sleep(180);

            string? previousClipboard = null;
            _dispatcher.Invoke(() =>
            {
                try
                {
                    if (Clipboard.ContainsText())
                        previousClipboard = Clipboard.GetText();
                    Clipboard.SetText(primaryTarget);
                }
                catch { }
            });

            SetForegroundWindow(hwnd);
            Thread.Sleep(80);

            // Tekan Ctrl+F untuk memfokuskan kotak pencarian daftar chat di kiri atas WhatsApp
            SendKeyCombo(VK_CONTROL, VK_F);
            Thread.Sleep(200);

            // Pilih semua teks pencarian lama lalu tempel nama teman/grup
            SendKeyCombo(VK_CONTROL, VK_A);
            Thread.Sleep(50);
            SendKeyCombo(VK_CONTROL, VK_V);

            // Tunggu daftar "Chats" selesai difilter hingga kartu chat teman/grup muncul di urutan teratas
            Thread.Sleep(450);

            // Pindahkan sorotan dari kotak pencarian ke kartu hasil chat teratas (Panah Bawah), lalu buka ruang obrolannya (Enter)
            SendSingleKey(VK_DOWN, isExtended: true);
            Thread.Sleep(110);
            SendSingleKey(VK_RETURN, isExtended: false);

            if (previousClipboard != null)
            {
                Thread.Sleep(120);
                _dispatcher.InvokeAsync(() =>
                {
                    try { Clipboard.SetText(previousClipboard); } catch { }
                });
            }
        }
        catch { }
    }

    private static List<string> ExtractWhatsAppChatTargets(string rawTitle, string rawSubtitle, string rawBody)
    {
        var list = new List<string>();

        void AddCandidate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            string trimmed = s.Trim().TrimStart('~').Trim();
            if (trimmed.Length >= 2 &&
                !trimmed.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase) &&
                !list.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(trimmed);
            }
        }

        string clean = rawTitle.Trim();
        if (string.IsNullOrWhiteSpace(clean) || clean.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase))
            return list;

        // Jika ada sufiks jumlah pesan seperti "Nama Grup (3 pesan)" -> ekstrak "Nama Grup" terlebih dahulu
        Match paren = Regex.Match(clean, @"^(.+?)\s*\(\d+.*?\)$");
        if (paren.Success)
        {
            clean = paren.Groups[1].Value.Trim();
        }

        // Jika notifikasi grup berformat "Pengirim • Nama Grup" atau "Pengirim @ Nama Grup"
        string[] groupSeparators = { " • ", " @ ", " dalam ", " in " };
        foreach (string sep in groupSeparators)
        {
            int idx = clean.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (idx > 0 && idx + sep.Length < clean.Length)
            {
                string groupPart = clean[(idx + sep.Length)..].Trim();
                string senderPart = clean[..idx].Trim();
                AddCandidate(groupPart);
                AddCandidate(senderPart);
            }
        }

        AddCandidate(clean);
        AddCandidate(rawSubtitle);

        // Jika body diawali "Nama Grup: ..." atau "Pengirim: ...", tambahkan bagian sebelum titik dua sebagai kandidat cadangan
        if (!string.IsNullOrWhiteSpace(rawBody))
        {
            int colonIdx = rawBody.IndexOf(": ", StringComparison.Ordinal);
            if (colonIdx >= 2 && colonIdx <= 35)
            {
                AddCandidate(rawBody[..colonIdx]);
            }
        }

        return list;
    }

    private static List<AutomationElement> GetWhatsAppAutomationRoots(IntPtr topHwnd)
    {
        var roots = new List<AutomationElement>();
        var childHwnds = new List<IntPtr>();

        try
        {
            EnumChildWindows(topHwnd, (childHwnd, _) =>
            {
                var sb = new StringBuilder(128);
                if (GetClassName(childHwnd, sb, sb.Capacity) > 0)
                {
                    string cls = sb.ToString();
                    if (cls.Contains("CoreWindow", StringComparison.OrdinalIgnoreCase) ||
                        cls.Contains("DesktopWindowXamlSource", StringComparison.OrdinalIgnoreCase) ||
                        cls.Contains("Chrome_RenderWidgetHostHWND", StringComparison.OrdinalIgnoreCase))
                    {
                        childHwnds.Add(childHwnd);
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }

        foreach (IntPtr child in childHwnds)
        {
            try
            {
                var el = AutomationElement.FromHandle(child);
                if (el != null) roots.Add(el);
            }
            catch { }
        }

        try
        {
            var topEl = AutomationElement.FromHandle(topHwnd);
            if (topEl != null) roots.Add(topEl);
        }
        catch { }

        return roots;
    }

    private static bool TryFillWhatsAppSearchBoxViaUia(IntPtr hwnd, string searchText)
    {
        try
        {
            var cacheReq = new CacheRequest
            {
                TreeFilter = Automation.RawViewCondition,
                AutomationElementMode = AutomationElementMode.Full
            };
            cacheReq.Add(AutomationElement.NameProperty);
            cacheReq.Add(AutomationElement.AutomationIdProperty);
            cacheReq.Add(AutomationElement.ClassNameProperty);
            cacheReq.Add(AutomationElement.ControlTypeProperty);
            cacheReq.Add(AutomationElement.BoundingRectangleProperty);

            using (cacheReq.Activate())
            {
                foreach (var root in GetWhatsAppAutomationRoots(hwnd))
                {
                    Rect winRect = root.Cached.BoundingRectangle;
                    if (winRect.IsEmpty || winRect.Width < 200) continue;

                    double maxSearchX = winRect.Left + Math.Min(480, winRect.Width * 0.48);
                    double maxSearchY = winRect.Top + Math.Max(180, winRect.Height * 0.24);

                    var elements = root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
                    foreach (AutomationElement el in elements)
                    {
                        try
                        {
                            Rect r = el.Cached.BoundingRectangle;
                            if (r.IsEmpty || r.Left < winRect.Left || r.Left > maxSearchX || r.Top < winRect.Top || r.Top > maxSearchY)
                                continue;

                            string cls = el.Cached.ClassName ?? string.Empty;
                            string aid = el.Cached.AutomationId ?? string.Empty;
                            string name = el.Cached.Name ?? string.Empty;
                            var ct = el.Cached.ControlType;

                            bool isSearchEdit =
                                ct == ControlType.Edit ||
                                cls.Contains("TextBox", StringComparison.OrdinalIgnoreCase) ||
                                aid.Contains("Search", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Cari", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Search", StringComparison.OrdinalIgnoreCase);

                            if (!isSearchEdit) continue;

                            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out object? valObj) &&
                                valObj is ValuePattern valPattern && !valPattern.Current.IsReadOnly)
                            {
                                try { el.SetFocus(); } catch { }
                                valPattern.SetValue(searchText);
                                return true;
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
        return false;
    }

    private static bool TryClickWhatsAppChatInLeftSidebar(IntPtr hwnd, List<string> targetNames, double minTopRatio)
    {
        try
        {
            bool targetAllowsSelf = targetNames.Any(t =>
                t.Contains("(Anda)", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("(You)", StringComparison.OrdinalIgnoreCase));

            var cacheReq = new CacheRequest
            {
                TreeFilter = Automation.RawViewCondition,
                AutomationElementMode = AutomationElementMode.Full
            };
            cacheReq.Add(AutomationElement.NameProperty);
            cacheReq.Add(AutomationElement.ClassNameProperty);
            cacheReq.Add(AutomationElement.ControlTypeProperty);
            cacheReq.Add(AutomationElement.BoundingRectangleProperty);

            AutomationElement? bestElement = null;
            Rect bestRect = Rect.Empty;
            int bestScore = 0;

            using (cacheReq.Activate())
            {
                foreach (var root in GetWhatsAppAutomationRoots(hwnd))
                {
                    Rect winRect = root.Cached.BoundingRectangle;
                    if (winRect.IsEmpty || winRect.Width < 200 || winRect.Height < 200)
                    {
                        if (GetWindowRect(hwnd, out RECT raw))
                            winRect = new Rect(raw.Left, raw.Top, raw.Right - raw.Left, raw.Bottom - raw.Top);
                    }
                    if (winRect.Width < 200 || winRect.Height < 200) continue;

                    // Area daftar chat di sisi kiri jendela WhatsApp
                    double minX = winRect.Left + 38;
                    double maxX = winRect.Left + Math.Min(520, Math.Max(310, winRect.Width * 0.48));
                    double minY = winRect.Top + Math.Max(96, winRect.Height * minTopRatio);
                    double maxY = winRect.Bottom - 12;

                    AutomationElementCollection descendants = root.FindAll(
                        TreeScope.Descendants,
                        System.Windows.Automation.Condition.TrueCondition);

                    // Pertama: kumpulkan koordinat Y dari baris "Chat Saya Sendiri / (Anda) / (You)" agar tidak pernah terklik
                    var selfChatRowTops = new List<double>();
                    if (!targetAllowsSelf)
                    {
                        foreach (AutomationElement el in descendants)
                        {
                            try
                            {
                                string? n = el.Cached.Name;
                                if (string.IsNullOrWhiteSpace(n)) continue;
                                if (n.Contains("(Anda)", StringComparison.OrdinalIgnoreCase) ||
                                    n.Contains("(You)", StringComparison.OrdinalIgnoreCase) ||
                                    n.Contains("Pesan untuk diri sendiri", StringComparison.OrdinalIgnoreCase) ||
                                    n.Contains("Message yourself", StringComparison.OrdinalIgnoreCase))
                                {
                                    Rect sr = el.Cached.BoundingRectangle;
                                    if (!sr.IsEmpty && sr.Left >= minX && sr.Left <= maxX)
                                        selfChatRowTops.Add(sr.Top + sr.Height * 0.5);
                                }
                            }
                            catch { }
                        }
                    }

                    foreach (AutomationElement el in descendants)
                    {
                        try
                        {
                            string? name = el.Cached.Name;
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            name = name.Trim();

                            // Jangan pernah memilih baris "Chat Saya Sendiri / (Anda)" jika notifikasi berasal dari teman/grup
                            if (!targetAllowsSelf &&
                                (name.Contains("(Anda)", StringComparison.OrdinalIgnoreCase) ||
                                 name.Contains("(You)", StringComparison.OrdinalIgnoreCase) ||
                                 name.Contains("Pesan untuk diri sendiri", StringComparison.OrdinalIgnoreCase) ||
                                 name.Contains("Message yourself", StringComparison.OrdinalIgnoreCase)))
                            {
                                continue;
                            }

                            Rect r = el.Cached.BoundingRectangle;
                            if (r.IsEmpty || double.IsInfinity(r.Left) || double.IsNaN(r.Left)) continue;
                            if (r.Width < 14 || r.Height < 10 || r.Height > 125) continue;

                            // Abaikan kotak pencarian sendiri agar kita mengklik hasil chat di bawahnya, bukan kotak input
                            var ct = el.Cached.ControlType;
                            string cls = el.Cached.ClassName ?? string.Empty;
                            if (ct == ControlType.Edit || cls.Contains("TextBox", StringComparison.OrdinalIgnoreCase))
                                continue;

                            double refX = r.Left + Math.Min(50, r.Width * 0.35);
                            double centerY = r.Top + r.Height * 0.5;
                            if (refX < minX || refX > maxX || centerY < minY || centerY > maxY)
                                continue;

                            // Pastikan elemen ini tidak berada pada baris vertikal yang sama dengan "Chat Saya Sendiri (Anda)"
                            if (!targetAllowsSelf && selfChatRowTops.Any(selfY => Math.Abs(centerY - selfY) < 34))
                                continue;

                            int score = 0;
                            for (int i = 0; i < targetNames.Count; i++)
                            {
                                string target = targetNames[i];
                                int priorityBonus = Math.Max(0, 6 - i * 2); // Prioritaskan kandidat utama (Nama Grup / Nama Teman)

                                if (name.Equals(target, StringComparison.OrdinalIgnoreCase) ||
                                    name.TrimStart('~').Trim().Equals(target, StringComparison.OrdinalIgnoreCase))
                                {
                                    score = Math.Max(score, 100 + priorityBonus);
                                }
                                else if (name.StartsWith(target + ",", StringComparison.OrdinalIgnoreCase) ||
                                         name.StartsWith(target + "\n", StringComparison.OrdinalIgnoreCase) ||
                                         name.StartsWith(target + "  ", StringComparison.OrdinalIgnoreCase))
                                {
                                    score = Math.Max(score, 92 + priorityBonus);
                                }
                                else if (name.StartsWith(target, StringComparison.OrdinalIgnoreCase))
                                {
                                    score = Math.Max(score, 84 + priorityBonus);
                                }
                                else if (target.Length >= 3 && name.Contains(target, StringComparison.OrdinalIgnoreCase))
                                {
                                    score = Math.Max(score, 68 + priorityBonus);
                                }
                            }

                            if (score <= 0) continue;

                            if (score > bestScore || (score == bestScore && !bestRect.IsEmpty && r.Top < bestRect.Top))
                            {
                                bestScore = score;
                                bestElement = el;
                                bestRect = r;
                            }
                        }
                        catch { }
                    }

                    if (bestElement != null && bestScore >= 84)
                        break;
                }
            }

            if (bestElement == null || bestRect.IsEmpty) return false;

            // Coba pola seleksi UI Automation terlebih dahulu
            try
            {
                if (bestElement.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? selObj) &&
                    selObj is SelectionItemPattern selPattern)
                {
                    selPattern.Select();
                }
                else if (bestElement.TryGetCurrentPattern(InvokePattern.Pattern, out object? invObj) &&
                         invObj is InvokePattern invPattern)
                {
                    invPattern.Invoke();
                }
            }
            catch { }

            // Klik langsung pada baris chat di sidebar kiri agar ruang obrolan kanan langsung berpindah ke teman/grup tersebut
            int clickX = (int)Math.Round(bestRect.Left + Math.Min(65, bestRect.Width * 0.45));
            int clickY = (int)Math.Round(bestRect.Top + bestRect.Height * 0.5);
            ClickScreenPointPreservingCursor(clickX, clickY);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ClickScreenPointPreservingCursor(int x, int y)
    {
        GetCursorPos(out POINT prevPos);
        SetCursorPos(x, y);
        Thread.Sleep(30);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(30);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(30);
        SetCursorPos(prevPos.X, prevPos.Y);
    }

    private static void SendKeyCombo(byte modifierVk, byte keyVk)
    {
        byte modScan = (byte)MapVirtualKey(modifierVk, 0);
        byte keyScan = (byte)MapVirtualKey(keyVk, 0);
        keybd_event(modifierVk, modScan, 0, UIntPtr.Zero);
        Thread.Sleep(15);
        keybd_event(keyVk, keyScan, 0, UIntPtr.Zero);
        Thread.Sleep(25);
        keybd_event(keyVk, keyScan, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(15);
        keybd_event(modifierVk, modScan, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static void SendSingleKey(byte keyVk, bool isExtended = false)
    {
        byte scan = (byte)MapVirtualKey(keyVk, 0);
        uint downFlags = isExtended ? KEYEVENTF_EXTENDEDKEY : 0u;
        uint upFlags = (isExtended ? KEYEVENTF_EXTENDEDKEY : 0u) | KEYEVENTF_KEYUP;
        keybd_event(keyVk, scan, downFlags, UIntPtr.Zero);
        Thread.Sleep(25);
        keybd_event(keyVk, scan, upFlags, UIntPtr.Zero);
    }

    // ----------------------------------------------------------------
    //  Win32: temukan & aktifkan jendela aplikasi asal
    // ----------------------------------------------------------------

    private const int SW_RESTORE = 9;
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private static bool TryActivateRunningWindow(string appName, string aumid, out IntPtr matchedHwnd)
    {
        matchedHwnd = IntPtr.Zero;
        try
        {
            var keywords = BuildMatchKeywords(appName, aumid);
            if (keywords.Count == 0) return false;

            int selfPid = Environment.ProcessId;
            IntPtr bestHwnd = IntPtr.Zero;
            int bestScore = 0;

            // Cari melalui EnumWindows agar jendela UWP/WinUI (ApplicationFrameHost / WhatsApp) selalu ditemukan
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                var sb = new StringBuilder(256);
                if (GetWindowText(hWnd, sb, sb.Capacity) <= 0) return true;
                string title = sb.ToString().ToLowerInvariant();

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == selfPid) return true;

                string procName = string.Empty;
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    procName = p.ProcessName.ToLowerInvariant();
                }
                catch { }

                int score = 0;
                foreach (string k in keywords)
                {
                    if (title == k || procName == k) score = Math.Max(score, 4);
                    else if (procName.Contains(k) || (k.Contains(procName) && procName.Length >= 4)) score = Math.Max(score, 3);
                    else if (title.StartsWith(k) || title.EndsWith(k)) score = Math.Max(score, 2);
                    else if (title.Contains(k)) score = Math.Max(score, 1);
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestHwnd = hWnd;
                }

                return true;
            }, IntPtr.Zero);

            if (bestHwnd == IntPtr.Zero) return false;

            matchedHwnd = bestHwnd;
            if (IsIconic(bestHwnd))
            {
                ShowWindow(bestHwnd, SW_RESTORE);
                Thread.Sleep(120);
            }

            // Tekan-lepas Alt agar Windows mengizinkan perpindahan fokus ke jendela tujuan
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            return SetForegroundWindow(bestHwnd);
        }
        catch
        {
            return false;
        }
    }

    private static List<string> BuildMatchKeywords(string appName, string aumid)
    {
        var list = new List<string>();

        if (!string.IsNullOrWhiteSpace(appName))
        {
            string n = appName.ToLowerInvariant().Trim();
            list.Add(n);
            list.Add(n.Replace(" ", string.Empty));
            string first = n.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (first.Length >= 4 && first != "microsoft" && first != "google") list.Add(first);
            if (n.Contains("chrome")) list.Add("chrome");
            if (n.Contains("edge")) list.Add("msedge");
            if (n.Contains("outlook")) list.Add("outlook");
            if (n.Contains("teams")) list.Add("teams");
        }

        if (!string.IsNullOrWhiteSpace(aumid))
        {
            // Contoh AUMID: "5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App" → "whatsapp"
            string a = aumid.ToLowerInvariant();
            int bang = a.IndexOf('!');
            if (bang > 0) a = a[..bang];
            int underscore = a.IndexOf('_');
            if (underscore > 0) a = a[..underscore];
            string last = a.Split('.', '\\').LastOrDefault() ?? a;
            last = last.Replace("desktop", string.Empty);
            if (last.Length >= 3) list.Add(last);
            if (a.EndsWith(".exe")) list.Add(Path.GetFileNameWithoutExtension(a));
        }

        return list.Where(k => k.Length >= 3).Distinct().ToList();
    }

    private static void TryShellExecute(string fileName, string? args = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args ?? string.Empty,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        StatusChanged?.Invoke(text);
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _timeAgoTimer.Stop();
    }
}

