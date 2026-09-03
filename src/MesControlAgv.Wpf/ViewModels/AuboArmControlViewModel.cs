using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows;
using System.Windows.Threading;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// Small, serialized AUBO program panel.  It talks only to MES and deliberately
/// does not expose vendor RPC names, register keys, or motion coordinates.
/// </summary>
public sealed class AuboArmControlViewModel : INotifyPropertyChanged, IDisposable
{
    public const string DefaultDeviceId = "ARM-01";
    public const string DefaultProgramName = "";

    private readonly IMesClient _mes;
    private readonly Dispatcher? _uiDispatcher;
    private readonly RuntimeConnectionSource _connectionSource;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private string _deviceId = DefaultDeviceId;
    private string _programName = DefaultProgramName;
    private string _operatorName = Environment.UserName;
    private string _connectionStatus = "未刷新";
    private string _robotName = "-";
    private string _robotMode = AuboArmMode.Unknown.ToString();
    private string _safetyMode = AuboArmSafetyMode.Unknown.ToString();
    private string _operationalMode = AuboArmOperationalMode.Unknown.ToString();
    private string _runtime = AuboArmRuntimeState.Unknown.ToString();
    private string _loadedProgram = "-";
    private string _readiness = "未知";
    private bool _controlEnabled;
    private string _lastResponse = "尚未执行操作";
    private string _errorMessage = string.Empty;
    private bool _isBusy;
    private DateTimeOffset? _observedAt;
    private string _lastObservedLoadedProgram = string.Empty;
    private string _programCatalogStatus = "尚未刷新";

    public AuboArmControlViewModel(IMesClient mes, string? runtimeMode = null)
    {
        _mes = mes ?? throw new ArgumentNullException(nameof(mes));
        _connectionSource = RuntimeConnectionSourcePresentation.Resolve(runtimeMode);
        // Network continuations may run on a pool thread (the dashboard refresh
        // timer does so by design). Capture the WPF dispatcher once and marshal
        // every notification/command-state change back to it.
        _uiDispatcher = ResolveDispatcher();
        RefreshCommand = new AsyncCommand(() => RunUiOperationAsync("刷新机械臂", RefreshCoreAsync), () => !IsBusy);
        LoadProgramCommand = new AsyncCommand(() => RunUiOperationAsync("加载工程", LoadCoreAsync), CanLoad);
        RunProgramCommand = new AsyncCommand(() => RunUiOperationAsync("启动工程", RunCoreAsync), CanRun);
        StopProgramCommand = new AsyncCommand(() => RunUiOperationAsync("停止工程", StopCoreAsync), CanStop);
        RefreshProgramsCommand = new AsyncCommand(
            () => RunUiOperationAsync("刷新可用程序", RefreshProgramCatalogCoreAsync),
            () => !IsBusy && !string.IsNullOrWhiteSpace(DeviceId));

        // Short aliases keep bindings readable in small host views and are useful
        // to callers that use the action names directly.
        LoadCommand = LoadProgramCommand;
        RunCommand = RunProgramCommand;
        StopCommand = StopProgramCommand;
    }

    public string DeviceId
    {
        get => _deviceId;
        set
        {
            if (!SetField(ref _deviceId, value?.Trim() ?? string.Empty)) return;
            RefreshCommandStates();
        }
    }

    public string ProgramName
    {
        get => _programName;
        set
        {
            if (!SetField(ref _programName, value ?? string.Empty)) return;
            RefreshCommandStates();
        }
    }

    public string OperatorName
    {
        get => _operatorName;
        set
        {
            if (!SetField(ref _operatorName, value ?? string.Empty)) return;
            RefreshCommandStates();
        }
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set
        {
            if (!SetField(ref _connectionStatus, value)) return;
            OnPropertyChanged(nameof(ConnectionStatusDisplay));
        }
    }
    public RuntimeConnectionSource ConnectionSource => _connectionSource;
    public string ConnectionSourceDisplay => RuntimeConnectionSourcePresentation.SourceDisplay(ConnectionSource);
    public string ConnectionSourceDetail => RuntimeConnectionSourcePresentation.SourceDetail(ConnectionSource);
    public string ConnectionSourceBrush => RuntimeConnectionSourcePresentation.SourceBrush(ConnectionSource);
    public string ConnectionSourceForeground => RuntimeConnectionSourcePresentation.SourceForeground(ConnectionSource);
    public string ConnectionStatusDisplay => RuntimeConnectionSourcePresentation.DescribeConnection(
        ConnectionSource,
        IsOnline,
        ConnectionStatus);
    public string RobotName { get => _robotName; private set => SetField(ref _robotName, value); }
    public string RobotMode { get => _robotMode; private set => SetField(ref _robotMode, value); }
    public string SafetyMode { get => _safetyMode; private set => SetField(ref _safetyMode, value); }
    public string OperationalMode { get => _operationalMode; private set => SetField(ref _operationalMode, value); }
    public string Runtime { get => _runtime; private set => SetField(ref _runtime, value); }
    public string LoadedProgram { get => _loadedProgram; private set => SetField(ref _loadedProgram, value); }
    public string Readiness { get => _readiness; private set => SetField(ref _readiness, value); }
    public bool ControlEnabled
    {
        get => _controlEnabled;
        private set
        {
            if (!SetField(ref _controlEnabled, value)) return;
            OnPropertyChanged(nameof(CanOperate));
            RefreshCommandStates();
        }
    }
    public string LastResponse { get => _lastResponse; private set => SetField(ref _lastResponse, value); }
    public string ErrorMessage { get => _errorMessage; private set => SetField(ref _errorMessage, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value)) return;
            RefreshCommandStates();
        }
    }
    public DateTimeOffset? ObservedAt { get => _observedAt; private set => SetField(ref _observedAt, value); }
    public ObservableCollection<string> AvailablePrograms { get; } = [];
    public string ProgramCatalogStatus { get => _programCatalogStatus; private set => SetField(ref _programCatalogStatus, value); }

    public bool IsOnline => ConnectionStatus == "在线";
    public bool HasLoadedProgram => !string.IsNullOrWhiteSpace(LoadedProgram) && LoadedProgram != "-";
    public bool CanOperate => IsOnline && ControlEnabled && !IsBusy && !string.IsNullOrWhiteSpace(OperatorName);

    public ICommand RefreshCommand { get; }
    public ICommand LoadProgramCommand { get; }
    public ICommand RunProgramCommand { get; }
    public ICommand StopProgramCommand { get; }
    public ICommand LoadCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RefreshProgramsCommand { get; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!TryEnterImmediate()) return;
        try
        {
            await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { _operationGate.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        InvokeOnUi(() => ErrorMessage = string.Empty);
        try
        {
            var status = await _mes.GetAuboArmStatusAsync(DeviceId, cancellationToken).ConfigureAwait(false);
            var program = await _mes.GetAuboArmProgramAsync(DeviceId, cancellationToken).ConfigureAwait(false);
            var readiness = await _mes.GetAuboArmReadinessAsync(DeviceId, cancellationToken).ConfigureAwait(false);

            if (status is null)
            {
                SetOffline("未配置 / 不可用");
                return;
            }

            var observedAt = program?.ObservedAtUtc ?? status.ObservedAtUtc;
            var loadedProgram = program is null || string.IsNullOrWhiteSpace(program.LoadedProgram)
                ? "-"
                : program.LoadedProgram!;
            var runtime = !string.IsNullOrWhiteSpace(program?.RuntimeStatus)
                ? program!.RuntimeStatus!
                : status.RuntimeState.ToString();
            var readinessText = readiness is null
                ? "未知"
                : readiness.Ready
                    ? "就绪"
                    : $"阻断：{string.Join("；", readiness.BlockingReasons)}";
            var controlEnabled = program?.ControlEnabled ?? false;
            InvokeOnUi(() =>
            {
                ConnectionStatus = status.Online ? "在线" : "离线";
                RobotName = string.IsNullOrWhiteSpace(status.RobotName) ? "-" : status.RobotName;
                RobotMode = status.Mode.ToString();
                SafetyMode = status.SafetyMode.ToString();
                OperationalMode = status.OperationalMode.ToString();
                Runtime = runtime;
                LoadedProgram = loadedProgram;
                if (loadedProgram != "-" &&
                    (string.IsNullOrWhiteSpace(ProgramName) ||
                     string.Equals(ProgramName, _lastObservedLoadedProgram, StringComparison.OrdinalIgnoreCase)))
                    ProgramName = loadedProgram;
                _lastObservedLoadedProgram = loadedProgram == "-" ? string.Empty : loadedProgram;
                ObservedAt = observedAt;
                ControlEnabled = controlEnabled;
                Readiness = readinessText;
                LastResponse = $"状态已更新（{observedAt.LocalDateTime:HH:mm:ss}）";
                OnPropertyChanged(nameof(IsOnline));
                OnPropertyChanged(nameof(HasLoadedProgram));
                OnPropertyChanged(nameof(CanOperate));
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            InvokeOnUi(() =>
            {
                SetOffline("通信异常");
                ErrorMessage = exception.Message;
                LastResponse = "状态读取失败；请人工确认后再重试。";
            });
        }
        finally
        {
            RefreshCommandStates();
        }
    }

    private async Task RefreshProgramCatalogCoreAsync(CancellationToken cancellationToken)
    {
        InvokeOnUi(() =>
        {
            ProgramCatalogStatus = "正在读取控制器程序目录…";
            ErrorMessage = string.Empty;
        });
        var catalog = await _mes.GetAuboArmProgramCatalogAsync(DeviceId, cancellationToken).ConfigureAwait(false);
        InvokeOnUi(() =>
        {
            AvailablePrograms.Clear();
            ProgramCatalogStatus = "程序目录不可用";
            if (catalog is not null)
            {
                foreach (var program in catalog.AvailablePrograms)
                    AvailablePrograms.Add(program);
                ProgramCatalogStatus = catalog.IsComplete
                    ? $"已读取 {AvailablePrograms.Count} 个程序（含配置允许列表）"
                    : catalog.ReadErrors.Any(error =>
                        error.Contains("program catalog scan timed out", StringComparison.OrdinalIgnoreCase))
                        ? $"程序目录读取超时，已显示当前/允许列表（另有 {catalog.ReadErrors.Count} 条读取信息）"
                        : $"程序列表部分读取：{catalog.ReadErrors.Count} 个槽位失败";
                if (!string.IsNullOrWhiteSpace(catalog.CurrentProgram))
                {
                    LoadedProgram = catalog.CurrentProgram!;
                    if (string.IsNullOrWhiteSpace(ProgramName) ||
                        string.Equals(ProgramName, _lastObservedLoadedProgram, StringComparison.OrdinalIgnoreCase))
                        ProgramName = catalog.CurrentProgram!;
                    _lastObservedLoadedProgram = catalog.CurrentProgram!;
                }
                ObservedAt = catalog.ObservedAtUtc;
            }
            else
            {
                ProgramCatalogStatus = "程序目录不可用";
            }
        });
    }

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var result = await _mes.LoadAuboProgramAsync(
            DeviceId,
            ProgramName,
            OperatorName.Trim(),
            operationId,
            cancellationToken).ConfigureAwait(false);
        ApplyOperation(result);
        await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var result = await _mes.RunAuboProgramAsync(
            DeviceId,
            string.IsNullOrWhiteSpace(ProgramName) ? null : ProgramName,
            OperatorName.Trim(),
            operationId,
            cancellationToken).ConfigureAwait(false);
        ApplyOperation(result);
        await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var result = await _mes.StopAuboProgramAsync(
            DeviceId,
            OperatorName.Trim(),
            operationId,
            cancellationToken).ConfigureAwait(false);
        ApplyOperation(result);
        await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunUiOperationAsync(
        string action,
        Func<CancellationToken, Task> operation)
    {
        if (!TryEnterImmediate())
        {
            LastResponse = "已有机械臂操作正在执行，请等待其结果。";
            return;
        }
        InvokeOnUi(() =>
        {
            IsBusy = true;
            ErrorMessage = string.Empty;
            LastResponse = $"正在执行：{action}";
        });
        try
        {
            await operation(_shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            InvokeOnUi(() => LastResponse = $"{action}已取消");
        }
        catch (Exception exception)
        {
            InvokeOnUi(() =>
            {
                ErrorMessage = exception.Message;
                LastResponse = $"{action}失败；未知结果不得自动重试。";
            });
        }
        finally
        {
            InvokeOnUi(() => IsBusy = false);
            try { _operationGate.Release(); }
            catch (ObjectDisposedException) { }
            RefreshCommandStates();
        }
    }

    private bool CanLoad() => CanOperate && !string.IsNullOrWhiteSpace(DeviceId)
        && !string.IsNullOrWhiteSpace(ProgramName);
    private bool CanRun() => CanLoad() && IsOnline;
    private bool CanStop() => !IsBusy && IsOnline && !string.IsNullOrWhiteSpace(OperatorName);

    private bool TryEnterImmediate()
    {
        try { return _operationGate.Wait(0); }
        catch (ObjectDisposedException) { return false; }
    }

    private void ApplyOperation(AuboArmProgramOperationResponse result)
    {
        InvokeOnUi(() =>
        {
            LoadedProgram = string.IsNullOrWhiteSpace(result.LoadedProgram) ? LoadedProgram : result.LoadedProgram!;
            Runtime = string.IsNullOrWhiteSpace(result.RuntimeStatus) ? result.RuntimeState.ToString() : result.RuntimeStatus!;
            LastResponse = $"{result.Operation}：{result.State}（操作 {result.OperationId:N}）";
            ErrorMessage = result.ErrorMessage ?? string.Empty;
            OnPropertyChanged(nameof(HasLoadedProgram));
            OnPropertyChanged(nameof(CanOperate));
        });
    }

    private void SetOffline(string message)
    {
        InvokeOnUi(() =>
        {
            ConnectionStatus = message;
            RobotName = "-";
            RobotMode = AuboArmMode.Unknown.ToString();
            SafetyMode = AuboArmSafetyMode.Unknown.ToString();
            OperationalMode = AuboArmOperationalMode.Unknown.ToString();
            Runtime = AuboArmRuntimeState.Unknown.ToString();
            // Clear controller-derived values once disconnected. Keeping the
            // last loaded project/catalog visible is misleading during offline
            // maintenance and could be mistaken for a live controller state.
            LoadedProgram = "-";
            AvailablePrograms.Clear();
            ProgramCatalogStatus = "程序目录不可用";
            ObservedAt = null;
            Readiness = "未知";
            ControlEnabled = false;
            OnPropertyChanged(nameof(IsOnline));
            OnPropertyChanged(nameof(HasLoadedProgram));
            OnPropertyChanged(nameof(CanOperate));
        });
    }

    private void RefreshCommandStates()
    {
        if (!CheckUiAccess())
        {
            PostToUi(RefreshCommandStates);
            return;
        }

        (RefreshCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (LoadProgramCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (RunProgramCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (StopProgramCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (RefreshProgramsCommand as AsyncCommand)?.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _operationGate.Dispose();
        _shutdown.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged(string propertyName) =>
        InvokeOnUi(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));

    private bool CheckUiAccess() =>
        _uiDispatcher is null || _uiDispatcher.CheckAccess();

    private static Dispatcher? ResolveDispatcher()
    {
        var currentDispatcher = Dispatcher.FromThread(Thread.CurrentThread);
        if (currentDispatcher is not null &&
               Thread.CurrentThread.GetApartmentState() == ApartmentState.STA &&
               !currentDispatcher.HasShutdownStarted &&
               !currentDispatcher.HasShutdownFinished)
            return currentDispatcher;

        // Do not capture another thread's application dispatcher. Unit hosts and
        // automation runners may leave an Application instance alive without
        // pumping its queue; synchronously invoking it would stall commands.
        var applicationDispatcher = Application.Current?.Dispatcher;
        return applicationDispatcher is not null &&
               applicationDispatcher.CheckAccess() &&
               !applicationDispatcher.HasShutdownStarted &&
               !applicationDispatcher.HasShutdownFinished
            ? applicationDispatcher
            : null;
    }

    private void InvokeOnUi(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var dispatcher = _uiDispatcher;
        if (dispatcher is null || dispatcher.CheckAccess() ||
            dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            action();
            return;
        }

        try
        {
            dispatcher.Invoke(action);
        }
        catch (InvalidOperationException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            // Application shutdown can race with a final timer tick; there is no
            // UI left to notify in that case.
        }
    }

    private void PostToUi(Action action)
    {
        var dispatcher = _uiDispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return;
        try
        {
            dispatcher.BeginInvoke(action, DispatcherPriority.DataBind);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute();
        public async void Execute(object? parameter) => await execute();
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
