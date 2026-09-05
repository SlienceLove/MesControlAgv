using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using MesControlAgv.Mes.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace MesControlAgv.Mes.Tests;

public sealed class ExperimentCompositeRuntimeWorkerTests
{
    [Fact]
    public async Task Process_pending_runs_two_children_serially_and_pins_step_parameters()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var prepared = await fixture.PrepareAsync();

        var first = await fixture.Runtime.ProcessPendingAsync(CancellationToken.None);
        var afterFirst = await fixture.GetRunAsync(prepared.ExperimentRunId);
        Assert.Equal(1, first.Scanned);
        Assert.Equal(1, first.Changed);
        Assert.Equal(0, first.Unknown);
        Assert.Equal(ExperimentRunStatus.Running, afterFirst!.Status);
        Assert.Equal(ExperimentStepRunStatus.Running, afterFirst.Steps[0].Status);
        Assert.Equal(ExperimentStepRunStatus.Pending, afterFirst.Steps[1].Status);
        Assert.NotNull(afterFirst.Steps[0].WorkflowRunId);
        Assert.Null(afterFirst.Steps[1].WorkflowRunId);

        var firstChildId = afterFirst.Steps[0].WorkflowRunId!.Value;
        var firstRequest = await fixture.Workflows.GetExecutionRequestAsync(
            firstChildId,
            CancellationToken.None);
        Assert.NotNull(firstRequest);
        Assert.Equal("step-one", firstRequest!.Parameters["sample.mode"]);
        Assert.Equal("job-default", firstRequest.Parameters["shared"]);

        await fixture.SetChildStatusAsync(firstChildId, WorkflowRuntimeStatus.Completed);
        var second = await fixture.Runtime.ProcessPendingAsync(CancellationToken.None);
        var afterSecond = await fixture.GetRunAsync(prepared.ExperimentRunId);
        Assert.Equal(1, second.Scanned);
        Assert.Equal(1, second.Changed);
        Assert.Equal(ExperimentStepRunStatus.Succeeded, afterSecond!.Steps[0].Status);
        Assert.Equal(ExperimentStepRunStatus.Ready, afterSecond.Steps[1].Status);
        Assert.Null(afterSecond.Steps[1].WorkflowRunId);
        Assert.Equal(1, await fixture.Database.WorkflowExecutions.CountAsync());

        var third = await fixture.Runtime.ProcessPendingAsync(CancellationToken.None);
        var afterThird = await fixture.GetRunAsync(prepared.ExperimentRunId);
        Assert.Equal(1, third.Scanned);
        Assert.Equal(1, third.Changed);
        Assert.Equal(ExperimentStepRunStatus.Running, afterThird!.Steps[1].Status);
        Assert.NotNull(afterThird.Steps[1].WorkflowRunId);
        Assert.Equal(2, await fixture.Database.WorkflowExecutions.CountAsync());

        await fixture.SetChildStatusAsync(
            afterThird.Steps[1].WorkflowRunId!.Value,
            WorkflowRuntimeStatus.Completed);
        var fourth = await fixture.Runtime.ProcessPendingAsync(CancellationToken.None);
        var completed = await fixture.GetRunAsync(prepared.ExperimentRunId);
        Assert.Equal(1, fourth.Scanned);
        Assert.Equal(1, fourth.Changed);
        Assert.Equal(ExperimentRunStatus.Completed, completed!.Status);
        Assert.All(completed.Steps, step => Assert.Equal(ExperimentStepRunStatus.Succeeded, step.Status));

        var coordinatorAudits = await fixture.Database.ExperimentSchedulingAudits
            .AsNoTracking()
            .Where(audit => audit.Actor == "experiment-composite-worker")
            .ToListAsync();
        Assert.NotEmpty(coordinatorAudits);
        Assert.All(coordinatorAudits, audit =>
        {
            Assert.Contains("deviceWritesAttempted", audit.DetailsJson, StringComparison.Ordinal);
            Assert.Contains("automaticRetry", audit.DetailsJson, StringComparison.Ordinal);
            Assert.Contains("False", audit.DetailsJson, StringComparison.Ordinal);
        });
        Assert.Empty(await fixture.Database.WorkflowDeviceOperations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Rejected_child_fails_the_outer_run_without_starting_the_next_step()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var prepared = await fixture.PrepareAsync();
        await fixture.SetWorkflowPublishedAsync(prepared.Steps[0].WorkflowId, published: false);

        var result = await fixture.Runtime.ProcessPendingAsync(CancellationToken.None);
        var failed = await fixture.GetRunAsync(prepared.ExperimentRunId);

        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Changed);
        Assert.Equal(0, result.Unknown);
        Assert.Equal(ExperimentRunStatus.Failed, failed!.Status);
        Assert.Equal(ExperimentStepRunStatus.Failed, failed.Steps[0].Status);
        Assert.Equal(ExperimentStepRunStatus.Pending, failed.Steps[1].Status);
        Assert.Null(failed.Steps[0].WorkflowRunId);
        Assert.Single(await fixture.Database.WorkflowExecutions.AsNoTracking().ToListAsync());

        var replay = await fixture.Runtime.ProcessPendingAsync(CancellationToken.None);
        Assert.Equal(0, replay.Scanned);
        Assert.Equal(0, replay.Changed);
    }

    [Fact]
    public async Task Unknown_child_is_fail_closed_and_is_not_automatically_retried()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var prepared = await fixture.PrepareAsync();
        await fixture.Runtime.ProcessPendingAsync(CancellationToken.None);
        var running = await fixture.GetRunAsync(prepared.ExperimentRunId);
        var childId = running!.Steps[0].WorkflowRunId!.Value;
        await fixture.SetChildStatusAsync(childId, WorkflowRuntimeStatus.Unknown);

        var result = await fixture.Runtime.ProcessPendingAsync(CancellationToken.None);
        var unknown = await fixture.GetRunAsync(prepared.ExperimentRunId);
        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Changed);
        Assert.Equal(1, result.Unknown);
        Assert.Equal(ExperimentRunStatus.Unknown, unknown!.Status);
        Assert.Equal(ExperimentStepRunStatus.Unknown, unknown.Steps[0].Status);
        Assert.False(unknown.IsTerminal);
        Assert.Equal(ExperimentStepRunStatus.Pending, unknown.Steps[1].Status);

        var replay = await fixture.Runtime.ProcessPendingAsync(CancellationToken.None);
        Assert.Equal(0, replay.Scanned);
        Assert.Equal(0, replay.Changed);
        Assert.Equal(1, await fixture.Database.WorkflowExecutions.CountAsync());
    }

    [Fact]
    public async Task Missing_child_snapshot_is_unknown_without_creating_a_replacement()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var prepared = await fixture.PrepareAsync();
        await fixture.Runtime.ProcessPendingAsync(CancellationToken.None);
        var running = await fixture.GetRunAsync(prepared.ExperimentRunId);
        var childId = running!.Steps[0].WorkflowRunId!.Value;
        await fixture.DeleteChildAsync(childId);

        var result = await fixture.Runtime.ProcessPendingAsync(CancellationToken.None);
        var unknown = await fixture.GetRunAsync(prepared.ExperimentRunId);
        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Changed);
        Assert.Equal(1, result.Unknown);
        Assert.Equal(ExperimentRunStatus.Unknown, unknown!.Status);
        Assert.Equal(ExperimentStepRunStatus.Unknown, unknown.Steps[0].Status);
        Assert.Null(unknown.Steps[1].WorkflowRunId);
        Assert.Empty(await fixture.Database.WorkflowExecutions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Restart_reuses_the_deterministic_child_request_id_idempotently()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var prepared = await fixture.PrepareAsync();
        var step = prepared.Steps[0];
        var requestId = StableChildRequestId(prepared.ExperimentRunId, step.StepId);
        var request = new WorkflowExecutionRequest
        {
            WorkflowId = step.WorkflowId,
            Version = step.WorkflowVersion,
            RequestId = requestId,
            RequestedBy = "experiment-composite-worker",
            CorrelationId = $"experiment-run:{prepared.ExperimentRunId:N}:step:{step.StepId:N}",
            Parameters = new Dictionary<string, string?>
            {
                ["shared"] = "job-default",
                ["sample.mode"] = "step-one"
            },
            RequestedAt = fixture.Now
        };
        var admitted = await fixture.Workflows.ExecuteAsync(request, CancellationToken.None);
        Assert.True(admitted.IsAccepted);
        Assert.Equal(1, await fixture.Database.WorkflowExecutions.CountAsync());

        var result = await fixture.Runtime.ProcessPendingAsync(CancellationToken.None);
        var recovered = await fixture.GetRunAsync(prepared.ExperimentRunId);
        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Changed);
        Assert.Equal(ExperimentStepRunStatus.Running, recovered!.Steps[0].Status);
        Assert.Equal(admitted.ExecutionId, recovered.Steps[0].WorkflowRunId);
        Assert.Equal(1, await fixture.Database.WorkflowExecutions.CountAsync());
        Assert.Equal(
            requestId,
            (await fixture.Database.WorkflowExecutions.AsNoTracking().SingleAsync()).RequestId);
    }

    [Fact]
    public void Simulator_composite_worker_is_explicitly_enabled_only_in_field_simulation_profile()
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        using var defaultDocument = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "src", "MesControlAgv.Mes", "appsettings.json")));
        using var fieldDocument = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "src", "MesControlAgv.Mes", "appsettings.FieldSimulation.json")));
        using var physicalDocument = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "src", "MesControlAgv.Mes", "appsettings.PhysicalAcceptance.json")));

        Assert.False(defaultDocument.RootElement.GetProperty("ExperimentCompositeRuntimeWorker").GetProperty("enabled").GetBoolean());
        Assert.True(fieldDocument.RootElement.GetProperty("ExperimentCompositeRuntimeWorker").GetProperty("enabled").GetBoolean());
        Assert.False(physicalDocument.RootElement.GetProperty("ExperimentCompositeRuntimeWorker").GetProperty("Enabled").GetBoolean());
    }

    private static Guid StableChildRequestId(Guid experimentRunId, Guid stepId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"experiment-child-request\u001f{experimentRunId:N}\u001f{stepId:N}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private RuntimeFixture(
            SqliteConnection connection,
            MesDbContext database,
            WorkflowApplicationService workflows,
            ExperimentCompositeRuntimeService runtime,
            DateTimeOffset now,
            Guid jobId)
        {
            Connection = connection;
            Database = database;
            Workflows = workflows;
            Runtime = runtime;
            Now = now;
            JobId = jobId;
        }

        public SqliteConnection Connection { get; }
        public MesDbContext Database { get; }
        public WorkflowApplicationService Workflows { get; }
        public ExperimentCompositeRuntimeService Runtime { get; }
        public DateTimeOffset Now { get; }
        public Guid JobId { get; }

        public static async Task<RuntimeFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<MesDbContext>()
                .UseSqlite(connection)
                .Options;
            var database = new MesDbContext(options);
            await database.Database.EnsureCreatedAsync();

            var now = new DateTimeOffset(2026, 9, 5, 4, 0, 0, TimeSpan.Zero);
            var timeProvider = new FixedTimeProvider(now);
            var profile = ProfileConfiguration.Default;
            var validator = new WorkflowValidator(
                BuiltInWorkflowCatalog.Create(),
                WorkflowPublicationContext.FromProfile(profile));
            var reader = new MesWorkflowVersionReader(database);
            var workflows = new WorkflowApplicationService(
                database,
                reader,
                new WorkflowRuntimeExecutor(
                    reader,
                    validator,
                    timeProvider,
                    [new ActiveProfileWorkflowAdmissionPolicy(profile)]),
                validator,
                timeProvider);

            var planId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            var steps = new[]
            {
                new ExperimentPlanWorkflowStep
                {
                    StepId = Guid.NewGuid(),
                    Order = 1,
                    WorkflowId = Guid.NewGuid(),
                    WorkflowVersion = 1,
                    Name = "第一步",
                    Parameters = new Dictionary<string, string?>
                    {
                        ["sample.mode"] = "step-one"
                    }
                },
                new ExperimentPlanWorkflowStep
                {
                    StepId = Guid.NewGuid(),
                    Order = 2,
                    WorkflowId = Guid.NewGuid(),
                    WorkflowVersion = 1,
                    Name = "第二步",
                    Parameters = new Dictionary<string, string?>
                    {
                        ["sample.mode"] = "step-two"
                    }
                }
            };
            var targets = new[] { "SAMPLE_01", "ST_OPEN_01" };
            for (var index = 0; index < steps.Length; index++)
            {
                var definition = WorkflowTestDefinitions.CreateMoveWorkflow(
                    steps[index].WorkflowId,
                    targets[index]);
                var validation = validator.Validate(definition);
                Assert.True(validation.IsValid, string.Join("; ", validation.Issues.Select(issue => issue.Message)));
                database.WorkflowVersions.Add(new WorkflowVersionRecord
                {
                    WorkflowId = steps[index].WorkflowId,
                    Version = steps[index].WorkflowVersion,
                    DefinitionJson = JsonSerializer.Serialize(definition),
                    Status = WorkflowVersionStatus.Published.ToString(),
                    PublishStatus = WorkflowPublishStatus.Published.ToString(),
                    ValidationJson = JsonSerializer.Serialize(validation),
                    CreatedBy = "composite-worker-test",
                    CreatedAtUtc = now.UtcDateTime,
                    PublishedBy = "composite-worker-test",
                    PublishedAtUtc = now.UtcDateTime,
                    UpdatedAtUtc = now.UtcDateTime
                });
            }

            var first = steps[0];
            database.ExperimentPlans.Add(new ExperimentPlanRecord
            {
                PlanId = planId,
                Version = 1,
                Name = "Composite worker test plan",
                Description = "Simulator composite runtime regression",
                WorkflowId = first.WorkflowId,
                WorkflowVersion = first.WorkflowVersion,
                WorkflowStepsJson = JsonSerializer.Serialize(steps),
                Status = ExperimentPlanStatus.Published.ToString(),
                MaterialRequirementsJson = "[]",
                DefaultParametersJson = JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["shared"] = "plan-default"
                }),
                ResourceRequirementsJson = "[]",
                ProfileProductId = profile.Product.ProductId,
                ProfileVersion = profile.Product.Version,
                CreatedBy = "composite-worker-test",
                CreatedAtUtc = now.UtcDateTime,
                PublishedAtUtc = now.UtcDateTime,
                UpdatedAtUtc = now.UtcDateTime
            });
            database.ExperimentJobs.Add(new ExperimentJobRecord
            {
                JobId = jobId,
                PlanId = planId,
                PlanVersion = 1,
                WorkflowId = first.WorkflowId,
                WorkflowVersion = first.WorkflowVersion,
                WorkflowStepsJson = JsonSerializer.Serialize(steps),
                SampleBatchId = "COMPOSITE-WORKER-1",
                ParametersJson = JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["shared"] = "job-default",
                    ["sample.mode"] = "job-default"
                }),
                Status = ExperimentJobStatus.Scheduled.ToString(),
                CreatedBy = "composite-worker-test",
                CreatedAtUtc = now.UtcDateTime,
                UpdatedAtUtc = now.UtcDateTime
            });
            database.ScheduleEntries.Add(new ScheduleEntryRecord
            {
                ScheduleEntryId = Guid.NewGuid(),
                ExperimentJobId = jobId,
                PlannedStartUtc = now.UtcDateTime,
                PlannedEndUtc = now.UtcDateTime.AddHours(1),
                Priority = 10,
                Status = ScheduleEntryStatus.Scheduled.ToString(),
                RequestedResourcesJson = "[]",
                BlockingReasonsJson = "[]",
                CreatedBy = "composite-worker-test",
                CreatedAtUtc = now.UtcDateTime,
                UpdatedAtUtc = now.UtcDateTime
            });
            await database.SaveChangesAsync();
            return new RuntimeFixture(
                connection,
                database,
                workflows,
                new ExperimentCompositeRuntimeService(
                    database,
                    new ExperimentSchedulingMutationGate(),
                    timeProvider,
                    workflows,
                    new TestHostEnvironment()),
                now,
                jobId);
        }

        public async Task<ExperimentRun> PrepareAsync()
        {
            return await Runtime.PrepareAsync(
                new PrepareExperimentRunRequest
                {
                    RequestId = Guid.NewGuid(),
                    ExperimentJobId = JobId,
                    Actor = "composite-worker-test",
                    Reason = "Prepare simulator composite runtime"
                },
                CancellationToken.None);
        }

        public async Task<ExperimentRun?> GetRunAsync(Guid runId)
        {
            Database.ChangeTracker.Clear();
            return await Runtime.GetAsync(runId, CancellationToken.None);
        }

        public async Task SetChildStatusAsync(Guid childId, WorkflowRuntimeStatus status)
        {
            Database.ChangeTracker.Clear();
            var child = await Database.WorkflowExecutions.SingleAsync(item => item.ExecutionId == childId);
            child.RuntimeStatus = status.ToString();
            child.LastError = status == WorkflowRuntimeStatus.Unknown ? "simulated unknown" : null;
            child.UpdatedAtUtc = Now.UtcDateTime;
            await Database.SaveChangesAsync();
        }

        public async Task DeleteChildAsync(Guid childId)
        {
            Database.ChangeTracker.Clear();
            var child = await Database.WorkflowExecutions.SingleAsync(item => item.ExecutionId == childId);
            Database.WorkflowExecutions.Remove(child);
            await Database.SaveChangesAsync();
        }

        public async Task SetWorkflowPublishedAsync(Guid workflowId, bool published)
        {
            Database.ChangeTracker.Clear();
            var version = await Database.WorkflowVersions.SingleAsync(item => item.WorkflowId == workflowId);
            version.Status = published
                ? WorkflowVersionStatus.Published.ToString()
                : WorkflowVersionStatus.Draft.ToString();
            version.PublishStatus = published
                ? WorkflowPublishStatus.Published.ToString()
                : WorkflowPublishStatus.NotPublished.ToString();
            await Database.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "MesControlAgv.Mes.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
