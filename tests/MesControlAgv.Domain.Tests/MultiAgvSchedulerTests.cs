using MesControlAgv.Domain;

namespace MesControlAgv.Domain.Tests;

public sealed class MultiAgvSchedulerTests
{
    [Fact]
    public void Scheduler_assigns_the_lowest_cost_idle_agv_and_reuses_assignment_for_same_task()
    {
        var scheduler = new MultiAgvScheduler(new PathPlanner(AgvMap.Default));
        var taskId = Guid.NewGuid();
        var candidates = new[]
        {
            new AgvCandidate("AGV-02", true, "adapter", "ST_PREP_01", false),
            new AgvCandidate("AGV-01", true, "adapter", "SAMPLE_01", false)
        };

        var first = scheduler.Schedule(taskId, "SAMPLE_01", "ST_PREP_01", candidates);
        var second = scheduler.Schedule(taskId, "SAMPLE_01", "ST_PREP_01", candidates);

        Assert.True(first.Assigned);
        Assert.Equal("AGV-01", first.AgvId);
        Assert.Equal(first.AgvId, second.AgvId);
        Assert.Equal(first.Path!.Stations, second.Path!.Stations);
        Assert.Single(scheduler.ActiveRoutes);
    }

    [Fact]
    public void Scheduler_rejects_offline_busy_or_non_adapter_cars()
    {
        var scheduler = new MultiAgvScheduler(new PathPlanner(AgvMap.Default));
        var result = scheduler.Schedule(
            Guid.NewGuid(),
            "SAMPLE_01",
            "ST_PREP_01",
            [
                new("AGV-01", false, "adapter", "CHARGE_01", false),
                new("AGV-02", true, "roboshop", "CHARGE_01", false),
                new("AGV-03", true, "adapter", "CHARGE_01", true)
            ]);

        Assert.False(result.Assigned);
        Assert.Contains("No online", result.Reason);
    }

    [Fact]
    public void Scheduler_can_assign_a_second_car_to_the_same_shared_work_points()
    {
        var scheduler = new MultiAgvScheduler(new PathPlanner(AgvMap.Default));
        var first = scheduler.Schedule(
            Guid.NewGuid(),
            "SAMPLE_01",
            "ST_PREP_01",
            [new("AGV-01", true, "adapter", "CHARGE_01", false)]);

        var second = scheduler.Schedule(
            Guid.NewGuid(),
            "SAMPLE_01",
            "ST_PREP_01",
            [
                new("AGV-01", true, "adapter", "CHARGE_01", true),
                new("AGV-02", true, "adapter", "CHARGE_01", false)
            ]);

        Assert.True(first.Assigned);
        Assert.True(second.Assigned);
        Assert.Equal("AGV-02", second.AgvId);
    }
}
