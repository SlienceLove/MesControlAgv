using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Entities;

namespace MesControlAgv.Mes.Services;

public sealed class ShineLabTaskService(
    ShineLabTaskRepository repository,
    ShineLabCommandService commands,
    ShineLabCommandJournal? journal = null)
{
    private static readonly SemaphoreSlim ConfigGate = new(1, 1);
    public async Task<ShineLabTaskResponse> CreateAsync(
        ShineLabTaskCreateRequest request,
        CancellationToken cancellationToken)
    {
        var (task, _) = await repository.CreateOrGetAsync(request, cancellationToken);
        return ToResponse(task);
    }

    public async Task<ShineLabTaskResponse> ConfigureAsync(
        string taskUuid,
        CancellationToken cancellationToken)
    {
        await ConfigGate.WaitAsync(cancellationToken);
        try { return await ConfigureCoreAsync(taskUuid, cancellationToken); }
        finally { ConfigGate.Release(); }
    }

    private async Task<ShineLabTaskResponse> ConfigureCoreAsync(
        string taskUuid, CancellationToken cancellationToken)
    {
        var task = await repository.GetAsync(taskUuid, cancellationToken)
            ?? throw new KeyNotFoundException($"ShineLab task '{taskUuid}' was not found.");
        if (task.Status is "Configured" or "Accepted" or "Running" or "Completed")
            return ToResponse(task);
        if (task.Status == "Unknown")
            throw new InvalidOperationException("Task outcome is unknown; reconcile with ShineLab before retrying Config.");
        var mappedSamples = await repository.GetSamplesAsync(taskUuid, cancellationToken);
        if (mappedSamples.Any(sample => sample.ConfigAttempted))
            throw new InvalidOperationException("This injection identity has already been used in a Config attempt. Do not append it twice; inspect the client and create a new task for a new run.");

        var request = JsonSerializer.Deserialize<ShineLabConfigRequest>(task.ConfigJson)
            ?? throw new InvalidOperationException("Stored ShineLab Config payload is invalid.");
        await repository.MarkOperationStartedAsync(
            taskUuid,
            "Configuring",
            "Configuring",
            cancellationToken);
        ShineLabCommandResponse commandResponse;
        try
        {
            commandResponse = await commands.SendConfigAsync(task.EquipmentCode, request, cancellationToken, trackTask: true);
        }
        catch (ShineLabNotConnectedException exception)
        {
            await repository.RestoreAfterNotConnectedAsync(
                taskUuid,
                "Created",
                "WaitingForConnection",
                exception.Message,
                cancellationToken);
            throw;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            await RecordUnknownAsync(taskUuid, "Config", "ConfigOutcomeUnknown", exception, cancellationToken);
            throw;
        }
        catch (ShineLabCommandCanceledException exception)
        {
            await RecordUnknownAsync(taskUuid, "Config", "ConfigOutcomeUnknown", exception, CancellationToken.None);
            throw;
        }
        task = journal is null ? await repository.MarkConfigResultAsync(taskUuid, commandResponse, cancellationToken) :
            await journal.ReconcileAsync(taskUuid, "Config", commandResponse.StrId, null, null, cancellationToken);
        return ToResponse(task);
    }

    public async Task<ShineLabTaskResponse> CommandAsync(
        string taskUuid,
        ShineLabCommandRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(request.StrAction))
            throw new ArgumentException("Use the device command endpoint for tray motion; sample tasks must not change state for tray operations.", nameof(request));
        var task = await repository.GetAsync(taskUuid, cancellationToken)
            ?? throw new KeyNotFoundException($"ShineLab task '{taskUuid}' was not found.");
        if (!string.Equals(request.TaskUuid, taskUuid, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Command TaskUuid must match the route taskUuid.", nameof(request));
        if (request.Action == 0 && task.Status is "Accepted" or "Running" or "Completed" or "Observed" or "AwaitingCompletion")
            return ToResponse(task);
        if (task.Status == "Unknown")
            throw new InvalidOperationException("Task outcome is unknown; reconcile with ShineLab before sending another Command.");

        var previousStatus = task.Status;
        var previousStage = task.CurrentStage;
        await repository.MarkOperationStartedAsync(
            taskUuid,
            "Commanding",
            "Commanding",
            cancellationToken);
        ShineLabCommandResponse commandResponse;
        try
        {
            commandResponse = await commands.SendCommandAsync(task.EquipmentCode, request, cancellationToken, trackTask: true);
        }
        catch (ShineLabNotConnectedException exception)
        {
            await repository.RestoreAfterNotConnectedAsync(
                taskUuid,
                previousStatus,
                previousStage,
                exception.Message,
                cancellationToken);
            throw;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            await RecordUnknownAsync(taskUuid, "Command", "CommandOutcomeUnknown", exception, cancellationToken);
            throw;
        }
        catch (ShineLabCommandCanceledException exception)
        {
            await RecordUnknownAsync(taskUuid, "Command", "CommandOutcomeUnknown", exception, CancellationToken.None);
            throw;
        }
        task = journal is null ? await repository.MarkCommandResultAsync(taskUuid, request, commandResponse, cancellationToken) :
            await journal.ReconcileAsync(taskUuid, "Command", commandResponse.StrId, null, null, cancellationToken);
        return ToResponse(task);
    }

    public async Task<IReadOnlyList<ShineLabTaskResponse>> ListAsync(
        int limit,
        CancellationToken cancellationToken) =>
        (await repository.ListAsync(limit, cancellationToken)).Select(ToResponse).ToList();

    public async Task<ShineLabTaskDetailResponse?> GetAsync(
        string taskUuid,
        CancellationToken cancellationToken)
    {
        var task = await repository.GetAsync(taskUuid, cancellationToken);
        if (task is null) return null;
        var events = await repository.GetEventsAsync(taskUuid, cancellationToken);
        var samples = await repository.GetSamplesAsync(taskUuid, cancellationToken);
        return new(
            ToResponse(task),
            events.Select(item => new ShineLabTaskEventResponse(
                item.Id,
                item.TaskUuid,
                item.EventType,
                item.PayloadJson,
                item.OccurredAtUtc)).ToList(),
            samples.Select(item => new ShineLabTaskSampleResponse(item.RowNumber, item.SourceSampleId,
                item.SampleId, item.SampleName, item.ConfigAttempted, item.SampleFinishObserved,
                item.TaskFinishObserved, item.EndMissionObserved, item.ResultJson, item.ResultConflict,
                item.LastReceivedAtUtcTicks)).ToList(),
            ShineLabTaskRepository.GetResultState(samples));
    }

    public Task ApplyPushAsync(
        string strMethod,
        string equipmentCode,
        JsonElement body,
        CancellationToken cancellationToken,
        string? strId = null,
        ShineLabPushContext? context = null) =>
        repository.ApplyPushAsync(strMethod, equipmentCode, body, cancellationToken, strId, context);

    private Task<ShineLabTaskRecord> RecordUnknownAsync(string taskUuid, string method, string stage,
        Exception exception, CancellationToken token) => journal is null
        ? repository.MarkOutcomeUnknownAsync(taskUuid, stage, exception.Message, token)
        : journal.ReconcileAsync(taskUuid, method, exception switch
        {
            ShineLabCommandTimeoutException timeout => timeout.StrId,
            ShineLabCommandTransportException transport => transport.StrId,
            ShineLabCommandCanceledException canceled => canceled.StrId,
            _ => null
        }, stage, exception.Message, token);

    private static ShineLabTaskResponse ToResponse(ShineLabTaskRecord task) => new(
        task.Id,
        task.TaskUuid,
        task.EquipmentCode,
        task.Status,
        task.CurrentStage,
        task.CreatedAtUtc,
        task.UpdatedAtUtc,
        task.StartedAtUtc,
        task.CompletedAtUtc,
        task.LastError);
}
