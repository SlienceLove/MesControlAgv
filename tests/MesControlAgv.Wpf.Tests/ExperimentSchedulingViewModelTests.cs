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
        Assert.Equal(8 + 3 * 26, lane.Height);
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
    public void Timeline_splits_composed_plan_into_ordered_workflow_step_segments()
    {
        var start = DateTimeOffset.Parse("2026-08-24T08:00:00+08:00");
        var end = start.AddHours(2);
        var resource = ResourceRef("instrument", "D160_01");
        var jobId = Guid.NewGuid();
        var job = Job("B-COMPOSED", ExperimentJobStatus.Scheduled) with
        {
            JobId = jobId,
            WorkflowSteps =
            [
                new ExperimentPlanWorkflowStep
                {
                    StepId = Guid.NewGuid(),
                    Order = 1,
                    WorkflowId = Guid.NewGuid(),
                    WorkflowVersion = 2,
                    Name = "搬运到检测位",
                    EstimatedDurationMinutes = 30
                },
                new ExperimentPlanWorkflowStep
                {
                    StepId = Guid.NewGuid(),
                    Order = 2,
                    WorkflowId = Guid.NewGuid(),
                    WorkflowVersion = 4,
                    Name = "执行检测模板",
                    EstimatedDurationMinutes = 90
                }
            ]
        };
        var snapshot = new ExperimentScheduleSnapshot
        {
            Entries =
            [
                new ScheduleEntry
                {
                    ScheduleEntryId = Guid.NewGuid(),
                    ExperimentJobId = jobId,
                    PlannedStart = start,
                    PlannedEnd = end,
                    Priority = 50,
                    Status = ScheduleEntryStatus.Scheduled,
                    RequestedResources = [resource]
                }
            ]
        };
        var availability = new ExperimentResourceAvailability
        {
            Resource = resource,
            DisplayName = "D160 #1",
            Enabled = true,
            Capacity = 1,
            AvailableCapacity = 1
        };

        var lane = Assert.Single(ExperimentScheduleTimelineProjector.CreateLanes(
            start,
            end,
            [availability],
            snapshot,
            new Dictionary<Guid, ExperimentJob> { [jobId] = job }));
        var blocks = lane.Blocks.Where(block => !block.IsRuntimeLease).OrderBy(block => block.WorkflowStepOrder).ToArray();

        Assert.Equal(2, blocks.Length);
        Assert.Equal("搬运到检测位", blocks[0].WorkflowStepName);
        Assert.Equal("执行检测模板", blocks[1].WorkflowStepName);
        Assert.Equal(1, blocks[0].WorkflowStepOrder);
        Assert.Equal(2, blocks[1].WorkflowStepOrder);
        Assert.Equal(2, blocks[0].WorkflowStepCount);
        Assert.Equal(ExperimentScheduleTimelineProjector.TimelineWidth / 4, blocks[0].Width, 3);
        Assert.Equal(ExperimentScheduleTimelineProjector.TimelineWidth * 3 / 4, blocks[1].Width, 3);
        Assert.Contains("步骤 1/2", blocks[0].Detail, StringComparison.Ordinal);
        Assert.Contains("步骤 2/2", blocks[1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Timeline_projects_actual_device_activity_with_runtime_status()
    {
        var start = DateTimeOffset.Parse("2026-08-24T08:00:00+08:00");
        var end = start.AddHours(2);
        var resource = ResourceRef("instrument", "D160_01");
        var jobId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var snapshot = new ExperimentScheduleSnapshot
        {
            GeneratedAt = start.AddMinutes(30),
            Activities =
            [
                new ExperimentScheduleActivity
                {
                    ActivityId = activityId,
                    ExperimentJobId = jobId,
                    ScheduleEntryId = Guid.NewGuid(),
                    WorkflowRunId = runId,
                    WorkflowId = Guid.NewGuid(),
                    WorkflowVersion = 3,
                    Resource = resource,
                    ActivityName = "读取 D160 状态",
                    Status = "Running",
                    PlannedStart = start,
                    PlannedEnd = end,
                    ActualStart = start.AddMinutes(15)
                }
            ]
        };
        var availability = new ExperimentResourceAvailability
        {
            Resource = resource,
            DisplayName = "D160 #1",
            Enabled = true,
            Capacity = 1,
            AvailableCapacity = 1
        };

        var lane = Assert.Single(ExperimentScheduleTimelineProjector.CreateLanes(
            start,
            end,
            [availability],
            snapshot,
            new Dictionary<Guid, ExperimentJob>()));
        var activity = Assert.Single(lane.Blocks.Where(block => block.IsRuntimeActivity));

        Assert.Equal(activityId, activity.ActivityId);
        Assert.Equal(runId, activity.WorkflowRunId);
        Assert.Equal("读取 D160 状态", activity.Label);
        Assert.Equal("#E0F2FE", activity.Fill);
        Assert.Contains("实际 Running", activity.Detail, StringComparison.Ordinal);
        Assert.Equal(120, activity.Width, 3);
    }

    [Fact]
    public void Timeline_marks_plan_lease_and_actual_current_layers_without_merging_them()
    {
        var start = DateTimeOffset.Parse("2026-08-24T08:00:00+08:00");
        var end = start.AddHours(4);
        var observed = start.AddHours(2);
        var resource = ResourceRef("instrument", "D160_01");
        var job = Job("B-CURRENT", ExperimentJobStatus.Running);
        var entry = Schedule(
            job.JobId,
            resource,
            start,
            end,
            ScheduleEntryStatus.Admitted);
        var activityId = Guid.NewGuid();
        var snapshot = new ExperimentScheduleSnapshot
        {
            GeneratedAt = observed,
            Entries = [entry],
            ActiveLeases =
            [
                new ResourceLease
                {
                    LeaseId = Guid.NewGuid(),
                    WorkflowRunId = Guid.NewGuid(),
                    Resource = resource,
                    Status = ResourceLeaseStatus.Active,
                    AcquiredAt = start.AddMinutes(30),
                    ExpiresAt = end
                }
            ],
            Activities =
            [
                new ExperimentScheduleActivity
                {
                    ActivityId = activityId,
                    ExperimentJobId = job.JobId,
                    ScheduleEntryId = entry.ScheduleEntryId,
                    WorkflowRunId = Guid.NewGuid(),
                    Resource = resource,
                    ActivityName = "读取仪器状态",
                    Status = "Running",
                    PlannedStart = start,
                    PlannedEnd = end,
                    ActualStart = observed.AddMinutes(-15)
                }
            ]
        };
        var availability = new ExperimentResourceAvailability
        {
            Resource = resource,
            DisplayName = "D160 #1",
            Enabled = true,
            Capacity = 1,
            AvailableCapacity = 0,
            HasActiveLease = true
        };

        var lane = Assert.Single(ExperimentScheduleTimelineProjector.CreateLanes(
            start,
            end,
            [availability],
            snapshot,
            new Dictionary<Guid, ExperimentJob> { [job.JobId] = job }));

        var plan = Assert.Single(lane.Blocks.Where(block => !block.IsRuntimeLease && !block.IsRuntimeActivity));
        var lease = Assert.Single(lane.Blocks.Where(block => block.IsRuntimeLease));
        var activity = Assert.Single(lane.Blocks.Where(block => block.IsRuntimeActivity));
        Assert.True(plan.IsCurrent);
        Assert.Equal("当前计划", plan.LayerDisplay);
        Assert.Equal(2, plan.BorderThickness);
        Assert.True(lease.IsCurrent);
        Assert.Equal("当前租约", lease.LayerDisplay);
        Assert.True(activity.IsCurrent);
        Assert.Equal("当前实际", activity.LayerDisplay);
        Assert.Equal(2, activity.BorderThickness);
    }

    [Fact]
    public void Timeline_routes_activity_without_resource_evidence_to_unassigned_lane()
    {
        var start = DateTimeOffset.Parse("2026-08-24T08:00:00+08:00");
        var end = start.AddHours(2);
        var activity = new ExperimentScheduleActivity
        {
            ActivityId = Guid.NewGuid(),
            ExperimentJobId = Guid.NewGuid(),
            Resource = new ExperimentResourceReference(),
            ActivityName = "待确认设备活动",
            Status = "Unknown",
            PlannedStart = start,
            PlannedEnd = end,
            ActualStart = start.AddMinutes(5)
        };

        var lanes = ExperimentScheduleTimelineProjector.CreateLanes(
            start,
            end,
            [],
            new ExperimentScheduleSnapshot { Activities = [activity] },
            new Dictionary<Guid, ExperimentJob>());

        var lane = Assert.Single(lanes);
        Assert.Equal("unassigned", lane.ResourceType);
        Assert.Equal("UNASSIGNED", lane.ResourceId);
        var block = Assert.Single(lane.Blocks);
        Assert.True(block.IsRuntimeActivity);
        Assert.Equal("#FCE7F3", block.Fill);
    }

    [Fact]
    public async Task Refresh_exposes_selected_job_activity_status_and_observed_window()
    {
        var fixture = SchedulingFixture.Create();
        var job = fixture.Client.AddJob("B-ACTIVITY", ExperimentJobStatus.Running);
        var start = fixture.WindowStart.AddHours(1);
        fixture.Client.SetSchedule(Schedule(
            job.JobId,
            fixture.Resource,
            start,
            start.AddHours(1),
            ScheduleEntryStatus.Admitted));
        fixture.Client.Activities.Add(new ExperimentScheduleActivity
        {
            ActivityId = Guid.NewGuid(),
            ExperimentJobId = job.JobId,
            ScheduleEntryId = Guid.NewGuid(),
            WorkflowRunId = Guid.NewGuid(),
            Resource = fixture.Resource,
            ActivityName = "读取仪器状态",
            Status = "Running",
            PlannedStart = start,
            PlannedEnd = start.AddHours(1),
            ActualStart = start.AddMinutes(5)
        });

        using var viewModel = fixture.CreateViewModel();
        viewModel.Reason = "Inspect observed activity";
        await viewModel.RefreshAsync(job.JobId);

        Assert.Equal("运行中 / Running", viewModel.SelectedActivityStatus);
        Assert.Equal("instrument/D160_01", viewModel.SelectedActivityResource);
        Assert.Contains("读取仪器状态", viewModel.SelectedActivity!.ActivityName, StringComparison.Ordinal);
        Assert.Contains("运行中", viewModel.SelectedActivityStatus, StringComparison.Ordinal);
        Assert.NotEqual("-", viewModel.SelectedActivityWindow);
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

    [Fact]
    public async Task Sample_verification_loads_per_selected_job_gates_workstation_admission_and_never_confirms_twice()
    {
        var fixture = SchedulingFixture.Create();
        var workstationJob = fixture.Client.AddJob("B-WORK", ExperimentJobStatus.Scheduled, workstation: true);
        var ordinaryJob = fixture.Client.AddJob("B-ORDINARY", ExperimentJobStatus.Scheduled);
        fixture.Client.SetSchedule(Schedule(workstationJob.JobId, fixture.Resource, fixture.WindowStart, fixture.WindowStart.AddHours(1), ScheduleEntryStatus.Scheduled));
        fixture.Client.SetSchedule(Schedule(ordinaryJob.JobId, fixture.Resource, fixture.WindowStart.AddHours(2), fixture.WindowStart.AddHours(3), ScheduleEntryStatus.Scheduled));
        fixture.Client.SetVerification(workstationJob.JobId, fixture.Client.CreateVerification(workstationJob, ExperimentSampleVerificationStatus.ReadyForVerification));
        using var viewModel = fixture.CreateViewModel();
        viewModel.Reason = "Visual verification";

        await viewModel.RefreshAsync(workstationJob.JobId);

        Assert.True(viewModel.SelectedJobRequiresSampleVerification);
        Assert.Equal("待核对", viewModel.VerificationStatus);
        Assert.Single(viewModel.SampleVerificationRows);
        Assert.Contains(ExperimentSampleVerificationIssueCodes.PositionDuplicate, viewModel.SampleVerificationRows[0].Validation, StringComparison.Ordinal);
        Assert.False(viewModel.CanAdmit);
        Assert.True(viewModel.CanCompleteSampleVerification);

        await viewModel.CompleteSampleVerificationAsync();

        Assert.Equal("已核对", viewModel.VerificationStatus);
        Assert.True(viewModel.CanAdmit);
        Assert.Equal(0, fixture.Confirmation.Count);

        viewModel.VerificationSampleNumber = "S-CHANGED";
        viewModel.VerificationSampleBarcode = "BC-CHANGED";
        viewModel.VerificationSamplePosition = "B01";
        viewModel.VerificationSampleOrder = 2;
        await viewModel.SaveSampleRowAsync();

        Assert.Contains("核对已失效", viewModel.VerificationStatus, StringComparison.Ordinal);
        Assert.False(viewModel.CanAdmit);

        await viewModel.RefreshAsync(ordinaryJob.JobId);
        Assert.False(viewModel.SelectedJobRequiresSampleVerification);
        Assert.Empty(viewModel.SampleVerificationRows);
        Assert.True(viewModel.CanAdmit);
    }

    [Theory]
    [InlineData("business")]
    [InlineData("barcode")]
    [InlineData("position")]
    [InlineData("order")]
    public async Task Unsaved_identity_edits_immediately_block_admission_and_verification_and_reverting_restores_state(string field)
    {
        var fixture = SchedulingFixture.Create();
        var job = fixture.Client.AddJob("B-UNSAVED", ExperimentJobStatus.Scheduled, workstation: true);
        fixture.Client.SetSchedule(Schedule(job.JobId, fixture.Resource, fixture.WindowStart, fixture.WindowStart.AddHours(1), ScheduleEntryStatus.Scheduled));
        var snapshot = fixture.Client.CreateVerification(job, ExperimentSampleVerificationStatus.Verified);
        fixture.Client.SetVerification(job.JobId, snapshot);
        using var viewModel = fixture.CreateViewModel();
        viewModel.Reason = "check unsaved changes";
        await viewModel.RefreshAsync(job.JobId);
        viewModel.SelectedSampleVerificationRow = Assert.Single(viewModel.SampleVerificationRows);
        var original = viewModel.SelectedSampleVerificationRow;
        Assert.True(viewModel.CanAdmit);
        void Edit(bool changed)
        {
            switch (field)
            {
                case "business": viewModel.VerificationSampleNumber = original.SampleNumber + (changed ? "-edit" : ""); break;
                case "barcode": viewModel.VerificationSampleBarcode = original.Barcode + (changed ? "-edit" : ""); break;
                case "position": viewModel.VerificationSamplePosition = original.Position + (changed ? "-edit" : ""); break;
                default: viewModel.VerificationSampleOrder = original.Order + (changed ? 1 : 0); break;
            }
        }
        Edit(true);
        Assert.True(viewModel.HasUnsavedVerificationIdentityChanges);
        Assert.False(viewModel.CanAdmit);
        Assert.False(viewModel.AdmitCommand.CanExecute(null));
        Assert.False(viewModel.CompleteSampleVerificationCommand.CanExecute(null));
        Assert.Contains("未保存修改", viewModel.VerificationStatus, StringComparison.Ordinal);
        Assert.Equal(ExperimentSampleVerificationStatus.Verified, viewModel.CurrentSampleVerification!.Status);
        Assert.Null(fixture.Client.LastSavedVerificationRequest);
        Edit(false);
        Assert.False(viewModel.HasUnsavedVerificationIdentityChanges);
        Assert.True(viewModel.CanAdmit);
        Assert.Equal("已核对", viewModel.VerificationStatus);
        fixture.Client.SetVerification(job.JobId, snapshot with { Status = ExperimentSampleVerificationStatus.ReadyForVerification });
        await viewModel.RefreshAsync(job.JobId);
        viewModel.SelectedSampleVerificationRow = Assert.Single(viewModel.SampleVerificationRows);
        Assert.True(viewModel.CanCompleteSampleVerification);
        Edit(true);
        Assert.False(viewModel.CanCompleteSampleVerification);
        Edit(false);
        Assert.True(viewModel.CanCompleteSampleVerification);
        viewModel.VerificationSampleDisplayName = "display only";
        Assert.False(viewModel.HasUnsavedVerificationIdentityChanges);
        Assert.True(viewModel.CanCompleteSampleVerification);
        fixture.Client.SetVerification(job.JobId, snapshot);
        await viewModel.RefreshAsync(job.JobId);
        viewModel.SelectedSampleVerificationRow = Assert.Single(viewModel.SampleVerificationRows);
        viewModel.VerificationSampleDisplayName = "another display name";
        Assert.True(viewModel.CanAdmit);
    }

    [Theory]
    [InlineData(ExperimentJobStatus.Admitted, ScheduleEntryStatus.Admitted)]
    [InlineData(ExperimentJobStatus.Completed, ScheduleEntryStatus.Completed)]
    [InlineData(ExperimentJobStatus.Running, ScheduleEntryStatus.Admitted)]
    [InlineData(ExperimentJobStatus.Scheduled, ScheduleEntryStatus.Admitted)]
    public async Task Protected_tasks_disable_sample_identity_editor_and_commands(ExperimentJobStatus jobStatus, ScheduleEntryStatus scheduleStatus)
    {
        var fixture = SchedulingFixture.Create();
        var job = fixture.Client.AddJob("B-PROTECTED", jobStatus, workstation: true);
        fixture.Client.SetSchedule(Schedule(job.JobId, fixture.Resource, fixture.WindowStart, fixture.WindowStart.AddHours(1), scheduleStatus));
        fixture.Client.SetVerification(job.JobId, fixture.Client.CreateVerification(job, ExperimentSampleVerificationStatus.ReadyForVerification));
        using var viewModel = fixture.CreateViewModel();
        viewModel.Reason = "late correction";
        await viewModel.RefreshAsync(job.JobId);
        viewModel.SelectedSampleVerificationRow = Assert.Single(viewModel.SampleVerificationRows);
        Assert.False(viewModel.CanEditSampleIdentity);
        Assert.False(viewModel.SaveSampleRowCommand.CanExecute(null));
        Assert.False(viewModel.CompleteSampleVerificationCommand.CanExecute(null));
        await viewModel.SaveSampleRowAsync();
        Assert.Null(fixture.Client.LastSavedVerificationRequest);
        Assert.Null(fixture.Client.LastSampleSaveRequest);
    }

    [Fact]
    public async Task Pending_or_failed_workstation_detection_conservatively_blocks_admission()
    {
        var fixture = SchedulingFixture.Create();
        var job = fixture.Client.AddJob("B-PENDING", ExperimentJobStatus.Scheduled, workstation: true);
        fixture.Client.SetSchedule(Schedule(job.JobId, fixture.Resource, fixture.WindowStart, fixture.WindowStart.AddHours(1), ScheduleEntryStatus.Scheduled));
        fixture.Client.SetVerification(job.JobId, fixture.Client.CreateVerification(job, ExperimentSampleVerificationStatus.Verified));
        fixture.Client.HoldWorkflowVersionRequest();
        using var viewModel = fixture.CreateViewModel();
        viewModel.Reason = "Check pending gate";

        var refresh = viewModel.RefreshAsync(job.JobId);
        await fixture.Client.WorkflowVersionRequested!.Task;

        Assert.False(viewModel.IsSampleVerificationRequirementResolved);
        Assert.False(viewModel.CanAdmit);
        Assert.Contains("正在确认", viewModel.AdmissionVerificationMessage, StringComparison.Ordinal);

        fixture.Client.ReleaseWorkflowVersionRequest(job.WorkflowId, job.WorkflowVersion);
        await refresh;
        Assert.True(viewModel.IsSampleVerificationRequirementResolved);
        Assert.True(viewModel.CanAdmit);
    }

    [Fact]
    public async Task Missing_workflow_version_keeps_sample_requirement_unresolved_and_blocks_admission()
    {
        var fixture = SchedulingFixture.Create();
        var job = fixture.Client.AddJob("B-MISSING-WORKFLOW", ExperimentJobStatus.Scheduled);
        fixture.Client.SetSchedule(Schedule(job.JobId, fixture.Resource, fixture.WindowStart, fixture.WindowStart.AddHours(1), ScheduleEntryStatus.Scheduled));
        fixture.Client.ReturnNullWorkflowVersion = true;
        using var viewModel = fixture.CreateViewModel();
        viewModel.Reason = "Conservative workflow gate";

        await viewModel.RefreshAsync(job.JobId);

        Assert.False(viewModel.IsSampleVerificationRequirementResolved);
        Assert.False(viewModel.CanAdmit);
        Assert.True(viewModel.HasError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Workflow_version_errors_keep_sample_requirement_unresolved_and_block_admission(bool notSupported)
    {
        var fixture = SchedulingFixture.Create();
        var job = fixture.Client.AddJob("B-WORKFLOW-ERROR", ExperimentJobStatus.Scheduled);
        fixture.Client.SetSchedule(Schedule(job.JobId, fixture.Resource, fixture.WindowStart, fixture.WindowStart.AddHours(1), ScheduleEntryStatus.Scheduled));
        fixture.Client.WorkflowVersionException = notSupported
            ? new NotSupportedException("workflow endpoint unavailable")
            : new InvalidOperationException("workflow lookup failed");
        using var viewModel = fixture.CreateViewModel();
        viewModel.Reason = "Conservative workflow gate";

        await viewModel.RefreshAsync(job.JobId);

        Assert.False(viewModel.IsSampleVerificationRequirementResolved);
        Assert.False(viewModel.CanAdmit);
        Assert.Contains(fixture.Client.WorkflowVersionException.Message, viewModel.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ordinary_workflow_preserves_admission_without_loading_irrelevant_sample_projection()
    {
        var fixture = SchedulingFixture.Create();
        var job = fixture.Client.AddJob("B-ORDINARY-SHORT-CIRCUIT", ExperimentJobStatus.Scheduled);
        fixture.Client.SetSchedule(Schedule(job.JobId, fixture.Resource, fixture.WindowStart, fixture.WindowStart.AddHours(1), ScheduleEntryStatus.Scheduled));
        fixture.Client.ThrowOnVerificationProjectionRead = true;
        using var viewModel = fixture.CreateViewModel();
        viewModel.Reason = "Ordinary admission";

        await viewModel.RefreshAsync(job.JobId);

        Assert.True(viewModel.IsSampleVerificationRequirementResolved);
        Assert.False(viewModel.SelectedJobRequiresSampleVerification);
        Assert.True(viewModel.CanAdmit);
        Assert.Equal(0, fixture.Client.CurrentVerificationReadCalls);
        Assert.Equal(0, fixture.Client.SampleReadCalls);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task Completion_response_for_an_old_selection_never_overwrites_the_new_selection()
    {
        var fixture = SchedulingFixture.Create();
        var workstationJob = fixture.Client.AddJob("B-ASYNC", ExperimentJobStatus.Scheduled, workstation: true);
        var ordinaryJob = fixture.Client.AddJob("B-OTHER", ExperimentJobStatus.Ready);
        fixture.Client.SetSchedule(Schedule(workstationJob.JobId, fixture.Resource, fixture.WindowStart, fixture.WindowStart.AddHours(1), ScheduleEntryStatus.Scheduled));
        fixture.Client.SetVerification(workstationJob.JobId, fixture.Client.CreateVerification(workstationJob, ExperimentSampleVerificationStatus.ReadyForVerification));
        using var viewModel = fixture.CreateViewModel();
        viewModel.Reason = "Check stale response";
        await viewModel.RefreshAsync(workstationJob.JobId);
        fixture.Client.HoldCompletionRequest();

        var completion = viewModel.CompleteSampleVerificationAsync();
        await fixture.Client.CompletionRequested!.Task;
        viewModel.SelectedJob = viewModel.TaskPool.Single(item => item.JobId == ordinaryJob.JobId);
        fixture.Client.ReleaseCompletionRequest(workstationJob.JobId);
        await completion;

        Assert.Equal(ordinaryJob.JobId, viewModel.SelectedJob!.JobId);
        Assert.Null(viewModel.CurrentSampleVerification);
        Assert.Empty(viewModel.SampleVerificationRows);
    }

    [Fact]
    public async Task Saving_a_row_snapshots_old_task_inputs_before_selection_changes()
    {
        var fixture = SchedulingFixture.Create();
        var workstationJob = fixture.Client.AddJob("B-SAVE", ExperimentJobStatus.Scheduled, workstation: true);
        var ordinaryJob = fixture.Client.AddJob("B-SAVE-OTHER", ExperimentJobStatus.Ready);
        fixture.Client.SetSchedule(Schedule(workstationJob.JobId, fixture.Resource, fixture.WindowStart, fixture.WindowStart.AddHours(1), ScheduleEntryStatus.Scheduled));
        fixture.Client.SetVerification(workstationJob.JobId, fixture.Client.CreateVerification(workstationJob, ExperimentSampleVerificationStatus.ReadyForVerification));
        using var viewModel = fixture.CreateViewModel();
        viewModel.Reason = "Check save snapshot";
        await viewModel.RefreshAsync(workstationJob.JobId);
        var original = Assert.Single(viewModel.SampleVerificationRows);
        viewModel.SelectedSampleVerificationRow = original;
        viewModel.VerificationSampleNumber = "S-SAVED";
        viewModel.VerificationSampleBarcode = "BC-SAVED";
        viewModel.VerificationSamplePosition = "B01";
        viewModel.VerificationSampleOrder = 2;
        viewModel.OperatorName = "frozen operator";
        viewModel.Reason = "frozen reason";
        fixture.Client.HoldSampleSaveRequest();

        var saving = viewModel.SaveSampleRowAsync();
        await fixture.Client.SampleSaveRequested!.Task;
        viewModel.OperatorName = "changed operator";
        viewModel.Reason = "changed reason";
        viewModel.SelectedJob = viewModel.TaskPool.Single(item => item.JobId == ordinaryJob.JobId);
        fixture.Client.ReleaseSampleSaveRequest();
        await saving;

        Assert.Equal(workstationJob.JobId, fixture.Client.LastSavedVerificationJobId);
        var saved = Assert.Single(fixture.Client.LastSavedVerificationRows!);
        Assert.Equal("BC-SAVED", saved.SampleBarcode);
        Assert.Equal("B01", saved.Position);
        Assert.Equal(2, saved.Order);
        Assert.Equal("frozen operator", fixture.Client.LastSampleSaveRequest!.Actor);
        Assert.Equal("frozen reason", fixture.Client.LastSampleSaveRequest.Reason);
        Assert.Equal("frozen operator", fixture.Client.LastSavedVerificationRequest!.Actor);
        Assert.Equal("frozen reason", fixture.Client.LastSavedVerificationRequest.Reason);
        Assert.Equal(ordinaryJob.JobId, viewModel.SelectedJob!.JobId);
        Assert.Null(viewModel.CurrentSampleVerification);
    }

    [Fact]
    public async Task Multi_row_sample_numbers_survive_save_and_completion()
    {
        var fixture = SchedulingFixture.Create();
        var job = fixture.Client.AddJob("B-MULTI", ExperimentJobStatus.Scheduled, workstation: true);
        fixture.Client.SetSchedule(Schedule(job.JobId, fixture.Resource, fixture.WindowStart, fixture.WindowStart.AddHours(1), ScheduleEntryStatus.Scheduled));
        fixture.Client.SetVerification(job.JobId, fixture.Client.CreateTwoRowVerification(job, ExperimentSampleVerificationStatus.ReadyForVerification));
        using var viewModel = fixture.CreateViewModel();
        viewModel.Reason = "multi rows";
        await viewModel.RefreshAsync(job.JobId);
        Assert.Equal(["S-B-MULTI-1", "S-B-MULTI-2"], viewModel.SampleVerificationRows.Select(row => row.SampleNumber).ToArray());

        viewModel.SelectedSampleVerificationRow = viewModel.SampleVerificationRows[0];
        await viewModel.SaveSampleRowAsync();

        Assert.NotNull(fixture.Client.LastSampleSaveRequest);
        Assert.Equal(string.Empty, fixture.Client.LastSavedVerificationRows![1].BusinessSampleId);
        Assert.Equal(["S-B-MULTI-1", "S-B-MULTI-2"], viewModel.SampleVerificationRows.Select(row => row.SampleNumber).ToArray());
        await viewModel.CompleteSampleVerificationAsync();
        Assert.Equal(["S-B-MULTI-1", "S-B-MULTI-2"], viewModel.SampleVerificationRows.Select(row => row.SampleNumber).ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_snapshot_save_and_failed_authoritative_reload_clears_cached_verified_state(bool failCurrentReload)
    {
        var fixture = SchedulingFixture.Create();
        var job = fixture.Client.AddJob("B-RELOAD-FAIL", ExperimentJobStatus.Scheduled, workstation: true);
        fixture.Client.SetSchedule(Schedule(job.JobId, fixture.Resource, fixture.WindowStart, fixture.WindowStart.AddHours(1), ScheduleEntryStatus.Scheduled));
        fixture.Client.SetVerification(job.JobId, fixture.Client.CreateVerification(job, ExperimentSampleVerificationStatus.Verified));
        using var viewModel = fixture.CreateViewModel();
        viewModel.Reason = "Force authoritative reload";
        await viewModel.RefreshAsync(job.JobId);
        viewModel.SelectedSampleVerificationRow = Assert.Single(viewModel.SampleVerificationRows);
        viewModel.VerificationSampleBarcode = "BC-CHANGED-BEFORE-FAILURE";
        fixture.Client.SnapshotSaveException = new InvalidOperationException("snapshot save failed");
        fixture.Client.FailCurrentVerificationReloadAfterSave = failCurrentReload;
        fixture.Client.FailSampleReloadAfterSave = !failCurrentReload;

        await viewModel.SaveSampleRowAsync();

        Assert.NotNull(fixture.Client.LastSampleSaveRequest);
        Assert.NotNull(fixture.Client.LastSavedVerificationRequest);
        Assert.Null(viewModel.CurrentSampleVerification);
        Assert.False(viewModel.IsSampleVerificationRequirementResolved);
        Assert.False(viewModel.CanAdmit);
        Assert.Contains("snapshot save failed", viewModel.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Old_job_failure_cannot_pollute_new_job_operation_or_release_its_busy_state()
    {
        var fixture = SchedulingFixture.Create();
        var oldJob = fixture.Client.AddJob("B-OLD-FAIL", ExperimentJobStatus.Scheduled, workstation: true);
        var newJob = fixture.Client.AddJob("B-NEW-BUSY", ExperimentJobStatus.Ready, workstation: true);
        fixture.Client.SetSchedule(Schedule(oldJob.JobId, fixture.Resource, fixture.WindowStart, fixture.WindowStart.AddHours(1), ScheduleEntryStatus.Scheduled));
        fixture.Client.SetVerification(oldJob.JobId, fixture.Client.CreateVerification(oldJob, ExperimentSampleVerificationStatus.ReadyForVerification));
        fixture.Client.SetVerification(newJob.JobId, fixture.Client.CreateVerification(newJob, ExperimentSampleVerificationStatus.ReadyForVerification));
        using var viewModel = fixture.CreateViewModel();
        viewModel.Reason = "Operation ownership";
        await viewModel.RefreshAsync(oldJob.JobId);
        viewModel.SelectedSampleVerificationRow = Assert.Single(viewModel.SampleVerificationRows);
        fixture.Client.HoldSampleSaveRequest();

        var oldSave = viewModel.SaveSampleRowAsync();
        await fixture.Client.SampleSaveRequested!.Task;
        viewModel.SelectedJob = viewModel.TaskPool.Single(item => item.JobId == newJob.JobId);
        Assert.False(viewModel.IsBusy);
        Assert.Equal(string.Empty, viewModel.StatusMessage);
        fixture.Client.HoldCompletionRequest();

        var newCompletion = viewModel.CompleteSampleVerificationAsync();
        await fixture.Client.CompletionRequested!.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(viewModel.IsBusy);
        var newJobStatus = viewModel.StatusMessage;
        fixture.Client.FailSampleSaveRequest(new InvalidOperationException("old job save failed"));
        await oldSave;

        Assert.True(viewModel.IsBusy);
        Assert.Equal(string.Empty, viewModel.ErrorMessage);
        Assert.Equal(newJobStatus, viewModel.StatusMessage);

        fixture.Client.ReleaseCompletionRequest(newJob.JobId);
        await newCompletion;
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Old_job_success_leaves_no_status_text_after_selection_changes()
    {
        var fixture = SchedulingFixture.Create();
        var oldJob = fixture.Client.AddJob("B-OLD-SUCCESS", ExperimentJobStatus.Scheduled, workstation: true);
        var newJob = fixture.Client.AddJob("B-NEW-IDLE", ExperimentJobStatus.Ready);
        fixture.Client.SetSchedule(Schedule(oldJob.JobId, fixture.Resource, fixture.WindowStart, fixture.WindowStart.AddHours(1), ScheduleEntryStatus.Scheduled));
        fixture.Client.SetVerification(oldJob.JobId, fixture.Client.CreateVerification(oldJob, ExperimentSampleVerificationStatus.ReadyForVerification));
        using var viewModel = fixture.CreateViewModel();
        viewModel.Reason = "Operation ownership";
        await viewModel.RefreshAsync(oldJob.JobId);
        fixture.Client.HoldCompletionRequest();

        var oldCompletion = viewModel.CompleteSampleVerificationAsync();
        await fixture.Client.CompletionRequested!.Task;
        viewModel.SelectedJob = viewModel.TaskPool.Single(item => item.JobId == newJob.JobId);
        fixture.Client.ReleaseCompletionRequest(oldJob.JobId);
        await oldCompletion;

        Assert.Equal(newJob.JobId, viewModel.SelectedJob!.JobId);
        Assert.Equal(string.Empty, viewModel.StatusMessage);
        Assert.Equal(string.Empty, viewModel.ErrorMessage);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Old_scheduling_success_cannot_reselect_over_or_end_busy_for_the_new_job()
    {
        var fixture = SchedulingFixture.Create();
        var oldJob = fixture.Client.AddJob("B-OLD-SCHEDULE", ExperimentJobStatus.Ready);
        var newJob = fixture.Client.AddJob("B-NEW-VERIFY", ExperimentJobStatus.Ready, workstation: true);
        fixture.Client.SetVerification(newJob.JobId, fixture.Client.CreateVerification(newJob, ExperimentSampleVerificationStatus.ReadyForVerification));
        using var viewModel = fixture.CreateViewModel();
        viewModel.Reason = "Cross-command ownership";
        await viewModel.RefreshAsync(oldJob.JobId);
        fixture.Client.HoldScheduleRequest();

        var oldSchedule = viewModel.ScheduleAsync();
        await fixture.Client.ScheduleRequested!.Task;
        viewModel.SelectedJob = viewModel.TaskPool.Single(item => item.JobId == newJob.JobId);
        fixture.Client.HoldCompletionRequest();
        var newCompletion = viewModel.CompleteSampleVerificationAsync();
        await fixture.Client.CompletionRequested!.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var newJobStatus = viewModel.StatusMessage;

        fixture.Client.ReleaseScheduleRequest();
        await oldSchedule;

        Assert.Equal(newJob.JobId, viewModel.SelectedJob!.JobId);
        Assert.True(viewModel.IsBusy);
        Assert.Equal(newJobStatus, viewModel.StatusMessage);
        Assert.Equal(string.Empty, viewModel.ErrorMessage);

        fixture.Client.ReleaseCompletionRequest(newJob.JobId);
        await newCompletion;
        Assert.False(viewModel.IsBusy);
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
        private readonly Dictionary<Guid, ExperimentSampleVerification> _verifications = [];
        private readonly Dictionary<Guid, ExperimentSample> _samples = [];
        private readonly HashSet<Guid> _workstationWorkflowIds = [];
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
        public List<ExperimentScheduleActivity> Activities { get; } = [];
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
        public TaskCompletionSource<WorkflowVersion?>? PendingWorkflowVersion { get; private set; }
        public TaskCompletionSource<bool>? WorkflowVersionRequested { get; private set; }
        public TaskCompletionSource<ExperimentSampleVerification>? PendingCompletion { get; private set; }
        public TaskCompletionSource<bool>? CompletionRequested { get; private set; }
        public TaskCompletionSource<ExperimentSample>? PendingSampleSave { get; private set; }
        public TaskCompletionSource<bool>? SampleSaveRequested { get; private set; }
        public TaskCompletionSource<ScheduleEntry>? PendingSchedule { get; private set; }
        public TaskCompletionSource<bool>? ScheduleRequested { get; private set; }
        public ScheduleEntry? PendingScheduleResult { get; private set; }
        public ExperimentSample? PendingSample { get; private set; }
        public Guid? LastSavedVerificationJobId { get; private set; }
        public IReadOnlyList<ExperimentSampleTaskRow>? LastSavedVerificationRows { get; private set; }
        public SaveExperimentSampleRequest? LastSampleSaveRequest { get; private set; }
        public SaveExperimentSampleVerificationRequest? LastSavedVerificationRequest { get; private set; }
        public bool ReturnNullWorkflowVersion { get; set; }
        public Exception? WorkflowVersionException { get; set; }
        public bool ThrowOnVerificationProjectionRead { get; set; }
        public int CurrentVerificationReadCalls { get; private set; }
        public int SampleReadCalls { get; private set; }
        public Exception? SnapshotSaveException { get; set; }
        public bool FailCurrentVerificationReloadAfterSave { get; set; }
        public bool FailSampleReloadAfterSave { get; set; }

        public void HoldWorkflowVersionRequest()
        {
            PendingWorkflowVersion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            WorkflowVersionRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseWorkflowVersionRequest(Guid workflowId, int version) =>
            PendingWorkflowVersion!.SetResult(CreateWorkflowVersion(workflowId, version));

        public void HoldCompletionRequest()
        {
            PendingCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CompletionRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void HoldSampleSaveRequest()
        {
            PendingSampleSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
            SampleSaveRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void HoldScheduleRequest()
        {
            PendingSchedule = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ScheduleRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseScheduleRequest() =>
            PendingSchedule!.SetResult(PendingScheduleResult!);

        public void ReleaseSampleSaveRequest()
        {
            _samples[PendingSample!.SampleId] = PendingSample;
            PendingSampleSave!.SetResult(PendingSample);
        }

        public void FailSampleSaveRequest(Exception exception) =>
            PendingSampleSave!.SetException(exception);

        public void ReleaseCompletionRequest(Guid jobId)
        {
            var current = _verifications[jobId];
            var completed = current with { Status = ExperimentSampleVerificationStatus.Verified, VerifiedBy = "operator", VerifiedAt = DateTimeOffset.Now };
            _verifications[jobId] = completed;
            PendingCompletion!.SetResult(completed);
        }

        public ExperimentJob AddJob(string batch, ExperimentJobStatus status, bool workstation = false)
        {
            var workflowId = workstation ? Guid.NewGuid() : _plan.WorkflowId;
            if (workstation) _workstationWorkflowIds.Add(workflowId);
            var job = new ExperimentJob
            {
                JobId = Guid.NewGuid(),
                PlanId = _plan.PlanId,
                PlanVersion = _plan.Version,
                WorkflowId = workflowId,
                WorkflowVersion = _plan.WorkflowVersion,
                SampleBatchId = batch,
                Status = status,
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now
            };
            UpsertJob(job);
            return job;
        }

        public ExperimentSampleVerification CreateVerification(ExperimentJob job, ExperimentSampleVerificationStatus status)
        {
            var sample = new ExperimentSample
            {
                SampleId = Guid.NewGuid(), BusinessSampleId = "S-" + job.SampleBatchId,
                BatchId = job.SampleBatchId, Barcode = "BC-" + job.SampleBatchId,
                DisplayName = "样品 " + job.SampleBatchId, Status = ExperimentSampleStatus.Active
            };
            _samples[sample.SampleId] = sample;
            var row = new ExperimentSampleTaskRow
            {
                RowId = Guid.NewGuid(), SampleId = sample.SampleId, SampleBarcode = sample.Barcode,
                Position = "A01", DisplayName = sample.DisplayName, Order = 1
            };
            return new ExperimentSampleVerification
            {
                VerificationId = Guid.NewGuid(), ExperimentJobId = job.JobId, Revision = 1, Status = status,
                Rows = [row], SnapshotHash = "HASH-" + job.JobId.ToString("N"),
                ValidationIssues = [new ExperimentSampleVerificationValidationIssue
                {
                    RowId = row.RowId, Order = row.Order,
                    Code = ExperimentSampleVerificationIssueCodes.PositionDuplicate, Message = "Position must be unique."
                }]
            };
        }

        public ExperimentSampleVerification CreateTwoRowVerification(ExperimentJob job, ExperimentSampleVerificationStatus status)
        {
            var first = CreateVerification(job, status);
            _samples[first.Rows[0].SampleId] = _samples[first.Rows[0].SampleId] with
            {
                BusinessSampleId = "S-" + job.SampleBatchId + "-1"
            };
            var second = new ExperimentSample
            {
                SampleId = Guid.NewGuid(), BusinessSampleId = "S-" + job.SampleBatchId + "-2", BatchId = job.SampleBatchId,
                Barcode = "BC-" + job.SampleBatchId + "-2", DisplayName = "second display", Status = ExperimentSampleStatus.Active
            };
            _samples[second.SampleId] = second;
            return first with { Rows = [first.Rows[0] with { BusinessSampleId = string.Empty }, new ExperimentSampleTaskRow
            {
                RowId = Guid.NewGuid(), SampleId = second.SampleId, BusinessSampleId = string.Empty, SampleBarcode = second.Barcode,
                Position = "A02", DisplayName = second.DisplayName, Order = 2
            }] };
        }

        public void SetVerification(Guid jobId, ExperimentSampleVerification verification) => _verifications[jobId] = verification;

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
                Entries = _schedules.ToArray(),
                Activities = Activities.ToArray()
            });

        public Task<IReadOnlyList<ExperimentResourceAvailability>> GetExperimentResourceAvailabilityAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExperimentResourceAvailability>>([_availability]);

        public Task<IReadOnlyList<ExperimentSchedulingAuditEntry>> GetExperimentSchedulingAuditsAsync(Guid? planId, Guid? experimentJobId, Guid? scheduleEntryId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExperimentSchedulingAuditEntry>>(Audits.Take(limit).ToArray());

        public Task<WorkflowVersion?> GetWorkflowVersionAsync(Guid workflowId, int version, CancellationToken cancellationToken)
        {
            if (WorkflowVersionException is not null)
                return Task.FromException<WorkflowVersion?>(WorkflowVersionException);
            if (PendingWorkflowVersion is not null)
            {
                WorkflowVersionRequested!.TrySetResult(true);
                return PendingWorkflowVersion.Task;
            }
            if (ReturnNullWorkflowVersion)
                return Task.FromResult<WorkflowVersion?>(null);
            return Task.FromResult<WorkflowVersion?>(CreateWorkflowVersion(workflowId, version));
        }

        private WorkflowVersion CreateWorkflowVersion(Guid workflowId, int version) => new()
        {
            WorkflowId = workflowId,
            Version = version,
            Definition = new WorkflowDefinition
            {
                Id = workflowId,
                Nodes = _workstationWorkflowIds.Contains(workflowId)
                    ? [new WorkflowNode { NodeTypeId = WorkflowGraphNodeTypeIds.SampleWorkstationExecuteExistingTask }]
                    : []
            }
        };

        public Task<IReadOnlyList<ExperimentSample>> GetExperimentSamplesAsync(string? batchId, CancellationToken cancellationToken)
        {
            SampleReadCalls++;
            if (ThrowOnVerificationProjectionRead || (FailSampleReloadAfterSave && LastSavedVerificationRequest is not null))
                return Task.FromException<IReadOnlyList<ExperimentSample>>(new InvalidOperationException("sample reload failed"));
            return Task.FromResult<IReadOnlyList<ExperimentSample>>(
                _samples.Values.Where(sample => batchId is null || sample.BatchId == batchId).ToArray());
        }

        public Task<ExperimentSample> SaveExperimentSampleAsync(Guid sampleId, SaveExperimentSampleRequest request, CancellationToken cancellationToken)
        {
            LastSampleSaveRequest = request;
            var sample = request.Sample with { SampleId = sampleId };
            if (PendingSampleSave is not null)
            {
                PendingSample = sample;
                SampleSaveRequested!.TrySetResult(true);
                return PendingSampleSave.Task;
            }
            _samples[sampleId] = sample;
            return Task.FromResult(sample);
        }

        public Task<ExperimentSampleVerification?> GetCurrentExperimentSampleVerificationAsync(Guid jobId, CancellationToken cancellationToken)
        {
            CurrentVerificationReadCalls++;
            if (ThrowOnVerificationProjectionRead || (FailCurrentVerificationReloadAfterSave && LastSavedVerificationRequest is not null))
                return Task.FromException<ExperimentSampleVerification?>(new InvalidOperationException("verification reload failed"));
            return Task.FromResult(_verifications.GetValueOrDefault(jobId));
        }

        public Task<ExperimentSampleVerification> SaveCurrentExperimentSampleVerificationAsync(Guid jobId, SaveExperimentSampleVerificationRequest request, CancellationToken cancellationToken)
        {
            LastSavedVerificationRequest = request;
            LastSavedVerificationJobId = jobId;
            LastSavedVerificationRows = request.Rows;
            if (SnapshotSaveException is not null)
                return Task.FromException<ExperimentSampleVerification>(SnapshotSaveException);
            var current = _verifications.GetValueOrDefault(jobId);
            var verification = new ExperimentSampleVerification
            {
                VerificationId = Guid.NewGuid(), ExperimentJobId = jobId, Revision = (current?.Revision ?? 0) + 1,
                Status = ExperimentSampleVerificationStatus.ReadyForVerification, Rows = request.Rows,
                SnapshotHash = "HASH-UPDATED-" + Guid.NewGuid().ToString("N")
            };
            _verifications[jobId] = verification;
            return Task.FromResult(verification);
        }

        public Task<ExperimentSampleVerification> CompleteExperimentSampleVerificationAsync(Guid jobId, int revision, CompleteExperimentSampleVerificationRequest request, CancellationToken cancellationToken)
        {
            if (PendingCompletion is not null)
            {
                CompletionRequested!.TrySetResult(true);
                return PendingCompletion.Task;
            }
            var completed = _verifications[jobId] with { Status = ExperimentSampleVerificationStatus.Verified, VerifiedBy = request.Actor, VerifiedAt = DateTimeOffset.Now };
            _verifications[jobId] = completed;
            return Task.FromResult(completed);
        }

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
            if (PendingSchedule is not null)
            {
                PendingScheduleResult = schedule;
                ScheduleRequested!.TrySetResult(true);
                return PendingSchedule.Task;
            }
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
