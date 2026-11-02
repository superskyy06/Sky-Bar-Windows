using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SkyBar.AnimationRendering;
using SkyBar.CoreServices;
using SkyBar.SystemIntegration;

namespace SkyBar.UI.Settings;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;
    private bool _isLoading;

    public SettingsWindow(SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        PopulateMonitorList();
        LoadCurrentSettings();
    }

    private void PopulateMonitorList()
    {
        _isLoading = true;
        MonitorComboBox.Items.Clear();

        var monitors = Win32WindowInterop.GetMonitors(this);
        foreach (var mon in monitors)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock
            {
                Text = "\uE7F4",
                FontFamily = (FontFamily)Application.Current.FindResource("MacIconFont"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(new TextBlock
            {
                Text = mon.DisplayName,
                VerticalAlignment = VerticalAlignment.Center
            });

            MonitorComboBox.Items.Add(new ComboBoxItem
            {
                Content = panel,
                Tag = mon.Index
            });
        }
        _isLoading = false;
    }

    private void LoadCurrentSettings()
    {
        _isLoading = true;
        var s = _settingsService.Current;

        string style = s.NotchStyle ?? "Notch";
        StyleNotchRadio.IsChecked = style.Equals("Notch", StringComparison.OrdinalIgnoreCase);
        StylePillRadio.IsChecked = style.Equals("Pill", StringComparison.OrdinalIgnoreCase);
        StyleBarRadio.IsChecked = style.Equals("Bar", StringComparison.OrdinalIgnoreCase);

        Clock24Radio.IsChecked = s.Use24HourClock;
        Clock12Radio.IsChecked = !s.Use24HourClock;

        int dist = Math.Clamp(s.ScreenEdgeDistancePx, 0, 40);
        EdgeDistanceSlider.Value = dist;
        EdgeDistanceValueText.Text = $"{dist}px";

        int pct = (int)Math.Round(Math.Clamp(s.ScaleFactor, 0.65, 1.30) * 100.0 / 5.0) * 5;
        ScaleSlider.Value = pct;
        ScaleValueText.Text = $"{pct}%";

        int targetIdx = Math.Clamp(s.TargetMonitorIndex, 0, Math.Max(0, MonitorComboBox.Items.Count - 1));
        if (MonitorComboBox.Items.Count > 0)
            MonitorComboBox.SelectedIndex = targetIdx;

        string leftMode = s.ClosedLeftMode ?? "Auto";
        foreach (ComboBoxItem item in ClosedLeftComboBox.Items)
        {
            if (string.Equals(item.Tag?.ToString(), leftMode, StringComparison.OrdinalIgnoreCase))
            {
                ClosedLeftComboBox.SelectedItem = item;
                break;
            }
        }
        if (ClosedLeftComboBox.SelectedItem == null && ClosedLeftComboBox.Items.Count > 0)
            ClosedLeftComboBox.SelectedIndex = 0;

        MusicVisualizerCheck.IsChecked = s.ShowMusicVisualizer;

        ShowNotchCheck.IsChecked = s.ShowNotch;
        AutoHideGameCheck.IsChecked = s.AutoHideNotchInFullscreenGame;
        EnableAnimationsCheck.IsChecked = s.EnableAnimations;
        StartWithWindowsCheck.IsChecked = s.StartWithWindows;

        foreach (ComboBoxItem item in ThemeComboBox.Items)
        {
            if (item.Tag?.ToString() == s.ThemeMode)
            {
                ThemeComboBox.SelectedItem = item;
                break;
            }
        }

        _isLoading = false;
    }

    private void SidebarNav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageGeneral == null) return;

        PageGeneral.Visibility = Visibility.Collapsed;
        PageAppearance.Visibility = Visibility.Collapsed;
        PageWidgets.Visibility = Visibility.Collapsed;

        if (ReferenceEquals(sender, NavGeneralRadio))
            PageGeneral.Visibility = Visibility.Visible;
        else if (ReferenceEquals(sender, NavAppearanceRadio))
            PageAppearance.Visibility = Visibility.Visible;
        else if (ReferenceEquals(sender, NavWidgetsRadio))
            PageWidgets.Visibility = Visibility.Visible;
    }

    private void NotchStyle_Checked(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        var s = _settingsService.Current;

        if (StylePillRadio.IsChecked == true)
        {
            s.NotchStyle = "Pill";
            if (s.ScreenEdgeDistancePx == 0)
            {
                _isLoading = true;
                s.ScreenEdgeDistancePx = 12;
                EdgeDistanceSlider.Value = 12;
                EdgeDistanceValueText.Text = "12px";
                _isLoading = false;
            }
        }
        else if (StyleBarRadio.IsChecked == true)
        {
            s.NotchStyle = "Bar";
            if (s.ScreenEdgeDistancePx == 0)
            {
                _isLoading = true;
                s.ScreenEdgeDistancePx = 8;
                EdgeDistanceSlider.Value = 8;
                EdgeDistanceValueText.Text = "8px";
                _isLoading = false;
            }
        }
        else
        {
            s.NotchStyle = "Notch";
            if (s.ScreenEdgeDistancePx is 12 or 8)
            {
                _isLoading = true;
                s.ScreenEdgeDistancePx = 0;
                EdgeDistanceSlider.Value = 0;
                EdgeDistanceValueText.Text = "0px";
                _isLoading = false;
            }
        }

        _settingsService.NotifyAndSave();
    }

    private void ClockFormat_Checked(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        _settingsService.Current.Use24HourClock = Clock24Radio.IsChecked == true;
        _settingsService.NotifyAndSave();
    }

    private void EdgeDistanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (EdgeDistanceValueText == null) return;
        int px = (int)Math.Round(e.NewValue);
        EdgeDistanceValueText.Text = $"{px}px";

        if (_isLoading) return;
        _settingsService.Current.ScreenEdgeDistancePx = px;
        _settingsService.NotifyAndSave();
    }

    private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ScaleValueText == null) return;
        int pct = (int)Math.Round(e.NewValue);
        ScaleValueText.Text = $"{pct}%";

        if (_isLoading) return;
        _settingsService.Current.ScaleFactor = pct / 100.0;
        _settingsService.NotifyAndSave();
    }

    private void MonitorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;
        if (MonitorComboBox.SelectedItem is ComboBoxItem { Tag: int monitorIdx })
        {
            _settingsService.Current.TargetMonitorIndex = monitorIdx;
            _settingsService.NotifyAndSave();
        }
    }

    private void ClosedLeftComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;
        if (ClosedLeftComboBox.SelectedItem is ComboBoxItem { Tag: string mode })
        {
            _settingsService.Current.ClosedLeftMode = mode;
            _settingsService.NotifyAndSave();
        }
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;
        if (ThemeComboBox.SelectedItem is ComboBoxItem { Tag: string mode })
        {
            var s = _settingsService.Current;
            s.ThemeMode = mode;
            ThemeEngineService.ApplyTheme(s.ThemeMode, s.AccentColorHex);
            _settingsService.NotifyAndSave();
        }
    }

    private void AccentColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex })
        {
            var s = _settingsService.Current;
            s.AccentColorHex = hex;
            ThemeEngineService.ApplyTheme(s.ThemeMode, s.AccentColorHex);
            _settingsService.NotifyAndSave();
        }
    }

    private void ToggleSetting_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        var s = _settingsService.Current;
        s.ShowMusicVisualizer = MusicVisualizerCheck.IsChecked == true;
        s.ShowNotch = ShowNotchCheck.IsChecked == true;
        s.AutoHideNotchInFullscreenGame = AutoHideGameCheck.IsChecked == true;
        s.EnableAnimations = EnableAnimationsCheck.IsChecked == true;
        s.StartWithWindows = StartWithWindowsCheck.IsChecked == true;

        SpringAnimator.AnimationsEnabled = s.EnableAnimations;
        _settingsService.NotifyAndSave();
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        var s = _settingsService.Current;
        s.NotchStyle = "Notch";
        s.Use24HourClock = true;
        s.ScreenEdgeDistancePx = 0;
        s.ScaleFactor = 1.0;
        s.TargetMonitorIndex = 0;
        s.ClosedLeftMode = "Auto";
        s.ShowMusicVisualizer = true;
        s.AccentColorHex = "#0A84FF";
        ThemeEngineService.ApplyTheme(s.ThemeMode, s.AccentColorHex);
        LoadCurrentSettings();
        _settingsService.NotifyAndSave();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();
}
