using System.Windows;
using System.Windows.Input;

namespace MesControlAgv.Wpf;

public partial class MainWindow
{
    private TwinWindowLayout? _twinWindowLayout;
    public bool IsTwinFullScreen => _twinWindowLayout is not null;

    private void InitializeTwinFullScreen()
    {
        DigitalTwinView.SetFullScreenHost(request => SetTwinFullScreen(request ?? !IsTwinFullScreen));
        PreviewKeyDown += (_, e) =>
        {
            if (e.IsRepeat || Keyboard.Modifiers != ModifierKeys.None) return;
            if (e.Key == Key.F11 && MainTabs.SelectedItem == DigitalTwinTab)
            { SetTwinFullScreen(!IsTwinFullScreen); e.Handled = true; }
            else if (e.Key == Key.Escape && IsTwinFullScreen)
            { SetTwinFullScreen(false); e.Handled = true; }
        };
    }

    /// <summary>Same visual tree and telemetry sessions; never creates another viewer.</summary>
    public void SetTwinFullScreen(bool enabled)
    {
        if (enabled == IsTwinFullScreen || enabled && (!IsLoaded || MainTabs.SelectedItem != DigitalTwinTab)) return;
        if (enabled)
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            _twinWindowLayout = new(WindowState, WindowStyle, ResizeMode, bounds,
                SidebarColumn.Width, ShellHeaderRow.Height, NavigationSidebar.Visibility, ShellHeader.Visibility,
                PageFrame.Margin, PageFrame.Padding, PageFrame.BorderThickness, PageFrame.CornerRadius);
            NavigationSidebar.Visibility = ShellHeader.Visibility = Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(0);
            ShellHeaderRow.Height = new GridLength(0);
            PageFrame.Margin = PageFrame.Padding = PageFrame.BorderThickness = new Thickness(0);
            PageFrame.CornerRadius = new CornerRadius(0);
            DigitalTwinView.SetPresentationMode(true);
            // Transition through Normal so an already-maximized window refits
            // without chrome to the display, rather than keeping its work-area bounds.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else if (_twinWindowLayout is { } saved)
        {
            _twinWindowLayout = null;
            WindowState = WindowState.Normal;
            WindowStyle = saved.Style;
            ResizeMode = saved.Resize;
            Left = saved.Bounds.Left; Top = saved.Bounds.Top;
            Width = saved.Bounds.Width; Height = saved.Bounds.Height;
            NavigationSidebar.Visibility = saved.SidebarVisibility;
            ShellHeader.Visibility = saved.HeaderVisibility;
            SidebarColumn.Width = saved.SidebarWidth;
            ShellHeaderRow.Height = saved.HeaderHeight;
            PageFrame.Margin = saved.Margin; PageFrame.Padding = saved.Padding;
            PageFrame.BorderThickness = saved.Border; PageFrame.CornerRadius = saved.Corners;
            DigitalTwinView.SetPresentationMode(false);
            WindowState = saved.State;
        }
    }

    private sealed record TwinWindowLayout(WindowState State, WindowStyle Style, ResizeMode Resize, Rect Bounds,
        GridLength SidebarWidth, GridLength HeaderHeight, Visibility SidebarVisibility, Visibility HeaderVisibility,
        Thickness Margin, Thickness Padding, Thickness Border, CornerRadius Corners);
}
