using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.Workflows;

using ContractWorkflowDefinition = MesControlAgv.Contracts.Workflows.WorkflowDefinition;
using ContractWorkflowGraphDocument = MesControlAgv.Contracts.Workflows.WorkflowGraphDocument;
using ContractWorkflowCanvasViewport = MesControlAgv.Contracts.Workflows.WorkflowCanvasViewport;
using ContractWorkflowNode = MesControlAgv.Contracts.Workflows.WorkflowNode;
using ContractWorkflowNodeConfigurationKeys = MesControlAgv.Contracts.Workflows.WorkflowNodeConfigurationKeys;
using ContractWorkflowExecutionRequest = MesControlAgv.Contracts.Workflows.WorkflowExecutionRequest;
using ContractWorkflowExecutionResult = MesControlAgv.Contracts.Workflows.WorkflowExecutionResult;
using ContractWorkflowExecutionSnapshot = MesControlAgv.Contracts.Workflows.WorkflowExecutionSnapshot;
using ContractWorkflowExecutionStatus = MesControlAgv.Contracts.Workflows.WorkflowExecutionStatus;
using ContractWorkflowRuntimeStatus = MesControlAgv.Contracts.Workflows.WorkflowRuntimeStatus;
using ContractWorkflowAuditResponse = MesControlAgv.Contracts.Workflows.WorkflowAuditResponse;
using ContractWorkflowVersion = MesControlAgv.Contracts.Workflows.WorkflowVersion;
using ContractWorkflowParameter = MesControlAgv.Contracts.Workflows.WorkflowParameter;
using ContractWorkflowPhysicalRunAuthorization = MesControlAgv.Contracts.Workflows.WorkflowPhysicalRunAuthorization;
using ContractWorkflowPublishStatus = MesControlAgv.Contracts.Workflows.WorkflowPublishStatus;
using ContractWorkflowValidationResult = MesControlAgv.Contracts.Workflows.WorkflowValidationResult;
using ContractWorkflowVersionStatus = MesControlAgv.Contracts.Workflows.WorkflowVersionStatus;
using ContractAuboArmProgramCatalogResponse = MesControlAgv.Contracts.AuboArmProgramCatalogResponse;

namespace MesControlAgv.Wpf.ViewModels;

public enum WorkflowRemoteState
{
    LocalFallback,
    Loading,
    DraftSaved,
    Validated,
    ValidationFailed,
    Published,
    DryRunAccepted,
    DryRunRejected,
    SimulatorAccepted,
    PhysicalBatchAccepted,
    PhysicalBatchRejected,
    Cancelled,
    ServiceUnavailable,
    Error
}

public sealed class WorkflowEditorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly WorkflowStore _store;
    private readonly IMesClient? _mes;
    private readonly Func<string> _actorProvider;
    private readonly WorkflowCatalogSet _catalog;
    private readonly bool _simulatorExecutionEnabled;
    private readonly bool _physicalBatchExecutionEnabled;
    private readonly IWorkflowRunControlConfirmation _confirmation;
    private ProfileConfiguration _profileConfiguration;
    private WorkflowPublicationContext _publicationContext;
    private readonly Dictionary<string, string> _profileStationNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _robotProgramCatalogs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ContractWorkflowGraphDocument> _documents = [];
    private readonly ReadOnlyCollection<ContractWorkflowGraphDocument> _documentView;
    private readonly ObservableCollection<WorkflowDefinition> _workflowProjections = [];
    private readonly SemaphoreSlim _remoteGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<Guid, ContractWorkflowVersion> _remoteVersions = [];
    private readonly ObservableCollection<WorkflowNode> _emptyNodes = [];
    private WorkflowDefinition? _selectedWorkflow;
    private WorkflowNode? _selectedNode;
    private WorkflowNodeParameter? _selectedParameter;
    private string _message = string.Empty;
    private WorkflowRemoteState _remoteState = WorkflowRemoteState.LocalFallback;
    private string _remoteStatus = "仅使用本地数据";
    private string _robotProgramCatalogStatus = "尚未刷新";
    private bool _isRemoteBusy;
    private ContractWorkflowValidationResult? _lastValidation;
    private ContractWorkflowExecutionResult? _lastExecution;
    private ContractWorkflowExecutionSnapshot? _lastExecutionSnapshot;
    private IReadOnlyList<ContractWorkflowAuditResponse> _lastExecutionAudits = [];
    private bool _profileDefaultsApplied;
    private WorkflowCanvasSpikeViewModel? _canvasViewModel;
    private WorkflowDefinition? _observedWorkflow;
    private readonly HashSet<WorkflowNode> _observedWorkflowNodes = [];
    private readonly HashSet<WorkflowNodeParameter> _observedWorkflowParameters = [];
    private int _projectionUpdateDepth;
    private bool _projectionRefreshPending;
    private bool _projectionRefreshRecordsHistory;
    private bool _isApplyingCanvasDocument;
    private bool _isSynchronizingCanvasSelection;
    private WorkflowImportReport? _lastImportReport;
    private string _physicalBatchSafetyObserverName =
        Environment.GetEnvironmentVariable("WORKFLOW_SAFETY_OBSERVER") ?? string.Empty;
    private string _physicalBatchOperatorName = string.Empty;
    private string _physicalBatchPermitPrefix =
        Environment.GetEnvironmentVariable("WORKFLOW_PERMIT_PREFIX") ?? "material-batch";
    private string _physicalBatchPermitMinutes =
        Environment.GetEnvironmentVariable("WORKFLOW_PERMIT_MINUTES") ?? "1440";

    public WorkflowEditorViewModel(
        WorkflowStore store,
        IMesClient? mes = null,
        Func<string>? actorProvider = null,
        WorkflowCatalogSet? catalog = null,
        ProfileConfiguration? profileConfiguration = null,
        bool simulatorExecutionEnabled = false,
        bool physicalBatchExecutionEnabled = false,
        IWorkflowRunControlConfirmation? confirmation = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _mes = mes;
        _actorProvider = actorProvider ?? (() => "wpf-editor");
        _physicalBatchOperatorName =
            Environment.GetEnvironmentVariable("WORKFLOW_OPERATOR")?.Trim() is { Length: > 0 } configuredOperator
                ? configuredOperator
                : Actor;
        _catalog = catalog ?? BuiltInWorkflowCatalog.Create();
        _simulatorExecutionEnabled = simulatorExecutionEnabled;
        _physicalBatchExecutionEnabled = physicalBatchExecutionEnabled;
        _confirmation = confirmation ?? MessageBoxWorkflowRunControlConfirmation.Instance;
        _profileConfiguration = EnsureRobotArmProfile(profileConfiguration ?? ProfileConfiguration.Default);
        _publicationContext = WorkflowPublicationContext.FromProfile(_profileConfiguration);
        foreach (var station in _profileConfiguration.Stations)
            _profileStationNames[station.StationId] = station.Name;
        Inspector = new WorkflowInspectorViewModel();
        Validation = new WorkflowValidationViewModel(NavigateToValidationIssue);
        NodeTypeOptions = CreateNodeTypeOptions();
        _documentView = _documents.AsReadOnly();
        _documents.AddRange(_store.LoadDocuments());
        foreach (var document in _documents)
            _workflowProjections.Add(WorkflowDocumentMapper.FromGraph(document));
        Workflows = new ReadOnlyObservableCollection<WorkflowDefinition>(_workflowProjections);
        _lastImportReport = _store.LastLoadReport;

        NewWorkflowCommand = new EditorCommand(CreateWorkflow);
        CreateAuboTemplateCommand = new EditorCommand(CreateAuboTemplate);
        RefreshRobotProgramsCommand = new AsyncEditorCommand(
            () => RunRemoteAsync(
                "刷新机械臂程序目录",
                () => RefreshRobotProgramsCoreAsync(_shutdown.Token),
                _shutdown.Token),
            CanUseRemote);
        CopyWorkflowCommand = new EditorCommand(CopyWorkflow, () => SelectedWorkflow is not null);
        DeleteWorkflowCommand = new EditorCommand(DeleteWorkflow, () => SelectedWorkflow is not null);
        SaveCommand = new EditorCommand(Save);
        AddNodeCommand = new EditorCommand(AddNode, () => SelectedWorkflow is not null);
        DeleteNodeCommand = new EditorCommand(DeleteNode, () => SelectedWorkflow is not null && SelectedNode is not null);
        AddParameterCommand = new EditorCommand(AddParameter, () => SelectedNode is not null);
        DeleteParameterCommand = new EditorCommand(
            DeleteParameter,
            () => SelectedNode is not null && SelectedParameter is not null);
        MoveNodeLeftCommand = new EditorCommand(() => MoveNode(-1), CanMoveNodeLeft);
        MoveNodeRightCommand = new EditorCommand(() => MoveNode(1), CanMoveNodeRight);
        LoadFromMesCommand = new AsyncCommand(
        () => RunRemoteAsync("加载工作流", () => LoadFromMesCoreAsync(_shutdown.Token), _shutdown.Token),
            CanUseRemote);
        SaveDraftCommand = new AsyncCommand(
        () => RunRemoteAsync("保存草稿", () => SaveDraftCoreAsync(_shutdown.Token), _shutdown.Token),
            CanSaveDraft);
        ValidateCommand = new AsyncCommand(
        () => RunRemoteAsync("校验工作流", () => ValidateCoreAsync(_shutdown.Token), _shutdown.Token),
            CanValidate);
        PublishCommand = new AsyncCommand(
        () => RunRemoteAsync("发布工作流", () => PublishCoreAsync(_shutdown.Token), _shutdown.Token),
            CanPublish);
        DryRunCommand = new AsyncCommand(
        () => RunRemoteAsync("模拟运行工作流", () => DryRunCoreAsync(_shutdown.Token), _shutdown.Token),
            CanDryRun);
        ExecuteSimulatorCommand = new AsyncCommand(
            () => RunRemoteAsync("执行模拟流程", () => ExecuteSimulatorCoreAsync(_shutdown.Token), _shutdown.Token),
            CanExecuteSimulator);
        ExecutePhysicalBatchCommand = new AsyncCommand(
            () => RunRemoteAsync("一键现场执行", () => ExecutePhysicalBatchCoreAsync(_shutdown.Token), _shutdown.Token),
            CanExecutePhysicalBatch);
        RefreshRemoteCommand = LoadFromMesCommand;

        SelectedWorkflow = Workflows.FirstOrDefault();
    }

    public WorkflowEditorViewModel(WorkflowStore store, IMesClient? mes, string? actor)
        : this(store, mes, () => string.IsNullOrWhiteSpace(actor) ? "wpf-editor" : actor.Trim())
    {
    }

    public WorkflowEditorViewModel(IMesClient mes, WorkflowStore store, string? actor = null)
        : this(store, mes, () => string.IsNullOrWhiteSpace(actor) ? "wpf-editor" : actor.Trim())
    {
    }

    public ReadOnlyObservableCollection<WorkflowDefinition> Workflows { get; }

    /// <summary>
    /// Canonical workflow definitions owned by the editor. Workflows and Nodes
    /// are WPF presentation projections only; persistence, MES requests, and
    /// canvas history always read from this graph-document collection.
    /// </summary>
    public IReadOnlyList<ContractWorkflowGraphDocument> GraphDocuments => _documentView;

    public WorkflowInspectorViewModel Inspector { get; }

    public WorkflowValidationViewModel Validation { get; }

    public IReadOnlyList<WorkflowNodeTypeOption> NodeTypeOptions { get; }

    public bool ApplyProfileStations(
        IReadOnlyList<DashboardStation> stations,
        IReadOnlyList<MesControlAgv.Contracts.MapEdgeResponse>? mapEdges = null)
    {
        ArgumentNullException.ThrowIfNull(stations);
        ApplyInspectorProfileStations(stations, mapEdges);
        if (_profileDefaultsApplied || !_store.LastLoadUsedDefaults)
        {
            return false;
        }

        var enabled = stations
            .Where(station => station.Enabled && !string.IsNullOrWhiteSpace(station.AgvStationId))
            .OrderBy(station => station.Code)
            .ToList();
        if (enabled.Count < 2)
        {
            return false;
        }

        var source = FindPreferredStation(enabled, ["Sample", "Pickup"]) ?? enabled[0];
        var remaining = enabled
            .Where(station => !string.Equals(station.AgvStationId, source.AgvStationId, StringComparison.Ordinal))
            .ToList();
        if (remaining.Count == 0)
        {
            return false;
        }
        var target = FindPreferredStation(remaining, ["Preparation", "Dropoff"]) ?? remaining[^1];
        var defaults = WorkflowStore.CreateDefaultWorkflows(source.AgvStationId, target.AgvStationId)
            .Select(WorkflowDocumentMapper.ToGraph)
            .ToArray();

        SelectedWorkflow = null;
        _documents.Clear();
        _workflowProjections.Clear();
        foreach (var document in defaults)
        {
            _documents.Add(document);
            _workflowProjections.Add(WorkflowDocumentMapper.FromGraph(document));
        }

        _profileDefaultsApplied = true;
        SelectedWorkflow = Workflows.FirstOrDefault();
        return true;
    }

    private static DashboardStation? FindPreferredStation(
        IEnumerable<DashboardStation> stations,
        IReadOnlyList<string> preferredTypes)
    {
        foreach (var type in preferredTypes)
        {
            var station = stations.FirstOrDefault(candidate =>
                string.Equals(candidate.Type, type, StringComparison.OrdinalIgnoreCase));
            if (station is not null)
            {
                return station;
            }
        }

        return null;
    }

    private static ProfileConfiguration EnsureRobotArmProfile(ProfileConfiguration profile)
    {
        if ((profile.WorkflowDevices ?? []).Any(device =>
                string.Equals(device.DeviceFamily, MesControlAgv.Contracts.Workflows.WorkflowDeviceFamilyIds.RobotArm, StringComparison.OrdinalIgnoreCase)))
        {
            return profile;
        }

        return profile with
        {
            WorkflowDevices = (profile.WorkflowDevices ?? [])
                .Concat([
                    new WorkflowDeviceProfile
                    {
                        DeviceId = "ARM-01",
                        DeviceFamily = MesControlAgv.Contracts.Workflows.WorkflowDeviceFamilyIds.RobotArm,
                        CapabilityIds = [MesControlAgv.Contracts.Workflows.WorkflowCapabilityIds.RobotExecuteProgram],
                        Enabled = true,
                        // Design-time editing is allowed so the operator can
                        // build a flow; publication/execution still requires
                        // an explicit control-enabled deployment profile.
                        ControlEnabled = false
                    }
                ])
                .ToArray()
        };
    }

    private void ApplyInspectorProfileStations(
        IReadOnlyList<DashboardStation> stations,
        IReadOnlyList<MesControlAgv.Contracts.MapEdgeResponse>? mapEdges = null)
    {
        var profileStations = stations
            .Where(station => !string.IsNullOrWhiteSpace(station.AgvStationId))
            .Select(station => new StationProfile
            {
                Code = station.Code,
                StationId = station.AgvStationId.Trim(),
                AgvStationId = station.AgvStationId.Trim(),
                Name = station.Name,
                Type = station.Type ?? string.Empty,
                Enabled = station.Enabled
            })
            .ToArray();
        var map = mapEdges is { Count: > 0 }
            ? new MapProfile
            {
                StationIds = profileStations.Select(station => station.AgvStationId).ToArray(),
                Edges = mapEdges.Select(edge => new MapEdgeProfile
                {
                    From = edge.From,
                    To = edge.To,
                    Cost = edge.Cost,
                    Bidirectional = edge.Bidirectional
                }).ToArray()
            }
            : _profileConfiguration.Map;
        _profileConfiguration = _profileConfiguration with { Stations = profileStations, Map = map };
        _publicationContext = WorkflowPublicationContext.FromProfile(_profileConfiguration);
        _profileStationNames.Clear();
        foreach (var station in profileStations)
            _profileStationNames[station.StationId] = station.Name;
        RefreshInspector();
    }

    private IReadOnlyList<WorkflowNodeTypeOption> CreateNodeTypeOptions()
    {
        var orderedIds = new[]
        {
            MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.Start,
            MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.End,
            MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.Move,
            MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.TimedWait,
            MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.ManualConfirmation,
            MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.InstrumentReadStatus,
            MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.InstrumentWaitUntilStable,
            MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.RobotExecuteProgram
        };
        return orderedIds.Select(nodeTypeId =>
        {
            var definition = _catalog.NodeTypes.GetLatest(nodeTypeId) ??
                throw new InvalidOperationException($"Built-in workflow node type '{nodeTypeId}' is missing.");
            var available = IsNodeTypeAvailable(definition, out var reason);
            return new WorkflowNodeTypeOption(
                definition.NodeTypeId,
                definition.SchemaVersion,
                LocalizeNodeType(definition.NodeTypeId, definition.DisplayName),
                definition.Category,
                CompatibilityTypeFor(definition.NodeTypeId),
                available,
                reason);
        }).ToArray();
    }

    private bool IsNodeTypeAvailable(
        MesControlAgv.Contracts.Workflows.WorkflowNodeTypeDefinition definition,
        out string? reason)
    {
        if (!definition.Enabled)
        {
            reason = "节点类型在当前目录中已禁用。";
            return false;
        }
        if (!SupportsProfile(definition.ProfileSupport, _publicationContext.ProductId))
        {
            reason = "节点类型不支持当前 Profile。";
            return false;
        }

        foreach (var capabilityId in definition.RequiredCapabilityIds)
        {
            var capability = _catalog.Capabilities.GetLatest(capabilityId);
            if (capability is null || !capability.Enabled)
            {
                reason = capability?.UnavailableReason ?? $"目录能力 {capabilityId} 不可用。";
                return false;
            }
            var requiresControl = capability.SafetyClassification is
                MesControlAgv.Contracts.Workflows.WorkflowSafetyClassification.ControlledDeviceAction or
                MesControlAgv.Contracts.Workflows.WorkflowSafetyClassification.RestrictedDeviceWrite;
            if (requiresControl && !capability.ControlEnabled &&
                !string.Equals(definition.NodeTypeId, MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.RobotExecuteProgram, StringComparison.OrdinalIgnoreCase))
            {
                reason = capability.UnavailableReason ?? $"目录能力 {capabilityId} 未启用控制。";
                return false;
            }
            if (!SupportsProfile(capability.ProfileSupport, _publicationContext.ProductId))
            {
                reason = $"目录能力 {capabilityId} 不支持当前 Profile。";
                return false;
            }

            var providers = _publicationContext.GetDevices(capability.DeviceFamily)
                .Where(device => device.Enabled && device.Provides(capabilityId));
            if (requiresControl) providers = providers.Where(device => device.ControlEnabled);
            if (!providers.Any() &&
                !string.Equals(definition.NodeTypeId, MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.RobotExecuteProgram, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"当前 Profile 没有可用设备提供 {capabilityId}.";
                return false;
            }
        }

        reason = null;
        return true;
    }

    private static bool SupportsProfile(
        MesControlAgv.Contracts.Workflows.WorkflowProfileSupport support,
        string productId) =>
        support.IsProfileIndependent ||
        support.SupportedProductIds.Contains(productId, StringComparer.OrdinalIgnoreCase);

    private static WorkflowNodeType CompatibilityTypeFor(string nodeTypeId) => nodeTypeId switch
    {
        MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.Start => WorkflowNodeType.Start,
        MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.End => WorkflowNodeType.End,
        MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.Move => WorkflowNodeType.Move,
        MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.TimedWait => WorkflowNodeType.Wait,
        MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.InstrumentReadStatus => WorkflowNodeType.InstrumentOperation,
        MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.InstrumentWaitUntilStable => WorkflowNodeType.InstrumentOperation,
        MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.RobotExecuteProgram => WorkflowNodeType.RobotProgram,
        _ => WorkflowNodeType.Custom
    };

    private static string LocalizeNodeType(string nodeTypeId, string fallback) => nodeTypeId switch
    {
        MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.Start => "开始",
        MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.End => "结束",
        MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.Move => "AGV 到站",
        MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.TimedWait => "定时等待",
        MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.ManualConfirmation => "人工确认",
        MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.InstrumentReadStatus => "仪器读取",
        MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.InstrumentWaitUntilStable => "仪器稳定等待",
        _ => fallback
    };

    public WorkflowDefinition? SelectedWorkflow
    {
        get => _selectedWorkflow;
        set
        {
            if (ReferenceEquals(_selectedWorkflow, value)) return;
            DetachWorkflowProjection();
            _selectedWorkflow = value;
            AttachWorkflowProjection();
            _isSynchronizingCanvasSelection = true;
            try
            {
                SelectedNode = value?.Nodes.OrderBy(node => node.Order).FirstOrDefault();
            }
            finally
            {
                _isSynchronizingCanvasSelection = false;
            }
            _lastValidation = value is not null && _remoteVersions.TryGetValue(value.Id, out var remote)
                ? remote.Validation
                : null;
            RefreshValidation();
            OnPropertyChanged();
            OnPropertyChanged(nameof(Nodes));
            OnPropertyChanged(nameof(SelectedGraphDocument));
            RebuildCanvasForSelection();
            OnPropertyChanged(nameof(SelectedRemoteVersion));
            OnPropertyChanged(nameof(RemoteStatus));
            OnPropertyChanged(nameof(ValidationSummary));
            RefreshCommandStates();
        }
    }

    public WorkflowNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (ReferenceEquals(_selectedNode, value)) return;
            _selectedNode = value;
            SelectedParameter = value?.Parameters.FirstOrDefault();
            RefreshInspector();
            if (!_isSynchronizingCanvasSelection &&
                value is not null &&
                _canvasViewModel?.SelectedNode?.Id != value.Id)
            {
                _isSynchronizingCanvasSelection = true;
                try
                {
                    _canvasViewModel?.SelectNode(value.Id);
                }
                finally
                {
                    _isSynchronizingCanvasSelection = false;
                }
            }
            OnPropertyChanged();
            RefreshCommandStates();
        }
    }

    /// <summary>The canonical graph currently selected by the WPF projection.</summary>
    public ContractWorkflowGraphDocument? SelectedGraphDocument =>
        SelectedWorkflow is { } workflow
            ? FindDocument(workflow.Id)
            : null;

    public WorkflowImportReport? LastImportReport
    {
        get => _lastImportReport;
        private set
        {
            if (ReferenceEquals(_lastImportReport, value)) return;
            _lastImportReport = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasImportReport));
            OnPropertyChanged(nameof(ImportReportSummary));
            OnPropertyChanged(nameof(ImportReportDetails));
        }
    }

    public bool HasImportReport => LastImportReport is not null;

    public string ImportReportSummary => LastImportReport?.Summary ?? string.Empty;

    public string ImportReportDetails => LastImportReport?.Details ?? string.Empty;

    public WorkflowCanvasSpikeViewModel? CanvasViewModel => _canvasViewModel;

    public void SelectNodeById(Guid nodeId)
    {
        var node = Nodes.FirstOrDefault(candidate => candidate.Id == nodeId);
        if (node is not null) SelectedNode = node;
    }

    private void NavigateToValidationIssue(WorkflowValidationIssueItemViewModel issue)
    {
        if (issue.EdgeId is { } edgeId &&
            _canvasViewModel?.Connections.FirstOrDefault(candidate => candidate.Id == edgeId) is { } edge)
        {
            _canvasViewModel.SelectedNodes.Clear();
            _canvasViewModel.SelectedConnection = edge;
            ValidationNavigationRequested?.Invoke(this, issue);
            Message = $"已定位校验问题 {issue.Code}：{issue.Location}。";
            return;
        }

        if (issue.NodeId is { } nodeId && Nodes.Any(candidate => candidate.Id == nodeId))
        {
            if (_canvasViewModel is not null) _canvasViewModel.SelectedConnection = null;
            SelectNodeById(nodeId);
            ValidationNavigationRequested?.Invoke(this, issue);
            Message = $"已定位校验问题 {issue.Code}：{issue.Location}。";
            return;
        }

        Message = $"校验问题 {issue.Code} 没有可定位的当前画布对象。";
    }

    private void RefreshValidation() => Validation.Load(_lastValidation, SelectedGraphDocument);

    private void RefreshInspector() => Inspector.Load(
        SelectedNode,
        _catalog,
        _publicationContext,
        _profileStationNames,
        CommitInspectorField,
        ResolveRobotProgramNames(SelectedNode));

    private IReadOnlyList<string> ResolveRobotProgramNames(WorkflowNode? node)
    {
        var deviceId = ResolveRobotDeviceId(node);
        return _robotProgramCatalogs.TryGetValue(deviceId, out var names)
            ? names
            : Array.Empty<string>();
    }

    private string ResolveRobotDeviceId(WorkflowNode? node)
    {
        var configured = node is not null && string.Equals(
                node.GraphNodeTypeId,
                MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.RobotExecuteProgram,
                StringComparison.OrdinalIgnoreCase)
            ? node.Configuration
                .FirstOrDefault(pair => string.Equals(
                    pair.Key,
                    MesControlAgv.Contracts.Workflows.WorkflowNodeConfigurationKeys.DeviceId,
                    StringComparison.OrdinalIgnoreCase))
                .Value
            : null;
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

        return _profileConfiguration.WorkflowDevices
                   .FirstOrDefault(device => string.Equals(
                       device.DeviceFamily,
                       MesControlAgv.Contracts.Workflows.WorkflowDeviceFamilyIds.RobotArm,
                       StringComparison.OrdinalIgnoreCase))?.DeviceId
               ?? AuboArmControlViewModel.DefaultDeviceId;
    }

    private async Task RefreshRobotProgramsCoreAsync(CancellationToken cancellationToken)
    {
        if (_mes is null) return;

        var deviceId = ResolveRobotDeviceId(SelectedNode);
        ContractAuboArmProgramCatalogResponse? catalog;
        try
        {
            catalog = await _mes.GetAuboArmProgramCatalogAsync(deviceId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RobotProgramCatalogStatus = $"{deviceId} 程序目录读取失败：{exception.Message}";
            Message = "机械臂程序目录读取失败；未自动重试，请确认连接后手动重试。";
            throw;
        }

        if (catalog is null)
        {
            RobotProgramCatalogStatus = $"{deviceId} 程序目录暂不可用；未修改已有选择";
            Message = "未读取到机械臂程序目录，请确认连接后再手动刷新。";
            RefreshInspector();
            return;
        }

        var names = catalog.AvailablePrograms
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _robotProgramCatalogs[deviceId] = names;
        var observation = catalog.IsCached
            ? $"缓存，观测于 {catalog.ObservedAtUtc.ToLocalTime():HH:mm:ss}"
            : $"新鲜读取，观测于 {catalog.ObservedAtUtc.ToLocalTime():HH:mm:ss}";
        RobotProgramCatalogStatus = catalog.IsComplete
            ? $"{deviceId} 已读取 {names.Length} 个可用程序（预加载槽位/允许列表；{observation}）"
            : $"{deviceId} 程序目录部分读取；仍有 {catalog.ReadErrors.Count} 个槽位错误（{observation}）";
        Message = names.Length == 0
            ? $"{deviceId} 未返回可选程序；流程节点不会写入程序名。"
            : $"已将 {deviceId} 的程序目录加载到流程编辑器，可在机械臂节点中选择。";
        RefreshInspector();
    }

    private void CommitInspectorField(WorkflowInspectorFieldViewModel field, string? value)
    {
        if (SelectedNode is not { } node || node.Id != field.NodeId || field.IsReadOnly) return;

        BeginProjectionUpdate();
        try
        {
            var configuration = new Dictionary<string, string?>(
                node.Configuration,
                StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(value))
                configuration.Remove(field.Key);
            else
                configuration[field.Key] = value;
            node.Configuration = configuration;

            if (string.Equals(
                    field.Key,
                    MesControlAgv.Contracts.Workflows.WorkflowNodeConfigurationKeys.TargetStation,
                    StringComparison.OrdinalIgnoreCase))
            {
                node.TargetStation = value;
            }

            Message = $"已更新节点字段“{field.DisplayName}”。";
        }
        finally
        {
            EndProjectionUpdate();
        }
    }

    public WorkflowNodeParameter? SelectedParameter
    {
        get => _selectedParameter;
        set
        {
            if (ReferenceEquals(_selectedParameter, value)) return;
            _selectedParameter = value;
            OnPropertyChanged();
            RefreshCommandStates();
        }
    }

    public ObservableCollection<WorkflowNode> Nodes => SelectedWorkflow?.Nodes ?? _emptyNodes;

    public string Message
    {
        get => _message;
        private set
        {
            if (_message == value) return;
            _message = value;
            OnPropertyChanged();
        }
    }

    public WorkflowRemoteState RemoteState
    {
        get => _remoteState;
        private set
        {
            if (_remoteState == value) return;
            _remoteState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RemoteStateDescription));
        }
    }

    public string RemoteStateDescription => RemoteState switch
    {
        WorkflowRemoteState.Loading => "MES 工作流请求进行中",
        WorkflowRemoteState.DraftSaved => "MES 草稿已保存",
        WorkflowRemoteState.Validated => "MES 校验通过",
        WorkflowRemoteState.ValidationFailed => "MES 校验未通过",
        WorkflowRemoteState.Published => "MES 版本已发布",
        WorkflowRemoteState.DryRunAccepted => "模拟运行已受理，未发送 AGV 指令",
        WorkflowRemoteState.SimulatorAccepted => "本地模拟流程已受理，正在由隔离 worker 执行",
        WorkflowRemoteState.PhysicalBatchAccepted => "现场批量流程已受理，等待已授权 worker 按顺序执行",
        WorkflowRemoteState.PhysicalBatchRejected => "现场批量流程被拒绝",
        WorkflowRemoteState.DryRunRejected => "模拟运行被拒绝",
        WorkflowRemoteState.Cancelled => "MES 工作流请求已取消，本地 JSON 仍可用",
        WorkflowRemoteState.ServiceUnavailable => "MES 不可用，本地 JSON 仍可用",
        WorkflowRemoteState.Error => "MES 工作流操作失败",
        _ => "仅使用本地 JSON"
    };

    public bool IsLoading => IsRemoteBusy;

    public bool IsSimulatorExecutionEnabled => _simulatorExecutionEnabled;

    public bool IsPhysicalBatchExecutionEnabled => _physicalBatchExecutionEnabled;

    public string PhysicalBatchExecutionStatus => _physicalBatchExecutionEnabled
        ? "现场一键执行已显式启用；仅限标准模板，有效期 30–1440 分钟，仍需 MES worker 和现场预检。"
        : "现场一键执行默认关闭；完成现场预检后由启动配置显式启用。";

    public string PhysicalBatchSafetyObserverName
    {
        get => _physicalBatchSafetyObserverName;
        set
        {
            if (SetField(ref _physicalBatchSafetyObserverName, value ?? string.Empty))
                RefreshCommandStates();
        }
    }

    public string PhysicalBatchOperatorName
    {
        get => _physicalBatchOperatorName;
        set
        {
            if (SetField(ref _physicalBatchOperatorName, value?.Trim() ?? string.Empty))
                RefreshCommandStates();
        }
    }

    public string PhysicalBatchPermitPrefix
    {
        get => _physicalBatchPermitPrefix;
        set
        {
            if (SetField(ref _physicalBatchPermitPrefix, value ?? string.Empty))
                RefreshCommandStates();
        }
    }

    public string PhysicalBatchPermitMinutes
    {
        get => _physicalBatchPermitMinutes;
        set
        {
            if (SetField(ref _physicalBatchPermitMinutes, value ?? string.Empty))
                RefreshCommandStates();
        }
    }

    public ContractWorkflowVersion? RemoteVersion => SelectedRemoteVersion;

    public string RemoteVersionDescription => RemoteVersion is { } version
        ? $"v{version.Version} / {version.Status} / {version.PublishStatus}"
        : "尚未确认 MES 版本";

    public ContractWorkflowValidationResult? ValidationResult => LastValidation;

    public ContractWorkflowExecutionResult? DryRunResult => LastExecution;

    /// <summary>Last read-only runtime snapshot returned by MES, if available.</summary>
    public ContractWorkflowExecutionSnapshot? ExecutionSnapshot => _lastExecutionSnapshot;

    public IReadOnlyList<ContractWorkflowAuditResponse> ExecutionAudits => _lastExecutionAudits;

    public string DryRunSummary => DryRunResult is null
        ? "尚未执行模拟运行"
        : DryRunResult.IsAccepted
            ? $"已受理：{DryRunResult.NextStep?.NodeName ?? "无下一步"}"
            : $"已拒绝：{DryRunResult.RejectionCode ?? "未知原因"}";

    public string ExecutionRuntimeSummary => ExecutionSnapshot is null
        ? "运行快照：尚未读取"
        : $"运行快照：{DescribeRuntimeStatus(ExecutionSnapshot.RuntimeStatus)}" +
          $"；当前节点：{ExecutionSnapshot.PendingStepRequest?.NodeName ?? "无"}" +
          $"；尝试：{ExecutionSnapshot.Attempt}";

    public string ExecutionAuditSummary => ExecutionAudits.Count == 0
        ? "运行审计：尚无记录"
        : $"运行审计：{ExecutionAudits.Count} 条；最新：{ExecutionAudits[^1].EventType}";
    public bool IsRemoteAvailable => _mes is not null;

    /// <summary>
    /// Status of the last explicit read-only AUBO program catalog refresh. The
    /// catalog is intentionally not queried on every node selection.
    /// </summary>
    public string RobotProgramCatalogStatus
    {
        get => _robotProgramCatalogStatus;
        private set => SetField(ref _robotProgramCatalogStatus, value);
    }

    public bool IsRemoteBusy
    {
        get => _isRemoteBusy;
        private set
        {
            if (_isRemoteBusy == value) return;
            _isRemoteBusy = value;
            OnPropertyChanged();
            RefreshCommandStates();
        }
    }

    public string RemoteStatus
    {
        get => _remoteStatus;
        private set => SetField(ref _remoteStatus, value);
    }

    public ContractWorkflowVersion? SelectedRemoteVersion =>
        SelectedWorkflow is { } workflow && _remoteVersions.TryGetValue(workflow.Id, out var version)
            ? version
            : null;

    public ContractWorkflowValidationResult? LastValidation => _lastValidation;

    public ContractWorkflowExecutionResult? LastExecution => _lastExecution;

    public string ValidationSummary => _lastValidation is null
        ? "尚未校验"
        : _lastValidation.IsValid
            ? (_lastValidation.HasWarnings ? "有效，但有警告" : "有效")
            : $"无效（{_lastValidation.Issues.Count} 个问题）";

    public ICommand NewWorkflowCommand { get; }
    public ICommand CreateAuboTemplateCommand { get; }
    public ICommand RefreshRobotProgramsCommand { get; }
    public ICommand CopyWorkflowCommand { get; }
    public ICommand DeleteWorkflowCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand RefreshRemoteCommand { get; }
    public ICommand SaveDraftCommand { get; }
    public ICommand ValidateCommand { get; }
    public ICommand PublishCommand { get; }
    public ICommand DryRunCommand { get; }
    public ICommand ExecuteSimulatorCommand { get; }
    public ICommand ExecutePhysicalBatchCommand { get; }
    public ICommand AddNodeCommand { get; }
    public ICommand DeleteNodeCommand { get; }
    public ICommand AddParameterCommand { get; }
    public ICommand DeleteParameterCommand { get; }
    public ICommand MoveNodeLeftCommand { get; }
    public ICommand MoveNodeRightCommand { get; }
    public ICommand LoadFromMesCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<WorkflowValidationIssueItemViewModel>? ValidationNavigationRequested;

    private void CreateWorkflow()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "新实验流程",
            Description = "可编辑的实验流程",
            IsPreset = false,
            Nodes =
            [
                new WorkflowNode { Type = WorkflowNodeType.Start, Name = "开始", Description = "启动实验流程", X = 0, Y = 100, Order = 1 },
                new WorkflowNode { Type = WorkflowNodeType.End, Name = "结束", Description = "实验流程完成", X = 220, Y = 100, Order = 2 }
            ]
        };
        var document = WorkflowDocumentMapper.ToGraph(workflow);
        _documents.Add(document);
        var projection = WorkflowDocumentMapper.FromGraph(document);
        _workflowProjections.Add(projection);
        SelectedWorkflow = projection;
        Message = "已新建实验流程。";
    }

    private void CreateAuboTemplate()
    {
        var enabled = _profileConfiguration.Stations
            .Where(station => station.Enabled && !string.IsNullOrWhiteSpace(station.AgvStationId))
            .ToArray();
        var origin = enabled.FirstOrDefault(station =>
                         string.Equals(station.Type, "Charge", StringComparison.OrdinalIgnoreCase))?.AgvStationId
                     ?? enabled.FirstOrDefault()?.AgvStationId
                     ?? string.Empty;
        var station1 = ResolveNumberedFieldStation(enabled, origin, "站点1", "LM7", 7);
        var station2 = ResolveNumberedFieldStation(enabled, origin, "站点2", "LM2", 2, station1);
        if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(station1) || string.IsNullOrWhiteSpace(station2))
        {
            Message = "当前站点目录不足原点、站点1和站点2三个可用站点，无法创建料盘标准模板。";
            return;
        }
        var armDeviceId = _profileConfiguration.WorkflowDevices
            .FirstOrDefault(device => string.Equals(
                device.DeviceFamily,
                MesControlAgv.Contracts.Workflows.WorkflowDeviceFamilyIds.RobotArm,
                StringComparison.OrdinalIgnoreCase))?.DeviceId;
        var template = WorkflowStore.CreateStandardMaterialHandlingWorkflow(
            origin,
            station1,
            station2,
            armDeviceId: armDeviceId);
        var document = WorkflowDocumentMapper.ToGraph(template);
        _documents.Add(document);
        var projection = WorkflowDocumentMapper.FromGraph(document);
        _workflowProjections.Add(projection);
        SelectedWorkflow = projection;
        Message = $"已创建料盘标准模板：原点 {origin} → 站点1 {station1}（取料盘.pro）→ 站点2 {station2}（放料盘.pro）→ 站点1 {station1}（回收料盘.pro）→ 原点 {origin}。请只读刷新目录并确认三个实际程序名，发布前需现场开启机械臂控制权限。";
    }

    private static string? ResolveNumberedFieldStation(
        IReadOnlyList<StationProfile> stations,
        string origin,
        string displayName,
        string fallbackStationId,
        int fallbackCode,
        string? excludedStationId = null)
    {
        bool IsCandidate(StationProfile station) =>
            station.Enabled &&
            !string.Equals(station.AgvStationId, origin, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(station.AgvStationId, excludedStationId, StringComparison.OrdinalIgnoreCase);

        return stations.FirstOrDefault(station => IsCandidate(station) &&
                (string.Equals(station.Name?.Trim(), displayName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(station.StationId?.Trim(), displayName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(station.AgvStationId?.Trim(), displayName, StringComparison.OrdinalIgnoreCase)))?.AgvStationId
            ?? stations.FirstOrDefault(station => IsCandidate(station) &&
                string.Equals(station.AgvStationId, fallbackStationId, StringComparison.OrdinalIgnoreCase))?.AgvStationId
            ?? stations.FirstOrDefault(station => IsCandidate(station) && station.Code == fallbackCode)?.AgvStationId
            ?? stations.Where(IsCandidate).OrderBy(station => station.Code).ThenBy(station => station.AgvStationId, StringComparer.OrdinalIgnoreCase).FirstOrDefault()?.AgvStationId;
    }

    private void CopyWorkflow()
    {
        if (SelectedGraphDocument is not { } source) return;
        var copy = CloneDocument(source);
        _documents.Add(copy);
        var projection = WorkflowDocumentMapper.FromGraph(copy);
        _workflowProjections.Add(projection);
        SelectedWorkflow = projection;
        Message = "已复制实验流程。";
    }

    private void DeleteWorkflow()
    {
        if (SelectedWorkflow is not { } workflow) return;
        var index = Workflows.IndexOf(workflow);
        _documents.RemoveAll(document => document.Id == workflow.Id);
        _workflowProjections.Remove(workflow);
        SelectedWorkflow = Workflows.ElementAtOrDefault(Math.Clamp(index, 0, Math.Max(Workflows.Count - 1, 0)));
        Message = "已删除实验流程。";
    }

    private void Save()
    {
        try
        {
            _store.SaveDocuments(_documents);
            Message = $"已保存到 {_store.FilePath}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // EditorCommand is synchronous; let a locked/read-only workflow
            // file become an in-panel diagnostic instead of an unhandled WPF
            // dispatcher exception that terminates the control centre.
            Message = $"本地流程保存失败：{exception.Message}";
        }
    }

    public bool ImportCompatibilityFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        try
        {
            return ApplyImport(_store.ImportFile(filePath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LastImportReport = CreateImportFailureReport(filePath, "IMPORT_READ_FAILED", exception.Message);
            Message = "兼容流程文件读取失败，未修改当前流程。";
            return false;
        }
    }

    public bool ImportCompatibilityJson(string json, string? sourceName = null) =>
        ApplyImport(_store.ImportJson(json, sourceName));

    private bool ApplyImport(WorkflowImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        LastImportReport = result.Report;
        if (!result.CanImport)
        {
            Message = "兼容流程转换失败，当前流程未修改。";
            return false;
        }

        WorkflowDefinition? firstImported = null;
        var added = 0;
        var replaced = 0;
        foreach (var document in result.Documents)
        {
            _remoteVersions.Remove(document.Id);
            var existing = Workflows.FirstOrDefault(workflow => workflow.Id == document.Id);
            if (existing is null)
            {
                _documents.Add(document);
                existing = WorkflowDocumentMapper.FromGraph(document);
                _workflowProjections.Add(existing);
                added++;
            }
            else
            {
                ReplaceDocument(document);
                ApplyDocumentToProjection(existing, document);
                replaced++;
            }
            firstImported ??= existing;
        }

        SelectedWorkflow = firstImported;
        RebuildCanvasForSelection();
        Message = $"兼容导入完成：新增 {added} 个流程，替换 {replaced} 个同 ID 流程；请查看转换报告并确认后保存。";
        RefreshCommandStates();
        return true;
    }

    private bool CanUseRemote() => _mes is not null && !IsRemoteBusy;

    private bool CanSaveDraft() => CanUseRemote() && SelectedWorkflow is not null;

    private bool CanValidate() => CanUseRemote() && SelectedWorkflow is not null;

    private bool CanPublish() =>
        CanUseRemote() &&
        SelectedRemoteVersion is { Status: ContractWorkflowVersionStatus.Draft or ContractWorkflowVersionStatus.Validated } version &&
        version.Validation?.IsValid == true;

    private bool CanDryRun() =>
        CanUseRemote() &&
        SelectedRemoteVersion is { Status: ContractWorkflowVersionStatus.Published, PublishStatus: ContractWorkflowPublishStatus.Published };

    private bool CanExecuteSimulator() =>
        _simulatorExecutionEnabled &&
        CanUseRemote() &&
        SelectedRemoteVersion is { Status: ContractWorkflowVersionStatus.Published, PublishStatus: ContractWorkflowPublishStatus.Published };

    private bool CanExecutePhysicalBatch() =>
        _physicalBatchExecutionEnabled &&
        CanUseRemote() &&
        IsStandardMaterialWorkflow(SelectedWorkflow) &&
        SelectedRemoteVersion is { Status: ContractWorkflowVersionStatus.Published, PublishStatus: ContractWorkflowPublishStatus.Published } &&
        !string.IsNullOrWhiteSpace(PhysicalBatchOperatorName) &&
        !string.IsNullOrWhiteSpace(PhysicalBatchSafetyObserverName) &&
        !string.IsNullOrWhiteSpace(PhysicalBatchPermitPrefix) &&
        int.TryParse(PhysicalBatchPermitMinutes, out var minutes) && minutes is >= 30 and <= 1440;

    private async Task RunRemoteAsync(string action, Func<Task> operation, CancellationToken cancellationToken)
    {
        if (_mes is null) return;

        if (!await _remoteGate.WaitAsync(0))
        {
            Message = "已有工作流操作正在执行，请稍候。";
            return;
        }

        IsRemoteBusy = true;
        RemoteState = WorkflowRemoteState.Loading;
        RemoteStatus = action + "...";
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RemoteState = WorkflowRemoteState.Cancelled;
            Message = "MES 工作流请求已取消，本地 JSON 仍可用。";
            RemoteStatus = RemoteStateDescription;
        }
        catch (OperationCanceledException exception)
        {
            RemoteState = WorkflowRemoteState.ServiceUnavailable;
            Message = $"MES 工作流请求超时：{exception.Message}；本地 JSON 仍可用。";
            RemoteStatus = RemoteStateDescription;
        }
        catch (HttpRequestException exception)
        {
            RemoteState = WorkflowRemoteState.ServiceUnavailable;
            Message = $"MES 不可用：{exception.Message}；本地 JSON 仍可用。";
            RemoteStatus = RemoteStateDescription;
        }
        catch (Exception exception)
        {
            RemoteState = WorkflowRemoteState.Error;
            Message = $"MES 工作流操作失败：{exception.Message}";
            RemoteStatus = RemoteStateDescription;
        }
        finally
        {
            IsRemoteBusy = false;
            try { _remoteGate.Release(); }
            catch (ObjectDisposedException) { }
            RefreshCommandStates();
        }
    }

    public Task LoadRemoteAsync(CancellationToken cancellationToken = default) =>
        RunRemoteAsync("加载工作流", () => LoadFromMesCoreAsync(cancellationToken), cancellationToken);

    public Task SaveDraftAsync(CancellationToken cancellationToken = default) =>
        RunRemoteAsync("保存草稿", () => SaveDraftCoreAsync(cancellationToken), cancellationToken);

    public Task ValidateRemoteAsync(CancellationToken cancellationToken = default) =>
        RunRemoteAsync("校验工作流", () => ValidateCoreAsync(cancellationToken), cancellationToken);

    public Task PublishRemoteAsync(CancellationToken cancellationToken = default) =>
        RunRemoteAsync("发布工作流", () => PublishWithLifecycleCoreAsync(cancellationToken), cancellationToken);

    public Task ExecuteDryRunAsync(CancellationToken cancellationToken = default) =>
        RunRemoteAsync("模拟运行工作流", () => DryRunCoreAsync(cancellationToken), cancellationToken);

    public Task ExecuteSimulatorAsync(CancellationToken cancellationToken = default) =>
        RunRemoteAsync("执行模拟流程", () => ExecuteSimulatorCoreAsync(cancellationToken), cancellationToken);

    private async Task LoadFromMesCoreAsync(CancellationToken cancellationToken)
    {
        if (_mes is null) return;

        var definitions = await _mes.GetWorkflowsAsync(cancellationToken);
        var selectedId = SelectedWorkflow?.Id;
        var loadedIds = new HashSet<Guid>();
        foreach (var definition in definitions)
        {
            var document = WorkflowGraphContractAdapter.FromContract(definition);
            var local = WorkflowDocumentMapper.FromGraph(document);
            var versions = await _mes.GetWorkflowVersionsAsync(definition.Id, cancellationToken);
            var latest = versions.OrderByDescending(version => version.Version).FirstOrDefault();
            if (latest is not null) _remoteVersions[definition.Id] = latest;

            var existing = Workflows.FirstOrDefault(workflow => workflow.Id == local.Id);
            if (existing is null)
            {
                _documents.Add(document);
                _workflowProjections.Add(local);
            }
            else
            {
                var index = Workflows.IndexOf(existing);
                ReplaceDocument(document);
                _workflowProjections[index] = local;
            }

            loadedIds.Add(local.Id);
        }

        if (selectedId is { } id && loadedIds.Contains(id))
        {
            SelectedWorkflow = Workflows.First(workflow => workflow.Id == id);
        }
        else if (loadedIds.Count > 0)
        {
            SelectedWorkflow = Workflows.First(workflow => loadedIds.Contains(workflow.Id));
        }

        UpdateRemotePresentation($"已从 MES 加载 {definitions.Count} 个工作流");
        RemoteState = SelectedRemoteVersion?.PublishStatus == ContractWorkflowPublishStatus.Published
            ? WorkflowRemoteState.Published
            : SelectedRemoteVersion?.Status == ContractWorkflowVersionStatus.Validated
                ? WorkflowRemoteState.Validated
                : WorkflowRemoteState.DraftSaved;
        Message = RemoteStatus;
    }

    private async Task SaveDraftCoreAsync(CancellationToken cancellationToken)
    {
        if (_mes is null || SelectedWorkflow is not { } workflow) return;

        try
        {
            _store.SaveDocuments(_documents);
        }
        catch (Exception exception)
        {
            RemoteState = WorkflowRemoteState.Error;
            Message = $"本地工作流保存失败，未向 MES 发送草稿：{exception.Message}";
            return;
        }

        var definition = WorkflowGraphContractAdapter.ToContract(
            RequireDocument(workflow.Id));
        var current = SelectedRemoteVersion;
        ContractWorkflowVersion saved;
        if (current is { Status: ContractWorkflowVersionStatus.Draft, PublishStatus: ContractWorkflowPublishStatus.NotPublished })
        {
            saved = await _mes.UpdateWorkflowDraftAsync(
                workflow.Id,
                current.Version,
                definition,
                Actor,
                cancellationToken);
        }
        else
        {
            saved = await _mes.CreateWorkflowDraftAsync(definition, Actor, cancellationToken);
        }

        SetRemoteVersion(saved);
        RemoteState = WorkflowRemoteState.DraftSaved;
        Message = $"草稿已保存为 v{saved.Version}。";
    }

    private async Task ValidateCoreAsync(CancellationToken cancellationToken)
    {
        if (_mes is null || SelectedWorkflow is not { } workflow) return;

        var current = SelectedRemoteVersion;
        var result = current is null
            ? await _mes.ValidateWorkflowAsync(
                WorkflowGraphContractAdapter.ToContract(RequireDocument(workflow.Id)),
                cancellationToken)
            : await _mes.ValidateWorkflowVersionAsync(workflow.Id, current.Version, cancellationToken);
        _lastValidation = result;
        if (current is not null)
        {
            _remoteVersions[workflow.Id] = current with
            {
                Validation = result,
                Status = current.Status
            };
        }

        UpdateRemotePresentation(result.IsValid ? "校验通过" : "校验未通过");
        RemoteState = result.IsValid ? WorkflowRemoteState.Validated : WorkflowRemoteState.ValidationFailed;
        RefreshValidation();
        OnPropertyChanged(nameof(LastValidation));
        OnPropertyChanged(nameof(ValidationSummary));
        Message = ValidationSummary;
    }

    private async Task PublishCoreAsync(CancellationToken cancellationToken)
    {
        if (_mes is null || SelectedWorkflow is not { } workflow || SelectedRemoteVersion is not { } current) return;

        var published = await _mes.PublishWorkflowAsync(
            workflow.Id,
            current.Version,
            Actor,
            cancellationToken);
        SetRemoteVersion(published);
        RemoteState = WorkflowRemoteState.Published;
        Message = $"工作流已发布为 v{published.Version}。";
    }

    private async Task PublishWithLifecycleCoreAsync(CancellationToken cancellationToken)
    {
        await SaveDraftCoreAsync(cancellationToken);
        await ValidateCoreAsync(cancellationToken);
        if (SelectedRemoteVersion?.Validation?.IsValid != true)
        {
            RemoteState = WorkflowRemoteState.ValidationFailed;
            Message = "工作流校验未通过，未向 MES 发送发布请求。";
            return;
        }

        await PublishCoreAsync(cancellationToken);
    }

    private async Task DryRunCoreAsync(CancellationToken cancellationToken)
    {
        if (_mes is null || SelectedWorkflow is not { } workflow) return;

        if (SelectedRemoteVersion is not { PublishStatus: ContractWorkflowPublishStatus.Published } version)
        {
            _lastExecution = new ContractWorkflowExecutionResult
            {
                Status = ContractWorkflowExecutionStatus.Rejected,
                RequestId = Guid.NewGuid(),
                WorkflowId = workflow.Id,
                RejectionCode = "WORKFLOW_VERSION_NOT_PUBLISHED",
                RejectionReason = "模拟运行需要已确认发布的 MES 工作流版本。",
                DryRun = true
            };
            _lastExecutionSnapshot = null;
            _lastExecutionAudits = [];
            RemoteState = WorkflowRemoteState.DryRunRejected;
            OnPropertyChanged(nameof(LastExecution));
            OnPropertyChanged(nameof(DryRunResult));
            OnPropertyChanged(nameof(ExecutionSnapshot));
            OnPropertyChanged(nameof(ExecutionRuntimeSummary));
            OnPropertyChanged(nameof(ExecutionAudits));
            OnPropertyChanged(nameof(ExecutionAuditSummary));
            Message = "模拟运行被拒绝：WORKFLOW_VERSION_NOT_PUBLISHED。";
            RemoteStatus = Message;
            return;
        }

        var result = await _mes.ExecuteWorkflowAsync(
            new ContractWorkflowExecutionRequest
            {
                WorkflowId = workflow.Id,
                Version = version.Version,
                RequestedBy = Actor,
                CorrelationId = $"wpf-dry-run-{Guid.NewGuid():N}",
                DryRun = true
            },
            cancellationToken);
        _lastExecution = result;
        _lastExecutionSnapshot = await ReadExecutionSnapshotAsync(result, cancellationToken);
        _lastExecutionAudits = await ReadExecutionAuditsAsync(result, cancellationToken);
        RemoteState = result.IsAccepted ? WorkflowRemoteState.DryRunAccepted : WorkflowRemoteState.DryRunRejected;
        OnPropertyChanged(nameof(LastExecution));
        OnPropertyChanged(nameof(ExecutionSnapshot));
        OnPropertyChanged(nameof(ExecutionRuntimeSummary));
        OnPropertyChanged(nameof(ExecutionAudits));
        OnPropertyChanged(nameof(ExecutionAuditSummary));
        Message = result.IsAccepted
            ? result.NextStep is null
                ? "模拟运行已受理，工作流已到达终点。"
                : $"模拟运行已受理，下一步：{result.NextStep.NodeName}。"
            : $"模拟运行被拒绝：{result.RejectionCode ?? result.RejectionReason ?? "未知原因"}。";
        RemoteStatus = Message;
    }

    private async Task ExecuteSimulatorCoreAsync(CancellationToken cancellationToken)
    {
        if (!_simulatorExecutionEnabled || SelectedWorkflow is not { } workflow ||
            SelectedRemoteVersion is not { } version || _mes is null)
        {
            return;
        }

        var result = await _mes.ExecuteWorkflowAsync(
            new ContractWorkflowExecutionRequest
            {
                WorkflowId = workflow.Id,
                Version = version.Version,
                RequestId = Guid.NewGuid(),
                RequestedBy = Actor,
                CorrelationId = $"wpf-simulator-{Guid.NewGuid():N}",
                DryRun = false
            },
            cancellationToken);
        _lastExecution = result;
        _lastExecutionSnapshot = await ReadExecutionSnapshotAsync(result, cancellationToken);
        _lastExecutionAudits = await ReadExecutionAuditsAsync(result, cancellationToken);
        RemoteState = result.IsAccepted ? WorkflowRemoteState.SimulatorAccepted : WorkflowRemoteState.DryRunRejected;
        OnPropertyChanged(nameof(LastExecution));
        OnPropertyChanged(nameof(ExecutionSnapshot));
        OnPropertyChanged(nameof(ExecutionRuntimeSummary));
        OnPropertyChanged(nameof(ExecutionAudits));
        OnPropertyChanged(nameof(ExecutionAuditSummary));
        Message = result.IsAccepted
            ? result.NextStep is null
                ? "本地模拟流程已受理并完成；运行监控已自动加载。"
                : $"本地模拟流程已受理，下一步：{result.NextStep.NodeName}。运行监控已自动加载。"
            : $"本地模拟流程被拒绝：{result.RejectionCode ?? result.RejectionReason ?? "未知原因"}。";
        RemoteStatus = Message;
    }

    private async Task ExecutePhysicalBatchCoreAsync(CancellationToken cancellationToken)
    {
        if (!_physicalBatchExecutionEnabled || _mes is null ||
            SelectedWorkflow is not { } workflow ||
            SelectedRemoteVersion is not { } version ||
            !int.TryParse(PhysicalBatchPermitMinutes, out var permitMinutes) ||
            permitMinutes is < 30 or > 1440)
        {
            return;
        }

        var operatorName = PhysicalBatchOperatorName.Trim();
        var observerName = PhysicalBatchSafetyObserverName.Trim();
        var permitPrefix = PhysicalBatchPermitPrefix.Trim();
        var expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(permitMinutes);
        if (!_confirmation.Confirm(
                "确认一键现场执行",
                $"将按已发布标准模板一次性提交完整现场流程，并由 MES 按节点顺序自动创建/授权 Move 许可。\n" +
                $"操作者：{operatorName}\n安全监护人：{observerName}\n许可前缀：{permitPrefix}\n" +
                "现场 worker 会在每个 AGV/机械臂节点前重新读取设备状态；可恢复条件只重试只读检查，已发送但结果不明确的命令不会自动重发。确认继续？"))
        {
            return;
        }

        var agvId = _profileConfiguration.Agvs
            .FirstOrDefault(agv => agv.Enabled)?.AgvId ?? string.Empty;
        var result = await _mes.ExecuteWorkflowAsync(
            new ContractWorkflowExecutionRequest
            {
                WorkflowId = workflow.Id,
                Version = version.Version,
                RequestId = Guid.NewGuid(),
                RequestedBy = operatorName,
                CorrelationId = $"wpf-physical-batch-{Guid.NewGuid():N}",
                DryRun = false,
                PhysicalAuthorization = new ContractWorkflowPhysicalRunAuthorization
                {
                    AgvId = agvId,
                    OperatorName = operatorName,
                    SafetyObserverName = observerName,
                    PermitPrefix = permitPrefix,
                    ExpiresAtUtc = expiresAtUtc
                }
            },
            cancellationToken);
        _lastExecution = result;
        _lastExecutionSnapshot = await ReadExecutionSnapshotAsync(result, cancellationToken);
        _lastExecutionAudits = await ReadExecutionAuditsAsync(result, cancellationToken);
        RemoteState = result.IsAccepted ? WorkflowRemoteState.PhysicalBatchAccepted : WorkflowRemoteState.PhysicalBatchRejected;
        OnPropertyChanged(nameof(LastExecution));
        OnPropertyChanged(nameof(ExecutionSnapshot));
        OnPropertyChanged(nameof(ExecutionAudits));
        OnPropertyChanged(nameof(ExecutionAuditSummary));
        Message = result.IsAccepted
            ? "现场批量流程已受理；后续 Move/AUBO 节点由已启用的 MES worker 按标准顺序执行。"
            : DescribePhysicalBatchRejection(result);
        RemoteStatus = Message;
    }

    private static string DescribePhysicalBatchRejection(ContractWorkflowExecutionResult result) =>
        result.RejectionCode switch
        {
            "WORKFLOW_PHYSICAL_AGV_BUSY" =>
                $"现场批量流程被拒绝：已有未结束的现场流程占用 AGV，请先在运行监控中完成或核销。{result.RejectionReason}",
            "WORKFLOW_PHYSICAL_EXECUTION_DISABLED" =>
                $"现场批量流程被拒绝：MES 现场 worker 尚未全部启用。{result.RejectionReason}",
            "WORKFLOW_PHYSICAL_TEMPLATE_REQUIRED" =>
                "现场批量流程被拒绝：所选发布版本不是批准的 LM1→LM7→LM2→LM7→LM1 料盘标准模板。",
            "WORKFLOW_PHYSICAL_AUTHORIZATION_INVALID" =>
                $"现场批量流程被拒绝：操作者、监护人或许可有效期无效。{result.RejectionReason}",
            _ => $"现场批量流程被拒绝：{result.RejectionCode ?? result.RejectionReason ?? "未知原因"}。"
        };

    private static bool IsStandardMaterialWorkflow(WorkflowDefinition? workflow)
    {
        if (workflow is null ||
            !workflow.Name.Contains("料盘标准流程", StringComparison.Ordinal))
            return false;

        var nodes = workflow.Nodes.OrderBy(node => node.Order).ToArray();
        var expectedTypes = new[]
        {
            WorkflowNodeType.Start,
            WorkflowNodeType.Move,
            WorkflowNodeType.RobotProgram,
            WorkflowNodeType.Move,
            WorkflowNodeType.RobotProgram,
            WorkflowNodeType.Move,
            WorkflowNodeType.RobotProgram,
            WorkflowNodeType.Move,
            WorkflowNodeType.End
        };
        if (nodes.Length != expectedTypes.Length ||
            nodes.Where((node, index) => node.Type != expectedTypes[index]).Any())
            return false;

        for (var index = 0; index < nodes.Length; index++)
        {
            var expectedNext = index + 1 < nodes.Length ? nodes[index + 1].Id : (Guid?)null;
            var actualNext = nodes[index].NextNodeIds?.ToArray() ?? [];
            if (expectedNext is null ? actualNext.Length != 0 :
                actualNext.Length != 1 || actualNext[0] != expectedNext.Value)
                return false;
        }

        var expectedStations = new[] { "LM7", "LM2", "LM7", "LM1" };
        var moveNodes = nodes.Where(node => node.Type == WorkflowNodeType.Move).ToArray();
        if (moveNodes.Where((node, index) => !string.Equals(
                ReadWorkflowNodeValue(node, ContractWorkflowNodeConfigurationKeys.TargetStation) ?? node.TargetStation,
                expectedStations[index],
                StringComparison.OrdinalIgnoreCase)).Any())
            return false;

        var expectedPrograms = new[] { "取料盘.pro", "放料盘.pro", "回收料盘.pro" };
        var programNodes = nodes.Where(node => node.Type == WorkflowNodeType.RobotProgram).ToArray();
        return programNodes.Select((node, index) => new
            {
                DeviceId = ReadWorkflowNodeValue(node, ContractWorkflowNodeConfigurationKeys.DeviceId),
                Program = ReadWorkflowNodeValue(node, ContractWorkflowNodeConfigurationKeys.ProgramName),
                Expected = expectedPrograms[index]
            })
            .All(item =>
                string.Equals(item.DeviceId, "ARM-01", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Program, item.Expected, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadWorkflowNodeValue(WorkflowNode node, string key) =>
        node.Configuration.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private async Task<ContractWorkflowExecutionSnapshot?> ReadExecutionSnapshotAsync(
        ContractWorkflowExecutionResult result,
        CancellationToken cancellationToken)
    {
        if (_mes is null || !result.IsAccepted || result.ExecutionId == Guid.Empty)
        {
            return null;
        }

        try
        {
            return await _mes.GetWorkflowExecutionAsync(result.ExecutionId, cancellationToken);
        }
        catch (HttpRequestException)
        {
            // A successful admission must remain visible when connected to an
            // older MES that does not yet expose the read-only snapshot route.
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<ContractWorkflowAuditResponse>> ReadExecutionAuditsAsync(
        ContractWorkflowExecutionResult result,
        CancellationToken cancellationToken)
    {
        if (_mes is null || result.WorkflowId == Guid.Empty)
        {
            return [];
        }

        try
        {
            return await _mes.GetWorkflowAuditsAsync(result.WorkflowId, result.Version, 20, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return [];
        }
        catch (NotSupportedException)
        {
            return [];
        }
    }

    private static string DescribeRuntimeStatus(ContractWorkflowRuntimeStatus status) => status switch
    {
        ContractWorkflowRuntimeStatus.Rejected => "已拒绝",
        ContractWorkflowRuntimeStatus.DryRunCompleted => "模拟运行完成",
        ContractWorkflowRuntimeStatus.Prepared => "等待编排",
        ContractWorkflowRuntimeStatus.Running => "运行中",
        ContractWorkflowRuntimeStatus.Paused => "已暂停",
        ContractWorkflowRuntimeStatus.Completed => "已完成",
        ContractWorkflowRuntimeStatus.Failed => "失败",
        ContractWorkflowRuntimeStatus.Unknown => "待对账",
        ContractWorkflowRuntimeStatus.Cancelled => "已取消",
        _ => "未知"
    };

    private string Actor
    {
        get
        {
            var actor = _actorProvider();
            return string.IsNullOrWhiteSpace(actor) ? "wpf-editor" : actor.Trim();
        }
    }

    private void SetRemoteVersion(ContractWorkflowVersion version)
    {
        _remoteVersions[version.WorkflowId] = version;
        _lastValidation = version.Validation;
        RefreshValidation();
        if (SelectedWorkflow?.Id == version.WorkflowId)
        {
            CommitLifecycleMetadata(
                version.WorkflowId,
                version.Definition.PublishedVersion);
        }

        OnPropertyChanged(nameof(SelectedRemoteVersion));
        OnPropertyChanged(nameof(LastValidation));
        OnPropertyChanged(nameof(ValidationSummary));
        UpdateRemotePresentation();
        RefreshCommandStates();
    }

    private void UpdateRemotePresentation(string? status = null)
    {
        if (status is not null)
        {
            RemoteStatus = status;
        }
        else if (SelectedRemoteVersion is { } version)
        {
            RemoteStatus = $"MES v{version.Version}：{version.Status}/{version.PublishStatus}";
        }
        else
        {
            RemoteStatus = IsRemoteAvailable ? "暂无 MES 版本" : "仅使用本地数据";
        }

        OnPropertyChanged(nameof(SelectedRemoteVersion));
        OnPropertyChanged(nameof(ValidationSummary));
    }

    private void AddNode() => AddNodeAt(
        MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.TimedWait,
        null,
        null);

    public void AddNodeAt(string nodeTypeId, double? x, double? y)
    {
        if (SelectedWorkflow is not { } workflow) return;
        var option = NodeTypeOptions.FirstOrDefault(candidate =>
            string.Equals(candidate.NodeTypeId, nodeTypeId, StringComparison.OrdinalIgnoreCase));
        if (option is null)
        {
            Message = $"节点类型 '{nodeTypeId}' 不在当前目录中。";
            return;
        }
        if (!option.IsAvailable)
        {
            Message = option.UnavailableReason ?? $"节点类型 '{nodeTypeId}' 当前不可用。";
            return;
        }
        if (!_catalog.NodeTypes.TryGet(option.NodeTypeId, option.SchemaVersion, out var definition) ||
            definition is null)
        {
            Message = $"节点类型 '{nodeTypeId}' 的 schema 无法解析。";
            return;
        }

        var configuration = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in definition.ConfigurationSchema.Fields.Where(field => field.DefaultValue is not null))
            configuration[field.Key] = field.DefaultValue;

        BeginProjectionUpdate();
        try
        {
            var nextOrder = workflow.Nodes.Count + 1;
            var node = new WorkflowNode
            {
                Type = option.CompatibilityType,
                GraphNodeTypeId = definition.NodeTypeId,
                SchemaVersion = definition.SchemaVersion,
                Name = option.DisplayName,
                Description = DefaultCatalogNodeDescription(definition.NodeTypeId),
                X = x ?? Math.Max(0, workflow.Nodes.Count * 180),
                Y = y ?? 100,
                Order = nextOrder,
                Ports = new ObservableCollection<MesControlAgv.Contracts.Workflows.WorkflowPortDefinition>(definition.Ports),
                Configuration = configuration
            };
            workflow.Nodes.Add(node);
            NormalizeOrders(workflow);
            SelectedNode = node;
            Message = $"已添加“{option.DisplayName}”节点。";
            RefreshCommandStates();
        }
        finally
        {
            EndProjectionUpdate();
        }
    }

    public void AddNodeAt(WorkflowNodeType type, double? x, double? y)
    {
        if (SelectedWorkflow is not { } workflow) return;
        BeginProjectionUpdate();
        try
        {
            var nextOrder = workflow.Nodes.Count + 1;
            var node = new WorkflowNode
            {
                Type = type,
                GraphNodeTypeId = MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.For(
                    (MesControlAgv.Contracts.Workflows.WorkflowNodeType)type),
                Name = DefaultNodeName(type, nextOrder),
                Description = DefaultNodeDescription(type),
                X = x ?? Math.Max(0, workflow.Nodes.Count * 180),
                Y = y ?? 100,
                Order = nextOrder
            };
            AddDefaultParameters(node);
            workflow.Nodes.Add(node);
            NormalizeOrders(workflow);
            SelectedNode = node;
            Message = "已添加流程节点。";
            RefreshCommandStates();
        }
        finally
        {
            EndProjectionUpdate();
        }
    }

    private static string DefaultNodeName(WorkflowNodeType type, int order) => type switch
    {
        WorkflowNodeType.Start => "开始",
        WorkflowNodeType.Move => "AGV 移动",
        WorkflowNodeType.Wait => "等待",
        WorkflowNodeType.Pickup => "确认取货",
        WorkflowNodeType.Dropoff => "确认放货",
        WorkflowNodeType.InstrumentOperation => "仪器操作",
        WorkflowNodeType.End => "结束",
        _ => $"自定义 {order}"
    };

    private static string DefaultNodeDescription(WorkflowNodeType type) => type switch
    {
        WorkflowNodeType.Start => "启动实验流程",
        WorkflowNodeType.Move => "控制 AGV 移动",
        WorkflowNodeType.Wait => "等待指定条件",
        WorkflowNodeType.Pickup => "等待人工确认取货",
        WorkflowNodeType.Dropoff => "等待人工确认放货",
        WorkflowNodeType.InstrumentOperation => "等待仪器协议适配器执行",
        WorkflowNodeType.End => "完成实验流程",
        _ => "自定义实验步骤"
    };

    private static void AddDefaultParameters(WorkflowNode node)
    {
        if (node.Type == WorkflowNodeType.Wait)
        {
            node.Parameters.Add(new WorkflowNodeParameter
            {
                Name = MesControlAgv.Contracts.Workflows.WorkflowRuntimeParameterNames.WaitDurationSeconds,
                Value = "1",
                DataType = "decimal"
            });
        }
        else if (node.Type == WorkflowNodeType.InstrumentOperation)
        {
            node.Parameters.Add(new WorkflowNodeParameter
            {
                Name = MesControlAgv.Contracts.Workflows.WorkflowRuntimeParameterNames.InstrumentId,
                DataType = "string",
                IsRequired = true
            });
            node.Parameters.Add(new WorkflowNodeParameter
            {
                Name = MesControlAgv.Contracts.Workflows.WorkflowRuntimeParameterNames.InstrumentOperation,
                DataType = "string",
                IsRequired = true
            });
        }
    }

    private static string DefaultCatalogNodeDescription(string nodeTypeId) => nodeTypeId switch
    {
        MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.Start => "启动实验流程",
        MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.End => "完成实验流程",
        MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.Move => "AGV 移动到 Profile 站点",
        MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.TimedWait => "等待指定时长",
        MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.ManualConfirmation => "等待操作员确认",
        MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.InstrumentReadStatus => "读取已验证的仪器状态",
        MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.InstrumentWaitUntilStable => "只读轮询仪器状态直至稳定",
        _ => "实验流程步骤"
    };

    private void AddParameter()
    {
        if (SelectedNode is not { } node) return;
        var parameter = new WorkflowNodeParameter
        {
            Name = $"parameter{node.Parameters.Count + 1}",
            DataType = "string"
        };
        node.Parameters.Add(parameter);
        SelectedParameter = parameter;
        Message = "已添加节点参数。";
    }

    private void DeleteParameter()
    {
        if (SelectedNode is not { } node || SelectedParameter is not { } parameter) return;
        var index = node.Parameters.IndexOf(parameter);
        node.Parameters.Remove(parameter);
        SelectedParameter = node.Parameters.ElementAtOrDefault(
            Math.Clamp(index, 0, Math.Max(node.Parameters.Count - 1, 0)));
        Message = "已删除节点参数。";
    }

    private void DeleteNode()
    {
        if (SelectedWorkflow is not { } workflow || SelectedNode is not { } node) return;
        BeginProjectionUpdate();
        try
        {
            workflow.Nodes.Remove(node);
            workflow.Edges = new ObservableCollection<MesControlAgv.Contracts.Workflows.WorkflowEdgeDefinition>(
                workflow.Edges.Where(edge =>
                    edge.SourceNodeId != node.Id && edge.TargetNodeId != node.Id));
            workflow.Layouts = new ObservableCollection<MesControlAgv.Contracts.Workflows.WorkflowNodeLayout>(
                workflow.Layouts.Where(layout => layout.NodeId != node.Id));
            NormalizeOrders(workflow);
            SelectedNode = workflow.Nodes.OrderBy(item => item.Order).ElementAtOrDefault(Math.Max(0, workflow.Nodes.Count - 1));
            Message = "已删除流程节点。";
            RefreshCommandStates();
        }
        finally
        {
            EndProjectionUpdate();
        }
    }

    private void MoveNode(int direction)
    {
        if (SelectedWorkflow is not { } workflow || SelectedNode is not { } node) return;
        var ordered = workflow.Nodes.OrderBy(item => item.Order).ToList();
        var index = ordered.IndexOf(node);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= ordered.Count) return;

        BeginProjectionUpdate();
        try
        {
            (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
            workflow.Nodes.Clear();
            foreach (var item in ordered) workflow.Nodes.Add(item);
            NormalizeOrders(workflow);
            Message = direction < 0 ? "节点已左移。" : "节点已右移。";
            RefreshCommandStates();
        }
        finally
        {
            EndProjectionUpdate();
        }
    }

    private bool CanMoveNodeLeft() => SelectedWorkflow is not null && SelectedNode is not null && SelectedNode.Order > 1;

    private bool CanMoveNodeRight() => SelectedWorkflow is not null && SelectedNode is not null && SelectedNode.Order < (SelectedWorkflow?.Nodes.Count ?? 0);

    private static void NormalizeOrders(WorkflowDefinition workflow)
    {
        var order = 1;
        foreach (var node in workflow.Nodes) node.Order = order++;
    }

    private void RefreshCommandStates()
    {
        foreach (var command in new[]
        {
            CreateAuboTemplateCommand,
            CopyWorkflowCommand,
            DeleteWorkflowCommand,
            AddNodeCommand,
            DeleteNodeCommand,
            AddParameterCommand,
            DeleteParameterCommand,
            MoveNodeLeftCommand,
            MoveNodeRightCommand,
            LoadFromMesCommand,
            SaveDraftCommand,
            ValidateCommand,
            PublishCommand,
            DryRunCommand,
            ExecuteSimulatorCommand,
            ExecutePhysicalBatchCommand
        }.OfType<EditorCommand>()) command.RaiseCanExecuteChanged();

        foreach (var command in new[]
        {
            LoadFromMesCommand,
            SaveDraftCommand,
            ValidateCommand,
            PublishCommand,
            DryRunCommand,
            ExecuteSimulatorCommand,
            ExecutePhysicalBatchCommand,
            RefreshRobotProgramsCommand
        }.OfType<AsyncCommand>()) command.RaiseCanExecuteChanged();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void RefreshRemoteCommandStates()
    {
        foreach (var command in new[]
                 {
                     RefreshRemoteCommand,
                     SaveDraftCommand,
                     ValidateCommand,
                     PublishCommand,
                     DryRunCommand,
                     RefreshRobotProgramsCommand
                 }.OfType<AsyncEditorCommand>())
        {
            command.RaiseCanExecuteChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        _shutdown.Cancel();
        DetachWorkflowProjection();
        _remoteGate.Dispose();
        _shutdown.Dispose();
    }

    private void RebuildCanvasForSelection()
    {
        if (_canvasViewModel is not null)
        {
            _canvasViewModel.SelectionChanged -= CanvasSelectionChanged;
            _canvasViewModel.ViewportChanged -= CanvasViewportChanged;
        }

        if (SelectedWorkflow is not { } workflow || FindDocument(workflow.Id) is not { } document)
        {
            _canvasViewModel = null;
            OnPropertyChanged(nameof(CanvasViewModel));
            return;
        }

        _canvasViewModel = new WorkflowCanvasSpikeViewModel(
            document,
            CommitCanvasDocument);
        _canvasViewModel.SelectionChanged += CanvasSelectionChanged;
        _canvasViewModel.ViewportChanged += CanvasViewportChanged;
        if (SelectedNode is { } selectedNode)
        {
            _isSynchronizingCanvasSelection = true;
            try
            {
                _canvasViewModel.SelectNode(selectedNode.Id);
            }
            finally
            {
                _isSynchronizingCanvasSelection = false;
            }
        }
        OnPropertyChanged(nameof(CanvasViewModel));
    }

    private void CommitCanvasDocument(ContractWorkflowGraphDocument document)
    {
        if (SelectedWorkflow is not { } workflow || document.Id != workflow.Id) return;

        var selectedNodeId = SelectedNode?.Id;
        ReplaceDocument(document);
        ApplyDocumentToProjection(workflow, document);

        _isSynchronizingCanvasSelection = true;
        try
        {
            SelectedNode = workflow.Nodes.FirstOrDefault(node => node.Id == selectedNodeId)
                ?? workflow.Nodes.OrderBy(node => node.Order).FirstOrDefault();
        }
        finally
        {
            _isSynchronizingCanvasSelection = false;
        }

        OnPropertyChanged(nameof(Nodes));
        OnPropertyChanged(nameof(SelectedGraphDocument));
        Message = "画布修改已回写到统一流程文档。";
    }

    private void CanvasSelectionChanged(object? sender, Guid? nodeId)
    {
        if (_isSynchronizingCanvasSelection) return;
        _isSynchronizingCanvasSelection = true;
        try
        {
            SelectedNode = nodeId is { } id
                ? Nodes.FirstOrDefault(node => node.Id == id)
                : null;
        }
        finally
        {
            _isSynchronizingCanvasSelection = false;
        }
    }

    private void CanvasViewportChanged(object? sender, ContractWorkflowCanvasViewport viewport)
    {
        if (!ReferenceEquals(sender, _canvasViewModel) || SelectedWorkflow is not { } workflow) return;
        var document = RequireDocument(workflow.Id) with { Viewport = viewport };
        ReplaceDocument(document);
        _isApplyingCanvasDocument = true;
        try
        {
            workflow.Viewport = viewport;
        }
        finally
        {
            _isApplyingCanvasDocument = false;
        }
        OnPropertyChanged(nameof(SelectedGraphDocument));
    }

    private void AttachWorkflowProjection()
    {
        _observedWorkflow = SelectedWorkflow;
        if (_observedWorkflow is null) return;

        _observedWorkflow.PropertyChanged += WorkflowProjectionPropertyChanged;
        _observedWorkflow.Nodes.CollectionChanged += WorkflowProjectionCollectionChanged;
        AttachWorkflowProjectionItems();
    }

    private void DetachWorkflowProjection()
    {
        if (_observedWorkflow is not null)
        {
            _observedWorkflow.PropertyChanged -= WorkflowProjectionPropertyChanged;
            _observedWorkflow.Nodes.CollectionChanged -= WorkflowProjectionCollectionChanged;
        }

        DetachWorkflowProjectionItems();
        _observedWorkflow = null;
    }

    private void AttachWorkflowProjectionItems()
    {
        if (_observedWorkflow is null) return;
        foreach (var node in _observedWorkflow.Nodes)
        {
            if (_observedWorkflowNodes.Add(node))
            {
                node.PropertyChanged += WorkflowProjectionPropertyChanged;
                node.Parameters.CollectionChanged += WorkflowProjectionCollectionChanged;
            }

            foreach (var parameter in node.Parameters)
            {
                if (!_observedWorkflowParameters.Add(parameter)) continue;
                parameter.PropertyChanged += WorkflowProjectionPropertyChanged;
            }
        }
    }

    private void DetachWorkflowProjectionItems()
    {
        foreach (var node in _observedWorkflowNodes)
        {
            node.PropertyChanged -= WorkflowProjectionPropertyChanged;
            node.Parameters.CollectionChanged -= WorkflowProjectionCollectionChanged;
        }

        foreach (var parameter in _observedWorkflowParameters)
            parameter.PropertyChanged -= WorkflowProjectionPropertyChanged;

        _observedWorkflowNodes.Clear();
        _observedWorkflowParameters.Clear();
    }

    private void WorkflowProjectionCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DetachWorkflowProjectionItems();
        AttachWorkflowProjectionItems();
        RequestCanvasRefresh();
    }

    private void WorkflowProjectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is WorkflowNode && e.PropertyName is not (
                nameof(WorkflowNode.Type) or
                nameof(WorkflowNode.Name) or
                nameof(WorkflowNode.Description) or
                nameof(WorkflowNode.TargetStation) or
                nameof(WorkflowNode.X) or
                nameof(WorkflowNode.Y) or
                nameof(WorkflowNode.Order) or
                nameof(WorkflowNode.Configuration)))
        {
            return;
        }

        if (sender is WorkflowNodeParameter && e.PropertyName is not (
                nameof(WorkflowNodeParameter.Name) or
                nameof(WorkflowNodeParameter.Value) or
                nameof(WorkflowNodeParameter.DataType) or
                nameof(WorkflowNodeParameter.IsRequired)))
        {
            return;
        }

        if (ReferenceEquals(sender, SelectedNode) && e.PropertyName == nameof(WorkflowNode.Type))
        {
            RefreshInspector();
        }

        var recordHistory = sender is not WorkflowDefinition ||
            e.PropertyName != nameof(WorkflowDefinition.PublishedVersion);
        RequestCanvasRefresh(recordHistory);
    }

    private void BeginProjectionUpdate() => _projectionUpdateDepth++;

    private void EndProjectionUpdate()
    {
        if (_projectionUpdateDepth == 0) return;
        _projectionUpdateDepth--;
        if (_projectionUpdateDepth != 0 || !_projectionRefreshPending) return;
        var recordHistory = _projectionRefreshRecordsHistory;
        _projectionRefreshPending = false;
        _projectionRefreshRecordsHistory = false;
        RefreshCanvasFromProjection(recordHistory);
    }

    private void RequestCanvasRefresh(bool recordHistory = true)
    {
        if (_isApplyingCanvasDocument) return;
        OnPropertyChanged(nameof(SelectedGraphDocument));
        if (_projectionUpdateDepth > 0)
        {
            _projectionRefreshPending = true;
            _projectionRefreshRecordsHistory |= recordHistory;
            return;
        }

        RefreshCanvasFromProjection(recordHistory);
    }

    private void RefreshCanvasFromProjection(bool recordHistory = true)
    {
        if (SelectedWorkflow is not { } workflow) return;
        var selectedNodeId = SelectedNode?.Id;
        var document = WorkflowDocumentMapper.ToGraph(workflow);
        ReplaceDocument(document);
        if (_canvasViewModel is null) return;
        _isSynchronizingCanvasSelection = true;
        try
        {
            _canvasViewModel.ApplyDocument(document, recordHistory);
            if (selectedNodeId is { } id) _canvasViewModel.SelectNode(id);
        }
        finally
        {
            _isSynchronizingCanvasSelection = false;
        }
    }

    private ContractWorkflowGraphDocument? FindDocument(Guid workflowId) =>
        _documents.FirstOrDefault(document => document.Id == workflowId);

    private ContractWorkflowGraphDocument RequireDocument(Guid workflowId) =>
        FindDocument(workflowId) ??
        throw new InvalidOperationException($"Workflow graph document '{workflowId}' is not owned by the editor.");

    private void ReplaceDocument(ContractWorkflowGraphDocument document)
    {
        var index = _documents.FindIndex(candidate => candidate.Id == document.Id);
        if (index < 0)
            _documents.Add(document);
        else
            _documents[index] = document;
        OnPropertyChanged(nameof(GraphDocuments));
        if (SelectedWorkflow?.Id == document.Id)
        {
            OnPropertyChanged(nameof(SelectedGraphDocument));
            RefreshValidation();
        }
    }

    private void ApplyDocumentToProjection(
        WorkflowDefinition projection,
        ContractWorkflowGraphDocument document)
    {
        var selectedNodeId = SelectedWorkflow?.Id == projection.Id ? SelectedNode?.Id : null;
        var updated = WorkflowDocumentMapper.FromGraph(document);
        var isObserved = ReferenceEquals(_observedWorkflow, projection);
        _isApplyingCanvasDocument = true;
        if (isObserved) DetachWorkflowProjection();
        try
        {
            projection.Name = updated.Name;
            projection.Description = updated.Description;
            projection.IsPreset = updated.IsPreset;
            projection.PublishedVersion = updated.PublishedVersion;
            projection.Nodes = updated.Nodes;
            projection.Edges = updated.Edges;
            projection.Layouts = updated.Layouts;
            projection.Viewport = updated.Viewport;
        }
        finally
        {
            if (isObserved) AttachWorkflowProjection();
            _isApplyingCanvasDocument = false;
        }

        if (!isObserved) return;
        _isSynchronizingCanvasSelection = true;
        try
        {
            SelectedNode = projection.Nodes.FirstOrDefault(node => node.Id == selectedNodeId)
                ?? projection.Nodes.OrderBy(node => node.Order).FirstOrDefault();
        }
        finally
        {
            _isSynchronizingCanvasSelection = false;
        }
        OnPropertyChanged(nameof(Nodes));
        OnPropertyChanged(nameof(SelectedGraphDocument));
    }

    private void CommitLifecycleMetadata(Guid workflowId, int? publishedVersion)
    {
        var document = RequireDocument(workflowId);
        if (document.PublishedVersion == publishedVersion) return;
        var updated = document with { PublishedVersion = publishedVersion };
        ReplaceDocument(updated);
        var projection = Workflows.FirstOrDefault(workflow => workflow.Id == workflowId);
        if (projection is not null) ApplyDocumentToProjection(projection, updated);
        if (_canvasViewModel is not null && SelectedWorkflow?.Id == workflowId)
            _canvasViewModel.ApplyDocument(updated, recordHistory: false);
    }

    private static ContractWorkflowGraphDocument CloneDocument(
        ContractWorkflowGraphDocument source)
    {
        var idMap = source.Nodes.ToDictionary(node => node.Id, _ => Guid.NewGuid());
        return source with
        {
            Id = Guid.NewGuid(),
            Name = $"{source.Name} - 副本",
            IsPreset = false,
            PublishedVersion = null,
            Nodes = source.Nodes.Select(node => node with { Id = idMap[node.Id] }).ToArray(),
            Edges = source.Edges
                .Where(edge => idMap.ContainsKey(edge.SourceNodeId) && idMap.ContainsKey(edge.TargetNodeId))
                .Select(edge => edge with
                {
                    Id = Guid.NewGuid(),
                    SourceNodeId = idMap[edge.SourceNodeId],
                    TargetNodeId = idMap[edge.TargetNodeId]
                }).ToArray(),
            Layouts = source.Layouts
                .Where(layout => idMap.ContainsKey(layout.NodeId))
                .Select(layout => layout with { NodeId = idMap[layout.NodeId] })
                .ToArray()
        };
    }

    private static WorkflowImportReport CreateImportFailureReport(
        string source,
        string code,
        string message) => new()
    {
        SourceName = source,
        Issues =
        [
            new WorkflowImportIssue(
                WorkflowImportIssueSeverity.Error,
                code,
                message,
                source)
        ]
    };

    private sealed class EditorCommand(Action execute, Func<bool>? canExecute = null) : ICommand
    {
        private readonly Action _execute = execute;
        private readonly Func<bool> _canExecute = canExecute ?? (() => true);

        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => _canExecute();
        public void Execute(object? parameter) => _execute();
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class AsyncEditorCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
    {
        private readonly Func<Task> _execute = execute;
        private readonly Func<bool> _canExecute = canExecute ?? (() => true);
        private bool _running;

        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => !_running && _canExecute();
        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) return;
            _running = true;
            RaiseCanExecuteChanged();
            try { await _execute(); }
            finally { _running = false; RaiseCanExecuteChanged(); }
        }
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
