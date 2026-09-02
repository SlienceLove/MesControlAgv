using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Entities;

namespace MesControlAgv.Mes.Services;

public sealed class ShineLabTaskService(
    ShineLabTaskRepository repository,
    ShineLabCommandService commands)
{
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
        var task = await repository.GetAsync(taskUuid, cancellationToken)
            ?? throw new KeyNotFoundException($"ShineLab task '{taskUuid}' was not found.");
        if (task.Status is "Configured" or "Accepted" or "Running" or "Completed")
            return ToResponse(task);
        if (task.Status == "Unknown")
            throw new InvalidOperationException("Task outcome is unknown; reconcile with ShineLab before retrying Config.");

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
            commandResponse = await commands.SendConfigAsync(task.EquipmentCode, request, cancellationToken);
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
            await repository.MarkOutcomeUnknownAsync(taskUuid, "ConfigOutcomeUnknown", exception.Message, cancellationToken);
            throw;
        }
        task = await repository.MarkConfigResultAsync(taskUuid, commandResponse, cancellationToken);
        return ToResponse(task);
    }

    public async Task<ShineLabTaskResponse> CommandAsync(
        string taskUuid,
        ShineLabCommandRequest request,
        CancellationToken cancellationToken)
    {
        var task = await repository.GetAsync(taskUuid, cancellationToken)
            ?? throw new KeyNotFoundException($"ShineLab task '{taskUuid}' was not found.");
        if (!string.Equals(request.TaskUuid, taskUuid, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Command TaskUuid must match the route taskUuid.", nameof(request));
        if (request.Action == 0 && task.Status is "Accepted" or "Running" or "Completed")
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
            commandResponse = await commands.SendCommandAsync(task.EquipmentCode, request, cancellationToken);
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
            await repository.MarkOutcomeUnknownAsync(taskUuid, "CommandOutcomeUnknown", exception.Message, cancellationToken);
            throw;
        }
        task = await repository.MarkCommandResultAsync(taskUuid, request, commandResponse, cancellationToken);
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
        return new(
            ToResponse(task),
            events.Select(item => new ShineLabTaskEventResponse(
                item.Id,
                item.TaskUuid,
                item.EventType,
                item.PayloadJson,
                item.OccurredAtUtc)).ToList());
    }

    public Task ApplyPushAsync(
        string strMethod,
        string equipmentCode,
        JsonElement body,
        CancellationToken cancellationToken) =>
        repository.ApplyPushAsync(strMethod, equipmentCode, body, cancellationToken);

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
