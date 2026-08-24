using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class ExperimentSchedulingViewModelTests
{
    [Fact]
    public void Timeline_projects_planned_blocked_and_runtime_lease_as_distinct_stable_blocks()
    {
        var start = DateTimeOffset.Parse("2026-08-24T08:00:00+08:00");
        var end = start.AddHours(12);
        var resource = ResourceRef("instrument", "D160_01");
        var readyJob = Job("B-1042", ExperimentJobStatus.Scheduled);
        var blockedJob = Job("B-1043", ExperimentJobStatus.Blocked);
        var scheduled = Schedule(readyJob.JobId, resource, start.AddHours(1), start.AddHours(3), ScheduleEntryStatus.Scheduled);
        var blocked = Schedule(blockedJob.JobId, resource, start.AddHours(2), start.AddHours(4), ScheduleEntryStatus.Blocked) with
        {
            BlockingReasons =
            [
                new ScheduleBlockReason
                {
                    Code = ExperimentSchedulingIssueCodes.ResourceReservationConflict,
                    Message = "No capacity.",
                    Resource = resource
                }
            ]
        };
        var runId = Guid.NewGuid();
        var snapshot = new ExperimentScheduleSnapshot
        {
            Entries = [scheduled, blocked],
            ActiveLeases =
            [
                new ResourceLease
                {
                    LeaseId = Guid.NewGuid(),
                    WorkflowRunId = runId,
                    Resource = resource,
                    Status = ResourceLeaseStatus.Active,
                    AcquiredAt = start.AddHours(-1),
                    ExpiresAt = start.AddMinutes(30)
                }
            ]
        };
        var availability = new ExperimentResourceAvailability
        {
            Resource = resource,
            DisplayName = "D160 #1",
            Enabled = true,
            Capacity = 1,
            PlannedReservationCount = 1,
            AvailableCapacity = 0,
            HasActiveLease = true
        };

        var ticks = ExperimentScheduleTimelineProjector.CreateTicks(start, end);
        var lane = Assert.Single(ExperimentScheduleTimelineProjector.CreateLanes(
            start,
            end,
            [availability],
            snapshot,
            new Dictionary<Guid, ExperimentJob>
            {
                [readyJob.JobId] = readyJob,
                [blockedJob.JobId] = blockedJob
            }));

        Assert.Equal(13, ticks.Count);
        Assert.Equal(ExperimentScheduleTimelineProjector.TimelineWidth, ticks[^1].Left, 3);
        Assert.Equal(ExperimentResourceLaneViewModel.LaneHeight, lane.Height);
        Assert.True(lane.HasActiveLease);
        Assert.Equal(3, lane.Blocks.Count);
        var plannedBlock = Assert.Single(lane.Blocks.Where(block => block.JobId == readyJob.JobId));
        Assert.Equal(80, plannedBlock.Left, 3);
        Assert.Equal(160, plannedBlock.Width, 3);
        var blockedBlock = Assert.Single(lane.Blocks.Where(block => block.JobId == blockedJob.JobId));
        Assert.Equal("#FEE4E2", blockedBlock.Fill);
        var leaseBlock = Assert.Single(lane.Blocks.Where(block => block.IsRuntimeLease));
        Assert.Equal(runId, leaseBlock.WorkflowRunId);
        Assert.Equal(0, leaseBlock.Left, 3);
        Assert.Equal(ExperimentScheduleTimelineProjector.TimelineWidth, leaseBlock.Width, 3);
    }

    [Fact]
    public async Task Refresh_builds_pending_pool_resource_load_blocking_reasons_and_audits()
    {
        var fixture = SchedulingFixture.Create();
        var ready = fixture.Client.AddJob("B-READY", ExperimentJobStatus.Ready);
        var blocked = fixture.Client.AddJob("B-BLOCKED", ExperimentJobStatus.Blocked);
        fixture.Client.SetSchedule(Schedule(
            blocked.JobId,
            fixture.Resource,
            fixture.WindowStart.AddHours(2),
            fixture.WindowStart.AddHours(3),
            ScheduleEntryStatus.Blocked) with
        {
            Priority = 90,
            BlockingReasons =
            [
                new ScheduleBlockReason
                {
                    Code = ExperimentSchedulingIssueCodes.ResourceReservationConflict,
                    Message = "Resource has no planning capacity.",
                    Resource = fixture.Resource
                }
            ]
        });
        fixture.Client.Audits.Add(new ExperimentSchedulingAuditEntry
        {
            Id = Guid.NewGuid(),
            EventType = "ExperimentJobScheduled",
            Outcome = "Blocked",
            RequestId = Guid.NewGuid(),
            Actor = "scheduler",
            Reason = "Conflict evidence",
            ExperimentJobId = blocked.JobId,
            OccurredAt = DateTimeOffset.Now
        });
        using var viewModel = fixture.CreateViewModel();
        viewModel.Reason = "Refresh board";

        await viewModel.RefreshAsync();

        Assert.Equal(2, viewModel.TaskPool.Count);
        Assert.Equal(blocked.JobId, viewModel.SelectedJob!.JobId);
        Assert.Equal(ExperimentSchedulingIssueCodes.ResourceReservationConflict, Assert.Single(viewModel.BlockingReasons).Code);
        Assert.Single(viewModel.SelectedJobAudits);
        Assert.Single(viewModel.ResourceLanes);
        Assert.Equal("计划 1/1 / 可用 0", viewModel.ResourceLanes[0].Load);
        Assert.True(viewModel.CanSchedule);
        Assert.True(viewModel.CanUnschedule);
        Assert.False(viewModel.CanAdmit);
        Assert.Contains(viewModel.TaskPool, item => item.JobId == ready.JobId);
    }

    [Fact]
    public async Task Create_schedule_reschedule_unschedule_and_cancel_use_only_manual_G5_commands()
    {
        var fixture = SchedulingFixture.Create();
        using var viewModel = fixture.CreateViewModel();
        viewModel.Reason = "Manual scheduling acceptance";
        await viewModel.RefreshAsync();

        viewModel.SampleBatchId = "B-NEW";
        viewModel.SampleId = "S-01";
        viewModel.AddJobParameterCommand.Execute(null);
        var parameter = Assert.Single(viewModel.JobParameters);
        parameter.Name = "dilution";
        parameter.Value = "10";
        Assert.True(viewModel.CanCreateJob);
        await viewModel.CreateJobAsync();

        Assert.NotNull(fixture.Client.LastCreateJobRequest);
        var created = fixture.Client.LastCreateJobRequest!;
        Assert.Equal("B-NEW", created.SampleBatchId);
        Assert.Equal("10", created.Parameters["dilution"]);
        Assert.NotNull(viewModel.SelectedJob);
        var selected = viewModel.SelectedJob!;
        Assert.Equal("B-NEW", selected.SampleBatchId);
        viewModel.Resources[0].IsSelected = true;
        viewModel.PlacementDate = DateTime.Today;
        viewModel.PlacementStartHour = 9;
        viewModel.PlacementStartMinute = 15;
        viewModel.DurationMinutes = 75;
        viewModel.Priority = 80;
        await viewModel.ScheduleAsync();

        Assert.Equal(1, fixture.Client.ScheduleCalls);
        Assert.Equal(fixture.Resource.ResourceId, Assert.Single(fixture.Client.LastScheduleRequest!.Resources).ResourceId);
        Assert.Equal(75, (fixture.Client.LastScheduleRequest.PlannedEnd - fixture.Client.LastScheduleRequest.PlannedStart).TotalMinutes);
        Assert.True(viewModel.CanAdmit);

        viewModel.PlacementStartHour = 11;
        await viewModel.ScheduleAsync();
        Assert.Equal(2, fixture.Client.ScheduleCalls);
        Assert.Equal(11, fixture.Client.LastScheduleRequest!.PlannedStart.ToLocalTime().Hour);

        await viewModel.UnscheduleAsync();
        Assert.Equal(1, fixture.Client.UnscheduleCalls);
        Assert.Equal(ExperimentJobStatus.Ready, viewModel.SelectedJob!.Job.Status);
        await viewModel.CancelJobAsync();
        Assert.Equal(1, fixture.Client.CancelCalls);
        Assert.Equal(ExperimentJobStatus.Cancelled, viewModel.SelectedJob!.Job.Status);
        Assert.Equal(0, fixture.Client.WorkflowExecuteCalls);
        Assert.Equal(0, fixture.Client.DeviceCommandCalls);
    }

    [Fact]
    public async Task Explicit_admission_requires_confirmation_and_does_not_advance_workflow_or_call_device()
    {
        var fixture = SchedulingFixture.Create();
        var scheduledJob = fixture.Client.AddJob("B-ADMIT", ExperimentJobStatus.Scheduled);
        fixture.Client.SetSchedule(Schedule(
            scheduledJob.JobId,
            fixture.Resource,
            fixture.WindowStart.AddHours(1),
            fixture.WindowStart.AddHours(2),
            ScheduleEntryStatus.Scheduled));
        using var viewModel = fixture.CreateViewModel();
        viewModel.Reason = "Explicit runtime admission";
        await viewModel.RefreshAsync(scheduledJob.JobId);

        Assert.True(viewModel.CanAdmit);
        await viewModel.AdmitAsync();

        Assert.Equal(1, fixture.Confirmation.Count);
        Assert.Equal(1, fixture.Client.AdmitCalls);
        Assert.Equal(0, fixture.Client.WorkflowExecuteCalls);
        Assert.Equal(0, fixture.Client.DeviceCommandCalls);
        Assert.Contains(fixture.Client.AdmittedRunId.ToString("N"), viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(viewModel.HasError);
        Assert.Equal(ExperimentJobStatus.Admitted, viewModel.SelectedJob!.Job.Status);
    }

    [Fact]
    public async Task Admission_rejection_keeps_stable_blocking_code_visible()
    {
        var fixture = SchedulingFixture.Create();
        var scheduledJob = fixture.Client.AddJob("B-REJECT", ExperimentJobStatus.Scheduled);
        fixture.Client.SetSchedule(Schedule(
            scheduledJob.JobId,
            fixture.Resource,
            fixture.WindowStart.AddHours(1),
            fixture.WindowStart.AddHours(2),
            ScheduleEntryStatus.Scheduled));
        fixture.Client.RejectAdmission = true;
        using var viewModel = fixture.CreateViewModel();
        viewModel.Reason = "Verify conflict evidence";
        await viewModel.RefreshAsync(scheduledJob.JobId);

        await viewModel.AdmitAsync();

        Assert.True(viewModel.HasError);
        Assert.Contains(ExperimentSchedulingIssueCodes.ResourceLeaseActive, viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(ExperimentJobStatus.Scheduled, viewModel.SelectedJob!.Job.Status);
    }

    private static ExperimentResourceReference ResourceRef(string type, string id) => new()
    {
        ResourceType = type,
        ResourceId = id
    };

    private static ExperimentJob Job(string batch, ExperimentJobStatus status) => new()
    {
        JobId = Guid.NewGuid(),
        PlanId = Guid.NewGuid(),
        PlanVersion = 1,
        WorkflowId = Guid.NewGuid(),
        WorkflowVersion = 1,
        SampleBatchId = batch,
        Status = status,
        CreatedAt = DateTimeOffset.Now,
        UpdatedAt = DateTimeOffset.Now
    };

    private static ScheduleEntry Schedule(
        Guid jobId,
        ExperimentResourceReference resource,
        DateTimeOffset start,
        DateTimeOffset end,
        ScheduleEntryStatus status) => new()
        {
            ScheduleEntryId = Guid.NewGuid(),
            ExperimentJobId = jobId,
            PlannedStart = start,
            PlannedEnd = end,
            Priority = 50,
            Status = status,
            RequestedResources = [resource],
            UpdatedAt = DateTimeOffset.Now
        };

    private sealed record SchedulingFixture(
        SchedulingClientStub Client,
        ConfirmationStub Confirmation,
        ExperimentResourceReference Resource,
        DateTimeOffset WindowStart)
    {
        public static SchedulingFixture Create()
        {
            var start = LocalTime(DateTime.Today, 6);
            var resource = ResourceRef(ExperimentResourceTypeIds.Instrument, "D160_01");
            return new SchedulingFixture(
                new SchedulingClientStub(resource),
                new ConfirmationStub(),
                resource,
                start);
        }

        public ExperimentSchedulingViewModel CreateViewModel() => new(Client, Confirmation)
        {
            BoardDate = DateTime.Today,
            WindowStartHour = 6,
            WindowHours = 16
        };

        private static DateTimeOffset LocalTime(DateTime date, int hour)
        {
            var local = DateTime.SpecifyKind(date.Date.AddHours(hour), DateTimeKind.Unspecified);
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        }
    }

    private sealed class ConfirmationStub : IExperimentSchedulingConfirmation
    {
        public int Count { get; private set; }
        public bool Confirm(string title, string message)
        {
            Count++;
            return true;
        }
    }

    private sealed class SchedulingClientStub : IMesClient
    {
        private readonly List<ExperimentJob> _jobs = [];
        private readonly List<ScheduleEntry> _schedules = [];
        private readonly ExperimentPlan _plan;
        private readonly ExperimentResourceAvailability _availability;
        public SchedulingClientStub(ExperimentResourceReference resource)
        {
            _plan = new ExperimentPlan
            {
                PlanId = Guid.NewGuid(),
                Version = 1,
                Name = "Published acceptance plan",
                WorkflowId = Guid.NewGuid(),
                WorkflowVersion = 2,
                Status = ExperimentPlanStatus.Published,
                UpdatedAt = DateTimeOffset.Now
            };
            _availability = new ExperimentResourceAvailability
            {
                Resource = resource,
                DisplayName = "D160 #1",
                Enabled = true,
                Capacity = 1,
                PlannedReservationCount = 1,
                AvailableCapacity = 0
            };
        }

        public List<ExperimentSchedulingAuditEntry> Audits { get; } = [];
        public CreateExperimentJobRequest? LastCreateJobRequest { get; private set; }
        public ScheduleExperimentJobRequest? LastScheduleRequest { get; private set; }
        public int ScheduleCalls { get; private set; }
        public int UnscheduleCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public int AdmitCalls { get; private set; }
        public int WorkflowExecuteCalls { get; private set; }
        public int DeviceCommandCalls { get; private set; }
        public bool RejectAdmission { get; set; }
        public Guid AdmittedRunId { get; } = Guid.NewGuid();

        public ExperimentJob AddJob(string batch, ExperimentJobStatus status)
        {
            var job = new ExperimentJob
            {
                JobId = Guid.NewGuid(),
                PlanId = _plan.PlanId,
                PlanVersion = _plan.Version,
                WorkflowId = _plan.WorkflowId,
                WorkflowVersion = _plan.WorkflowVersion,
                SampleBatchId = batch,
                Status = status,
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now
            };
            UpsertJob(job);
            return job;
        }

        public void SetSchedule(ScheduleEntry schedule)
        {
            _schedules.RemoveAll(item => item.ExperimentJobId == schedule.ExperimentJobId);
            _schedules.Add(schedule);
        }

        public Task<IReadOnlyList<ExperimentPlan>> GetExperimentPlansAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExperimentPlan>>([_plan]);

        public Task<IReadOnlyList<ExperimentPlan>> GetExperimentPlanVersionsAsync(Guid planId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExperimentPlan>>([_plan]);

        public Task<IReadOnlyList<ExperimentJob>> GetExperimentJobsAsync(ExperimentJobStatus? status, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExperimentJob>>(_jobs.Where(job => status is null || job.Status == status).ToArray());

        public Task<ExperimentScheduleSnapshot> GetExperimentScheduleAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken) =>
            Task.FromResult(new ExperimentScheduleSnapshot
            {
                GeneratedAt = DateTimeOffset.Now,
                Entries = _schedules.ToArray()
            });

        public Task<IReadOnlyList<ExperimentResourceAvailability>> GetExperimentResourceAvailabilityAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExperimentResourceAvailability>>([_availability]);

        public Task<IReadOnlyList<ExperimentSchedulingAuditEntry>> GetExperimentSchedulingAuditsAsync(Guid? planId, Guid? experimentJobId, Guid? scheduleEntryId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExperimentSchedulingAuditEntry>>(Audits.Take(limit).ToArray());

        public Task<ExperimentJob> CreateExperimentJobAsync(CreateExperimentJobRequest request, CancellationToken cancellationToken)
        {
            LastCreateJobRequest = request;
            var job = new ExperimentJob
            {
                JobId = Guid.NewGuid(),
                PlanId = request.PlanId,
                PlanVersion = request.PlanVersion,
                WorkflowId = _plan.WorkflowId,
                WorkflowVersion = _plan.WorkflowVersion,
                SampleBatchId = request.SampleBatchId,
                SampleId = request.SampleId,
                Parameters = request.Parameters,
                Status = ExperimentJobStatus.Ready,
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now
            };
            UpsertJob(job);
            return Task.FromResult(job);
        }

        public Task<ScheduleEntry> ScheduleExperimentJobAsync(Guid jobId, ScheduleExperimentJobRequest request, CancellationToken cancellationToken)
        {
            ScheduleCalls++;
            LastScheduleRequest = request;
            var schedule = new ScheduleEntry
            {
                ScheduleEntryId = _schedules.FirstOrDefault(item => item.ExperimentJobId == jobId)?.ScheduleEntryId ?? Guid.NewGuid(),
                ExperimentJobId = jobId,
                PlannedStart = request.PlannedStart,
                PlannedEnd = request.PlannedEnd,
                Priority = request.Priority,
                Status = ScheduleEntryStatus.Scheduled,
                RequestedResources = request.Resources,
                UpdatedAt = DateTimeOffset.Now
            };
            SetSchedule(schedule);
            UpsertJob(FindJob(jobId) with { Status = ExperimentJobStatus.Scheduled, UpdatedAt = DateTimeOffset.Now });
            return Task.FromResult(schedule);
        }

        public Task<ExperimentJob> UnscheduleExperimentJobAsync(Guid jobId, ExperimentSchedulingActionRequest request, CancellationToken cancellationToken)
        {
            UnscheduleCalls++;
            var job = FindJob(jobId) with { Status = ExperimentJobStatus.Ready, UpdatedAt = DateTimeOffset.Now };
            UpsertJob(job);
            var schedule = _schedules.Single(item => item.ExperimentJobId == jobId);
            SetSchedule(schedule with { Status = ScheduleEntryStatus.Draft, RequestedResources = [], UpdatedAt = DateTimeOffset.Now });
            return Task.FromResult(job);
        }

        public Task<ExperimentJob> CancelExperimentJobAsync(Guid jobId, ExperimentSchedulingActionRequest request, CancellationToken cancellationToken)
        {
            CancelCalls++;
            var job = FindJob(jobId) with { Status = ExperimentJobStatus.Cancelled, UpdatedAt = DateTimeOffset.Now };
            UpsertJob(job);
            return Task.FromResult(job);
        }

        public Task<ExperimentJobAdmissionResult> AdmitExperimentJobAsync(Guid jobId, AdmitExperimentJobRequest request, CancellationToken cancellationToken)
        {
            AdmitCalls++;
            if (RejectAdmission)
            {
                return Task.FromResult(new ExperimentJobAdmissionResult
                {
                    RequestId = request.RequestId,
                    Status = ExperimentJobAdmissionStatus.Rejected,
                    RejectionCode = ExperimentSchedulingIssueCodes.ResourceLeaseActive,
                    RejectionReason = "Resource already has an active lease."
                });
            }
            var job = FindJob(jobId) with { Status = ExperimentJobStatus.Admitted, WorkflowRunId = AdmittedRunId, UpdatedAt = DateTimeOffset.Now };
            UpsertJob(job);
            var schedule = _schedules.Single(item => item.ExperimentJobId == jobId);
            SetSchedule(schedule with { Status = ScheduleEntryStatus.Admitted, UpdatedAt = DateTimeOffset.Now });
            return Task.FromResult(new ExperimentJobAdmissionResult
            {
                RequestId = request.RequestId,
                Status = ExperimentJobAdmissionStatus.Admitted,
                WorkflowRunId = AdmittedRunId,
                Job = job
            });
        }

        public Task<WorkflowExecutionResult> ExecuteWorkflowAsync(WorkflowExecutionRequest request, CancellationToken cancellationToken)
        {
            WorkflowExecuteCalls++;
            throw new InvalidOperationException("Scheduling UI must not execute workflow nodes.");
        }

        public Task<AgvCommandResult?> ExecuteAgvCommandAsync(string agvId, string command, Guid? taskId, CancellationToken cancellationToken)
        {
            DeviceCommandCalls++;
            throw new InvalidOperationException("Scheduling UI must not issue device commands.");
        }

        private ExperimentJob FindJob(Guid jobId) => _jobs.Single(job => job.JobId == jobId);
        private void UpsertJob(ExperimentJob job)
        {
            _jobs.RemoveAll(item => item.JobId == job.JobId);
            _jobs.Add(job);
        }

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
