using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SkyBar.AnimationRendering;
using SkyBar.SystemIntegration;

namespace SkyBar.UI.Dock;

public partial class DockWindow : Window
{
    private readonly DockViewModel _viewModel;
    private readonly Action _openSettingsCallback;
    public double BaseIconSize { get; set; } = 48.0;
    public double MaxMagnifiedSize { get; set; } = 74.0;
    private const double MagnificationRadius = 130.0;
    private Point? _dragStartPoint;
    private DockAppItem? _draggedDockItem;

    public DockWindow(DockViewModel viewModel, Action openSettingsCallback)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _openSettingsCallback = openSettingsCallback;
        DataContext = _viewModel;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2.0;
        Top = SystemParameters.PrimaryScreenHeight - Height;
        Win32WindowInterop.ConfigureFloatingOverlay(this);
    }

    private void DockGlassBorder_MouseMove(object sender, MouseEventArgs e)
    {
        if (!SpringAnimator.AnimationsEnabled) return;
        Point cursor = e.GetPosition(DockGlassBorder);
        ApplyMagnification(PinnedItemsControl, cursor);
        ApplyMagnification(ActiveItemsControl, cursor);
    }

    private void ApplyMagnification(ItemsControl control, Point cursorInDock)
    {
        for (int i = 0; i < control.Items.Count; i++)
        {
            if (control.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container ||
                control.Items[i] is not DockAppItem appItem) continue;

            Point itemCenter = container.TranslatePoint(new Point(container.ActualWidth / 2.0, container.ActualHeight / 2.0), DockGlassBorder);
            double distance = Math.Abs(cursorInDock.X - itemCenter.X);
            if (distance < MagnificationRadius)
            {
                double factor = (Math.Cos((distance / MagnificationRadius) * Math.PI) + 1.0) / 2.0;
                appItem.CurrentSize = BaseIconSize + (MaxMagnifiedSize - BaseIconSize) * factor;
            }
            else appItem.CurrentSize = BaseIconSize;
        }
    }

    private void DockGlassBorder_MouseLeave(object sender, MouseEventArgs e)
    {
        foreach (var item in _viewModel.PinnedApps.Concat(_viewModel.ActiveUnpinnedApps)) item.CurrentSize = BaseIconSize;
    }

    private void DockGlassBorder_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void DockGlassBorder_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (string file in files) _viewModel.PinApplication(file);
    }

    private void DockItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DockAppItem item })
        {
            _dragStartPoint = e.GetPosition(this);
            _draggedDockItem = item;
        }
    }

    private void DockItem_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && _dragStartPoint.HasValue && _draggedDockItem is { IsPinned: true })
        {
            Point currentPos = e.GetPosition(this);
            if (_dragStartPoint.Value.Y - currentPos.Y > 95)
            {
                var itemToRemove = _draggedDockItem;
                _draggedDockItem = null;
                _dragStartPoint = null;
                _viewModel.UnpinApplication(itemToRemove);
            }
        }
    }

    private void DockItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = null;
        _draggedDockItem = null;
        if (sender is FrameworkElement { DataContext: DockAppItem item }) _viewModel.LaunchOrFocusApp(item);
    }

    private void DockItem_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DockAppItem item } element) return;
        var menu = new ContextMenu();
        var openItem = new MenuItem { Header = item.IsRunning ? "Fokus ke Jendela" : "Buka Aplikasi" };
        openItem.Click += (_, _) => _viewModel.LaunchOrFocusApp(item);
        menu.Items.Add(openItem);

        var pinToggleItem = new MenuItem { Header = item.IsPinned ? "Hapus dari Dock" : "Simpan di Dock (Keep in Dock)" };
        pinToggleItem.Click += (_, _) => { if (item.IsPinned) _viewModel.UnpinApplication(item); else _viewModel.PinApplication(item.ExecutablePath); };
        menu.Items.Add(pinToggleItem);

        if (item.IsRunning)
        {
            menu.Items.Add(new Separator());
            var quitItem = new MenuItem { Header = "Tutup Aplikasi (Quit)" };
            quitItem.Click += (_, _) => _viewModel.QuitApplication(item);
            menu.Items.Add(quitItem);
        }
        menu.PlacementTarget = element;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void SettingsButton_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _openSettingsCallback.Invoke();
}
