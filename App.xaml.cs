using System.Threading;
using System.Windows;
using SkyBar.AnimationRendering;
using SkyBar.CoreServices;
using SkyBar.UI.Notch;
using SkyBar.UI.Settings;

namespace SkyBar;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    private SettingsService? _settingsService;
    private MediaListenerService? _mediaService;
    private ClipboardManagerService? _clipboardService;
    private FileShelfService? _fileShelfService;
    private ProductivityWidgetsService? _productivityService;
    private NotificationCenterService? _notificationService;

    private NotchWindow? _notchWindow;
    private SettingsWindow? _settingsWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, "SkyBar_Windows_SingleInstance_Mutex", out bool isNewInstance);
        if (!isNewInstance)
        {
            Shutdown();
            return;
        }

        _settingsService = new SettingsService();
        SpringAnimator.AnimationsEnabled = _settingsService.Current.EnableAnimations;
        ThemeEngineService.ApplyTheme(_settingsService.Current.ThemeMode, _settingsService.Current.AccentColorHex);

        _mediaService = new MediaListenerService();
        _clipboardService = new ClipboardManagerService();
        _fileShelfService = new FileShelfService();
        _productivityService = new ProductivityWidgetsService();
        _notificationService = new NotificationCenterService();

        _notchWindow = new NotchWindow(
            _mediaService,
            _clipboardService,
            _fileShelfService,
            _productivityService,
            _settingsService,
            _notificationService);
        _notchWindow.Show();

        await _mediaService.InitializeAsync();
        await _notificationService.InitializeAsync();
    }

    public void OpenSettingsWindow()
    {
        if (_settingsService == null) return;
        _settingsWindow ??= new SettingsWindow(_settingsService);
        if (_settingsWindow.WindowState == WindowState.Minimized)
            _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mediaService?.Dispose();
        _clipboardService?.Dispose();
        _notificationService?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
