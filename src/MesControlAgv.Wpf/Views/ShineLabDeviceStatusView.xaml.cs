using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Views;

public partial class ShineLabDeviceStatusView : UserControl
{
    private readonly DispatcherTimer _currentTaskTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private ShineLabTaskWindow? _taskWindow;
    public static readonly DependencyProperty ControlModelProperty = DependencyProperty.Register(nameof(ControlModel),
        typeof(ShineLabControlViewModel), typeof(ShineLabDeviceStatusView));
    public ShineLabControlViewModel? ControlModel
    {
        get => (ShineLabControlViewModel?)GetValue(ControlModelProperty);
        set => SetValue(ControlModelProperty, value);
    }

    public ShineLabDeviceStatusView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => WorkstationTestScroll.MaxHeight = ActualHeight >= 500 ? 150 : 54;
        Loaded += RefreshIonRecordsOnLoad;
        Loaded += (_, _) => UpdateCurrentTaskTimer();
        IsVisibleChanged += (_, _) => UpdateCurrentTaskTimer();
        Unloaded += (_, _) => _currentTaskTimer.Stop();
        _currentTaskTimer.Tick += async (_, _) => await RefreshCurrentTaskAsync();
        DeviceSummaryGrid.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && IsLoaded) RefreshIonRecordsOnLoad(this, new RoutedEventArgs());
        };
    }

    private void SingleTaskTemplate_Click(object sender, RoutedEventArgs e) => OpenTaskWorkspace(false);
    private async void ImportIonTasks_Click(object sender, RoutedEventArgs e)
    {
        if (ControlModel is not { IsBusy: false } model) return;
        if (_taskWindow is not null) { _taskWindow.Activate(); return; }
        _taskWindow = new ShineLabTaskWindow(model) { Owner = Window.GetWindow(this) };
        try { _taskWindow.ShowDialog(); }
        finally { _taskWindow = null; }
        await model.RefreshAsync(); // Read back records after explicit user operations; never dispatch.
        await RefreshCurrentTaskAsync();
    }
    private void BatchTaskImport_Click(object sender, RoutedEventArgs e) => OpenTaskWorkspace(true);
    private void OpenTaskWorkspace(bool batch)
    {
        if (DataContext is not ViewModels.ShineLabDeviceStatusViewModel vm) return;
        var dialog = new WorkstationTaskWindow(vm.TaskTemplateClient, vm.TaskTemplateDeviceId,
            vm.SelectedWorkstationTask?.TaskNo, batch) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }

    private async void RefreshIonRecordsOnLoad(object sender, RoutedEventArgs e)
    {
        await RefreshCurrentTaskAsync();
        if (DeviceSummaryGrid.IsVisible && ControlModel is { } model)
            await model.RefreshAsync(); // GET-only refresh; never dispatches device commands.
    }

    private async Task RefreshCurrentTaskAsync()
    {
        if (!IsVisible || DataContext is not ShineLabDeviceStatusViewModel status) return;
        status.InvalidateCurrentTaskDisplay(); // Expire old observations even while another read is pending.
        await status.RefreshAsync(); // Shared model gate coalesces timer/dashboard/manual refreshes.
    }

    private void UpdateCurrentTaskTimer()
    {
        if (IsLoaded && IsVisible)
        {
            if (DataContext is ShineLabDeviceStatusViewModel status) status.InvalidateCurrentTaskDisplay();
            _currentTaskTimer.Start();
        }
        else _currentTaskTimer.Stop();
    }
}
