using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Wpf.Experiments;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class ExperimentPlanningViewBindingTests
{
    [Fact]
    public async Task Independent_plan_and_scheduling_views_bind_lifecycle_manual_actions_and_timeline()
    {
        var client = ExperimentUiClientStub.Create();
        using var plans = new ExperimentPlanManagementViewModel(client) { Reason = "Binding acceptance" };
        using var scheduling = new ExperimentSchedulingViewModel(client, new AllowConfirmation())
        {
            Reason = "Binding acceptance",
            BoardDate = DateTime.Today,
            WindowStartHour = 6,
            WindowHours = 16
        };
        await plans.RefreshAsync();
        await scheduling.RefreshAsync(client.Job.JobId);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var planView = new ExperimentPlanManagementView { DataContext = plans };
                var planWindow = Show(planView, 1380, 780);
                Assert.Same(plans.RefreshCommand, Assert.IsType<Button>(planView.FindName("PlanRefreshButton")).Command);
                Assert.Same(plans.SaveDraftCommand, Assert.IsType<Button>(planView.FindName("PlanSaveButton")).Command);
                Assert.Same(plans.ValidateCommand, Assert.IsType<Button>(planView.FindName("PlanValidateButton")).Command);
                Assert.Same(plans.PublishCommand, Assert.IsType<Button>(planView.FindName("PlanPublishButton")).Command);
                Assert.Same(plans.CreateNextDraftCommand, Assert.IsType<Button>(planView.FindName("PlanNextDraftButton")).Command);
                Assert.Same(plans.AddWorkflowStepCommand, Assert.IsType<Button>(planView.FindName("AddWorkflowStepButton")).Command);
                Assert.Same(plans.MoveWorkflowStepUpCommand, Assert.IsType<Button>(planView.FindName("MoveWorkflowStepUpButton")).Command);
                Assert.Same(plans.MoveWorkflowStepDownCommand, Assert.IsType<Button>(planView.FindName("MoveWorkflowStepDownButton")).Command);
                Assert.Single(Assert.IsType<DataGrid>(planView.FindName("WorkflowStepGrid")).Items);
                Assert.Single(Assert.IsType<DataGrid>(planView.FindName("PlanGrid")).Items);
                Assert.Single(Assert.IsType<DataGrid>(planView.FindName("PlanVersionGrid")).Items);
                planWindow.Close();

                var schedulingView = new ExperimentSchedulingView { DataContext = scheduling };
                var schedulingWindow = Show(schedulingView, 1380, 780);
                Assert.Same(scheduling.RefreshCommand, Assert.IsType<Button>(schedulingView.FindName("SchedulingRefreshButton")).Command);
                Assert.Same(scheduling.CreateJobCommand, Assert.IsType<Button>(schedulingView.FindName("CreateJobButton")).Command);
                Assert.Same(scheduling.ScheduleCommand, Assert.IsType<Button>(schedulingView.FindName("ScheduleButton")).Command);
                Assert.Same(scheduling.UnscheduleCommand, Assert.IsType<Button>(schedulingView.FindName("UnscheduleButton")).Command);
                Assert.Same(scheduling.AdmitCommand, Assert.IsType<Button>(schedulingView.FindName("AdmitButton")).Command);
                Assert.Same(scheduling.CancelJobCommand, Assert.IsType<Button>(schedulingView.FindName("CancelJobButton")).Command);
                Assert.Single(Assert.IsType<DataGrid>(schedulingView.FindName("TaskPoolGrid")).Items);
                Assert.Single(Assert.IsType<DataGrid>(schedulingView.FindName("BlockingReasonGrid")).Items);
                var timeline = Assert.IsType<ScrollViewer>(schedulingView.FindName("ResourceTimeline"));
                Assert.True(timeline.ActualWidth > 0);
                Assert.True(timeline.ActualHeight > 0);
                Assert.Single(scheduling.ResourceLanes);
                Assert.Contains(scheduling.ResourceLanes[0].Blocks, block => block.JobId == client.Job.JobId);
                schedulingWindow.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Experiment planning binding test did not complete.");
        Assert.Null(failure);
    }

    [Fact]
    public async Task Main_navigation_keeps_plan_scheduling_designer_and_runtime_as_separate_pages()
    {
        var client = ExperimentUiClientStub.Create();
        using var plans = new ExperimentPlanManagementViewModel(client) { Reason = "Navigation acceptance" };
        using var scheduling = new ExperimentSchedulingViewModel(client, new AllowConfirmation()) { Reason = "Navigation acceptance" };
        await plans.RefreshAsync();
        await scheduling.RefreshAsync(client.Job.JobId);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow
                {
                    DataContext = new ExperimentNavigationHost(plans, scheduling)
                };
                window.ApplyTemplate();
                window.Measure(new Size(1420, 860));
                window.Arrange(new Rect(0, 0, 1420, 860));
                window.UpdateLayout();
                PumpDispatcher(window.Dispatcher);

                var planTab = Assert.IsType<TabItem>(window.FindName("ExperimentPlansTab"));
                var schedulingTab = Assert.IsType<TabItem>(window.FindName("ExperimentSchedulingTab"));
                Assert.IsType<ExperimentPlanManagementView>(planTab.Content);
                Assert.IsType<ExperimentSchedulingView>(schedulingTab.Content);
                Assert.NotSame(planTab.Content, schedulingTab.Content);
                var headers = Assert.IsType<TabControl>(window.FindName("MainTabs"))
                    .Items.OfType<TabItem>()
                    .Select(item => item.Header?.ToString())
                    .ToArray();
                Assert.Contains("实验流程管理", headers);
                Assert.Contains("实验方案", headers);
                Assert.Contains("任务排程", headers);
                Assert.Contains("流程运行监控", headers);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Experiment navigation binding test did not complete.");
        Assert.Null(failure);
    }

    private static Window Show(UserControl view, double width, double height)
    {
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = view,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };
        window.Show();
        PumpDispatcher(window.Dispatcher);
        return window;
    }

    private static void PumpDispatcher(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed record ExperimentNavigationHost(
        ExperimentPlanManagementViewModel ExperimentPlans,
        ExperimentSchedulingViewModel ExperimentScheduling);

    private sealed class AllowConfirmation : IExperimentSchedulingConfirmation
    {
        public bool Confirm(string title, string message) => true;
    }

    private sealed class ExperimentUiClientStub : IMesClient
    {
        private ExperimentUiClientStub(
            WorkflowDefinition workflow,
            WorkflowVersion workflowVersion,
            ExperimentPlan plan,
            ExperimentJob job,
            ScheduleEntry schedule,
            ExperimentResourceAvailability availability,
            ExperimentSchedulingAuditEntry audit)
        {
            Workflow = workflow;
            WorkflowVersion = workflowVersion;
            Plan = plan;
            Job = job;
            Schedule = schedule;
            Availability = availability;
            Audit = audit;
        }

        public WorkflowDefinition Workflow { get; }
        public WorkflowVersion WorkflowVersion { get; }
        public ExperimentPlan Plan { get; }
        public ExperimentJob Job { get; }
        public ScheduleEntry Schedule { get; }
        public ExperimentResourceAvailability Availability { get; }
        public ExperimentSchedulingAuditEntry Audit { get; }

        public static ExperimentUiClientStub Create()
        {
            var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Acceptance workflow" };
            var workflowVersion = new WorkflowVersion
            {
                WorkflowId = workflow.Id,
                Version = 2,
                Definition = workflow,
                Status = WorkflowVersionStatus.Published,
                PublishStatus = WorkflowPublishStatus.Published
            };
            var resource = new ExperimentResourceReference
            {
                ResourceType = ExperimentResourceTypeIds.Instrument,
                ResourceId = "D160_01"
            };
            var plan = new ExperimentPlan
            {
                PlanId = Guid.NewGuid(),
                Version = 1,
                Name = "Acceptance plan",
                WorkflowId = workflow.Id,
                WorkflowVersion = 2,
                Status = ExperimentPlanStatus.Published,
                Validation = new ExperimentPlanValidationResult
                {
                    ValidatorVersion = "1.0",
                    ValidatedAt = DateTimeOffset.Now,
                    ValidatedBy = "planner"
                },
                UpdatedAt = DateTimeOffset.Now
            };
            var job = new ExperimentJob
            {
                JobId = Guid.NewGuid(),
                PlanId = plan.PlanId,
                PlanVersion = plan.Version,
                WorkflowId = workflow.Id,
                WorkflowVersion = 2,
                SampleBatchId = "B-BINDING",
                Status = ExperimentJobStatus.Blocked,
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now
            };
            var local = DateTime.SpecifyKind(DateTime.Today.AddHours(9), DateTimeKind.Unspecified);
            var start = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
            var reason = new ScheduleBlockReason
            {
                Code = ExperimentSchedulingIssueCodes.ResourceReservationConflict,
                Message = "Resource has no planning capacity.",
                Resource = resource
            };
            var schedule = new ScheduleEntry
            {
                ScheduleEntryId = Guid.NewGuid(),
                ExperimentJobId = job.JobId,
                PlannedStart = start,
                PlannedEnd = start.AddHours(1),
                Priority = 80,
                Status = ScheduleEntryStatus.Blocked,
                RequestedResources = [resource],
                BlockingReasons = [reason],
                UpdatedAt = DateTimeOffset.Now
            };
            var availability = new ExperimentResourceAvailability
            {
                Resource = resource,
                DisplayName = "D160 #1",
                Enabled = true,
                Capacity = 1,
                PlannedReservationCount = 1,
                AvailableCapacity = 0,
                BlockingReasons = [reason]
            };
            var audit = new ExperimentSchedulingAuditEntry
            {
                Id = Guid.NewGuid(),
                EventType = "ExperimentJobScheduled",
                Outcome = "Blocked",
                RequestId = Guid.NewGuid(),
                Actor = "scheduler",
                Reason = "Binding acceptance",
                ExperimentJobId = job.JobId,
                ScheduleEntryId = schedule.ScheduleEntryId,
                OccurredAt = DateTimeOffset.Now
            };
            return new ExperimentUiClientStub(workflow, workflowVersion, plan, job, schedule, availability, audit);
        }

        public Task<IReadOnlyList<WorkflowDefinition>> GetWorkflowsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WorkflowDefinition>>([Workflow]);
        public Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(Guid workflowId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WorkflowVersion>>([WorkflowVersion]);
        public Task<IReadOnlyList<ExperimentPlan>> GetExperimentPlansAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ExperimentPlan>>([Plan]);
        public Task<IReadOnlyList<ExperimentPlan>> GetExperimentPlanVersionsAsync(Guid planId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ExperimentPlan>>([Plan]);
        public Task<IReadOnlyList<ExperimentJob>> GetExperimentJobsAsync(ExperimentJobStatus? status, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ExperimentJob>>([Job]);
        public Task<ExperimentScheduleSnapshot> GetExperimentScheduleAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken) => Task.FromResult(new ExperimentScheduleSnapshot { Entries = [Schedule] });
        public Task<IReadOnlyList<ExperimentResourceAvailability>> GetExperimentResourceAvailabilityAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ExperimentResourceAvailability>>([Availability]);
        public Task<IReadOnlyList<ExperimentSchedulingAuditEntry>> GetExperimentSchedulingAuditsAsync(Guid? planId, Guid? experimentJobId, Guid? scheduleEntryId, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ExperimentSchedulingAuditEntry>>([Audit]);

        public Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DashboardTask>>([]);
        public Task<KpiDashboard> GetKpiDashboardAsync(DateOnly date, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DashboardTaskDetail?> GetTaskDetailAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult<DashboardTaskDetail?>(null);
        public Task<AgvDashboardSnapshot> GetAgvSnapshotAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DashboardTask> CreateTaskAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DashboardTask> CreateTaskAsync(int sourceStationCode, int targetStationCode, int priority, string? description, string? externalId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DashboardTask> MarkArrivedAsync(Guid taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DashboardTask> ConfirmPickupAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DashboardTask> ConfirmDropoffAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DashboardTask> RetryAsync(Guid taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DashboardTask> RecoverAsync(Guid taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DashboardTask> CancelAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
