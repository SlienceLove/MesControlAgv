using System.Text.Json;
using MesControlAgv.Contracts.Experiments;

namespace MesControlAgv.WorkflowContract.Tests;

public sealed class ExperimentSchedulingContractTests
{
    [Fact]
    public void Plan_and_job_pin_plan_and_workflow_versions()
    {
        var planId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var plan = new ExperimentPlan
        {
            PlanId = planId,
            Version = 4,
            Name = "Anion batch",
            WorkflowId = workflowId,
            WorkflowVersion = 7,
            Status = ExperimentPlanStatus.Published
        };
        var job = new ExperimentJob
        {
            JobId = Guid.NewGuid(),
            PlanId = plan.PlanId,
            PlanVersion = plan.Version,
            WorkflowId = plan.WorkflowId,
            WorkflowVersion = plan.WorkflowVersion,
            SampleBatchId = "B-1042",
            Status = ExperimentJobStatus.Ready
        };

        Assert.Equal((planId, 4), (job.PlanId, job.PlanVersion));
        Assert.Equal((workflowId, 7), (job.WorkflowId, job.WorkflowVersion));
    }

    [Fact]
    public void Resource_key_is_case_insensitive_and_reservation_is_not_a_lease()
    {
        var first = ExperimentResourceKeys.Create(ExperimentResourceTypeIds.Agv, "AGV-01");
        var same = ExperimentResourceKeys.Create(" AGV ", "agv-01 ");
        var scheduleEntryId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var reservation = new ResourceReservation
        {
            ReservationId = Guid.NewGuid(),
            ScheduleEntryId = scheduleEntryId,
            Resource = new ExperimentResourceReference
            {
                ResourceType = ExperimentResourceTypeIds.Agv,
                ResourceId = "AGV-01"
            },
            StartsAt = now.AddMinutes(10),
            EndsAt = now.AddMinutes(40),
            Status = ResourceReservationStatus.Planned
        };
        var lease = new ResourceLease
        {
            LeaseId = Guid.NewGuid(),
            ScheduleEntryId = scheduleEntryId,
            WorkflowRunId = runId,
            Resource = reservation.Resource,
            Status = ResourceLeaseStatus.Active,
            AcquiredAt = now,
            ExpiresAt = now.AddMinutes(5)
        };

        Assert.Equal(first, same);
        Assert.NotEqual(reservation.ReservationId, lease.LeaseId);
        Assert.Equal(runId, lease.WorkflowRunId);
        Assert.DoesNotContain("WorkflowRunId", JsonSerializer.Serialize(reservation));
    }

    [Fact]
    public void Resource_key_rejects_incomplete_identity()
    {
        Assert.Throws<ArgumentException>(() => ExperimentResourceKeys.Create("", "AGV-01"));
        Assert.Throws<ArgumentException>(() => ExperimentResourceKeys.Create("agv", " "));
    }
}
