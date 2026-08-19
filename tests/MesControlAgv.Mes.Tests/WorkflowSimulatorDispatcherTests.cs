using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Tests;

public sealed class WorkflowSimulatorDispatcherTests
{
    [Fact]
    public async Task Dispatcher_dispatches_each_move_once_and_advances_only_after_arrival()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MesDbContext>().UseSqlite(connection).Options;
        await using var database = new MesDbContext(options);
        await database.Database.EnsureCreatedAsync();
        var workflows = CreateService(database);
        var executionId = await AdmitAsync(workflows, CreateTwoMoveWorkflow());
        var adapter = new ScriptedSimulatorGateway { TaskState = "moving" };
        var dispatcher = CreateDispatcher(workflows, adapter);

        await dispatcher.ProcessAsync(CancellationToken.None);

        var firstRunning = await workflows.GetExecutionAsync(executionId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Running, firstRunning!.RuntimeStatus);
        Assert.Equal(1, adapter.DispatchCalls);
        Assert.Equal(1, adapter.GetTaskCalls);

        adapter.TaskState = "arrived";
        await dispatcher.ProcessAsync(CancellationToken.None);

        var secondPrepared = await workflows.GetExecutionAsync(executionId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Prepared, secondPrepared!.RuntimeStatus);
        Assert.Equal(WorkflowNodeType.Move, secondPrepared.PendingStepRequest!.NodeType);
        Assert.Equal(1, adapter.DispatchCalls);

        adapter.TaskState = "moving";
        await dispatcher.ProcessAsync(CancellationToken.None);
        Assert.Equal(2, adapter.DispatchCalls);
        Assert.Equal(WorkflowRuntimeStatus.Running, (await workflows.GetExecutionAsync(executionId, CancellationToken.None))!.RuntimeStatus);

        adapter.TaskState = "completed";
        await dispatcher.ProcessAsync(CancellationToken.None);

        var completed = await workflows.GetExecutionAsync(executionId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Completed, completed!.RuntimeStatus);
        Assert.Equal(2, adapter.DispatchCalls);
        Assert.Equal(2, database.WorkflowAudits.Count(audit => audit.EventType == "WorkflowStepClaimed"));
        Assert.Equal(2, database.WorkflowAudits.Count(audit => audit.EventType == "WorkflowStepCompleted"));
    }

    [Fact]
    public async Task Dispatcher_marks_an_ambiguous_dispatch_unknown_and_never_retries_it()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MesDbContext>().UseSqlite(connection).Options;
        await using var database = new MesDbContext(options);
        await database.Database.EnsureCreatedAsync();
        var workflows = CreateService(database);
        var executionId = await AdmitAsync(workflows, CreateTwoMoveWorkflow());
        var adapter = new ScriptedSimulatorGateway { DispatchException = new TimeoutException("ambiguous simulator response") };
        var dispatcher = CreateDispatcher(workflows, adapter);

        await dispatcher.ProcessAsync(CancellationToken.None);
        await dispatcher.ProcessAsync(CancellationToken.None);

        var unknown = await workflows.GetExecutionAsync(executionId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Unknown, unknown!.RuntimeStatus);
        Assert.Equal(1, adapter.DispatchCalls);
        Assert.Equal(0, adapter.GetTaskCalls);
        Assert.NotNull(unknown.TransportOperationId);
    }

    [Fact]
    public async Task Dispatcher_leaves_wait_steps_prepared_without_claiming_or_adapter_io()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MesDbContext>().UseSqlite(connection).Options;
        await using var database = new MesDbContext(options);
        await database.Database.EnsureCreatedAsync();
        var workflows = CreateService(database);
        var executionId = await AdmitAsync(workflows, CreateMoveThenWaitWorkflow());
        var adapter = new ScriptedSimulatorGateway { TaskState = "arrived" };
        var dispatcher = CreateDispatcher(workflows, adapter);

        await dispatcher.ProcessAsync(CancellationToken.None);
        await dispatcher.ProcessAsync(CancellationToken.None);

        var pendingWait = await workflows.GetExecutionAsync(executionId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Prepared, pendingWait!.RuntimeStatus);
        Assert.Equal(WorkflowNodeType.Wait, pendingWait.PendingStepRequest!.NodeType);
        Assert.Null(pendingWait.TransportOperationId);
        Assert.Equal(1, adapter.DispatchCalls);
        Assert.Equal(0, adapter.GetTaskCalls);
    }

    [Fact]
    public async Task Dispatcher_completes_an_explicit_timed_wait_without_adapter_io()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MesDbContext>().UseSqlite(connection).Options;
        await using var database = new MesDbContext(options);
        await database.Database.EnsureCreatedAsync();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 18, 4, 0, 0, TimeSpan.Zero));
        var workflows = CreateService(database, clock);
        var executionId = await AdmitAsync(workflows, CreateTimedWaitWorkflow("5"));
        var adapter = new ScriptedSimulatorGateway();
        var dispatcher = CreateDispatcher(workflows, adapter, clock);

        await dispatcher.ProcessAsync(CancellationToken.None);

        var running = await workflows.GetExecutionAsync(executionId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Running, running!.RuntimeStatus);
        Assert.Equal(WorkflowNodeType.Wait, running.PendingStepRequest!.NodeType);
        Assert.NotNull(running.TransportOperationId);

        clock.Advance(TimeSpan.FromSeconds(4));
        await dispatcher.ProcessAsync(CancellationToken.None);
        Assert.Equal(
            WorkflowRuntimeStatus.Running,
            (await workflows.GetExecutionAsync(executionId, CancellationToken.None))!.RuntimeStatus);

        clock.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.ProcessAsync(CancellationToken.None);

        var completed = await workflows.GetExecutionAsync(executionId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Completed, completed!.RuntimeStatus);
        Assert.Equal(0, adapter.DispatchCalls);
        Assert.Equal(0, adapter.GetTaskCalls);
        Assert.Contains(database.WorkflowAudits, audit => audit.EventType == "WorkflowStepCompleted");
    }

    [Fact]
    public async Task Dispatcher_advances_move_wait_move_workflow_across_poll_cycles()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MesDbContext>().UseSqlite(connection).Options;
        await using var database = new MesDbContext(options);
        await database.Database.EnsureCreatedAsync();
        var workflows = CreateService(database);
        var executionId = await AdmitAsync(workflows, CreateMoveWaitMoveWorkflow());
        var adapter = new ScriptedSimulatorGateway { TaskState = "arrived" };
        var dispatcher = CreateDispatcher(workflows, adapter);

        await dispatcher.ProcessAsync(CancellationToken.None);
        Assert.Equal(
            WorkflowNodeType.Wait,
            (await workflows.GetExecutionAsync(executionId, CancellationToken.None))!.PendingStepRequest!.NodeType);

        await dispatcher.ProcessAsync(CancellationToken.None);
        Assert.Equal(
            WorkflowNodeType.Move,
            (await workflows.GetExecutionAsync(executionId, CancellationToken.None))!.PendingStepRequest!.NodeType);

        await dispatcher.ProcessAsync(CancellationToken.None);

        var completed = await workflows.GetExecutionAsync(executionId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Completed, completed!.RuntimeStatus);
        Assert.Equal(2, adapter.DispatchCalls);
        Assert.Equal(0, adapter.GetTaskCalls);
        Assert.Equal(3, database.WorkflowAudits.Count(audit => audit.EventType == "WorkflowStepClaimed"));
        Assert.Equal(3, database.WorkflowAudits.Count(audit => audit.EventType == "WorkflowStepCompleted"));
    }

    [Fact]
    public async Task Dispatcher_leaves_instrument_operations_prepared_without_device_io()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MesDbContext>().UseSqlite(connection).Options;
        await using var database = new MesDbContext(options);
        await database.Database.EnsureCreatedAsync();
        var workflows = CreateService(database);
        var executionId = await AdmitAsync(workflows, CreateInstrumentWorkflow());
        var adapter = new ScriptedSimulatorGateway();
        var dispatcher = CreateDispatcher(workflows, adapter);

        await dispatcher.ProcessAsync(CancellationToken.None);

        var prepared = await workflows.GetExecutionAsync(executionId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Prepared, prepared!.RuntimeStatus);
        Assert.Equal(WorkflowNodeType.InstrumentOperation, prepared.PendingStepRequest!.NodeType);
        Assert.Equal("D160-01", prepared.PendingStepRequest.Parameters[WorkflowRuntimeParameterNames.InstrumentId]);
        Assert.Null(prepared.TransportOperationId);
        Assert.Equal(0, adapter.DispatchCalls);
        Assert.Equal(0, adapter.GetTaskCalls);
    }

    [Fact]
    public async Task Dispatcher_does_not_contact_adapter_when_the_profile_is_not_simulator()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MesDbContext>().UseSqlite(connection).Options;
        await using var database = new MesDbContext(options);
        await database.Database.EnsureCreatedAsync();
        var workflows = CreateService(database);
        var executionId = await AdmitAsync(workflows, CreateTwoMoveWorkflow());
        var adapter = new ScriptedSimulatorGateway();
        var physicalProfile = ProfileConfiguration.Default with
        {
            Features = ProfileConfiguration.Default.Features with { UseSimulator = false }
        };
        var dispatcher = new WorkflowSimulatorDispatcher(
            workflows,
            adapter,
            physicalProfile,
            new WorkflowSimulatorWorkerOptions { Enabled = true });

        await dispatcher.ProcessAsync(CancellationToken.None);

        Assert.Equal(0, adapter.IdentityCalls);
        Assert.Equal(0, adapter.DispatchCalls);
        Assert.Equal(WorkflowRuntimeStatus.Prepared, (await workflows.GetExecutionAsync(executionId, CancellationToken.None))!.RuntimeStatus);
    }

    private static WorkflowSimulatorDispatcher CreateDispatcher(
        WorkflowApplicationService workflows,
        ScriptedSimulatorGateway adapter,
        TimeProvider? timeProvider = null) =>
        new(
            workflows,
            adapter,
            ProfileConfiguration.Default,
            new WorkflowSimulatorWorkerOptions { Enabled = true },
            timeProvider);

    private static async Task<Guid> AdmitAsync(WorkflowApplicationService workflows, WorkflowDefinition definition)
    {
        var draft = await workflows.CreateDraftAsync(definition, "simulator-test", CancellationToken.None);
        await workflows.ValidateVersionAsync(draft.WorkflowId, draft.Version, CancellationToken.None);
        await workflows.PublishAsync(draft.WorkflowId, draft.Version, "simulator-test", CancellationToken.None);
        var result = await workflows.ExecuteAsync(new WorkflowExecutionRequest
        {
            WorkflowId = draft.WorkflowId,
            Version = draft.Version,
            RequestId = Guid.NewGuid(),
            RequestedBy = "simulator-test"
        }, CancellationToken.None);
        Assert.True(result.IsAccepted);
        return result.ExecutionId;
    }

    private static WorkflowApplicationService CreateService(
        MesDbContext database,
        TimeProvider? timeProvider = null)
    {
        var validator = new WorkflowValidator();
        var reader = new MesWorkflowVersionReader(database);
        return new WorkflowApplicationService(
            database,
            reader,
            new WorkflowRuntimeExecutor(
                reader,
                validator,
                timeProvider,
                [new ActiveProfileWorkflowAdmissionPolicy(ProfileConfiguration.Default)]),
            validator,
            timeProvider);
    }

    private static WorkflowDefinition CreateTwoMoveWorkflow()
    {
        var workflowId = Guid.NewGuid();
        var start = Guid.NewGuid();
        var firstMove = Guid.NewGuid();
        var secondMove = Guid.NewGuid();
        var end = Guid.NewGuid();
        return new WorkflowDefinition
        {
            Id = workflowId,
            Name = "Two move Simulator workflow",
            Nodes =
            [
                new WorkflowNode { Id = start, Type = WorkflowNodeType.Start, Name = "Start", Order = 1, NextNodeIds = [firstMove] },
                new WorkflowNode { Id = firstMove, Type = WorkflowNodeType.Move, Name = "Move one", TargetStation = "SAMPLE_01", Order = 2, NextNodeIds = [secondMove] },
                new WorkflowNode { Id = secondMove, Type = WorkflowNodeType.Move, Name = "Move two", TargetStation = "ST_OPEN_01", Order = 3, NextNodeIds = [end] },
                new WorkflowNode { Id = end, Type = WorkflowNodeType.End, Name = "End", Order = 4 }
            ]
        };
    }

    private static WorkflowDefinition CreateMoveThenWaitWorkflow()
    {
        var workflowId = Guid.NewGuid();
        var start = Guid.NewGuid();
        var move = Guid.NewGuid();
        var wait = Guid.NewGuid();
        var end = Guid.NewGuid();
        return new WorkflowDefinition
        {
            Id = workflowId,
            Name = "Move then wait workflow",
            Nodes =
            [
                new WorkflowNode { Id = start, Type = WorkflowNodeType.Start, Name = "Start", Order = 1, NextNodeIds = [move] },
                new WorkflowNode { Id = move, Type = WorkflowNodeType.Move, Name = "Move", TargetStation = "SAMPLE_01", Order = 2, NextNodeIds = [wait] },
                new WorkflowNode { Id = wait, Type = WorkflowNodeType.Wait, Name = "Wait", Order = 3, NextNodeIds = [end] },
                new WorkflowNode { Id = end, Type = WorkflowNodeType.End, Name = "End", Order = 4 }
            ]
        };
    }

    private static WorkflowDefinition CreateTimedWaitWorkflow(string durationSeconds)
    {
        var workflowId = Guid.NewGuid();
        var start = Guid.NewGuid();
        var wait = Guid.NewGuid();
        var end = Guid.NewGuid();
        return new WorkflowDefinition
        {
            Id = workflowId,
            Name = "Timed Wait Simulator workflow",
            Nodes =
            [
                new WorkflowNode { Id = start, Type = WorkflowNodeType.Start, Name = "Start", Order = 1, NextNodeIds = [wait] },
                new WorkflowNode
                {
                    Id = wait,
                    Type = WorkflowNodeType.Wait,
                    Name = "Timed wait",
                    Order = 2,
                    NextNodeIds = [end],
                    Parameters =
                    [
                        new WorkflowParameter
                        {
                            Name = WorkflowRuntimeParameterNames.WaitDurationSeconds,
                            Value = durationSeconds
                        }
                    ]
                },
                new WorkflowNode { Id = end, Type = WorkflowNodeType.End, Name = "End", Order = 3 }
            ]
        };
    }

    private static WorkflowDefinition CreateMoveWaitMoveWorkflow()
    {
        var workflowId = Guid.NewGuid();
        var start = Guid.NewGuid();
        var firstMove = Guid.NewGuid();
        var wait = Guid.NewGuid();
        var secondMove = Guid.NewGuid();
        var end = Guid.NewGuid();
        return new WorkflowDefinition
        {
            Id = workflowId,
            Name = "Move Wait Move Simulator workflow",
            Nodes =
            [
                new WorkflowNode { Id = start, Type = WorkflowNodeType.Start, Name = "Start", Order = 1, NextNodeIds = [firstMove] },
                new WorkflowNode { Id = firstMove, Type = WorkflowNodeType.Move, Name = "Move one", TargetStation = "SAMPLE_01", Order = 2, NextNodeIds = [wait] },
                new WorkflowNode
                {
                    Id = wait,
                    Type = WorkflowNodeType.Wait,
                    Name = "Wait",
                    Order = 3,
                    NextNodeIds = [secondMove],
                    Parameters =
                    [
                        new WorkflowParameter
                        {
                            Name = WorkflowRuntimeParameterNames.WaitDurationSeconds,
                            Value = "0"
                        }
                    ]
                },
                new WorkflowNode { Id = secondMove, Type = WorkflowNodeType.Move, Name = "Move two", TargetStation = "ST_OPEN_01", Order = 4, NextNodeIds = [end] },
                new WorkflowNode { Id = end, Type = WorkflowNodeType.End, Name = "End", Order = 5 }
            ]
        };
    }

    private static WorkflowDefinition CreateInstrumentWorkflow()
    {
        var workflowId = Guid.NewGuid();
        var start = Guid.NewGuid();
        var instrument = Guid.NewGuid();
        var end = Guid.NewGuid();
        return new WorkflowDefinition
        {
            Id = workflowId,
            Name = "Instrument Simulator workflow",
            Nodes =
            [
                new WorkflowNode { Id = start, Type = WorkflowNodeType.Start, Name = "Start", Order = 1, NextNodeIds = [instrument] },
                new WorkflowNode
                {
                    Id = instrument,
                    Type = WorkflowNodeType.InstrumentOperation,
                    Name = "Read D160 status",
                    Order = 2,
                    NextNodeIds = [end],
                    Parameters =
                    [
                        new WorkflowParameter
                        {
                            Name = WorkflowRuntimeParameterNames.InstrumentId,
                            Value = "D160-01"
                        },
                        new WorkflowParameter
                        {
                            Name = WorkflowRuntimeParameterNames.InstrumentOperation,
                            Value = "read-status"
                        }
                    ]
                },
                new WorkflowNode { Id = end, Type = WorkflowNodeType.End, Name = "End", Order = 3 }
            ]
        };
    }

    private sealed class MutableTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class ScriptedSimulatorGateway : IAgvGateway, IAdapterRuntimeIdentityGateway
    {
        public int IdentityCalls { get; private set; }
        public int DispatchCalls { get; private set; }
        public int GetTaskCalls { get; private set; }
        public string TaskState { get; set; } = "moving";
        public Exception? DispatchException { get; init; }

        public Task<AdapterRuntimeIdentityResponse> GetRuntimeIdentityAsync(CancellationToken cancellationToken)
        {
            IdentityCalls++;
            return Task.FromResult(new AdapterRuntimeIdentityResponse("adapter", "ok", "normal", "simulator"));
        }

        public Task<AgvTaskResponse> DispatchAsync(Guid operationId, string targetStationId, CancellationToken cancellationToken)
        {
            DispatchCalls++;
            if (DispatchException is not null)
            {
                return Task.FromException<AgvTaskResponse>(DispatchException);
            }

            return Task.FromResult(new AgvTaskResponse(operationId, operationId.ToString("N"), targetStationId, TaskState, null));
        }

        public Task<AgvTaskResponse?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken)
        {
            GetTaskCalls++;
            return Task.FromResult<AgvTaskResponse?>(new AgvTaskResponse(operationId, operationId.ToString("N"), "SAMPLE_01", TaskState, null));
        }

        public Task<AgvTaskResponse?> CancelAsync(Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult<AgvTaskResponse?>(null);

        public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AgvSnapshotResponse(true, "simulator", "CHARGE_01", null));

        public Task<AgvTaskResponse?> ExecuteAgvCommandAsync(string agvId, string command, Guid? taskId, CancellationToken cancellationToken) =>
            Task.FromResult<AgvTaskResponse?>(null);
    }
}
