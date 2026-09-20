using System.Windows.Input;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class WorkflowRunMonitorViewModelTests
{
    [Fact]
    public void Physical_warning_title_binds_to_actual_gate_state()
    {
        var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !System.IO.Directory.Exists(System.IO.Path.Combine(root.FullName, "src", "MesControlAgv.Wpf")))
            root = root.Parent;
        Assert.NotNull(root);
        var document = System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(root.FullName,
            "src", "MesControlAgv.Wpf", "WorkflowCanvas", "WorkflowRunMonitorView.xaml"));
        var title = Assert.Single(document.Descendants().Where(element => element.Attributes().Any(attribute =>
            attribute.Name.LocalName == "AutomationProperties.AutomationId" &&
            attribute.Value == "WorkflowPhysicalGateWarningTitle")));
        Assert.Equal("{Binding PhysicalGateStatus}", title.Attribute("Text")?.Value);
    }

    [Fact]
    public async Task Load_projects_the_pinned_graph_and_preserves_the_runtime_viewport_on_refresh()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var client = new WorkflowRunMonitorClientStub(fixture);
        var monitor = new WorkflowRunMonitorViewModel(client);

        await monitor.LoadAsync(fixture.Run.ExecutionId);

        Assert.Equal(fixture.Version.Definition.Name, monitor.RunTitle);
        Assert.Equal("运行中", monitor.RunStatusDisplay);
        Assert.Equal(WorkflowCanvasMode.Runtime, monitor.CanvasViewModel!.CanvasMode);
        Assert.False(monitor.CanvasViewModel.IsEditing);
        Assert.Equal(2, monitor.CanvasViewModel.Nodes.Single(node => node.Id == fixture.MoveNodeId).Location.X);
        Assert.Equal("Completed", monitor.CanvasViewModel.Nodes.Single(node => node.Id == fixture.StartNodeId).RuntimeState);
        Assert.Equal("Running", monitor.CanvasViewModel.Nodes.Single(node => node.Id == fixture.MoveNodeId).RuntimeState);
        Assert.Equal("NotStarted", monitor.CanvasViewModel.Nodes.Single(node => node.Id == fixture.EndNodeId).RuntimeState);
        Assert.Equal(fixture.NodeExecution.Id, monitor.SelectedNode!.Id);
        Assert.Equal(fixture.DeviceOperation.OperationId, monitor.SelectedDeviceOperation!.OperationId);
        Assert.Equal("targetStation=SAMPLE_01", monitor.SelectedNode.InputSummary);
        Assert.False(monitor.HasUnknownState);

        var canvas = monitor.CanvasViewModel;
        var publishedLayouts = fixture.Version.Definition.Layouts
            .Select(layout => (layout.NodeId, layout.X, layout.Y, layout.Width, layout.Height))
            .ToArray();
        canvas.UpdateViewport(320, 180, 0.75);
        client.Run = fixture.Run with
        {
            RuntimeStatus = WorkflowRuntimeStatus.Completed,
            CurrentNodeId = fixture.EndNodeId,
            UpdatedAt = fixture.Run.UpdatedAt.AddMinutes(1)
        };
        client.Nodes =
        [
            fixture.NodeExecution with
            {
                Status = WorkflowNodeExecutionStatus.Succeeded,
                CompletedAt = fixture.NodeExecution.UpdatedAt.AddSeconds(30),
                UpdatedAt = fixture.NodeExecution.UpdatedAt.AddSeconds(30)
            }
        ];
        client.DeviceOperations =
        [
            fixture.DeviceOperation with
            {
                Status = WorkflowDeviceOperationStatus.Succeeded,
                CompletedAt = fixture.DeviceOperation.UpdatedAt.AddSeconds(30),
                UpdatedAt = fixture.DeviceOperation.UpdatedAt.AddSeconds(30)
            }
        ];

        await monitor.LoadAsync(fixture.Run.ExecutionId);

        Assert.Same(canvas, monitor.CanvasViewModel);
        Assert.Equal(320, monitor.CanvasViewModel.Document.Viewport.X);
        Assert.Equal(180, monitor.CanvasViewModel.Document.Viewport.Y);
        Assert.Equal(0.75, monitor.CanvasViewModel.Document.Viewport.Zoom);
        Assert.Equal("Completed", monitor.CanvasViewModel.Nodes.Single(node => node.Id == fixture.MoveNodeId).RuntimeState);
        Assert.Equal("Completed", monitor.CanvasViewModel.Nodes.Single(node => node.Id == fixture.EndNodeId).RuntimeState);
        Assert.Equal(
            publishedLayouts,
            fixture.Version.Definition.Layouts.Select(layout =>
                (layout.NodeId, layout.X, layout.Y, layout.Width, layout.Height)));
        Assert.Equal(0, fixture.Version.Definition.Viewport.X);
    }

    [Fact]
    public async Task Experiment_job_and_step_selectors_load_a_run_without_manual_id_input()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var job = new ExperimentJob
        {
            JobId = Guid.NewGuid(),
            PlanId = Guid.NewGuid(),
            PlanVersion = 3,
            WorkflowId = fixture.Run.WorkflowId,
            WorkflowVersion = fixture.Run.Version,
            WorkflowRunId = fixture.Run.ExecutionId,
            SampleBatchId = "B-SELECTOR",
            Status = ExperimentJobStatus.Running,
            WorkflowSteps =
            [
                new ExperimentPlanWorkflowStep
                {
                    StepId = Guid.NewGuid(),
                    Order = 1,
                    WorkflowId = fixture.Run.WorkflowId,
                    WorkflowVersion = fixture.Run.Version,
                    Name = "读取仪器"
                }
            ]
        };
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            ExperimentJobs = [job],
            ExperimentPlans =
            [
                new ExperimentPlan
                {
                    PlanId = job.PlanId,
                    Version = job.PlanVersion,
                    Name = "离子检测方案",
                    Status = ExperimentPlanStatus.Published
                }
            ]
        };
        using var monitor = new WorkflowRunMonitorViewModel(client);

        await monitor.RefreshExperimentJobsAsync();

        var selectedJob = Assert.Single(monitor.ExperimentJobOptions);
        Assert.Equal("B-SELECTOR · 离子检测方案 / v3 · 运行中", selectedJob.Display);
        Assert.Same(selectedJob, monitor.SelectedExperimentJob);
        var selectedStep = Assert.Single(monitor.ExperimentStepOptions);
        Assert.Equal("步骤 1/1 · 读取仪器 · 运行中", selectedStep.Display);
        Assert.Same(selectedStep, monitor.SelectedExperimentStep);
        Assert.Equal(fixture.Run.ExecutionId, monitor.Run!.ExecutionId);
        Assert.Equal(fixture.Run.ExecutionId.ToString("D"), monitor.RunIdText);
    }

    [Fact]
    public async Task Composite_job_selector_explains_step_context_without_asking_for_a_guid()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var job = new ExperimentJob
        {
            JobId = Guid.NewGuid(),
            PlanId = Guid.NewGuid(),
            PlanVersion = 1,
            WorkflowId = fixture.Run.WorkflowId,
            WorkflowVersion = fixture.Run.Version,
            SampleBatchId = "B-COMPOSITE-SELECTOR",
            Status = ExperimentJobStatus.Scheduled,
            WorkflowSteps =
            [
                new ExperimentPlanWorkflowStep
                {
                    StepId = Guid.NewGuid(),
                    Order = 1,
                    WorkflowId = fixture.Run.WorkflowId,
                    WorkflowVersion = fixture.Run.Version,
                    Name = "准备"
                },
                new ExperimentPlanWorkflowStep
                {
                    StepId = Guid.NewGuid(),
                    Order = 2,
                    WorkflowId = fixture.Run.WorkflowId,
                    WorkflowVersion = fixture.Run.Version,
                    Name = "检测"
                }
            ]
        };
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            ExperimentJobs = [job]
        };
        using var monitor = new WorkflowRunMonitorViewModel(client);

        await monitor.RefreshExperimentJobsAsync();

        Assert.Null(monitor.Run);
        Assert.Equal(2, monitor.ExperimentStepOptions.Count);
        Assert.Contains("复合运行上下文", monitor.SelectedExperimentStepHint, StringComparison.Ordinal);
        Assert.Contains("无需输入运行 ID", monitor.SelectedExperimentStepHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Composite_job_selector_loads_outer_snapshot_and_step_status_without_run_id()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var job = new ExperimentJob
        {
            JobId = Guid.NewGuid(),
            PlanId = Guid.NewGuid(),
            PlanVersion = 2,
            WorkflowId = fixture.Run.WorkflowId,
            WorkflowVersion = fixture.Run.Version,
            SampleBatchId = "B-COMPOSITE-RUN",
            Status = ExperimentJobStatus.Scheduled,
            WorkflowSteps =
            [
                new ExperimentPlanWorkflowStep
                {
                    StepId = Guid.NewGuid(),
                    Order = 1,
                    WorkflowId = fixture.Run.WorkflowId,
                    WorkflowVersion = fixture.Run.Version,
                    Name = "准备"
                },
                new ExperimentPlanWorkflowStep
                {
                    StepId = Guid.NewGuid(),
                    Order = 2,
                    WorkflowId = fixture.Run.WorkflowId,
                    WorkflowVersion = fixture.Run.Version,
                    Name = "检测"
                }
            ]
        };
        var run = new ExperimentRun
        {
            ExperimentRunId = Guid.NewGuid(),
            ExperimentJobId = job.JobId,
            PlanId = job.PlanId,
            PlanVersion = job.PlanVersion,
            AdmissionRequestId = Guid.NewGuid(),
            Status = ExperimentRunStatus.Prepared,
            CurrentStepOrder = 1,
            Steps = job.WorkflowSteps.Select(step => new ExperimentStepRun
            {
                ExperimentRunId = Guid.NewGuid(),
                StepRunId = Guid.NewGuid(),
                StepId = step.StepId,
                Order = step.Order,
                WorkflowId = step.WorkflowId,
                WorkflowVersion = step.WorkflowVersion,
                Name = step.Name,
                Status = ExperimentStepRunStatus.Pending
            }).ToArray(),
            CreatedAt = DateTimeOffset.Parse("2026-09-05T01:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-09-05T01:00:00Z")
        };
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            ExperimentJobs = [job],
            CompositeRuns = new Dictionary<Guid, ExperimentRun> { [job.JobId] = run }
        };
        using var monitor = new WorkflowRunMonitorViewModel(client);

        await monitor.RefreshExperimentJobsAsync();

        Assert.Null(monitor.Run);
        Assert.Empty(monitor.RunIdText);
        Assert.Same(run, monitor.CompositeRun);
        Assert.Equal(2, monitor.CompositeSteps.Count);
        Assert.Equal("已准备", monitor.CompositeRunStatusDisplay);
        Assert.Equal("步骤 1 · 准备 / 等待中", monitor.CurrentNodeDisplay);
        Assert.Contains("已完成 0/2", monitor.CompositeRunSummary, StringComparison.Ordinal);
        Assert.All(monitor.CompositeSteps, step => Assert.Equal("等待中", step.StatusDisplay));

        monitor.SelectedExperimentStep = monitor.ExperimentStepOptions[1];

        Assert.Contains("步骤 2", monitor.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("等待子流程", monitor.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scheduled_composite_job_can_prepare_context_without_device_dispatch()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var job = new ExperimentJob
        {
            JobId = Guid.NewGuid(),
            PlanId = Guid.NewGuid(),
            PlanVersion = 2,
            WorkflowId = fixture.Run.WorkflowId,
            WorkflowVersion = fixture.Run.Version,
            SampleBatchId = "B-COMPOSITE-PREPARE",
            Status = ExperimentJobStatus.Scheduled,
            WorkflowSteps =
            [
                new ExperimentPlanWorkflowStep
                {
                    StepId = Guid.NewGuid(),
                    Order = 1,
                    WorkflowId = fixture.Run.WorkflowId,
                    WorkflowVersion = fixture.Run.Version,
                    Name = "准备"
                },
                new ExperimentPlanWorkflowStep
                {
                    StepId = Guid.NewGuid(),
                    Order = 2,
                    WorkflowId = fixture.Run.WorkflowId,
                    WorkflowVersion = fixture.Run.Version,
                    Name = "检测"
                }
            ]
        };
        var prepared = new ExperimentRun
        {
            ExperimentRunId = Guid.NewGuid(),
            ExperimentJobId = job.JobId,
            PlanId = job.PlanId,
            PlanVersion = job.PlanVersion,
            AdmissionRequestId = Guid.NewGuid(),
            Status = ExperimentRunStatus.Prepared,
            CurrentStepOrder = 1,
            Steps = job.WorkflowSteps.Select(step => new ExperimentStepRun
            {
                ExperimentRunId = Guid.NewGuid(),
                StepRunId = Guid.NewGuid(),
                StepId = step.StepId,
                Order = step.Order,
                WorkflowId = step.WorkflowId,
                WorkflowVersion = step.WorkflowVersion,
                Name = step.Name,
                Status = ExperimentStepRunStatus.Pending
            }).ToArray(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var confirmation = new WorkflowRunControlConfirmationStub();
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            ExperimentJobs = [job],
            PreparedCompositeRun = prepared
        };
        using var monitor = new WorkflowRunMonitorViewModel(client, confirmation)
        {
            ControlReason = "Prepare the approved composite context"
        };

        await monitor.RefreshExperimentJobsAsync();

        Assert.True(monitor.CanPrepareCompositeRun);
        monitor.PrepareCompositeRunCommand.Execute(null);
        await WaitUntilAsync(() => client.PrepareCompositeRequests.Count == 1 && !monitor.IsBusy);

        var request = Assert.Single(client.PrepareCompositeRequests);
        Assert.Equal(job.JobId, request.ExperimentJobId);
        Assert.Equal("local-operator", request.Actor);
        Assert.Contains("不会向设备发送命令", Assert.Single(confirmation.Messages), StringComparison.Ordinal);
        Assert.Same(prepared, monitor.CompositeRun);
        Assert.Empty(monitor.RunIdText);
        Assert.False(monitor.CanPrepareCompositeRun);
    }

    [Fact]
    public async Task Unknown_node_and_device_evidence_remain_distinct_and_never_expose_a_retry_action()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            Run = fixture.Run with { RuntimeStatus = WorkflowRuntimeStatus.Unknown },
            Nodes = [fixture.NodeExecution with { Status = WorkflowNodeExecutionStatus.Unknown, LastError = "response timeout" }],
            DeviceOperations =
            [
                fixture.DeviceOperation with
                {
                    Status = WorkflowDeviceOperationStatus.Unknown,
                    LastError = "response timeout"
                }
            ]
        };
        var monitor = new WorkflowRunMonitorViewModel(client);

        await monitor.LoadAsync(fixture.Run.ExecutionId);

        Assert.True(monitor.HasUnknownState);
        Assert.Equal("结果未知", monitor.RunStatusDisplay);
        Assert.Equal("#A30D5D", monitor.RunStatusBrush);
        Assert.Equal("结果未知", monitor.SelectedNode!.StatusDisplay);
        Assert.Equal("#A30D5D", monitor.SelectedNode.StatusBrush);
        Assert.Equal("Unknown", monitor.CanvasViewModel!.Nodes.Single(node => node.Id == fixture.MoveNodeId).RuntimeState);
        Assert.Contains("禁止自动重试", monitor.UnknownWarning, StringComparison.Ordinal);
        var commandProperties = monitor.GetType().GetProperties()
            .Where(property => typeof(ICommand).IsAssignableFrom(property.PropertyType))
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(commandProperties, name => name.Contains("Retry", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(nameof(WorkflowRunMonitorViewModel.ResolveUnknownSucceededCommand), commandProperties);
        Assert.Contains(nameof(WorkflowRunMonitorViewModel.ResolveUnknownFailedCommand), commandProperties);
        Assert.False(monitor.CanResolveUnknown);
        Assert.Contains(WorkflowRunControlPermissions.ResolveUnknown, monitor.UnknownResolutionUnavailableReason);
    }

    [Fact]
    public async Task Pause_requires_server_permission_reason_and_confirmation_and_explains_active_device_semantics()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            GrantedPermissions =
            [
                WorkflowRunControlPermissions.Pause,
                WorkflowRunControlPermissions.Cancel
            ]
        };
        var confirmation = new WorkflowRunControlConfirmationStub();
        var monitor = new WorkflowRunMonitorViewModel(client, confirmation);
        await monitor.LoadAsync(fixture.Run.ExecutionId);

        Assert.False(monitor.CanPause);
        Assert.Contains("原因", monitor.PauseUnavailableReason, StringComparison.Ordinal);
        monitor.ControlReason = "Hold before the next node";

        Assert.True(monitor.CanPause);
        Assert.False(monitor.CanCancel);
        Assert.Contains("仅静止", monitor.CancelUnavailableReason, StringComparison.Ordinal);
        monitor.PauseCommand.Execute(null);
        await WaitUntilAsync(() => client.PauseRequests.Count == 1 && !monitor.IsBusy);

        var request = Assert.Single(client.PauseRequests);
        Assert.Equal("local-operator", request.Actor);
        Assert.Equal("Hold before the next node", request.Reason);
        Assert.Contains("不会向设备发送暂停命令", Assert.Single(confirmation.Messages), StringComparison.Ordinal);
        Assert.Equal(WorkflowRuntimeStatus.Paused, monitor.Run!.RuntimeStatus);
        Assert.Empty(monitor.ControlReason);
    }

    [Fact]
    public async Task Manual_confirmation_requires_permission_and_reason_then_advances_without_direct_device_control()
    {
        var fixture = WorkflowRunMonitorFixture.CreateManualConfirmation(requireComment: true);
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            DeviceOperations = [],
            Timeline = []
        };
        var confirmation = new WorkflowRunControlConfirmationStub();
        var monitor = new WorkflowRunMonitorViewModel(client, confirmation);

        await monitor.LoadAsync(fixture.Run.ExecutionId);

        Assert.True(monitor.HasPendingManualConfirmation);
        Assert.Equal("确认厂家主程序已完成整机初始化", monitor.ManualConfirmationTitle);
        Assert.Equal("已在厂家主程序完成整机初始化", monitor.ManualConfirmationPrompt);
        Assert.False(monitor.CanCompleteManualConfirmation);
        Assert.Contains(
            WorkflowRunControlPermissions.CompleteManualTask,
            monitor.ManualConfirmationUnavailableReason,
            StringComparison.Ordinal);

        client.GrantedPermissions = [WorkflowRunControlPermissions.CompleteManualTask];
        monitor.CheckPermissionsCommand.Execute(null);
        await WaitUntilAsync(() => monitor.ManualConfirmationUnavailableReason.Contains("原因", StringComparison.Ordinal));
        Assert.Contains("原因", monitor.ManualConfirmationUnavailableReason, StringComparison.Ordinal);

        monitor.ControlReason = "现场已完成初始化";
        Assert.True(monitor.CanCompleteManualConfirmation);
        monitor.ConfirmManualTaskCommand.Execute(null);
        await WaitUntilAsync(() => client.ManualConfirmationRequests.Count == 1 && !monitor.IsBusy);

        var submitted = Assert.Single(client.ManualConfirmationRequests);
        Assert.Equal(fixture.Run.ExecutionId, submitted.WorkflowRunId);
        Assert.Equal(fixture.NodeExecution.Id, submitted.NodeExecutionId);
        Assert.NotEqual(Guid.Empty, submitted.Request.RequestId);
        Assert.Equal("local-operator", submitted.Request.Actor);
        Assert.Equal("现场已完成初始化", submitted.Request.Reason);
        Assert.Equal("现场已完成初始化", submitted.Request.Comment);
        Assert.Equal(WorkflowManualConfirmationOutcome.Confirmed, submitted.Request.Outcome);
        Assert.Contains("设备启动请求", Assert.Single(confirmation.Messages), StringComparison.Ordinal);
        Assert.False(monitor.HasPendingManualConfirmation);
        Assert.Empty(monitor.ControlReason);
    }

    [Fact]
    public async Task Manual_confirmation_cancel_stops_future_steps_without_sending_a_device_stop()
    {
        var fixture = WorkflowRunMonitorFixture.CreateManualConfirmation();
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            DeviceOperations = [],
            Timeline = [],
            GrantedPermissions = [WorkflowRunControlPermissions.CompleteManualTask]
        };
        var confirmation = new WorkflowRunControlConfirmationStub();
        var monitor = new WorkflowRunMonitorViewModel(client, confirmation);
        await monitor.LoadAsync(fixture.Run.ExecutionId);
        monitor.ControlReason = "现场条件未满足";

        monitor.CancelManualTaskCommand.Execute(null);
        await WaitUntilAsync(() => client.ManualConfirmationRequests.Count == 1 && !monitor.IsBusy);

        var submitted = Assert.Single(client.ManualConfirmationRequests);
        Assert.Equal(WorkflowManualConfirmationOutcome.Cancelled, submitted.Request.Outcome);
        Assert.Contains("不会向设备发送停止命令", Assert.Single(confirmation.Messages), StringComparison.Ordinal);
        Assert.Equal(WorkflowRuntimeStatus.Completed, monitor.Run!.RuntimeStatus);
        Assert.False(monitor.HasPendingManualConfirmation);
    }

    [Fact]
    public async Task Manual_confirmation_does_not_fall_back_when_current_node_does_not_match()
    {
        var fixture = WorkflowRunMonitorFixture.CreateManualConfirmation();
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            Run = fixture.Run with { CurrentNodeId = fixture.EndNodeId },
            DeviceOperations = [],
            Timeline = [],
            GrantedPermissions = [WorkflowRunControlPermissions.CompleteManualTask]
        };
        var monitor = new WorkflowRunMonitorViewModel(client);

        await monitor.LoadAsync(fixture.Run.ExecutionId);
        monitor.ControlReason = "不应发送";

        Assert.False(monitor.HasPendingManualConfirmation);
        Assert.False(monitor.CanCompleteManualConfirmation);
        Assert.Contains("不一致", monitor.ManualConfirmationUnavailableReason, StringComparison.Ordinal);
        Assert.False(monitor.ConfirmManualTaskCommand.CanExecute(null));
    }

    [Fact]
    public async Task Manual_confirmation_requires_a_unique_waiting_node_when_current_node_is_absent()
    {
        var fixture = WorkflowRunMonitorFixture.CreateManualConfirmation();
        var duplicate = fixture.NodeExecution with
        {
            Id = Guid.NewGuid(),
            StepRequestId = Guid.NewGuid(),
            Attempt = fixture.NodeExecution.Attempt + 1,
            UpdatedAt = fixture.NodeExecution.UpdatedAt.AddSeconds(1)
        };
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            Run = fixture.Run with { CurrentNodeId = null },
            Nodes = [fixture.NodeExecution, duplicate],
            DeviceOperations = [],
            Timeline = [],
            GrantedPermissions = [WorkflowRunControlPermissions.CompleteManualTask]
        };
        var monitor = new WorkflowRunMonitorViewModel(client);

        await monitor.LoadAsync(fixture.Run.ExecutionId);
        monitor.ControlReason = "不应发送";

        Assert.False(monitor.HasPendingManualConfirmation);
        Assert.Contains("多个", monitor.ManualConfirmationUnavailableReason, StringComparison.Ordinal);
        Assert.False(monitor.CancelManualTaskCommand.CanExecute(null));
    }

    [Fact]
    public async Task Manual_confirmation_revalidates_the_pinned_node_after_the_confirmation_dialog()
    {
        var fixture = WorkflowRunMonitorFixture.CreateManualConfirmation();
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            DeviceOperations = [],
            Timeline = [],
            GrantedPermissions = [WorkflowRunControlPermissions.CompleteManualTask]
        };
        var confirmation = new WorkflowRunControlConfirmationStub(beforeReturn: () =>
        {
            client.Nodes =
            [
                fixture.NodeExecution with
                {
                    Status = WorkflowNodeExecutionStatus.Succeeded,
                    CompletedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                }
            ];
            client.Run = fixture.Run with
            {
                RuntimeStatus = WorkflowRuntimeStatus.Prepared,
                CurrentNodeId = fixture.EndNodeId,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        });
        var monitor = new WorkflowRunMonitorViewModel(client, confirmation);
        await monitor.LoadAsync(fixture.Run.ExecutionId);
        monitor.ControlReason = "弹窗期间状态变化";

        monitor.ConfirmManualTaskCommand.Execute(null);
        await WaitUntilAsync(() => !monitor.IsBusy && monitor.StatusMessage.Contains("状态已变化", StringComparison.Ordinal));

        Assert.Empty(client.ManualConfirmationRequests);
        Assert.False(monitor.HasPendingManualConfirmation);
        Assert.Contains("未发送", monitor.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manual_confirmation_failure_keeps_the_waiting_node_and_never_retries()
    {
        var fixture = WorkflowRunMonitorFixture.CreateManualConfirmation();
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            DeviceOperations = [],
            Timeline = [],
            GrantedPermissions = [WorkflowRunControlPermissions.CompleteManualTask],
            ManualConfirmationException = new InvalidOperationException("manual confirmation rejected")
        };
        var monitor = new WorkflowRunMonitorViewModel(
            client,
            new WorkflowRunControlConfirmationStub());
        await monitor.LoadAsync(fixture.Run.ExecutionId);
        monitor.ControlReason = "保留现场输入";

        monitor.ConfirmManualTaskCommand.Execute(null);
        await WaitUntilAsync(() => client.ManualConfirmationRequests.Count == 1 && !monitor.IsBusy);

        Assert.Single(client.ManualConfirmationRequests);
        Assert.True(monitor.HasPendingManualConfirmation);
        Assert.Equal("保留现场输入", monitor.ControlReason);
        Assert.Contains("manual confirmation rejected", monitor.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_resolution_has_two_explicit_conclusions_and_confirmed_success_never_calls_retry()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            Run = fixture.Run with { RuntimeStatus = WorkflowRuntimeStatus.Unknown },
            Nodes = [fixture.NodeExecution with { Status = WorkflowNodeExecutionStatus.Unknown }],
            DeviceOperations = [fixture.DeviceOperation with { Status = WorkflowDeviceOperationStatus.Unknown }],
            GrantedPermissions = [WorkflowRunControlPermissions.ResolveUnknown]
        };
        var confirmation = new WorkflowRunControlConfirmationStub();
        var monitor = new WorkflowRunMonitorViewModel(client, confirmation);
        await monitor.LoadAsync(fixture.Run.ExecutionId);
        monitor.ControlReason = "Field log confirms arrival";

        Assert.True(monitor.CanResolveUnknown);
        monitor.ResolveUnknownSucceededCommand.Execute(null);
        await WaitUntilAsync(() => client.UnknownResolutionRequests.Count == 1 && !monitor.IsBusy);

        var request = Assert.Single(client.UnknownResolutionRequests);
        Assert.Equal(fixture.NodeExecution.Id, request.NodeExecutionId);
        Assert.Equal(WorkflowUnknownResolutionOutcome.ConfirmedSucceeded, request.Outcome);
        Assert.Contains("不会重发设备命令", Assert.Single(confirmation.Messages), StringComparison.Ordinal);
        Assert.Equal(WorkflowRuntimeStatus.Prepared, monitor.Run!.RuntimeStatus);
        Assert.Single(monitor.DeviceOperations);
    }

    [Fact]
    public async Task Load_rejects_device_evidence_from_another_run()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            DeviceOperations =
            [
                fixture.DeviceOperation with { WorkflowRunId = Guid.NewGuid() }
            ]
        };
        var monitor = new WorkflowRunMonitorViewModel(client);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            monitor.LoadAsync(fixture.Run.ExecutionId));

        Assert.Contains("device evidence", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(monitor.Run);
        Assert.Null(monitor.CanvasViewModel);
        Assert.Empty(monitor.Nodes);
        Assert.False(monitor.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Arrival_close_requires_cancel_permission_and_sends_distinct_noncontinuing_outcome(bool canCancel)
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            Run = fixture.Run with { RuntimeStatus = WorkflowRuntimeStatus.Unknown },
            Nodes = [fixture.NodeExecution with { Status = WorkflowNodeExecutionStatus.Unknown }],
            DeviceOperations = [fixture.DeviceOperation with { Status = WorkflowDeviceOperationStatus.Unknown }],
            GrantedPermissions = canCancel
                ? [WorkflowRunControlPermissions.ResolveUnknown, WorkflowRunControlPermissions.Cancel]
                : [WorkflowRunControlPermissions.ResolveUnknown]
        };
        var confirmation = new WorkflowRunControlConfirmationStub();
        var monitor = new WorkflowRunMonitorViewModel(client, confirmation);
        await monitor.LoadAsync(fixture.Run.ExecutionId);
        monitor.ControlReason = "Verified arrival; end old run without dispatch";
        Assert.Equal(canCancel, monitor.ResolveArrivedAndCancelCommand.CanExecute(null));
        if (!canCancel) return;
        monitor.ResolveArrivedAndCancelCommand.Execute(null);
        await WaitUntilAsync(() => client.UnknownResolutionRequests.Count == 1 && !monitor.IsBusy);
        Assert.Equal(WorkflowUnknownResolutionOutcome.ConfirmedArrivedAndCancel,
            Assert.Single(client.UnknownResolutionRequests).Outcome);
        Assert.Contains("不会派发后续动作", Assert.Single(confirmation.Messages), StringComparison.Ordinal);
        Assert.Equal(WorkflowRuntimeStatus.Cancelled, monitor.Run!.RuntimeStatus);
    }

    [Fact]
    public async Task Load_rejects_timeline_evidence_without_a_linked_node_or_operation()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            Timeline =
            [
                fixture.TimelineEntry with
                {
                    NodeExecutionId = Guid.NewGuid(),
                    DeviceOperationId = Guid.NewGuid()
                }
            ]
        };
        var monitor = new WorkflowRunMonitorViewModel(client);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            monitor.LoadAsync(fixture.Run.ExecutionId));

        Assert.Contains("timeline evidence", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(monitor.Run);
        Assert.Null(monitor.CanvasViewModel);
        Assert.Empty(monitor.Timeline);
        Assert.False(monitor.IsBusy);
    }

    [Fact]
    public async Task Selecting_device_and_timeline_evidence_navigates_to_the_linked_node_attempt()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var secondNode = fixture.NodeExecution with
        {
            Id = Guid.NewGuid(),
            StepRequestId = Guid.NewGuid(),
            NodeId = fixture.EndNodeId,
            NodeTypeId = WorkflowGraphNodeTypeIds.End,
            NodeName = "End",
            Status = WorkflowNodeExecutionStatus.Ready,
            CreatedAt = fixture.NodeExecution.CreatedAt.AddMinutes(1),
            UpdatedAt = fixture.NodeExecution.UpdatedAt.AddMinutes(1)
        };
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            Run = fixture.Run with { CurrentNodeId = fixture.EndNodeId },
            Nodes = [fixture.NodeExecution, secondNode],
            Timeline =
            [
                fixture.TimelineEntry,
                fixture.TimelineEntry with
                {
                    Id = Guid.NewGuid(),
                    NodeExecutionId = secondNode.Id,
                    DeviceOperationId = null,
                    EventType = "WorkflowNodePrepared",
                    OccurredAt = fixture.TimelineEntry.OccurredAt.AddMinutes(1)
                }
            ]
        };
        var monitor = new WorkflowRunMonitorViewModel(client);
        await monitor.LoadAsync(fixture.Run.ExecutionId);

        Assert.Equal(secondNode.Id, monitor.SelectedNode!.Id);

        monitor.SelectedDeviceOperation = Assert.Single(monitor.DeviceOperations);

        Assert.Equal(fixture.NodeExecution.Id, monitor.SelectedNode!.Id);
        Assert.Equal(fixture.MoveNodeId, monitor.CanvasViewModel!.SelectedNode!.Id);

        monitor.SelectedTimelineEntry = monitor.Timeline[^1];

        Assert.Equal(secondNode.Id, monitor.SelectedNode!.Id);
        Assert.Equal(fixture.EndNodeId, monitor.CanvasViewModel.SelectedNode!.Id);
    }

    [Fact]
    public async Task Ready_move_can_create_and_authorize_a_linked_field_acceptance_without_dispatching()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            Run = fixture.Run with { RuntimeStatus = WorkflowRuntimeStatus.Prepared },
            Nodes = [fixture.NodeExecution with
            {
                Status = WorkflowNodeExecutionStatus.Ready,
                StartedAt = null
            }],
            DeviceOperations = [],
            Timeline = []
        };
        var confirmation = new WorkflowRunControlConfirmationStub();
        var monitor = new WorkflowRunMonitorViewModel(client, confirmation);
        await monitor.LoadAsync(fixture.Run.ExecutionId);
        monitor.FieldAgvId = "AGV-01";
        monitor.FieldSourceStationId = "LM1";
        monitor.FieldSafetyObserverName = "safety-observer";
        monitor.FieldPermitId = "permit-ui-1";
        monitor.FieldPermitMinutes = "30";

        Assert.True(monitor.CanCreateAndAuthorizeFieldMove);
        monitor.CreateAndAuthorizeFieldMoveCommand.Execute(null);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (client.AuthorizedAcceptances.Count == 0 &&
               !monitor.StatusMessage.Contains("失败", StringComparison.Ordinal) &&
               DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.True(client.AuthorizedAcceptances.Count == 1, monitor.StatusMessage);
        await WaitUntilAsync(() => !monitor.IsBusy);

        var request = Assert.Single(client.CreatedAcceptanceRequests);
        Assert.Equal(fixture.Run.ExecutionId, request.WorkflowRunId);
        Assert.Equal(fixture.NodeExecution.Id, request.WorkflowNodeExecutionId);
        Assert.Equal("SAMPLE_01", request.TargetStationId);
        Assert.Equal("permit-ui-1", Assert.Single(client.AuthorizationRequests).PermitId);
        Assert.Contains("不会由 WPF 直接派发 AGV", Assert.Single(confirmation.Messages), StringComparison.Ordinal);
        Assert.True(monitor.HasFieldAcceptance);
        Assert.Equal(FieldNavigationAcceptanceStatuses.Authorized, monitor.SelectedFieldAcceptance!.Status);
        Assert.Contains("未直接发送 AGV 命令", monitor.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_projection_reports_progress_failure_evidence_and_cancel_semantics()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var failedNode = fixture.NodeExecution with
        {
            Status = WorkflowNodeExecutionStatus.Failed,
            LastError = "simulator rejected the command"
        };
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            Run = fixture.Run with
            {
                RuntimeStatus = WorkflowRuntimeStatus.Failed,
                LastError = "workflow stopped"
            },
            Nodes = [failedNode],
            DeviceOperations =
            [
                fixture.DeviceOperation with
                {
                    Status = WorkflowDeviceOperationStatus.Failed,
                    LastError = "adapter returned rejected"
                }
            ],
            Timeline =
            [
                fixture.TimelineEntry with
                {
                    Outcome = "Failed",
                    Reason = "operator acknowledgement required"
                }
            ]
        };
        var monitor = new WorkflowRunMonitorViewModel(client);

        await monitor.LoadAsync(fixture.Run.ExecutionId);

        Assert.Equal(1, monitor.TotalNodeCount);
        Assert.Equal(0, monitor.CompletedNodeCount);
        Assert.Equal(1, monitor.FailedNodeCount);
        Assert.Equal(1, monitor.TerminalNodeCount);
        Assert.Equal(100, monitor.ProgressPercent);
        Assert.Contains("1/1", monitor.ProgressDisplay, StringComparison.Ordinal);
        Assert.Contains("workflow stopped", monitor.FailureReasonDisplay, StringComparison.Ordinal);
        Assert.Contains("simulator rejected the command", monitor.FailureReasonDisplay, StringComparison.Ordinal);
        Assert.Contains("adapter returned rejected", monitor.FailureReasonDisplay, StringComparison.Ordinal);
        Assert.Equal("错误=adapter returned rejected", monitor.DeviceOperations.Single().ResultOrErrorSummary);
        Assert.False(monitor.IsCancelled);

        client.Run = client.Run with { RuntimeStatus = WorkflowRuntimeStatus.Cancelled };
        await monitor.LoadAsync(fixture.Run.ExecutionId);

        Assert.True(monitor.IsCancelled);
        Assert.Contains("不会自动撤销", monitor.CancellationStatusDisplay, StringComparison.Ordinal);
        Assert.Equal("流程已终态，自动刷新已暂停", monitor.AutoRefreshStatusDisplay);
    }

    [Fact]
    public async Task Auto_refresh_updates_the_run_and_stops_after_terminal_state()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var client = new WorkflowRunMonitorClientStub(fixture);
        var monitor = new WorkflowRunMonitorViewModel(client)
        {
            AutoRefreshInterval = TimeSpan.FromMilliseconds(25)
        };
        monitor.StartAutoRefresh();

        await monitor.LoadAsync(fixture.Run.ExecutionId);
        var initialReads = client.ExecutionReadCount;
        client.Run = fixture.Run with
        {
            RuntimeStatus = WorkflowRuntimeStatus.Completed,
            CurrentNodeId = fixture.EndNodeId
        };
        client.Nodes =
        [
            fixture.NodeExecution with
            {
                Status = WorkflowNodeExecutionStatus.Succeeded,
                CompletedAt = fixture.NodeExecution.UpdatedAt.AddSeconds(1),
                UpdatedAt = fixture.NodeExecution.UpdatedAt.AddSeconds(1)
            }
        ];

        await WaitUntilAsync(() => client.ExecutionReadCount > initialReads &&
                                   monitor.Run?.RuntimeStatus == WorkflowRuntimeStatus.Completed);

        Assert.Equal(100, monitor.ProgressPercent);
        Assert.False(monitor.IsAutoRefreshRunning);
        Assert.Equal("流程已终态，自动刷新已暂停", monitor.AutoRefreshStatusDisplay);
        monitor.Dispose();
    }

    [Theory]
    [InlineData("adapter", true)]
    [InlineData("none", false)]
    public async Task Cancelled_physical_run_rechecks_control_release_state(
        string owner,
        bool expectsWarning)
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var authorization = new WorkflowPhysicalRunAuthorization
        {
            AgvId = "AGV-01",
            OperatorName = "operator",
            SafetyObserverName = "observer",
            PermitPrefix = "cancelled-run",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
        };
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            Run = fixture.Run with
            {
                RuntimeStatus = WorkflowRuntimeStatus.Cancelled,
                PhysicalAuthorization = authorization
            },
            PhysicalPreflight = new PhysicalAgvPreflightResponse(
                new AgvSnapshotResponse(true, owner, "LM2", null, "AGV-01"),
                null,
                false,
                ["automatic_dispatch_disabled"])
        };
        var monitor = new WorkflowRunMonitorViewModel(client, physicalRuntime: true);

        await monitor.LoadAsync(fixture.Run.ExecutionId);

        Assert.Equal(expectsWarning, monitor.HasPhysicalGateWarning);
        Assert.Contains("\u6D41\u7A0B\u5DF2\u53D6\u6D88", monitor.PhysicalGateStatus, StringComparison.Ordinal);
        Assert.Contains(
            expectsWarning
                ? "\u91CA\u653E\u5C1A\u672A\u786E\u8BA4"
                : "\u63A7\u5236\u6743\u5DF2\u91CA\u653E",
            monitor.PhysicalGateStatus,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_physical_run_does_not_promise_automatic_continuation()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            Run = fixture.Run with { RuntimeStatus = WorkflowRuntimeStatus.Unknown, LastError = "device_epoch_mismatch" }
        };
        var monitor = new WorkflowRunMonitorViewModel(client, physicalRuntime: true);
        await monitor.LoadAsync(fixture.Run.ExecutionId);
        Assert.Contains("人工核对", monitor.PhysicalGateStatus);
        Assert.Contains("不会自动", monitor.PhysicalGateWarning);
        Assert.DoesNotContain("条件恢复后将继续", monitor.PhysicalGateWarning);
        Assert.Contains("device_epoch_mismatch", monitor.PhysicalGateWarning);
    }

    [Theory]
    [InlineData(WorkflowGraphNodeTypeIds.Move, false)]
    [InlineData(WorkflowGraphNodeTypeIds.Move, true)]
    [InlineData(WorkflowGraphNodeTypeIds.RobotExecuteProgram, false)]
    [InlineData(WorkflowGraphNodeTypeIds.RobotExecuteProgram, true)]
    public async Task Running_physical_node_observes_run_without_repeating_startup_preflight(string nodeTypeId, bool expired)
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            Run = fixture.Run with
            {
                RuntimeStatus = WorkflowRuntimeStatus.Prepared,
                PendingStepRequest = new WorkflowNextStepRequest
                {
                    StepRequestId = fixture.NodeExecution.StepRequestId,
                    ExecutionId = fixture.Run.ExecutionId,
                    WorkflowId = fixture.Run.WorkflowId,
                    Version = fixture.Run.Version,
                    NodeId = fixture.MoveNodeId,
                    NodeType = nodeTypeId == WorkflowGraphNodeTypeIds.Move ? WorkflowNodeType.Move : WorkflowNodeType.RobotProgram,
                    NodeTypeId = nodeTypeId,
                    NodeName = "offline observation",
                    Parameters = new Dictionary<string, string?> { [WorkflowNodeConfigurationKeys.DeviceId] = "ARM-01" }
                }
            },
            PhysicalReadException = new HttpRequestException("offline fake preflight unavailable")
        };
        var alerts = new WorkflowRuntimeAlertPresenterStub();
        var monitor = new WorkflowRunMonitorViewModel(client, alertPresenter: alerts, physicalRuntime: true);
        await monitor.LoadAsync(fixture.Run.ExecutionId);
        Assert.True(monitor.HasPhysicalGateWarning); // startup checks remain
        var readsBefore = client.PhysicalPreflightReadCount + client.AuboProgramReadCount;
        Assert.Equal(1, readsBefore);
        var alertsBefore = alerts.Messages.Count;
        client.Run = client.Run with
        {
            RuntimeStatus = WorkflowRuntimeStatus.Running,
            PhysicalAuthorization = new WorkflowPhysicalRunAuthorization
            {
                AgvId = "AGV-01", OperatorName = "admin", SafetyObserverName = "admin",
                PermitPrefix = "offline", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 60)
            }
        };
        await monitor.LoadAsync(fixture.Run.ExecutionId);
        await monitor.LoadAsync(fixture.Run.ExecutionId);
        Assert.Equal(readsBefore, client.PhysicalPreflightReadCount + client.AuboProgramReadCount);
        Assert.False(monitor.HasPhysicalGateWarning);
        Assert.Contains("等待", monitor.PhysicalGateStatus);
        Assert.DoesNotContain("暂停", monitor.PhysicalGateStatus);
        Assert.Equal(alertsBefore, alerts.Messages.Count);
        Assert.Empty(client.CreatedAcceptanceRequests);
        Assert.Empty(client.AuthorizationRequests);
    }

    [Fact]
    public async Task Physical_robot_node_warns_once_for_unready_arm_and_clears_after_recovery()
    {
        var baseline = WorkflowRunMonitorFixture.Create();
        var robotNode = baseline.Version.Definition.Nodes.Single(node => node.Id == baseline.MoveNodeId) with
        {
            Type = WorkflowNodeType.RobotProgram,
            NodeTypeId = WorkflowGraphNodeTypeIds.RobotExecuteProgram,
            Name = "运行 取料盘",
            TargetStation = null,
            Configuration = new Dictionary<string, string?>
            {
                [WorkflowNodeConfigurationKeys.DeviceId] = "ARM-01",
                [WorkflowNodeConfigurationKeys.ProgramName] = "取料盘.pro"
            }
        };
        var fixture = baseline with
        {
            Run = baseline.Run with
            {
                RuntimeStatus = WorkflowRuntimeStatus.Prepared,
                PendingStepRequest = new WorkflowNextStepRequest
                {
                    StepRequestId = baseline.NodeExecution.StepRequestId,
                    ExecutionId = baseline.Run.ExecutionId,
                    WorkflowId = baseline.Run.WorkflowId,
                    Version = baseline.Run.Version,
                    NodeId = baseline.MoveNodeId,
                    NodeType = WorkflowNodeType.RobotProgram,
                    NodeTypeId = WorkflowGraphNodeTypeIds.RobotExecuteProgram,
                    NodeName = "运行 取料盘",
                    Parameters = robotNode.Configuration
                }
            },
            Version = baseline.Version with
            {
                Definition = baseline.Version.Definition with
                {
                    Nodes = baseline.Version.Definition.Nodes
                        .Select(node => node.Id == baseline.MoveNodeId ? robotNode : node)
                        .ToArray()
                }
            },
            NodeExecution = baseline.NodeExecution with
            {
                NodeTypeId = WorkflowGraphNodeTypeIds.RobotExecuteProgram,
                NodeName = "运行 取料盘",
                Status = WorkflowNodeExecutionStatus.Ready,
                Inputs = robotNode.Configuration,
                StartedAt = null
            },
            DeviceOperation = baseline.DeviceOperation with
            {
                CapabilityId = WorkflowCapabilityIds.RobotExecuteProgram,
                Status = WorkflowDeviceOperationStatus.Prepared
            }
        };
        var client = new WorkflowRunMonitorClientStub(fixture)
        {
            AuboProgramStatus = new AuboArmProgramStatusResponse(
                "ARM-01",
                false,
                "回收料盘",
                AuboArmRuntimeState.Unknown,
                "Unknown",
                DateTimeOffset.UtcNow)
        };
        var alerts = new WorkflowRuntimeAlertPresenterStub();
        var monitor = new WorkflowRunMonitorViewModel(
            client,
            alertPresenter: alerts,
            physicalRuntime: true);

        await monitor.LoadAsync(fixture.Run.ExecutionId);
        await monitor.LoadAsync(fixture.Run.ExecutionId);

        Assert.True(monitor.HasPhysicalGateWarning);
        Assert.Contains("机械臂离线", monitor.PhysicalGateWarning, StringComparison.Ordinal);
        Assert.Single(alerts.Messages);

        client.AuboProgramStatus = new AuboArmProgramStatusResponse(
            "ARM-01",
            true,
            "取料盘",
            AuboArmRuntimeState.Stopped,
            "Stopped",
            DateTimeOffset.UtcNow)
        {
            RobotMode = AuboArmMode.Running,
            SafetyMode = AuboArmSafetyMode.Normal,
            OperationalMode = AuboArmOperationalMode.Automatic,
            ControlEnabled = true
        };
        await monitor.LoadAsync(fixture.Run.ExecutionId);

        Assert.False(monitor.HasPhysicalGateWarning);
        Assert.Contains("现场条件正常", monitor.PhysicalGateStatus, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition(), "The workflow run control command did not complete in time.");
    }
}

internal sealed class WorkflowRunMonitorClientStub : IMesClient
{
    private readonly WorkflowRunMonitorFixture _fixture;

    public WorkflowRunMonitorClientStub(WorkflowRunMonitorFixture fixture)
    {
        _fixture = fixture;
        Run = fixture.Run;
        Nodes = [fixture.NodeExecution];
        DeviceOperations = [fixture.DeviceOperation];
        Timeline = [fixture.TimelineEntry];
    }

    public WorkflowExecutionSnapshot? Run { get; set; }
    public IReadOnlyList<WorkflowNodeExecutionSnapshot> Nodes { get; set; }
    public IReadOnlyList<WorkflowDeviceOperationSnapshot> DeviceOperations { get; set; }
    public IReadOnlyList<WorkflowRunTimelineEntry> Timeline { get; set; }
    public IReadOnlyList<FieldNavigationAcceptanceResponse> FieldAcceptances { get; set; } = [];
    public IReadOnlyList<ExperimentJob> ExperimentJobs { get; set; } = [];
    public IReadOnlyList<ExperimentPlan> ExperimentPlans { get; set; } = [];
    public IReadOnlyDictionary<Guid, ExperimentRun> CompositeRuns { get; set; } =
        new Dictionary<Guid, ExperimentRun>();
    public ExperimentRun? PreparedCompositeRun { get; set; }
    public List<PrepareExperimentRunRequest> PrepareCompositeRequests { get; } = [];
    public AuboArmProgramStatusResponse? AuboProgramStatus { get; set; }
    public PhysicalAgvPreflightResponse? PhysicalPreflight { get; set; }
    public Exception? PhysicalReadException { get; set; }
    public int PhysicalPreflightReadCount { get; private set; }
    public int AuboProgramReadCount { get; private set; }
    public IReadOnlyList<string> GrantedPermissions { get; set; } = [];
    public List<WorkflowRunControlRequest> PauseRequests { get; } = [];
    public List<WorkflowRunControlRequest> ResumeRequests { get; } = [];
    public List<WorkflowRunControlRequest> CancelRequests { get; } = [];
    public List<WorkflowUnknownResolutionRequest> UnknownResolutionRequests { get; } = [];
    public List<(Guid WorkflowRunId, Guid NodeExecutionId, WorkflowManualConfirmationRequest Request)>
        ManualConfirmationRequests { get; } = [];
    public Exception? ManualConfirmationException { get; set; }
    public List<CreateFieldNavigationAcceptanceRequest> CreatedAcceptanceRequests { get; } = [];
    public List<AuthorizeFieldNavigationAcceptanceRequest> AuthorizationRequests { get; } = [];
    public List<FieldNavigationAcceptanceResponse> AuthorizedAcceptances { get; } = [];
    public int ExecutionReadCount { get; private set; }

    public Task<WorkflowExecutionSnapshot?> GetWorkflowExecutionAsync(Guid executionId, CancellationToken cancellationToken) =>
        ReadExecution();

    public Task<IReadOnlyList<ExperimentJob>> GetExperimentJobsAsync(
        ExperimentJobStatus? status,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExperimentJob>>(
            ExperimentJobs.Where(job => status is null || job.Status == status).ToArray());

    public Task<IReadOnlyList<ExperimentPlan>> GetExperimentPlansAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(ExperimentPlans);

    public Task<ExperimentRun?> GetExperimentRunForJobAsync(
        Guid experimentJobId,
        CancellationToken cancellationToken) =>
        Task.FromResult(CompositeRuns.GetValueOrDefault(experimentJobId));

    public Task<ExperimentRun> PrepareExperimentRunAsync(
        PrepareExperimentRunRequest request,
        CancellationToken cancellationToken)
    {
        PrepareCompositeRequests.Add(request);
        return PreparedCompositeRun is null
            ? Task.FromException<ExperimentRun>(new InvalidOperationException("No prepared composite run configured."))
            : Task.FromResult(PreparedCompositeRun);
    }

    private Task<WorkflowExecutionSnapshot?> ReadExecution()
    {
        ExecutionReadCount++;
        return Task.FromResult(Run);
    }

    public Task<WorkflowVersion?> GetWorkflowVersionAsync(Guid workflowId, int version, CancellationToken cancellationToken) =>
        Task.FromResult<WorkflowVersion?>(_fixture.Version);

    public Task<IReadOnlyList<WorkflowNodeExecutionSnapshot>> GetWorkflowNodeExecutionsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken) => Task.FromResult(Nodes);

    public Task<IReadOnlyList<WorkflowDeviceOperationSnapshot>> GetWorkflowDeviceOperationsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken) => Task.FromResult(DeviceOperations);

    public Task<IReadOnlyList<WorkflowRunTimelineEntry>> GetWorkflowRunTimelineAsync(
        Guid workflowRunId,
        int limit,
        CancellationToken cancellationToken) => Task.FromResult(Timeline);

    public Task<IReadOnlyList<FieldNavigationAcceptanceResponse>> GetWorkflowFieldNavigationAcceptancesAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken) => Task.FromResult(FieldAcceptances);

    public Task<AuboArmProgramStatusResponse?> GetAuboArmProgramAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        AuboProgramReadCount++;
        return PhysicalReadException is { } exception
            ? Task.FromException<AuboArmProgramStatusResponse?>(exception)
            : Task.FromResult(AuboProgramStatus);
    }

    public Task<PhysicalAgvPreflightResponse?> GetPhysicalPreflightAsync(
        CancellationToken cancellationToken)
    {
        PhysicalPreflightReadCount++;
        return PhysicalReadException is { } exception
            ? Task.FromException<PhysicalAgvPreflightResponse?>(exception)
            : Task.FromResult(PhysicalPreflight);
    }

    public Task<FieldNavigationAcceptanceResponse> CreateFieldNavigationAcceptanceAsync(
        CreateFieldNavigationAcceptanceRequest request,
        CancellationToken cancellationToken)
    {
        CreatedAcceptanceRequests.Add(request);
        return Task.FromResult(CreateAcceptance(request, FieldNavigationAcceptanceStatuses.Draft));
    }

    public Task<FieldNavigationAcceptanceResponse> AuthorizeFieldNavigationAcceptanceAsync(
        Guid acceptanceId,
        AuthorizeFieldNavigationAcceptanceRequest request,
        CancellationToken cancellationToken)
    {
        AuthorizationRequests.Add(request);
        var created = CreatedAcceptanceRequests[^1];
        var authorized = CreateAcceptance(created, FieldNavigationAcceptanceStatuses.Authorized, acceptanceId) with
        {
            OperatorName = request.OperatorName,
            SafetyObserverName = request.SafetyObserverName,
            PermitId = request.PermitId,
            AuthorizedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = request.ExpiresAtUtc
        };
        AuthorizedAcceptances.Add(authorized);
        FieldAcceptances = [authorized];
        return Task.FromResult(authorized);
    }

    private static FieldNavigationAcceptanceResponse CreateAcceptance(
        CreateFieldNavigationAcceptanceRequest request,
        string status,
        Guid? acceptanceId = null) => new(
            acceptanceId ?? Guid.NewGuid(),
            status,
            request.AgvId,
            request.SourceStationId,
            request.TargetStationId,
            "test-map",
            "0123456789abcdef0123456789abcdef",
            [request.SourceStationId, request.TargetStationId],
            request.Description,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow)
        {
            WorkflowRunId = request.WorkflowRunId,
            WorkflowNodeExecutionId = request.WorkflowNodeExecutionId
        };

    public Task<WorkflowRunControlPermissionsSnapshot> GetWorkflowRunControlPermissionsAsync(
        string actor,
        CancellationToken cancellationToken) => Task.FromResult(new WorkflowRunControlPermissionsSnapshot
    {
        Actor = actor,
        Permissions = GrantedPermissions
    });

    public Task<WorkflowRunControlResult> PauseWorkflowRunAsync(
        Guid workflowRunId,
        WorkflowRunControlRequest request,
        CancellationToken cancellationToken)
    {
        PauseRequests.Add(request);
        Run = Run! with { RuntimeStatus = WorkflowRuntimeStatus.Paused };
        return Task.FromResult(ControlResult(request.RequestId, WorkflowRunControlAction.Pause));
    }

    public Task<WorkflowRunControlResult> ResumeWorkflowRunAsync(
        Guid workflowRunId,
        WorkflowRunControlRequest request,
        CancellationToken cancellationToken)
    {
        ResumeRequests.Add(request);
        Run = Run! with { RuntimeStatus = WorkflowRuntimeStatus.Prepared };
        return Task.FromResult(ControlResult(request.RequestId, WorkflowRunControlAction.Resume));
    }

    public Task<WorkflowRunControlResult> CancelWorkflowRunAsync(
        Guid workflowRunId,
        WorkflowRunControlRequest request,
        CancellationToken cancellationToken)
    {
        CancelRequests.Add(request);
        Run = Run! with { RuntimeStatus = WorkflowRuntimeStatus.Cancelled };
        return Task.FromResult(ControlResult(request.RequestId, WorkflowRunControlAction.Cancel));
    }

    public Task<WorkflowRunControlResult> ResolveWorkflowRunUnknownAsync(
        Guid workflowRunId,
        WorkflowUnknownResolutionRequest request,
        CancellationToken cancellationToken)
    {
        UnknownResolutionRequests.Add(request);
        Run = Run! with { RuntimeStatus = request.Outcome == WorkflowUnknownResolutionOutcome.ConfirmedArrivedAndCancel
            ? WorkflowRuntimeStatus.Cancelled : WorkflowRuntimeStatus.Prepared };
        Nodes = Nodes.Select(node => node.Id == request.NodeExecutionId
            ? node with { Status = WorkflowNodeExecutionStatus.Succeeded }
            : node).ToArray();
        DeviceOperations = DeviceOperations.Select(operation => operation.NodeExecutionId == request.NodeExecutionId
            ? operation with { Status = WorkflowDeviceOperationStatus.Succeeded }
            : operation).ToArray();
        return Task.FromResult(ControlResult(request.RequestId, WorkflowRunControlAction.ResolveUnknown));
    }

    public Task<WorkflowRuntimeInteractionResult> CompleteWorkflowManualConfirmationAsync(
        Guid workflowRunId,
        Guid nodeExecutionId,
        WorkflowManualConfirmationRequest request,
        CancellationToken cancellationToken)
    {
        ManualConfirmationRequests.Add((workflowRunId, nodeExecutionId, request));
        if (ManualConfirmationException is not null)
            return Task.FromException<WorkflowRuntimeInteractionResult>(ManualConfirmationException);
        var confirmed = request.Outcome == WorkflowManualConfirmationOutcome.Confirmed;
        Nodes = Nodes.Select(node => node.Id == nodeExecutionId
            ? node with
            {
                Status = confirmed
                    ? WorkflowNodeExecutionStatus.Succeeded
                    : WorkflowNodeExecutionStatus.Cancelled,
                CompletedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            }
            : node).ToArray();
        Run = Run! with
        {
            RuntimeStatus = confirmed
                ? WorkflowRuntimeStatus.Prepared
                : WorkflowRuntimeStatus.Completed,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return Task.FromResult(new WorkflowRuntimeInteractionResult
        {
            RequestId = request.RequestId,
            WorkflowRunId = workflowRunId,
            NodeExecutionId = nodeExecutionId,
            InteractionType = WorkflowRuntimeInteractionType.ManualConfirmation,
            Status = WorkflowRuntimeInteractionStatus.Applied,
            Run = Run
        });
    }

    private WorkflowRunControlResult ControlResult(Guid requestId, WorkflowRunControlAction action) => new()
    {
        RequestId = requestId,
        WorkflowRunId = Run!.ExecutionId,
        Action = action,
        Run = Run
    };

    public Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DashboardTask>>([]);

    public Task<KpiDashboard> GetKpiDashboardAsync(DateOnly date, CancellationToken cancellationToken) =>
        Task.FromResult(new KpiDashboard(
            date,
            new KpiTaskSummary(0, 0, 0, 0, 0),
            [],
            new KpiSampleSummary(0, 0, 0, 0, 0, 0, "test"),
            [],
            []));

    public Task<DashboardTaskDetail?> GetTaskDetailAsync(Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult<DashboardTaskDetail?>(null);

    public Task<AgvDashboardSnapshot> GetAgvSnapshotAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AgvDashboardSnapshot(false, "none", null, null));

    public Task<DashboardTask> CreateTaskAsync(CancellationToken cancellationToken) => UnsupportedTask();

    public Task<DashboardTask> CreateTaskAsync(
        int sourceStationCode,
        int targetStationCode,
        int priority,
        string? description,
        string? externalId,
        CancellationToken cancellationToken) => UnsupportedTask();

    public Task<DashboardTask> MarkArrivedAsync(Guid taskId, CancellationToken cancellationToken) => UnsupportedTask();
    public Task<DashboardTask> ConfirmPickupAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => UnsupportedTask();
    public Task<DashboardTask> ConfirmDropoffAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => UnsupportedTask();
    public Task<DashboardTask> RetryAsync(Guid taskId, CancellationToken cancellationToken) => UnsupportedTask();
    public Task<DashboardTask> RecoverAsync(Guid taskId, CancellationToken cancellationToken) => UnsupportedTask();
    public Task<DashboardTask> CancelAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => UnsupportedTask();

    private static Task<DashboardTask> UnsupportedTask() =>
        Task.FromException<DashboardTask>(new NotSupportedException());
}

internal sealed class WorkflowRunControlConfirmationStub(
    bool result = true,
    Action? beforeReturn = null) : IWorkflowRunControlConfirmation
{
    public List<string> Messages { get; } = [];

    public bool Confirm(string title, string message)
    {
        Messages.Add(message);
        beforeReturn?.Invoke();
        return result;
    }
}

internal sealed class WorkflowRuntimeAlertPresenterStub : IWorkflowRuntimeAlertPresenter
{
    public List<(string Title, string Message)> Messages { get; } = [];

    public void ShowWarning(string title, string message) => Messages.Add((title, message));
}

internal sealed record WorkflowRunMonitorFixture(
    Guid StartNodeId,
    Guid MoveNodeId,
    Guid EndNodeId,
    WorkflowExecutionSnapshot Run,
    WorkflowVersion Version,
    WorkflowNodeExecutionSnapshot NodeExecution,
    WorkflowDeviceOperationSnapshot DeviceOperation,
    WorkflowRunTimelineEntry TimelineEntry)
{
    public static WorkflowRunMonitorFixture Create()
    {
        var workflowId = Guid.NewGuid();
        var workflowRunId = Guid.NewGuid();
        var startNodeId = Guid.NewGuid();
        var moveNodeId = Guid.NewGuid();
        var endNodeId = Guid.NewGuid();
        var nodeExecutionId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var startedAt = DateTimeOffset.Parse("2026-08-21T08:00:00Z");
        var definition = new WorkflowDefinition
        {
            Id = workflowId,
            Name = "Runtime monitor workflow",
            PublishedVersion = 3,
            Nodes =
            [
                new WorkflowNode
                {
                    Id = startNodeId,
                    Type = WorkflowNodeType.Start,
                    NodeTypeId = WorkflowGraphNodeTypeIds.Start,
                    Name = "Start",
                    Order = 1,
                    NextNodeIds = [moveNodeId]
                },
                new WorkflowNode
                {
                    Id = moveNodeId,
                    Type = WorkflowNodeType.Move,
                    NodeTypeId = WorkflowGraphNodeTypeIds.Move,
                    Name = "Move sample",
                    TargetStation = "SAMPLE_01",
                    Order = 2,
                    NextNodeIds = [endNodeId]
                },
                new WorkflowNode
                {
                    Id = endNodeId,
                    Type = WorkflowNodeType.End,
                    NodeTypeId = WorkflowGraphNodeTypeIds.End,
                    Name = "End",
                    Order = 3
                }
            ],
            Layouts =
            [
                new WorkflowNodeLayout { NodeId = startNodeId, X = 1, Y = 10, Width = 200, Height = 120 },
                new WorkflowNodeLayout { NodeId = moveNodeId, X = 2, Y = 20, Width = 200, Height = 120 },
                new WorkflowNodeLayout { NodeId = endNodeId, X = 3, Y = 30, Width = 200, Height = 120 }
            ],
            Viewport = new WorkflowCanvasViewport { X = 0, Y = 0, Zoom = 1 }
        };
        var run = new WorkflowExecutionSnapshot
        {
            ExecutionId = workflowRunId,
            RequestId = requestId,
            WorkflowId = workflowId,
            Version = 3,
            RuntimeStatus = WorkflowRuntimeStatus.Running,
            CurrentNodeId = moveNodeId,
            Attempt = 1,
            CreatedAt = startedAt,
            UpdatedAt = startedAt.AddSeconds(15)
        };
        var version = new WorkflowVersion
        {
            WorkflowId = workflowId,
            Version = 3,
            Definition = definition,
            Status = WorkflowVersionStatus.Published,
            PublishStatus = WorkflowPublishStatus.Published
        };
        var nodeExecution = new WorkflowNodeExecutionSnapshot
        {
            Id = nodeExecutionId,
            WorkflowRunId = workflowRunId,
            WorkflowId = workflowId,
            Version = 3,
            StepRequestId = Guid.NewGuid(),
            NodeId = moveNodeId,
            NodeTypeId = WorkflowGraphNodeTypeIds.Move,
            NodeName = "Move sample",
            Attempt = 1,
            Status = WorkflowNodeExecutionStatus.Running,
            Inputs = new Dictionary<string, string?> { ["targetStation"] = "SAMPLE_01" },
            StartedAt = startedAt,
            CreatedAt = startedAt,
            UpdatedAt = startedAt.AddSeconds(15)
        };
        var deviceOperation = new WorkflowDeviceOperationSnapshot
        {
            OperationId = operationId,
            WorkflowRunId = workflowRunId,
            NodeExecutionId = nodeExecutionId,
            RequestId = requestId,
            Attempt = 1,
            CapabilityId = "agv.navigate-to-station",
            IdempotencyKey = operationId.ToString("N"),
            CorrelationId = "sample-42",
            Status = WorkflowDeviceOperationStatus.Running,
            RequestSummary = new Dictionary<string, string?> { ["targetStation"] = "SAMPLE_01" },
            RequestedAt = startedAt,
            UpdatedAt = startedAt.AddSeconds(15)
        };
        var timelineEntry = new WorkflowRunTimelineEntry
        {
            Id = Guid.NewGuid(),
            WorkflowRunId = workflowRunId,
            NodeExecutionId = nodeExecutionId,
            DeviceOperationId = operationId,
            EventType = "WorkflowDeviceOperationUpdated",
            Outcome = "Running",
            Actor = "simulator-worker",
            CorrelationId = "sample-42",
            Details = new Dictionary<string, string?> { ["attempt"] = "1" },
            OccurredAt = startedAt.AddSeconds(15)
        };
        return new WorkflowRunMonitorFixture(
            startNodeId,
            moveNodeId,
            endNodeId,
            run,
            version,
            nodeExecution,
            deviceOperation,
            timelineEntry);
    }

    public static WorkflowRunMonitorFixture CreateManualConfirmation(bool requireComment = false)
    {
        var fixture = Create();
        var manualNode = fixture.Version.Definition.Nodes
            .Single(node => node.Id == fixture.MoveNodeId) with
        {
            Type = WorkflowNodeType.Custom,
            NodeTypeId = WorkflowGraphNodeTypeIds.ManualConfirmation,
            Name = "确认厂家主程序已完成整机初始化",
            Description = "操作员确认完整初始化",
            TargetStation = null,
            Configuration = new Dictionary<string, string?>
            {
                ["prompt"] = "已在厂家主程序完成整机初始化",
                ["requireComment"] = requireComment.ToString()
            }
        };
        var definition = fixture.Version.Definition with
        {
            Nodes = fixture.Version.Definition.Nodes
                .Select(node => node.Id == fixture.MoveNodeId ? manualNode : node)
                .ToArray()
        };
        return fixture with
        {
            Version = fixture.Version with { Definition = definition },
            NodeExecution = fixture.NodeExecution with
            {
                NodeTypeId = WorkflowGraphNodeTypeIds.ManualConfirmation,
                NodeName = manualNode.Name,
                Status = WorkflowNodeExecutionStatus.WaitingForSignal,
                Inputs = new Dictionary<string, string?>
                {
                    ["prompt"] = "已在厂家主程序完成整机初始化",
                    ["requireComment"] = requireComment.ToString()
                }
            }
        };
    }
}
