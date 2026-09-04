using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Workflows;

using ContractWorkflowDefinition = MesControlAgv.Contracts.Workflows.WorkflowDefinition;
using ContractWorkflowExecutionRequest = MesControlAgv.Contracts.Workflows.WorkflowExecutionRequest;
using ContractWorkflowExecutionResult = MesControlAgv.Contracts.Workflows.WorkflowExecutionResult;
using ContractWorkflowExecutionStatus = MesControlAgv.Contracts.Workflows.WorkflowExecutionStatus;
using ContractWorkflowNode = MesControlAgv.Contracts.Workflows.WorkflowNode;
using ContractWorkflowNodeType = MesControlAgv.Contracts.Workflows.WorkflowNodeType;
using ContractWorkflowPublishStatus = MesControlAgv.Contracts.Workflows.WorkflowPublishStatus;
using ContractWorkflowValidationResult = MesControlAgv.Contracts.Workflows.WorkflowValidationResult;
using ContractWorkflowVersion = MesControlAgv.Contracts.Workflows.WorkflowVersion;
using ContractWorkflowVersionStatus = MesControlAgv.Contracts.Workflows.WorkflowVersionStatus;
using WorkflowNodeType = MesControlAgv.Contracts.Workflows.WorkflowNodeType;

namespace MesControlAgv.Wpf.Tests;

public sealed class WorkflowEditorRemoteTests
{
    [Fact]
    public async Task Editor_runs_remote_draft_validation_publish_and_dry_run_flow()
    {
        using var fixture = new TempWorkflowFile();
        var client = new WorkflowEditorClientStub(CreateContractWorkflow());
        var editor = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path), client, () => "operator-remote");

        editor.LoadFromMesCommand.Execute(null);
        await WaitUntilAsync(() => !editor.IsRemoteBusy && editor.SelectedRemoteVersion is not null);
        Assert.Equal("remote-transport", editor.SelectedWorkflow!.Name);
        Assert.Equal(1, editor.SelectedRemoteVersion!.Version);
        Assert.Equal(ContractWorkflowVersionStatus.Draft, editor.SelectedRemoteVersion.Status);

        editor.SaveDraftCommand.Execute(null);
        await WaitUntilAsync(() => !editor.IsRemoteBusy && client.UpdateDraftCallCount == 1);
        Assert.Equal("operator-remote", client.LastActor);

        editor.ValidateCommand.Execute(null);
        await WaitUntilAsync(() => !editor.IsRemoteBusy && editor.LastValidation is not null);
        Assert.True(editor.LastValidation!.IsValid);
        Assert.True(editor.PublishCommand.CanExecute(null));

        editor.PublishCommand.Execute(null);
        await WaitUntilAsync(() => !editor.IsRemoteBusy && editor.SelectedRemoteVersion?.PublishStatus == ContractWorkflowPublishStatus.Published);
        Assert.Equal(ContractWorkflowVersionStatus.Published, editor.SelectedRemoteVersion!.Status);
        Assert.True(editor.DryRunCommand.CanExecute(null));

        editor.DryRunCommand.Execute(null);
        await WaitUntilAsync(() => !editor.IsRemoteBusy && editor.LastExecution is not null);
        Assert.True(editor.LastExecution!.IsAccepted);
        Assert.True(editor.LastExecution.DryRun);
        Assert.Equal("Move", editor.LastExecution.NextStep!.NodeName);
        Assert.True(client.LastExecutionRequest!.DryRun);
        Assert.Equal(1, client.LastExecutionRequest.Version);
    }

    [Fact]
    public void Local_clone_remaps_edges_and_preserves_node_parameters()
    {
        var startId = Guid.NewGuid();
        var endId = Guid.NewGuid();
        var workflow = new WorkflowDefinition
        {
            Name = "local",
            Nodes =
            [
                new WorkflowNode { Id = startId, Type = MesControlAgv.Wpf.Workflows.WorkflowNodeType.Start, Name = "Start", Order = 1, NextNodeIds = [endId] },
                new WorkflowNode
                {
                    Id = endId,
                    Type = MesControlAgv.Wpf.Workflows.WorkflowNodeType.End,
                    Name = "End",
                    Order = 2,
                    Parameters = [new WorkflowNodeParameter { Name = "batch", Value = "B-1", IsRequired = true }]
                }
            ]
        };

        var copy = workflow.Clone();

        Assert.NotEqual(workflow.Id, copy.Id);
        var copiedStart = Assert.Single(copy.Nodes.Where(node => node.Type == MesControlAgv.Wpf.Workflows.WorkflowNodeType.Start));
        var copiedEnd = Assert.Single(copy.Nodes.Where(node => node.Type == MesControlAgv.Wpf.Workflows.WorkflowNodeType.End));
        Assert.Equal(copiedEnd.Id, Assert.Single(copiedStart.NextNodeIds));
        var parameter = Assert.Single(copiedEnd.Parameters);
        Assert.Equal("batch", parameter.Name);
        Assert.Equal("B-1", parameter.Value);
    }

    [Fact]
    public async Task Robot_program_catalog_refresh_populates_workflow_selector()
    {
        using var fixture = new TempWorkflowFile();
        var client = new WorkflowEditorClientStub(CreateContractWorkflow())
        {
            ProgramCatalog = new MesControlAgv.Contracts.AuboArmProgramCatalogResponse(
                "ARM-01",
                true,
                null,
                ["现场程序.pro", "校准程序.pro"],
                [],
                true,
                [],
                DateTimeOffset.UtcNow)
        };
        var editor = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path), client);
        editor.AddNodeAt(MesControlAgv.Contracts.Workflows.WorkflowGraphNodeTypeIds.RobotExecuteProgram, 100, 200);

        editor.RefreshRobotProgramsCommand.Execute(null);
        await WaitUntilAsync(() => !editor.IsRemoteBusy &&
            editor.RobotProgramCatalogStatus.Contains("2", StringComparison.Ordinal));

        var program = editor.Inspector.Fields.Single(field =>
            field.Key == MesControlAgv.Contracts.Workflows.WorkflowNodeConfigurationKeys.ProgramName);
        Assert.Equal(WorkflowInspectorEditorKind.Selection, program.EditorKind);
        program.Value = "校准程序.pro";
        Assert.Equal("校准程序.pro", editor.SelectedNode!.Configuration[
            MesControlAgv.Contracts.Workflows.WorkflowNodeConfigurationKeys.ProgramName]);
    }

    [Fact]
    public async Task Published_workflow_can_execute_only_when_simulator_mode_is_enabled()
    {
        using var fixture = new TempWorkflowFile();
        var client = new WorkflowEditorClientStub(CreateContractWorkflow());
        var editor = new WorkflowEditorViewModel(
            new WorkflowStore(fixture.Path),
            client,
            () => "simulator-operator",
            simulatorExecutionEnabled: true);

        editor.LoadFromMesCommand.Execute(null);
        await WaitUntilAsync(() => !editor.IsRemoteBusy && editor.SelectedRemoteVersion is not null);
        editor.ValidateCommand.Execute(null);
        await WaitUntilAsync(() => !editor.IsRemoteBusy && editor.LastValidation is not null);
        editor.PublishCommand.Execute(null);
        await WaitUntilAsync(() => !editor.IsRemoteBusy && editor.SelectedRemoteVersion?.PublishStatus == ContractWorkflowPublishStatus.Published);

        Assert.True(editor.ExecuteSimulatorCommand.CanExecute(null));
        editor.ExecuteSimulatorCommand.Execute(null);
        await WaitUntilAsync(() => !editor.IsRemoteBusy && client.LastExecutionRequest is not null);

        Assert.False(client.LastExecutionRequest!.DryRun);
        Assert.Equal("simulator-operator", client.LastExecutionRequest.RequestedBy);
        Assert.Contains("本地模拟流程已受理", editor.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Published_workflow_does_not_expose_simulator_execution_in_default_mode()
    {
        using var fixture = new TempWorkflowFile();
        var client = new WorkflowEditorClientStub(CreateContractWorkflow());
        var editor = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path), client);

        editor.LoadFromMesCommand.Execute(null);
        await WaitUntilAsync(() => !editor.IsRemoteBusy && editor.SelectedRemoteVersion is not null);
        editor.ValidateCommand.Execute(null);
        await WaitUntilAsync(() => !editor.IsRemoteBusy && editor.LastValidation is not null);
        editor.PublishCommand.Execute(null);
        await WaitUntilAsync(() => !editor.IsRemoteBusy && editor.SelectedRemoteVersion?.PublishStatus == ContractWorkflowPublishStatus.Published);

        Assert.False(editor.IsSimulatorExecutionEnabled);
        Assert.False(editor.ExecuteSimulatorCommand.CanExecute(null));
        Assert.False(editor.IsPhysicalBatchExecutionEnabled);
        Assert.False(editor.ExecutePhysicalBatchCommand.CanExecute(null));
    }

    [Fact]
    public async Task Published_standard_material_workflow_can_submit_one_physical_batch_authorization()
    {
        using var fixture = new TempWorkflowFile();
        var client = new WorkflowEditorClientStub(CreateStandardMaterialContractWorkflow());
        var editor = new WorkflowEditorViewModel(
            new WorkflowStore(fixture.Path),
            client,
            () => "33206",
            physicalBatchExecutionEnabled: true,
            confirmation: new AlwaysConfirm());

        editor.LoadFromMesCommand.Execute(null);
        await WaitUntilAsync(() => !editor.IsRemoteBusy && editor.SelectedRemoteVersion is not null);
        editor.ValidateCommand.Execute(null);
        await WaitUntilAsync(() => !editor.IsRemoteBusy && editor.LastValidation is not null);
        editor.PublishCommand.Execute(null);
        await WaitUntilAsync(() => !editor.IsRemoteBusy && editor.SelectedRemoteVersion?.PublishStatus == ContractWorkflowPublishStatus.Published);

        editor.PhysicalBatchOperatorName = "admin";
        editor.PhysicalBatchSafetyObserverName = "admin";
        editor.PhysicalBatchPermitPrefix = "material-test";
        editor.PhysicalBatchPermitMinutes = "60";
        Assert.True(editor.ExecutePhysicalBatchCommand.CanExecute(null));

        editor.ExecutePhysicalBatchCommand.Execute(null);
        await WaitUntilAsync(() => !editor.IsRemoteBusy && client.LastExecutionRequest is not null);

        var request = client.LastExecutionRequest!;
        Assert.False(request.DryRun);
        Assert.Equal("admin", request.RequestedBy);
        Assert.Equal("AGV-01", request.PhysicalAuthorization!.AgvId);
        Assert.Equal("admin", request.PhysicalAuthorization.OperatorName);
        Assert.Equal("admin", request.PhysicalAuthorization.SafetyObserverName);
        Assert.Equal("material-test", request.PhysicalAuthorization.PermitPrefix);
        Assert.True(request.PhysicalAuthorization.ExpiresAtUtc > DateTimeOffset.UtcNow);
        Assert.Contains("现场批量流程已受理", editor.Message, StringComparison.Ordinal);

        var programNode = editor.SelectedWorkflow!.Nodes
            .First(node => node.Type == MesControlAgv.Wpf.Workflows.WorkflowNodeType.RobotProgram);
        programNode.Configuration[MesControlAgv.Contracts.Workflows.WorkflowNodeConfigurationKeys.ProgramName] =
            "取料盘.lua";
        Assert.False(editor.ExecutePhysicalBatchCommand.CanExecute(null));
    }

    private static ContractWorkflowDefinition CreateContractWorkflow()
    {
        var startId = Guid.NewGuid();
        var moveId = Guid.NewGuid();
        var endId = Guid.NewGuid();
        return new ContractWorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "remote-transport",
            Description = "Remote test workflow",
            Nodes =
            [
                new ContractWorkflowNode { Id = startId, Type = ContractWorkflowNodeType.Start, Name = "Start", Order = 1, NextNodeIds = [moveId] },
                new ContractWorkflowNode { Id = moveId, Type = ContractWorkflowNodeType.Move, Name = "Move", TargetStation = "SAMPLE_01", Order = 2, NextNodeIds = [endId] },
                new ContractWorkflowNode { Id = endId, Type = ContractWorkflowNodeType.End, Name = "End", Order = 3 }
            ]
        };
    }

    private static ContractWorkflowDefinition CreateStandardMaterialContractWorkflow()
    {
        var nodes = new List<ContractWorkflowNode>();
        var targets = new[] { "LM7", null, "LM2", null, "LM7", null, "LM1" };
        var types = new[]
        {
            ContractWorkflowNodeType.Start,
            ContractWorkflowNodeType.Move,
            ContractWorkflowNodeType.RobotProgram,
            ContractWorkflowNodeType.Move,
            ContractWorkflowNodeType.RobotProgram,
            ContractWorkflowNodeType.Move,
            ContractWorkflowNodeType.RobotProgram,
            ContractWorkflowNodeType.Move,
            ContractWorkflowNodeType.End
        };
        for (var index = 0; index < types.Length; index++)
        {
            var id = Guid.NewGuid();
            var next = index + 1 < types.Length ? Guid.Empty : Guid.Empty;
            var node = new ContractWorkflowNode
            {
                Id = id,
                Type = types[index],
                Name = index switch
                {
                    0 => "从原点开始",
                    8 => "结束",
                    _ => $"节点 {index + 1}"
                },
                TargetStation = index is 1 or 3 or 5 or 7 ? targets[index - 1] : null,
                Order = index + 1,
                NextNodeIds = []
            };
            if (types[index] == ContractWorkflowNodeType.RobotProgram)
            {
                node = node with
                {
                    Configuration = new Dictionary<string, string?>
                    {
                        [MesControlAgv.Contracts.Workflows.WorkflowNodeConfigurationKeys.DeviceId] = "ARM-01",
                        [MesControlAgv.Contracts.Workflows.WorkflowNodeConfigurationKeys.ProgramName] = index switch
                        {
                            2 => "取料盘.pro",
                            4 => "放料盘.pro",
                            _ => "回收料盘.pro"
                        }
                    }
                };
            }
            nodes.Add(node);
        }
        for (var index = 0; index < nodes.Count - 1; index++)
            nodes[index] = nodes[index] with { NextNodeIds = [nodes[index + 1].Id] };

        return new ContractWorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "料盘标准流程（LM1→LM7→LM2→LM7→LM1）",
            Description = "标准料盘现场流程",
            Nodes = nodes,
            Edges = nodes.Zip(nodes.Skip(1), (source, target) => new MesControlAgv.Contracts.Workflows.WorkflowEdgeDefinition
            {
                SourceNodeId = source.Id,
                TargetNodeId = target.Id,
                SourcePort = "success",
                TargetPort = "in"
            }).ToArray()
        };
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("The asynchronous workflow editor action did not complete.");
    }

    private sealed class WorkflowEditorClientStub(ContractWorkflowDefinition definition) : IMesClient
    {
        private ContractWorkflowVersion _version = new()
        {
            WorkflowId = definition.Id,
            Version = 1,
            Definition = definition,
            Status = ContractWorkflowVersionStatus.Draft,
            PublishStatus = ContractWorkflowPublishStatus.NotPublished
        };

        public string? LastActor { get; private set; }
        public int UpdateDraftCallCount { get; private set; }
        public ContractWorkflowExecutionRequest? LastExecutionRequest { get; private set; }
        public MesControlAgv.Contracts.AuboArmProgramCatalogResponse? ProgramCatalog { get; init; }

        public Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DashboardTask>>([]);
        public Task<KpiDashboard> GetKpiDashboardAsync(DateOnly date, CancellationToken cancellationToken) => Task.FromResult(new KpiDashboard(date, new KpiTaskSummary(0, 0, 0, 0, 0), [], new KpiSampleSummary(0, 0, 0, 0, 0, 0, "test"), [], []));
        public Task<DashboardTaskDetail?> GetTaskDetailAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult<DashboardTaskDetail?>(null);
        public Task<AgvDashboardSnapshot> GetAgvSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(new AgvDashboardSnapshot(false, "none", null, null));
        public Task<MesControlAgv.Contracts.AuboArmProgramCatalogResponse?> GetAuboArmProgramCatalogAsync(string deviceId, CancellationToken cancellationToken) =>
            Task.FromResult(ProgramCatalog);
        public Task<DashboardTask> CreateTaskAsync(CancellationToken cancellationToken) => Task.FromException<DashboardTask>(new NotSupportedException());
        public Task<DashboardTask> CreateTaskAsync(int sourceStationCode, int targetStationCode, int priority, string? description, string? externalId, CancellationToken cancellationToken) => Task.FromException<DashboardTask>(new NotSupportedException());
        public Task<DashboardTask> MarkArrivedAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromException<DashboardTask>(new NotSupportedException());
        public Task<DashboardTask> ConfirmPickupAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => Task.FromException<DashboardTask>(new NotSupportedException());
        public Task<DashboardTask> ConfirmDropoffAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => Task.FromException<DashboardTask>(new NotSupportedException());
        public Task<DashboardTask> RetryAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromException<DashboardTask>(new NotSupportedException());
        public Task<DashboardTask> RecoverAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromException<DashboardTask>(new NotSupportedException());
        public Task<DashboardTask> CancelAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => Task.FromException<DashboardTask>(new NotSupportedException());

        public Task<IReadOnlyList<ContractWorkflowDefinition>> GetWorkflowsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ContractWorkflowDefinition>>([definition]);
        public Task<IReadOnlyList<ContractWorkflowVersion>> GetWorkflowVersionsAsync(Guid workflowId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ContractWorkflowVersion>>([_version]);
        public Task<ContractWorkflowVersion?> GetWorkflowVersionAsync(Guid workflowId, int version, CancellationToken cancellationToken) => Task.FromResult<ContractWorkflowVersion?>(_version);
        public Task<ContractWorkflowVersion> CreateWorkflowDraftAsync(ContractWorkflowDefinition value, string actor, CancellationToken cancellationToken) => Update(value, actor);

        public Task<ContractWorkflowVersion> UpdateWorkflowDraftAsync(Guid workflowId, int version, ContractWorkflowDefinition value, string actor, CancellationToken cancellationToken)
        {
            UpdateDraftCallCount++;
            return Update(value, actor);
        }

        public Task<ContractWorkflowValidationResult> ValidateWorkflowAsync(ContractWorkflowDefinition value, CancellationToken cancellationToken) => Task.FromResult(ContractWorkflowValidationResult.Valid("stub"));

        public Task<ContractWorkflowValidationResult> ValidateWorkflowVersionAsync(Guid workflowId, int version, CancellationToken cancellationToken)
        {
            var result = ContractWorkflowValidationResult.Valid("stub");
            _version = _version with { Status = ContractWorkflowVersionStatus.Validated, Validation = result };
            return Task.FromResult(result);
        }

        public Task<ContractWorkflowVersion> PublishWorkflowAsync(Guid workflowId, int version, string actor, CancellationToken cancellationToken)
        {
            LastActor = actor;
            _version = _version with
            {
                Status = ContractWorkflowVersionStatus.Published,
                PublishStatus = ContractWorkflowPublishStatus.Published,
                Definition = _version.Definition with { PublishedVersion = version },
                PublishedBy = actor,
                PublishedAt = DateTimeOffset.UtcNow
            };
            return Task.FromResult(_version);
        }

        public Task<ContractWorkflowExecutionResult> ExecuteWorkflowAsync(ContractWorkflowExecutionRequest request, CancellationToken cancellationToken)
        {
            LastExecutionRequest = request;
            var executionId = Guid.NewGuid();
            return Task.FromResult(new ContractWorkflowExecutionResult
            {
                Status = ContractWorkflowExecutionStatus.Accepted,
                RequestId = request.RequestId,
                ExecutionId = executionId,
                WorkflowId = request.WorkflowId,
                Version = request.Version,
                RequestedAt = DateTimeOffset.UtcNow,
                DryRun = request.DryRun,
                NextStepRequest = new MesControlAgv.Contracts.Workflows.WorkflowNextStepRequest
                {
                    StepRequestId = Guid.NewGuid(),
                    ExecutionId = executionId,
                    WorkflowId = request.WorkflowId,
                    Version = request.Version,
                    NodeId = Guid.NewGuid(),
                    NodeType = WorkflowNodeType.Move,
                    NodeName = "Move",
                    TargetStation = "SAMPLE_01",
                    DryRun = request.DryRun,
                    Parameters = new Dictionary<string, string?>()
                }
            });
        }

        private Task<ContractWorkflowVersion> Update(ContractWorkflowDefinition value, string actor)
        {
            LastActor = actor;
            _version = _version with { Definition = value, CreatedBy = actor };
            return Task.FromResult(_version);
        }
    }

    private sealed class AlwaysConfirm : IWorkflowRunControlConfirmation
    {
        public bool Confirm(string title, string message) => true;
    }

    private sealed class TempWorkflowFile : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MesControlAgv.WorkflowRemoteTests", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "workflows.json");

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
