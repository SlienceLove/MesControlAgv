using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Domain.Profiles;

namespace MesControlAgv.Mes.Services;

public sealed record AgvAuboSequenceOptions
{
    public int PollIntervalMs { get; init; } = 500;
    public int CompletionTimeoutMs { get; init; } = 60000;
}

/// <summary>
/// Operator-triggered AGV-arrival → AUBO-program coordinator.  The coordinator
/// deliberately starts at an already-created/arrived task; it never dispatches
/// navigation, and every sequence id is single-flight and idempotent in memory.
/// </summary>
public sealed class AgvAuboSequenceService : IAgvAuboSequenceService, IDisposable
{
    private readonly ITaskApplicationService _tasks;
    private readonly IAuboArmGateway _arm;
    private readonly AgvAuboSequenceOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly PhysicalExecutionAdmissionPolicy _admissionPolicy;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, AgvAuboSequenceResponse> _records = [];

    public AgvAuboSequenceService(
        ITaskApplicationService tasks,
        IAuboArmGateway arm,
        AgvAuboSequenceOptions? options = null,
        TimeProvider? timeProvider = null,
        PhysicalExecutionAdmissionPolicy? admissionPolicy = null)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _arm = arm ?? throw new ArgumentNullException(nameof(arm));
        _options = options ?? new AgvAuboSequenceOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _admissionPolicy = admissionPolicy ?? new PhysicalExecutionAdmissionPolicy(
            ProfileConfiguration.Default,
            new DisabledPhysicalReadinessState(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PhysicalExecutionAdmissionPolicy>.Instance);
        if (_options.PollIntervalMs < 50 || _options.CompletionTimeoutMs < _options.PollIntervalMs)
            throw new ArgumentException("Invalid AGV/AUBO sequence timing options.", nameof(options));
    }

    public async Task<AgvAuboSequenceResponse> StartAsync(
        AgvAuboSequenceRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var sequenceId = request.SequenceId.GetValueOrDefault(Guid.NewGuid());
        var taskId = request.EffectiveTaskId;
        var normalizedProgram = NormalizeProgramName(request.ProgramName);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_records.TryGetValue(sequenceId, out var existing)
                && existing.State is not AgvAuboSequenceState.WaitingForArrival)
            {
                if (existing.TransportTaskId != taskId
                    || !string.Equals(existing.ArmId, request.ArmId.Trim(), StringComparison.Ordinal)
                    || !string.Equals(existing.ProgramName, normalizedProgram, StringComparison.Ordinal)
                    || !string.Equals(existing.OperatorName, request.OperatorName.Trim(), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Sequence id {sequenceId:N} is already bound to a different AGV/AUBO operation.");
                }
                return existing;
            }

            _admissionPolicy.RejectUnboundPhysicalWrite("agv-aubo-sequence.start");

            var task = await _tasks.GetDetailAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (task is null)
            {
                return Save(new AgvAuboSequenceResponse(
                    sequenceId,
                    taskId,
                    request.ArmId.Trim(),
                    normalizedProgram,
                    request.OperatorName.Trim(),
                    AgvAuboSequenceState.Failed,
                    "AGV task was not found in MES.",
                    null,
                    _timeProvider.GetUtcNow()));
            }

            if (!IsArrived(task.Task.Status))
            {
                return Save(new AgvAuboSequenceResponse(
                    sequenceId,
                    taskId,
                    request.ArmId.Trim(),
                    normalizedProgram,
                    request.OperatorName.Trim(),
                    AgvAuboSequenceState.WaitingForArrival,
                    $"AGV task is {task.Task.Status}; waiting for an arrived state.",
                    null,
                    _timeProvider.GetUtcNow()));
            }

            // A fresh fleet snapshot is an additional correlation check.  A
            // missing snapshot is not treated as arrival evidence.
            var fleet = await _tasks.GetFleetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!fleet.Any(item => item.ActiveTask?.TransportTaskId == taskId
                || item.Snapshot.CurrentTaskId == taskId))
            {
                return Save(new AgvAuboSequenceResponse(
                    sequenceId,
                    taskId,
                    request.ArmId.Trim(),
                    normalizedProgram,
                    request.OperatorName.Trim(),
                    AgvAuboSequenceState.ManualInterventionRequired,
                    "AGV arrival could not be correlated with a live fleet snapshot.",
                    null,
                    _timeProvider.GetUtcNow()));
            }

            var record = new AgvAuboSequenceResponse(
                sequenceId,
                taskId,
                request.ArmId.Trim(),
                normalizedProgram,
                request.OperatorName.Trim(),
                AgvAuboSequenceState.LoadingProgram,
                "AGV arrived; checking the explicitly approved AUBO project.",
                null,
                _timeProvider.GetUtcNow());
            Save(record);

            AuboArmProgramStatusResponse program;
            try
            {
                program = await _arm.GetProgramAsync(record.ArmId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return Save(Finish(
                    record,
                    AgvAuboSequenceState.Failed,
                    $"AUBO program status is unavailable: {exception.Message}",
                    null));
            }
            AuboArmProgramOperationResponse? operationResult = null;
            var mutationMayHaveStarted = false;
            try
            {
                if (!string.Equals(
                        NormalizeLoadedProgram(program.LoadedProgram),
                        normalizedProgram,
                        StringComparison.Ordinal))
                {
                    record = record with
                    {
                        State = AgvAuboSequenceState.LoadingProgram,
                        Message = "Loading the approved AUBO project."
                    };
                    Save(record);
                    operationResult = await _arm.LoadProgramAsync(
                        record.ArmId,
                        normalizedProgram,
                        record.OperatorName,
                        Guid.NewGuid(),
                        cancellationToken).ConfigureAwait(false);
                    mutationMayHaveStarted = operationResult.MayHaveWritten;
                    if (!operationResult.Succeeded)
                        return Save(Finish(record, operationResult.State == AuboArmProgramOperationState.Unknown
                            ? AgvAuboSequenceState.ManualInterventionRequired
                            : AgvAuboSequenceState.Failed,
                            operationResult.ErrorMessage ?? "AUBO project load failed.", operationResult));
                }

                record = record with
                {
                    State = AgvAuboSequenceState.StartingProgram,
                    Message = "Starting the loaded AUBO project once."
                };
                Save(record);
                operationResult = await _arm.RunProgramAsync(
                    record.ArmId,
                    normalizedProgram,
                    record.OperatorName,
                    Guid.NewGuid(),
                    cancellationToken).ConfigureAwait(false);
                mutationMayHaveStarted |= operationResult.MayHaveWritten;
                if (operationResult.State == AuboArmProgramOperationState.Failed)
                    return Save(Finish(record, AgvAuboSequenceState.Failed,
                        operationResult.ErrorMessage ?? "AUBO project start failed.", operationResult));
                if (operationResult.State == AuboArmProgramOperationState.Unknown)
                    return Save(Finish(record, AgvAuboSequenceState.ManualInterventionRequired,
                        operationResult.ErrorMessage ?? "AUBO project start is unknown; reconcile before retrying.", operationResult));

                return await ObserveRuntimeAsync(record, operationResult, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (mutationMayHaveStarted)
                    return Save(Finish(record, AgvAuboSequenceState.ManualInterventionRequired,
                        "The sequence was cancelled after a possible AUBO write; reconcile manually.", operationResult));
                throw;
            }
            catch (Exception exception)
            {
                return Save(Finish(
                    record,
                    mutationMayHaveStarted
                        ? AgvAuboSequenceState.ManualInterventionRequired
                        : AgvAuboSequenceState.Failed,
                    exception.Message,
                    operationResult));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AgvAuboSequenceResponse?> GetAsync(
        Guid sequenceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _records.GetValueOrDefault(sequenceId); }
        finally { _gate.Release(); }
    }

    private async Task<AgvAuboSequenceResponse> ObserveRuntimeAsync(
        AgvAuboSequenceResponse record,
        AuboArmProgramOperationResponse operationResult,
        CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow().AddMilliseconds(_options.CompletionTimeoutMs);
        var current = record with
        {
            State = operationResult.State == AuboArmProgramOperationState.Running
                ? AgvAuboSequenceState.Running
                : AgvAuboSequenceState.StartingProgram,
            Message = "AUBO runtime is being observed.",
            ProgramResult = operationResult
        };
        Save(current);

        while (_timeProvider.GetUtcNow() < deadline)
        {
            var status = await _arm.GetProgramAsync(current.ArmId, cancellationToken).ConfigureAwait(false);
            if (status.RuntimeState == AuboArmRuntimeState.Stopped)
            {
                return Save(Finish(current, AgvAuboSequenceState.Completed,
                    "AGV arrival and AUBO project execution completed.", operationResult));
            }

            if (status.RuntimeState == AuboArmRuntimeState.Running)
            {
                current = current with
                {
                    State = AgvAuboSequenceState.Running,
                    Message = "AUBO project is running.",
                    UpdatedAtUtc = _timeProvider.GetUtcNow()
                };
                Save(current);
            }
            else if (status.RuntimeState == AuboArmRuntimeState.Unknown)
            {
                return Save(Finish(current, AgvAuboSequenceState.ManualInterventionRequired,
                    "AUBO runtime state became unknown; do not retry automatically.", operationResult));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(_options.PollIntervalMs), _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }

        return Save(Finish(current, AgvAuboSequenceState.ManualInterventionRequired,
            "AUBO runtime did not reach a terminal state before the observation timeout.", operationResult));
    }

    private AgvAuboSequenceResponse Save(AgvAuboSequenceResponse response)
    {
        _records[response.SequenceId] = response;
        return response;
    }

    private AgvAuboSequenceResponse Finish(
        AgvAuboSequenceResponse record,
        AgvAuboSequenceState state,
        string message,
        AuboArmProgramOperationResponse? result) => record with
        {
            State = state,
            Message = message,
            ProgramResult = result,
            UpdatedAtUtc = _timeProvider.GetUtcNow()
        };

    private static bool IsArrived(string status)
    {
        var normalized = status?.Trim().ToLowerInvariant();
        return normalized is "arrived"
            or "waitingpickupconfirmation"
            or "waitingdropoffconfirmation"
            or "waiting_pickup_confirmation"
            or "waiting_dropoff_confirmation";
    }

    private static string NormalizeProgramName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.EndsWith(".pro", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];
        if (normalized.Length is 0 or > 128
            || normalized.Any(char.IsControl)
            || normalized.Contains('/')
            || normalized.Contains('\\')
            || normalized.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Program name must be a simple project name.", nameof(value));
        return normalized;
    }

    private static string? NormalizeLoadedProgram(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return NormalizeProgramName(value); }
        catch (ArgumentException) { return value.Trim(); }
    }

    private static void ValidateRequest(AgvAuboSequenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EffectiveTaskId == Guid.Empty)
            throw new ArgumentException("TransportTaskId is required.", nameof(request));
        if (request.SequenceId is Guid id && id == Guid.Empty)
            throw new ArgumentException("SequenceId cannot be empty.", nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ArmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProgramName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperatorName);
    }

    public void Dispose() => _gate.Dispose();
}
