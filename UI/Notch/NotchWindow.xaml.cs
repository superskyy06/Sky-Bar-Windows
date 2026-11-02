using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SkyBar.AnimationRendering;
using SkyBar.CoreServices;
using SkyBar.SystemIntegration;

namespace SkyBar.UI.Notch;

public partial class NotchWindow : Window
{
    private const double CollapsedIdleWidth = 200;
    private const double CollapsedMediaWidth = 252;
    private const double BarCollapsedIdleWidth = 370;
    private const double BarCollapsedMediaWidth = 416;
    private const double CollapsedHeight = 32;
    private const double ExpandedWidth = 594;
    private const double ExpandedHeight = 252;

    private const double CollapsedRadius = 12;
    private const double PillCollapsedRadius = 16;
    private const double BarCollapsedRadius = 10;
    private const double ExpandedRadius = 28;
    private const double CollapsedShoulder = 7;
    private const double ExpandedShoulder = 14;
    private const double CollapsedContentInset = 14;

    private const double CanvasWidth = 650;
    private const double CanvasHeight = 276;
    private const double WindowPaddingX = 80;
    private const double WindowPaddingY = 50;

    private static readonly SolidColorBrush TransparentStroke = FreezeBrush(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush PillStroke = FreezeBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush MutedIconBrush = FreezeBrush(Color.FromRgb(0x8E, 0x8E, 0x98));

    private bool _isExpanded;
    private bool _isPinnedOpen;
    private bool _isUpdatingAudioList;
    private bool _isDraggingMediaSlider;
    private bool _isDraggingTopNavBar;
    private bool _isSyncingSliders;
    private bool _hasMedia;
    private bool _isMediaPlaying;
    private string _currentTrackTitle = string.Empty;
    private string _latestPomodoroText = "25:00";
    private string _latestWeatherCompactText = "30°C";
    private int _activeLyricIndex = -1;
    private int _activeTopTabIndex;
    private int _pulseVersion;

    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _fullscreenCheckTimer;
    private readonly DispatcherTimer _collapseDelayTimer;
    private readonly DispatcherTimer _equalizerTimer;
    private readonly DispatcherTimer _mediaTimelineTimer;
    private readonly Random _random = new();

    private readonly SpringValue _width;
    private readonly SpringValue _height;
    private readonly SpringValue _expandedReveal;
    private readonly SpringValue _collapsedReveal;
    private readonly SpringValue _coverFade;
    private readonly SpringValue _lyricShiftSpring;
    private readonly SpringValue _topNavPillSpring;
    private readonly SpringValue _tabStageShiftSpring;
    private readonly SpringValue _tabStageFadeSpring;
    private readonly SpringValue[] _eqBars;
    private readonly RadioButton[] _topNavRadios;

    private readonly MediaListenerService _mediaService;
    private readonly ClipboardManagerService _clipboardService;
    private readonly FileShelfService _fileShelfService;
    private readonly ProductivityWidgetsService _productivityService;
    private readonly SettingsService _settingsService;
    private readonly NotificationCenterService _notificationService;

    // Banner notifikasi dinamis (ala Dynamic Island)
    private const double BannerWidth = 392;
    private const double BannerHeight = 74;
    private bool _isShowingBanner;
    private NotificationItem? _bannerItem;
    private readonly DispatcherTimer _bannerTimer;
    private readonly SpringValue _bannerReveal;
    private bool? _lastChargingState;
    private bool _lowBatteryNotified;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    public NotchWindow(
        MediaListenerService mediaService,
        ClipboardManagerService clipboardService,
        FileShelfService fileShelfService,
        ProductivityWidgetsService productivityService,
        SettingsService settingsService,
        NotificationCenterService notificationService)
    {
        InitializeComponent();

        _mediaService = mediaService;
        _clipboardService = clipboardService;
        _fileShelfService = fileShelfService;
        _productivityService = productivityService;
        _settingsService = settingsService;
        _notificationService = notificationService;

        _bannerReveal = new SpringValue(0, 0.34, 0.82);
        _bannerReveal.ValueChanged += ApplyBannerReveal;
        _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _bannerTimer.Tick += (_, _) =>
        {
            if (IsCursorOverNotch())
                return; // tetap tampil selama kursor berada di atas banner
            HideNotificationBanner();
        };

        _width = new SpringValue(CurrentCollapsedWidth) { RestDisplacement = 0.01, RestVelocity = 0.2 };
        _height = new SpringValue(CollapsedHeight) { RestDisplacement = 0.01, RestVelocity = 0.2 };
        _width.ValueChanged += _ => UpdateNotchShape();
        _height.ValueChanged += _ => UpdateNotchShape();

        _expandedReveal = new SpringValue(0);
        _expandedReveal.ValueChanged += ApplyExpandedReveal;

        _collapsedReveal = new SpringValue(1);
        _collapsedReveal.ValueChanged += v => CollapsedContentGrid.Opacity = Math.Clamp(v, 0, 1);

        _coverFade = new SpringValue(1);
        _coverFade.ValueChanged += v => CoverArtImage.Opacity = Math.Clamp(v, 0, 1);

        _lyricShiftSpring = new SpringValue(0, 0.28, 0.86);
        _lyricShiftSpring.ValueChanged += v => ActiveLyricShift.Y = v;

        _topNavPillSpring = new SpringValue(0, 0.30, 0.86) { RestDisplacement = 0.005, RestVelocity = 0.05 };
        _topNavPillSpring.ValueChanged += v =>
        {
            TopNavPillShift.X = v;
            double stretch = 1.0 + Math.Clamp(Math.Abs(_topNavPillSpring.Velocity) / 1450.0, 0, 0.16);
            TopNavPillScale.ScaleX = stretch;
            TopNavPillScale.ScaleY = 1.0 - (stretch - 1.0) * 0.25;
        };

        _tabStageShiftSpring = new SpringValue(0, 0.30, 0.88) { RestDisplacement = 0.01, RestVelocity = 0.05 };
        _tabStageShiftSpring.ValueChanged += v => TabContentShift.X = v;

        _tabStageFadeSpring = new SpringValue(1, 0.26, 0.92) { RestDisplacement = 0.005, RestVelocity = 0.02 };
        _tabStageFadeSpring.ValueChanged += v =>
        {
            double clamped = Math.Clamp(v, 0, 1);
            TabContentStageGrid.Opacity = clamped;
            double scale = 0.985 + 0.015 * clamped;
            TabContentScale.ScaleX = scale;
            TabContentScale.ScaleY = scale;
        };
        _topNavRadios = new[]
        {
            TabHubRadio,
            TabMusicLyricsRadio,
            TabWeatherRadio,
            TabShelfRadio,
            TabClipboardRadio,
            TabPomodoroRadio,
            TabNotificationsRadio
        };

        var bars = new[] { EqBar0, EqBar1, EqBar2, EqBar3 };
        _eqBars = new SpringValue[bars.Length];
        for (int i = 0; i < bars.Length; i++)
        {
            var scale = (ScaleTransform)bars[i].RenderTransform;
            var spring = new SpringValue(0.3, 0.22, 0.55);
            spring.ValueChanged += v => scale.ScaleY = Math.Clamp(v, 0.15, 1.1);
            _eqBars[i] = spring;
        }

        ClipboardListBox.ItemsSource = _clipboardService.Items;
        FileShelfItemsControl.ItemsSource = _fileShelfService.ShelfItems;
        _fileShelfService.ShelfItems.CollectionChanged += OnShelfItemsCollectionChanged;
        UpdateShelfBadgeCount();
        RefreshAudioDeviceOptions();

        _mediaService.MediaStateUpdated += OnMediaStateUpdated;
        _mediaService.LyricsUpdated += OnLyricsUpdated;
        _productivityService.PomodoroTick += OnPomodoroTick;
        _productivityService.SystemStatsUpdated += OnSystemStatsUpdated;
        _productivityService.WeatherUpdated += OnWeatherUpdated;
        OnWeatherUpdated(_productivityService.LatestWeather);

        NotificationsListBox.ItemsSource = _notificationService.Items;
        _notificationService.Items.CollectionChanged += (_, _) => UpdateNotificationCounters();
        _notificationService.NotificationArrived += item => Dispatcher.InvokeAsync(() => ShowNotificationBanner(item));
        _notificationService.UnreadCountChanged += _ => Dispatcher.InvokeAsync(UpdateNotificationCounters);
        _notificationService.StatusChanged += _ => Dispatcher.InvokeAsync(UpdateNotificationStatus);
        UpdateNotificationStatus();
        UpdateNotificationCounters();

        _settingsService.SettingsChanged += _ =>
        {
            ApplyScale();
            UpdateClockDisplay();
            RefreshCollapsedIndicators();
            UpdateNotchShape();
            EvaluateNotchVisibility();
        };

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _clockTimer.Tick += (_, _) => UpdateClockDisplay();
        UpdateClockDisplay();
        _clockTimer.Start();

        _fullscreenCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _fullscreenCheckTimer.Tick += (_, _) => EvaluateNotchVisibility();
        _fullscreenCheckTimer.Start();

        _collapseDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _collapseDelayTimer.Tick += (_, _) =>
        {
            _collapseDelayTimer.Stop();
            if (!_isPinnedOpen && !_isDraggingMediaSlider && !_isDraggingTopNavBar && !AudioOutputComboBox.IsDropDownOpen && !IsCursorOverNotch())
                CollapseNotch();
        };

        _equalizerTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(140) };
        _equalizerTimer.Tick += (_, _) =>
        {
            foreach (var bar in _eqBars) bar.AnimateTo(0.25 + _random.NextDouble() * 0.8);
        };

        // Timer real-time 50ms (20 FPS) untuk menggerakkan slide musik & menyinkronkan lirik secara mulus
        _mediaTimelineTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _mediaTimelineTimer.Tick += (_, _) => TickMediaTimelineAndLyrics();
        _mediaTimelineTimer.Start();

        ApplyScale();
        RefreshCollapsedIndicators();
        UpdateNotchShape();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PositionAtTopCenter();
        Win32WindowInterop.ConfigureFloatingOverlay(this);
        _clipboardService.AttachToWindow(this);
    }

    public void PositionAtTopCenter()
    {
        var monitors = Win32WindowInterop.GetMonitors(this);
        int idx = Math.Clamp(_settingsService.Current.TargetMonitorIndex, 0, Math.Max(0, monitors.Count - 1));
        Rect bounds = monitors[idx].DipBounds;

        Left = Math.Round(bounds.Left + (bounds.Width - Width) / 2.0);
        Top = Math.Round(bounds.Top + Math.Clamp(_settingsService.Current.ScreenEdgeDistancePx, 0, 60));
    }

    private void ApplyScale()
    {
        double scale = Math.Clamp(_settingsService.Current.ScaleFactor, 0.5, 2.0);
        NotchScale.ScaleX = scale;
        NotchScale.ScaleY = scale;
        Width = Math.Ceiling((CanvasWidth + WindowPaddingX) * scale);
        Height = Math.Ceiling((CanvasHeight + WindowPaddingY) * scale);
        PositionAtTopCenter();
    }

    private void EvaluateNotchVisibility()
    {
        if (!_settingsService.Current.ShowNotch)
        {
            if (Visibility != Visibility.Collapsed) Visibility = Visibility.Collapsed;
            UpdateEqualizerState();
            return;
        }

        if (_settingsService.Current.AutoHideNotchInFullscreenGame &&
            Win32SystemHooks.IsFullscreenGameRunning())
        {
            if (Visibility != Visibility.Collapsed) Visibility = Visibility.Collapsed;
            UpdateEqualizerState();
            return;
        }

        if (Visibility != Visibility.Visible)
        {
            Visibility = Visibility.Visible;
            UpdateEqualizerState();
        }
    }

    // ================================================================
    //  BENTUK NOTCH / PILL / BAR
    // ================================================================

    private void UpdateNotchShape()
    {
        double w = Math.Max(_width.Value, 40);
        double h = Math.Max(_height.Value, 20);

        double progress = Math.Clamp((h - CollapsedHeight) / (ExpandedHeight - CollapsedHeight), 0, 1);
        string style = _settingsService.Current.NotchStyle ?? "Notch";
        bool isFloatingShape = style.Equals("Pill", StringComparison.OrdinalIgnoreCase) ||
                               style.Equals("Bar", StringComparison.OrdinalIgnoreCase) ||
                               _settingsService.Current.ScreenEdgeDistancePx > 0;

        Geometry geometry;
        if (isFloatingShape)
        {
            double baseRadius = style.Equals("Bar", StringComparison.OrdinalIgnoreCase)
                ? BarCollapsedRadius
                : PillCollapsedRadius;
            double radius = Lerp(baseRadius, ExpandedRadius - 2, progress);
            radius = Math.Max(0, Math.Min(radius, Math.Min(w / 2.0, h / 2.0)));

            double left = (CanvasWidth - w) / 2.0;
            var rectGeo = new RectangleGeometry(new Rect(left, 0, w, h), radius, radius);
            rectGeo.Freeze();
            geometry = rectGeo;

            NotchShape.Stroke = PillStroke;
            NotchShadow.Opacity = 0.35 + 0.30 * progress;
        }
        else
        {
            double radius = Lerp(CollapsedRadius, ExpandedRadius, progress);
            double shoulder = Lerp(CollapsedShoulder, ExpandedShoulder, progress);
            radius = Math.Max(0, Math.Min(radius, Math.Min(w / 2.0, h - shoulder)));

            double ox = (CanvasWidth - (w + 2 * shoulder)) / 2.0;
            double left = ox + shoulder;
            double right = left + w;

            var streamGeo = new StreamGeometry();
            using (StreamGeometryContext ctx = streamGeo.Open())
            {
                ctx.BeginFigure(new Point(ox, 0), isFilled: true, isClosed: true);
                ctx.ArcTo(new Point(left, shoulder), new Size(shoulder, shoulder), 0, false, SweepDirection.Clockwise, true, true);
                ctx.LineTo(new Point(left, h - radius), true, true);
                ctx.ArcTo(new Point(left + radius, h), new Size(radius, radius), 0, false, SweepDirection.Counterclockwise, true, true);
                ctx.LineTo(new Point(right - radius, h), true, true);
                ctx.ArcTo(new Point(right, h - radius), new Size(radius, radius), 0, false, SweepDirection.Counterclockwise, true, true);
                ctx.LineTo(new Point(right, shoulder), true, true);
                ctx.ArcTo(new Point(right + shoulder, 0), new Size(shoulder, shoulder), 0, false, SweepDirection.Clockwise, true, true);
            }
            streamGeo.Freeze();
            geometry = streamGeo;

            NotchShape.Stroke = progress > 0.1 ? PillStroke : TransparentStroke;
            NotchShadow.Opacity = 0.60 * progress;
        }

        NotchShape.Data = geometry;
        ContentHost.Clip = geometry;
        CollapsedContentGrid.Width = Math.Max(0, w - 2 * CollapsedContentInset);
    }

    private void ApplyExpandedReveal(double reveal)
    {
        double opacity = Math.Clamp((reveal - 0.12) / 0.88, 0, 1);
        ExpandedContentGrid.Opacity = opacity;
        double scale = 0.92 + 0.08 * reveal;
        ExpandedScale.ScaleX = scale;
        ExpandedScale.ScaleY = scale;
        ExpandedShift.Y = -10 * (1 - reveal);
    }

    private double CurrentCollapsedWidth
    {
        get
        {
            string style = _settingsService.Current.NotchStyle ?? "Notch";
            if (style.Equals("Bar", StringComparison.OrdinalIgnoreCase))
                return _hasMedia && _isMediaPlaying ? BarCollapsedMediaWidth : BarCollapsedIdleWidth;

            bool hasWideLeftText = (_settingsService.Current.ClosedLeftMode is "Pomodoro" or "Weather") ||
                                   (_hasMedia && _isMediaPlaying);
            return hasWideLeftText ? CollapsedMediaWidth : CollapsedIdleWidth;
        }
    }

    private bool IsCursorOverNotch()
    {
        try
        {
            if (NotchShape.Data != null && GetCursorPos(out POINT pt) && PresentationSource.FromVisual(NotchRoot) != null)
            {
                Point local = NotchRoot.PointFromScreen(new Point(pt.X, pt.Y));
                return NotchShape.Data.FillContains(local);
            }
        }
        catch { }
        return NotchRoot.IsMouseOver;
    }

    // ================================================================
    //  EKSPANSI / KOLAPS / PIN
    // ================================================================

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        _clipboardService.RecordLastActiveWindow();
        base.OnPreviewMouseMove(e);
    }

    private void NotchRoot_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseDelayTimer.Stop();

        // Saat banner notifikasi tampil, jangan langsung ekspansi agar banner bisa dipencet
        if (_isShowingBanner)
        {
            _bannerTimer.Stop();
            return;
        }

        ExpandNotch();
    }

    private void NotchRoot_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_isShowingBanner)
        {
            _bannerTimer.Stop();
            _bannerTimer.Interval = TimeSpan.FromSeconds(2);
            _bannerTimer.Start();
            return;
        }

        if (!_isPinnedOpen && !_isDraggingMediaSlider && !_isDraggingTopNavBar && !AudioOutputComboBox.IsDropDownOpen)
            ScheduleCollapse(160);
    }

    private void ScheduleCollapse(int delayMs)
    {
        if (_isPinnedOpen || _isDraggingMediaSlider || _isDraggingTopNavBar || AudioOutputComboBox.IsDropDownOpen) return;
        _collapseDelayTimer.Stop();
        _collapseDelayTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _collapseDelayTimer.Start();
    }

    public void ExpandNotch()
    {
        if (_isExpanded) return;
        _isExpanded = true;

        if (_isShowingBanner) DismissBannerVisualOnly();

        _width.AnimateTo(ExpandedWidth, SpringPresets.Expand);
        _height.AnimateTo(ExpandedHeight, 0.50, 0.80);
        _expandedReveal.AnimateTo(1, 0.40, 0.90);
        _collapsedReveal.AnimateTo(0, 0.14, 1.0);

        ExpandedContentGrid.IsHitTestVisible = true;
        UpdateEqualizerState();
    }

    public void CollapseNotch()
    {
        if (!_isExpanded) return;
        _isExpanded = false;
        _collapseDelayTimer.Stop();

        ExpandedContentGrid.IsHitTestVisible = false;
        _expandedReveal.AnimateTo(0, 0.18, 1.0);
        _collapsedReveal.AnimateTo(1, 0.42, 1.0);
        _width.AnimateTo(CurrentCollapsedWidth, SpringPresets.Collapse);
        _height.AnimateTo(CollapsedHeight, 0.36, 0.95);

        UpdateEqualizerState();
    }

    private void PinNotch_Click(object sender, RoutedEventArgs e)
    {
        _isPinnedOpen = !_isPinnedOpen;
        if (_isPinnedOpen)
            PinNotchGlyph.SetResourceReference(TextBlock.ForegroundProperty, "MacAccentBrush");
        else
            PinNotchGlyph.Foreground = MutedIconBrush;
    }

    private void ForceCollapse_Click(object sender, RoutedEventArgs e)
    {
        _isPinnedOpen = false;
        PinNotchGlyph.Foreground = MutedIconBrush;
        CollapseNotch();
    }

    public async Task TriggerEventPulseAsync(int holdDurationMs = 2500)
    {
        int version = ++_pulseVersion;
        ExpandNotch();
        await Task.Delay(holdDurationMs);
        if (version == _pulseVersion && !_isPinnedOpen && !IsCursorOverNotch()) CollapseNotch();
    }

    // ================================================================
    //  PILIHAN PERANGKAT OUTPUT AUDIO DARI BAR NOTCH
    // ================================================================

    private void RefreshAudioDeviceOptions()
    {
        _isUpdatingAudioList = true;
        try
        {
            var devices = AudioEndpointService.GetPlaybackDevices();
            AudioOutputComboBox.Items.Clear();

            int selectedIndex = 0;
            for (int i = 0; i < devices.Count; i++)
            {
                var dev = devices[i];
                AudioOutputComboBox.Items.Add(new ComboBoxItem
                {
                    Content = dev.Name,
                    Tag = dev.Id
                });
                if (dev.IsDefault) selectedIndex = i;
            }

            if (AudioOutputComboBox.Items.Count > 0)
                AudioOutputComboBox.SelectedIndex = selectedIndex;
        }
        finally
        {
            _isUpdatingAudioList = false;
        }
    }

    private void AudioOutputComboBox_DropDownOpened(object sender, EventArgs e)
    {
        _collapseDelayTimer.Stop();
        RefreshAudioDeviceOptions();
    }

    private void AudioOutputComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingAudioList) return;
        if (AudioOutputComboBox.SelectedItem is ComboBoxItem { Tag: string deviceId })
        {
            AudioEndpointService.SetDefaultPlaybackDevice(deviceId);
        }
    }

    // ================================================================
    //  NAVIGASI TAB DI DALAM NOTCH (BISA DIGESER DENGAN KURSOR)
    // ================================================================

    private const double TopNavSlotWidth = 32.0; // Width 30 + Margin 1,0

    private void TopNavTab_Checked(object sender, RoutedEventArgs e)
    {
        if (HubViewGrid == null) return;

        int prevTabIndex = _activeTopTabIndex;

        HubViewGrid.Visibility = Visibility.Collapsed;
        MusicLyricsViewGrid.Visibility = Visibility.Collapsed;
        ShelfViewGrid.Visibility = Visibility.Collapsed;
        ClipboardViewGrid.Visibility = Visibility.Collapsed;
        PomodoroViewGrid.Visibility = Visibility.Collapsed;
        WeatherViewGrid.Visibility = Visibility.Collapsed;
        NotificationsViewGrid.Visibility = Visibility.Collapsed;

        if (ReferenceEquals(sender, TabHubRadio))
        {
            _activeTopTabIndex = 0;
            HubViewGrid.Visibility = Visibility.Visible;
        }
        else if (ReferenceEquals(sender, TabMusicLyricsRadio))
        {
            _activeTopTabIndex = 1;
            MusicLyricsViewGrid.Visibility = Visibility.Visible;
        }
        else if (ReferenceEquals(sender, TabWeatherRadio))
        {
            _activeTopTabIndex = 2;
            WeatherViewGrid.Visibility = Visibility.Visible;
        }
        else if (ReferenceEquals(sender, TabShelfRadio))
        {
            _activeTopTabIndex = 3;
            ShelfViewGrid.Visibility = Visibility.Visible;
        }
        else if (ReferenceEquals(sender, TabClipboardRadio))
        {
            _activeTopTabIndex = 4;
            ClipboardViewGrid.Visibility = Visibility.Visible;
        }
        else if (ReferenceEquals(sender, TabPomodoroRadio))
        {
            _activeTopTabIndex = 5;
            PomodoroViewGrid.Visibility = Visibility.Visible;
        }
        else if (ReferenceEquals(sender, TabNotificationsRadio))
        {
            _activeTopTabIndex = 6;
            NotificationsViewGrid.Visibility = Visibility.Visible;
            _notificationService?.MarkAllRead();
        }

        if (_activeTopTabIndex != prevTabIndex && _tabStageShiftSpring != null && _tabStageFadeSpring != null)
        {
            int dir = _activeTopTabIndex > prevTabIndex ? 1 : -1;
            _tabStageShiftSpring.SnapTo(dir * 16.0);
            _tabStageShiftSpring.AnimateTo(0, 0.30, 0.88);
            _tabStageFadeSpring.SnapTo(0.28);
            _tabStageFadeSpring.AnimateTo(1.0, 0.26, 0.92);
        }

        if (!_isDraggingTopNavBar && _topNavPillSpring != null)
        {
            _topNavPillSpring.AnimateTo(_activeTopTabIndex * TopNavSlotWidth, 0.32, 0.86);
        }
    }

    private void TopNavBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingTopNavBar = true;
        _collapseDelayTimer.Stop();
        TopNavBarBorder.CaptureMouse();
        ScrubTopNavBarToCursor(e.GetPosition(TopNavTabsPanel).X, isInitialDown: true);
        e.Handled = true;
    }

    private void TopNavBar_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingTopNavBar) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndTopNavBarDrag();
            return;
        }

        ScrubTopNavBarToCursor(e.GetPosition(TopNavTabsPanel).X, isInitialDown: false);
        e.Handled = true;
    }

    private void TopNavBar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingTopNavBar) return;
        EndTopNavBarDrag();
        e.Handled = true;
    }

    private void EndTopNavBarDrag()
    {
        _isDraggingTopNavBar = false;
        if (TopNavBarBorder.IsMouseCaptured)
            TopNavBarBorder.ReleaseMouseCapture();

        _topNavPillSpring.AnimateTo(_activeTopTabIndex * TopNavSlotWidth, 0.32, 0.86);
    }

    private void ScrubTopNavBarToCursor(double cursorX, bool isInitialDown)
    {
        if (_topNavRadios == null || _topNavRadios.Length == 0) return;

        double maxPillX = (_topNavRadios.Length - 1) * TopNavSlotWidth;
        double targetPillX = Math.Clamp(cursorX - (TopNavSlotWidth / 2.0), 0, maxPillX);

        if (isInitialDown)
            _topNavPillSpring.AnimateTo(targetPillX, 0.22, 0.88);
        else
            _topNavPillSpring.AnimateTo(targetPillX, 0.11, 0.96);

        int idx = Math.Clamp((int)Math.Floor(cursorX / TopNavSlotWidth), 0, _topNavRadios.Length - 1);
        if (_topNavRadios[idx].IsChecked != true)
        {
            _topNavRadios[idx].IsChecked = true;
        }
    }

    private void TopNavBar_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_topNavRadios == null || _topNavRadios.Length == 0) return;
        int step = e.Delta < 0 ? 1 : -1;
        int nextIdx = Math.Clamp(_activeTopTabIndex + step, 0, _topNavRadios.Length - 1);
        if (nextIdx != _activeTopTabIndex)
        {
            _topNavRadios[nextIdx].IsChecked = true;
        }
        e.Handled = true;
    }

    private void OpenLyricsSlide_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        TabMusicLyricsRadio.IsChecked = true;
    }

    private void OpenLyricsSlideButton_Click(object sender, RoutedEventArgs e)
    {
        TabMusicLyricsRadio.IsChecked = true;
    }

    private void WeatherBadge_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (TabWeatherRadio.IsChecked == true)
            OpenWeatherWebsite();
        else
            TabWeatherRadio.IsChecked = true;
    }

    private void OpenWeatherWebsite_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        OpenWeatherWebsite();
    }

    private void OpenWeatherWebsite_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OpenWeatherWebsite();
    }

    private void OpenWeatherWebsite()
    {
        string url = _productivityService.GetWeatherWebsiteUrl();
        TryStartProcess(url);
    }

    private void QuickOpenShelf_Click(object sender, RoutedEventArgs e)
    {
        TabShelfRadio.IsChecked = true;
    }

    // ================================================================
    //  MEDIA, SLIDE MUSIK INTERAKTIF & LIRIK TERSINKRONISASI
    // ================================================================

    private void OnMediaStateUpdated(MediaTrackInfo? info, bool isTrackChanged)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (info == null)
            {
                _hasMedia = false;
                _isMediaPlaying = false;
                _currentTrackTitle = string.Empty;
                TrackTitleText.Text = "Not playing";
                TrackArtistText.Text = "Play something to see it here";
                LyricsTrackTitleText.Text = "Not playing";
                LyricsTrackArtistText.Text = "Play music to sync lyrics";
                LyricsSourceBadgeText.Text = "Synced • Media Player";
                PlayPauseGlyph.Text = "\uE768";
                LyricsPlayPauseGlyph.Text = "\uE768";
                CoverArtImage.Source = null;
                LyricsCoverArtImage.Source = null;
                MiniCoverBrush.ImageSource = null;

                _isSyncingSliders = true;
                MediaSeekSlider.Value = 0;
                LyricsViewSeekSlider.Value = 0;
                _isSyncingSliders = false;

                MediaElapsedText.Text = "0:00";
                MediaDurationText.Text = "--:--";
                LyricsElapsedText.Text = "0:00";
                LyricsDurationText.Text = "--:--";
                HubLiveLyricText.Text = "Putar lagu untuk sinkronisasi lirik";
                RefreshCollapsedIndicators();
                return;
            }

            bool coverChanged = !ReferenceEquals(CoverArtImage.Source, info.CoverArt);

            _hasMedia = true;
            _isMediaPlaying = info.IsPlaying;
            _currentTrackTitle = info.Title;

            TrackTitleText.Text = info.Title;
            TrackArtistText.Text = info.Artist;
            LyricsTrackTitleText.Text = info.Title;
            LyricsTrackArtistText.Text = info.Artist;
            LyricsSourceBadgeText.Text = $"Synced • {info.SourceAppName}";

            string playGlyph = info.IsPlaying ? "\uE769" : "\uE768";
            PlayPauseGlyph.Text = playGlyph;
            LyricsPlayPauseGlyph.Text = playGlyph;

            CoverArtImage.Source = info.CoverArt;
            LyricsCoverArtImage.Source = info.CoverArt;
            MiniCoverBrush.ImageSource = info.CoverArt;

            UpdateSlidersFromTime(info.Position, info.Duration);

            if (isTrackChanged && coverChanged && info.CoverArt != null)
            {
                _coverFade.SnapTo(0);
                _coverFade.AnimateTo(1, 0.45, 1.0);
            }

            RefreshCollapsedIndicators();

            if (isTrackChanged && Visibility == Visibility.Visible)
                await TriggerEventPulseAsync(2600);
        });
    }

    private void OnLyricsUpdated(IReadOnlyList<LyricLine> lyrics, bool isSynced, string statusText)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _activeLyricIndex = -1;
            if (lyrics.Count == 0)
            {
                HubLiveLyricText.Text = statusText;
                LyricPrevLineText.Text = string.Empty;
                LyricCurrentLineText.Text = statusText;
                LyricNext1LineText.Text = string.Empty;
                LyricNext2LineText.Text = string.Empty;
                return;
            }

            SyncActiveLyricToPosition(_mediaService.GetLivePosition(), forceUpdate: true);
        });
    }

    private void TickMediaTimelineAndLyrics()
    {
        if (!_hasMedia) return;

        TimeSpan livePos = _mediaService.GetLivePosition();
        TimeSpan dur = _mediaService.Duration;

        if (!_isDraggingMediaSlider)
        {
            UpdateSlidersFromTime(livePos, dur);
        }

        if (_mediaService.CurrentLyrics.Count > 0)
        {
            SyncActiveLyricToPosition(livePos, forceUpdate: false);
        }
    }

    private void UpdateSlidersFromTime(TimeSpan pos, TimeSpan dur)
    {
        _isSyncingSliders = true;
        try
        {
            if (dur.TotalSeconds > 1)
            {
                MediaSeekSlider.Maximum = dur.TotalSeconds;
                LyricsViewSeekSlider.Maximum = dur.TotalSeconds;

                double clamped = Math.Clamp(pos.TotalSeconds, 0, dur.TotalSeconds);
                MediaSeekSlider.Value = clamped;
                LyricsViewSeekSlider.Value = clamped;

                string elapsed = FormatMediaTime(pos);
                string total = FormatMediaTime(dur);
                MediaElapsedText.Text = elapsed;
                MediaDurationText.Text = total;
                LyricsElapsedText.Text = elapsed;
                LyricsDurationText.Text = total;
            }
            else
            {
                MediaSeekSlider.Maximum = 100;
                LyricsViewSeekSlider.Maximum = 100;
                MediaSeekSlider.Value = _isMediaPlaying ? 35 : 0;
                LyricsViewSeekSlider.Value = _isMediaPlaying ? 35 : 0;

                string elapsed = FormatMediaTime(pos);
                MediaElapsedText.Text = elapsed;
                MediaDurationText.Text = "--:--";
                LyricsElapsedText.Text = elapsed;
                LyricsDurationText.Text = "--:--";
            }
        }
        finally
        {
            _isSyncingSliders = false;
        }
    }

    private void SyncActiveLyricToPosition(TimeSpan pos, bool forceUpdate)
    {
        var lyrics = _mediaService.CurrentLyrics;
        if (lyrics.Count == 0) return;

        int index = 0;
        for (int i = 0; i < lyrics.Count; i++)
        {
            if (lyrics[i].Time <= pos + TimeSpan.FromMilliseconds(180))
                index = i;
            else
                break;
        }

        if (!forceUpdate && index == _activeLyricIndex) return;
        _activeLyricIndex = index;

        string currentLine = lyrics[index].Text;
        HubLiveLyricText.Text = currentLine;

        LyricPrevLineText.Text = index > 0 ? lyrics[index - 1].Text : string.Empty;
        LyricCurrentLineText.Text = currentLine;
        LyricNext1LineText.Text = index + 1 < lyrics.Count ? lyrics[index + 1].Text : string.Empty;
        LyricNext2LineText.Text = index + 2 < lyrics.Count ? lyrics[index + 2].Text : string.Empty;

        // Efek slide naik halus saat baris lirik berganti
        _lyricShiftSpring.SnapTo(8);
        _lyricShiftSpring.AnimateTo(0, 0.28, 0.86);
    }

    // Event saat pengguna menggeser Slide Musik (Seek)
    private void MediaSeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingMediaSlider = true;
        _collapseDelayTimer.Stop();
    }

    private async void MediaSeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider)
        {
            _isDraggingMediaSlider = false;
            return;
        }

        double seconds = slider.Value;
        _isDraggingMediaSlider = false;

        if (_mediaService.Duration.TotalSeconds > 1)
        {
            var target = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, _mediaService.Duration.TotalSeconds));
            await _mediaService.SeekToPositionAsync(target);
            SyncActiveLyricToPosition(target, forceUpdate: true);
        }
    }

    private void MediaSeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSyncingSliders || !_isDraggingMediaSlider) return;
        var previewPos = TimeSpan.FromSeconds(Math.Max(0, e.NewValue));
        string formatted = FormatMediaTime(previewPos);
        MediaElapsedText.Text = formatted;
        LyricsElapsedText.Text = formatted;

        _isSyncingSliders = true;
        LyricsViewSeekSlider.Value = e.NewValue;
        _isSyncingSliders = false;

        SyncActiveLyricToPosition(previewPos, forceUpdate: false);
    }

    private void LyricsViewSeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSyncingSliders || !_isDraggingMediaSlider) return;
        var previewPos = TimeSpan.FromSeconds(Math.Max(0, e.NewValue));
        string formatted = FormatMediaTime(previewPos);
        MediaElapsedText.Text = formatted;
        LyricsElapsedText.Text = formatted;

        _isSyncingSliders = true;
        MediaSeekSlider.Value = e.NewValue;
        _isSyncingSliders = false;

        SyncActiveLyricToPosition(previewPos, forceUpdate: false);
    }

    // Klik pada baris lirik untuk melompat ke waktu lirik tersebut
    private async void LyricPrevLine_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => await SeekToLyricIndexAsync(_activeLyricIndex - 1);

    private async void LyricNext1Line_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => await SeekToLyricIndexAsync(_activeLyricIndex + 1);

    private async void LyricNext2Line_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => await SeekToLyricIndexAsync(_activeLyricIndex + 2);

    private async Task SeekToLyricIndexAsync(int index)
    {
        var lyrics = _mediaService.CurrentLyrics;
        if (index < 0 || index >= lyrics.Count) return;
        TimeSpan target = lyrics[index].Time;
        await _mediaService.SeekToPositionAsync(target);
        UpdateSlidersFromTime(target, _mediaService.Duration);
        SyncActiveLyricToPosition(target, forceUpdate: true);
    }

    private static string FormatMediaTime(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return ts.ToString(@"h\:mm\:ss");
        return ts.ToString(@"m\:ss");
    }

    private void RefreshCollapsedIndicators()
    {
        string leftMode = _settingsService.Current.ClosedLeftMode ?? "Auto";
        bool isBar = (_settingsService.Current.NotchStyle ?? "Notch").Equals("Bar", StringComparison.OrdinalIgnoreCase);

        if (leftMode.Equals("Nothing", StringComparison.OrdinalIgnoreCase))
        {
            CollapsedLeftContainer.Visibility = Visibility.Collapsed;
        }
        else if (leftMode.Equals("Pomodoro", StringComparison.OrdinalIgnoreCase))
        {
            CollapsedLeftContainer.Visibility = Visibility.Visible;
            AutoLeftGraphic.Visibility = Visibility.Collapsed;
            CollapsedWeatherIcon.Visibility = Visibility.Collapsed;
            CollapsedLeftIcon.Text = "\uE916";
            CollapsedLeftIcon.Visibility = Visibility.Visible;
            CollapsedLeftText.Text = _latestPomodoroText;
            CollapsedLeftText.Visibility = Visibility.Visible;
        }
        else if (leftMode.Equals("Weather", StringComparison.OrdinalIgnoreCase))
        {
            CollapsedLeftContainer.Visibility = Visibility.Visible;
            AutoLeftGraphic.Visibility = Visibility.Collapsed;
            CollapsedLeftIcon.Visibility = Visibility.Collapsed;
            CollapsedWeatherIcon.Visibility = Visibility.Visible;
            CollapsedLeftText.Text = _latestWeatherCompactText;
            CollapsedLeftText.Visibility = Visibility.Visible;
        }
        else
        {
            CollapsedLeftContainer.Visibility = Visibility.Visible;
            AutoLeftGraphic.Visibility = Visibility.Visible;
            CollapsedLeftIcon.Visibility = Visibility.Collapsed;
            CollapsedWeatherIcon.Visibility = Visibility.Collapsed;

            bool hasCover = MiniCoverBrush.ImageSource != null;
            MiniCoverHost.Visibility = hasCover ? Visibility.Visible : Visibility.Collapsed;
            IdleDot.Visibility = hasCover ? Visibility.Collapsed : Visibility.Visible;

            if (isBar && _hasMedia && !string.IsNullOrWhiteSpace(_currentTrackTitle))
            {
                CollapsedLeftText.Text = _currentTrackTitle;
                CollapsedLeftText.MaxWidth = 180;
                CollapsedLeftText.Visibility = Visibility.Visible;
            }
            else
            {
                CollapsedLeftText.Visibility = Visibility.Collapsed;
            }
        }

        bool showEqualizer = _hasMedia && _isMediaPlaying && _settingsService.Current.ShowMusicVisualizer;
        EqualizerPanel.Visibility = showEqualizer ? Visibility.Visible : Visibility.Collapsed;
        MiniClockText.Visibility = (!showEqualizer || isBar) ? Visibility.Visible : Visibility.Collapsed;

        if (!_isExpanded && !_isShowingBanner) _width.AnimateTo(CurrentCollapsedWidth, SpringPresets.Expand);

        UpdateEqualizerState();
    }

    private void UpdateEqualizerState()
    {
        bool shouldRun = _hasMedia && _isMediaPlaying && _settingsService.Current.ShowMusicVisualizer &&
                         !_isExpanded && Visibility == Visibility.Visible && SpringAnimator.AnimationsEnabled;

        if (shouldRun)
        {
            if (!_equalizerTimer.IsEnabled) _equalizerTimer.Start();
        }
        else
        {
            _equalizerTimer.Stop();
            foreach (var bar in _eqBars) bar.AnimateTo(_isMediaPlaying ? 0.55 : 0.3);
        }
    }

    // ================================================================
    //  JARINGAN, BATERAI, POMODORO & CUACA MODERN
    // ================================================================

    private void OnPomodoroTick(string timeText, bool isCompletedPulse)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            _latestPomodoroText = timeText;
            PomodoroText.Text = timeText;
            LargePomodoroText.Text = timeText;
            if (_settingsService.Current.ClosedLeftMode == "Pomodoro")
                CollapsedLeftText.Text = timeText;

            if (isCompletedPulse)
            {
                bool isBreakNow = _productivityService.IsBreakSession;
                _notificationService.PushInternal(
                    isBreakNow ? "Sesi fokus selesai" : "Waktu istirahat selesai",
                    isBreakNow ? "Kerja bagus! Saatnya istirahat 5 menit." : "Yuk lanjut fokus 25 menit lagi.",
                    "\uE916",
                    Color.FromRgb(0xFF, 0x9F, 0x0A),
                    () => OpenNotchTab(TabPomodoroRadio),
                    "Focus Timer");
            }
            else if (isCompletedPulse && Visibility == Visibility.Visible)
                await TriggerEventPulseAsync(3000);
        });
    }

    private void OnSystemStatsUpdated(SystemStatsSnapshot stats)
    {
        Dispatcher.InvokeAsync(() =>
        {
            NetDownText.Text = stats.DownloadSpeedText;
            NetUpText.Text = stats.UploadSpeedText;

            BatteryText.Text = $"{stats.BatteryPercent}%";
            BatteryGlyph.Text = stats.IsCharging ? "\uE945" : "\uE83F";

            // Notifikasi dinamis status baterai (klik → pengaturan baterai Windows)
            if (_lastChargingState.HasValue && _lastChargingState.Value != stats.IsCharging)
            {
                _notificationService.PushInternal(
                    stats.IsCharging ? "Pengisian daya tersambung" : "Pengisian daya dilepas",
                    $"Baterai saat ini {stats.BatteryPercent}%",
                    stats.IsCharging ? "\uE945" : "\uE83F",
                    Color.FromRgb(0x30, 0xD1, 0x58),
                    () => TryStartProcess("ms-settings:batterysaver"),
                    "Baterai");
            }
            _lastChargingState = stats.IsCharging;

            if (!stats.IsCharging && stats.BatteryPercent > 0 && stats.BatteryPercent <= 20 && !_lowBatteryNotified)
            {
                _lowBatteryNotified = true;
                _notificationService.PushInternal(
                    "Baterai lemah",
                    $"Tersisa {stats.BatteryPercent}%. Sambungkan pengisi daya.",
                    "\uE850",
                    Color.FromRgb(0xFF, 0x45, 0x3A),
                    () => TryStartProcess("ms-settings:batterysaver"),
                    "Baterai");
            }
            else if (stats.IsCharging || stats.BatteryPercent > 25)
            {
                _lowBatteryNotified = false;
            }
        });
    }

    private void OnWeatherUpdated(WeatherSnapshot weather)
    {
        Dispatcher.InvokeAsync(() =>
        {
            string iconKind = string.IsNullOrWhiteSpace(weather.IconKind) ? "PartlyCloudy" : weather.IconKind;
            bool isRain = iconKind.Equals("Rain", StringComparison.OrdinalIgnoreCase);

            _latestWeatherCompactText = $"{weather.TempCelsius}° • {weather.Condition}";
            if (_settingsService.Current.ClosedLeftMode == "Weather")
                CollapsedLeftText.Text = _latestWeatherCompactText;

            // Sinkronkan semua ikon cuaca 3D volumetrik macOS
            CollapsedWeatherIcon.IconKind = iconKind;
            NavBarWeatherIcon.IconKind = iconKind;
            TopWeatherIconControl.IconKind = iconKind;
            HubWeatherIconControl.IconKind = iconKind;
            DetailWeatherIconControl.IconKind = iconKind;

            // Badge bar atas
            TopWeatherTempText.Text = $"{weather.TempCelsius}°";

            // Kartu Cuaca macOS di Hub Utama
            HubWeatherTempText.Text = $"{weather.TempCelsius}°";
            HubWeatherHighLowText.Text = $"H:{weather.HighCelsius}° L:{weather.LowCelsius}°";
            HubWeatherLocText.Text = weather.Location;
            HubWeatherCondText.Text = weather.Condition;
            HubWeatherSummaryText.Text = weather.NaturalSummary;

            // Tampilan Penuh macOS Weather (Melbourne / London style)
            DetailWeatherLocText.Text = weather.Location;
            DetailWeatherTempText.Text = $"{weather.TempCelsius}°";
            DetailWeatherCondText.Text = weather.Condition;
            DetailWeatherHighLowText.Text = $"H:{weather.HighCelsius}°  L:{weather.LowCelsius}°";
            DetailWeatherSummaryText.Text = weather.NaturalSummary;

            // Hourly Forecast (Now, +1h .. +5h)
            HourlyForecastItemsControl.ItemsSource = weather.HourlySlots;

            // Kartu Metrik: UV Index, Sunset/Sunrise, Wind, Humidity
            DetailUvIndexText.Text = weather.UvIndex.ToString();
            DetailUvLevelText.Text = weather.UvLevelText;
            double uvProgress = Math.Clamp(weather.UvIndex / 11.0, 0, 1);
            UvIndicatorDot.Margin = new Thickness(Math.Round(uvProgress * 78.0), 0, 0, 0);

            DetailSunsetText.Text = weather.SunsetTimeText;
            DetailSunriseText.Text = $"Sunrise: {weather.SunriseTimeText}";
            DetailWeatherWindText.Text = $"{weather.WindKmh} km/h";
            DetailWeatherFeelsText.Text = $"Feels like {weather.FeelsLikeCelsius}°";
            DetailWeatherHumidityText.Text = $"{weather.HumidityPercent}%";

            // Gradasi latar suasana langit macOS Weather
            Color gradStart = isRain
                ? Color.FromRgb(0x18, 0x24, 0x38)
                : weather.IsDaytime
                    ? Color.FromRgb(0x1B, 0x2F, 0x4E)
                    : Color.FromRgb(0x15, 0x19, 0x2E);

            HubWeatherGradStart.Color = gradStart;
            DetailWeatherGradStart.Color = gradStart;
        });
    }

    private async void RefreshWeather_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        DetailWeatherSummaryText.Text = "Menyinkronkan cuaca & lokasi laptop secara real-time...";
        await _productivityService.RefreshWeatherAsync();
    }

    // ================================================================
    //  CLIPBOARD (F4)
    // ================================================================

    private async void ClipboardItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ClipboardEntry entry })
        {
            _isPinnedOpen = false;
            CollapseNotch();
            await _clipboardService.AutoPasteEntryAsync(entry);
        }
    }

    // ================================================================
    //  FILE DROP / DRAG & DROP FILE (F7)
    // ================================================================

    private void OnShelfItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateShelfBadgeCount();
    }

    private void UpdateShelfBadgeCount()
    {
        int count = _fileShelfService.ShelfItems.Count;
        ShelfHasItemsDot.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        QuickShelfLabelText.Text = count > 0 ? $"File ({count})" : "Drop File";
    }

    private void NotchRoot_PreviewDragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        _collapseDelayTimer.Stop();
        ExpandNotch();
        TabShelfRadio.IsChecked = true;
        SetShelfHighlight(true);
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void NotchRoot_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        _collapseDelayTimer.Stop();
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void NotchRoot_DragLeave(object sender, DragEventArgs e)
    {
        SetShelfHighlight(false);
        ScheduleCollapse(360);
    }

    private void FileShelfDropZone_Drop(object sender, DragEventArgs e)
    {
        SetShelfHighlight(false);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            _fileShelfService.AddFilesToShelf(files);
            TabShelfRadio.IsChecked = true;
        }
    }

    private void SetShelfHighlight(bool active)
    {
        ShelfDropOutline.SetResourceReference(
            System.Windows.Shapes.Shape.StrokeProperty,
            active ? "MacAccentBrush" : "NotchCardStrokeBrush");
        if (!active) ShelfDropOutline.Stroke = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
        ShelfDropOutline.StrokeThickness = active ? 1.5 : 1;
    }

    private void ShelfItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed &&
            sender is FrameworkElement { DataContext: ShelfFileItem shelfItem } element)
        {
            var dataObject = new DataObject(DataFormats.FileDrop, new[] { shelfItem.FullPath });
            DragDropEffects result = DragDrop.DoDragDrop(element, dataObject, DragDropEffects.Copy | DragDropEffects.Move);
            if (result == DragDropEffects.Move)
                _fileShelfService.RemoveFromShelf(shelfItem);
        }
    }

    private void RemoveShelfItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ShelfFileItem shelfItem })
            _fileShelfService.RemoveFromShelf(shelfItem);
    }

    private void AddFilesPicker_Click(object sender, RoutedEventArgs e)
    {
        bool wasPinned = _isPinnedOpen;
        _isPinnedOpen = true;
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "Pilih file untuk disimpan ke File Drop Zone",
                Multiselect = true
            };
            if (dialog.ShowDialog(this) == true && dialog.FileNames.Length > 0)
                _fileShelfService.AddFilesToShelf(dialog.FileNames);
        }
        finally
        {
            _isPinnedOpen = wasPinned;
        }
    }

    private void ClearShelf_Click(object sender, RoutedEventArgs e) => _fileShelfService.ClearShelf();

    // ================================================================
    //  NOTIFIKASI DINAMIS & PUSAT NOTIFIKASI (KLIK -> MENUJU ASALNYA)
    // ================================================================

    private void OpenNotchTab(RadioButton tabRadio)
    {
        ExpandNotch();
        tabRadio.IsChecked = true;
    }

    private void ApplyBannerReveal(double reveal)
    {
        double clamped = Math.Clamp(reveal, 0, 1);
        NotificationBannerGrid.Opacity = clamped;
        double scale = 0.90 + 0.10 * clamped;
        BannerScale.ScaleX = scale;
        BannerScale.ScaleY = scale;
        BannerShift.Y = -8 * (1 - clamped);

        if (clamped <= 0.01 && !_isShowingBanner)
        {
            NotificationBannerGrid.Visibility = Visibility.Collapsed;
            NotificationBannerGrid.IsHitTestVisible = false;
        }
    }

    private void ShowNotificationBanner(NotificationItem item)
    {
        if (Visibility != Visibility.Visible) return;

        // Jika pengguna sedang membuka tab Notifikasi di Notch yang terbuka, cukup tampilkan di daftar
        if (_isExpanded && TabNotificationsRadio.IsChecked == true)
        {
            _notificationService.MarkAllRead();
            return;
        }

        _bannerItem = item;
        BannerAppNameText.Text = item.AppName;
        BannerTitleText.Text = item.Title;
        BannerBodyText.Text = item.BodyOrPlaceholder;

        if (item.HasIcon && item.Icon != null)
        {
            BannerIconImage.Source = item.Icon;
            BannerIconHost.Visibility = Visibility.Visible;
            BannerGlyphHost.Visibility = Visibility.Collapsed;
        }
        else
        {
            BannerGlyphHost.Background = item.AccentBrush;
            BannerGlyphText.Text = item.GlyphText;
            BannerGlyphText.FontFamily = item.GlyphFont;
            BannerGlyphHost.Visibility = Visibility.Visible;
            BannerIconHost.Visibility = Visibility.Collapsed;
        }

        // Jika notch sedang tertutup, mekarkan ke ukuran Banner Dinamis
        if (!_isExpanded)
        {
            _isShowingBanner = true;
            NotificationBannerGrid.Visibility = Visibility.Visible;
            NotificationBannerGrid.IsHitTestVisible = true;

            _collapsedReveal.AnimateTo(0, 0.14, 1.0);
            _width.AnimateTo(BannerWidth, SpringPresets.Expand);
            _height.AnimateTo(BannerHeight, 0.42, 0.80);
            _bannerReveal.AnimateTo(1, 0.34, 0.84);

            _bannerTimer.Stop();
            _bannerTimer.Interval = TimeSpan.FromSeconds(4.5);
            _bannerTimer.Start();
            UpdateEqualizerState();
        }
    }

    private void HideNotificationBanner()
    {
        _bannerTimer.Stop();
        if (!_isShowingBanner) return;

        _isShowingBanner = false;
        NotificationBannerGrid.IsHitTestVisible = false;
        _bannerReveal.AnimateTo(0, 0.18, 1.0);

        if (!_isExpanded)
        {
            _collapsedReveal.AnimateTo(1, 0.36, 1.0);
            _width.AnimateTo(CurrentCollapsedWidth, SpringPresets.Collapse);
            _height.AnimateTo(CollapsedHeight, 0.34, 0.94);
        }

        UpdateEqualizerState();
    }

    private void DismissBannerVisualOnly()
    {
        _bannerTimer.Stop();
        _isShowingBanner = false;
        NotificationBannerGrid.IsHitTestVisible = false;
        NotificationBannerGrid.Visibility = Visibility.Collapsed;
        _bannerReveal.SnapTo(0);
    }

    private void UpdateNotificationCounters()
    {
        int total = _notificationService.Items.Count;
        int unread = _notificationService.UnreadCount;

        NotificationCountText.Text = total.ToString();

        if (unread > 0)
        {
            string badge = unread > 9 ? "9+" : unread.ToString();
            CollapsedUnreadText.Text = badge;
            NavUnreadText.Text = badge;
            CollapsedUnreadBadge.Visibility = Visibility.Visible;
            NavUnreadBadge.Visibility = Visibility.Visible;
        }
        else
        {
            CollapsedUnreadBadge.Visibility = Visibility.Collapsed;
            NavUnreadBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateNotificationStatus()
    {
        NotificationStatusText.Text = _notificationService.StatusText;
        GrantNotificationAccessButton.Visibility = _notificationService.IsSystemAccessAllowed
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void GrantNotificationAccess_Click(object sender, RoutedEventArgs e)
    {
        NotificationCenterService.OpenWindowsNotificationPrivacySettings();
        await _notificationService.RetryAccessAsync();
        UpdateNotificationStatus();
    }

    private void ClearNotifications_Click(object sender, RoutedEventArgs e)
    {
        _notificationService.ClearAll();
        UpdateNotificationCounters();
    }

    private void NotificationItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NotificationItem item })
        {
            _isPinnedOpen = false;
            CollapseNotch();
            _notificationService.OpenOrigin(item);
        }
    }

    private void DismissNotification_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: NotificationItem item })
        {
            _notificationService.Dismiss(item);
            UpdateNotificationCounters();
        }
    }

    private void NotificationBanner_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = _bannerItem;
        HideNotificationBanner();
        if (item != null)
        {
            _notificationService.MarkAllRead();
            _notificationService.OpenOrigin(item);
        }
    }

    private void BannerShowAll_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        DismissBannerVisualOnly();
        OpenNotchTab(TabNotificationsRadio);
    }

    private void BannerDismiss_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        HideNotificationBanner();
    }

    // ================================================================
    //  TOMBOL AKSI CEPAT & UTILITAS
    // ================================================================

    private void PomodoroButton_Click(object sender, RoutedEventArgs e) => _productivityService.TogglePomodoro();
    private void PomodoroButton_RightClick(object sender, MouseButtonEventArgs e) => _productivityService.ResetPomodoro();
    private void ResetFocus25_Click(object sender, RoutedEventArgs e) => _productivityService.ResetPomodoro(25, false);
    private void ResetBreak5_Click(object sender, RoutedEventArgs e) => _productivityService.ResetPomodoro(5, true);

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => (Application.Current as App)?.OpenSettingsWindow();

    private void QuickCalc_Click(object sender, RoutedEventArgs e) => TryStartProcess("calc.exe");
    private void QuickSnip_Click(object sender, RoutedEventArgs e) => TryStartProcess("explorer.exe", "ms-screenclip:");

    private static void TryStartProcess(string fileName, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private async void PrevTrack_Click(object sender, RoutedEventArgs e) => await _mediaService.SkipPreviousAsync();
    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await _mediaService.TogglePlayPauseAsync();
    private async void NextTrack_Click(object sender, RoutedEventArgs e) => await _mediaService.SkipNextAsync();

    private void UpdateClockDisplay()
    {
        DateTime now = DateTime.Now;
        MiniClockText.Text = _settingsService.Current.Use24HourClock
            ? now.ToString("HH:mm")
            : now.ToString("h:mm tt");
        ExpandedDateText.Text = now.ToString("ddd, d MMM");
    }

    private static double Lerp(double from, double to, double t) => from + (to - from) * t;

    private static SolidColorBrush FreezeBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
