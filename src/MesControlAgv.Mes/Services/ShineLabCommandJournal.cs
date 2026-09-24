using System.Text.Json;
using System.Text.Json.Nodes;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

/// <summary>Durable request identity and reply history, independent of the HTTP wait window.</summary>
public sealed class ShineLabCommandJournal(IServiceScopeFactory scopes)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task PrepareAsync(ShineLabCommandAttempt attempt, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            if (attempt.TaskUuid is not null)
            {
                var task = await db.ShineLabTasks.SingleAsync(t => t.TaskUuid == attempt.TaskUuid, token);
                if (!task.EquipmentCode.Equals(attempt.EquipmentCode, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Command task equipment does not match; nothing sent.");
                if (attempt.Method == "Config")
                {
                    task.ConfigRequestId = attempt.StrId;
                    task.ConfigResponseJson = null;
                }
                else
                {
                    task.CommandRequestId = attempt.StrId;
                    task.CommandResponseJson = null;
                }
            }
            db.ShineLabCommandAttempts.Add(attempt);
            // Must finish before WriteAsync; otherwise a fast reply can outrun registration.
            await db.SaveChangesAsync(token);
        }
        finally { gate.Release(); }
    }

    public async Task RecordWaitAsync(string id, string outcome, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            var attempt = await db.ShineLabCommandAttempts.SingleAsync(a => a.StrId == id, token);
            if (outcome == "Sent") attempt.WriteCompletedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
            // Reply receipt and HTTP timeout are separate facts. Neither erases the other.
            attempt.WaitOutcome = outcome;
            await db.SaveChangesAsync(token);
        }
        finally { gate.Release(); }
    }

    public async Task RecordReplyAsync(string id, string method, string equipment, JsonElement body,
        ShineLabPushContext context, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            var repo = scope.ServiceProvider.GetRequiredService<ShineLabTaskRepository>();
            var attempt = await db.ShineLabCommandAttempts.SingleOrDefaultAsync(a => a.StrId == id, token);
            var correlation = attempt is null ? "UnknownRequestId" :
                !attempt.EquipmentCode.Equals(equipment, StringComparison.OrdinalIgnoreCase) ? "EquipmentMismatch" :
                attempt.Method != method ? "MethodMismatch" : "Matched";
            if (correlation == "Matched" && !TryResponse(id, method, equipment, body, out _))
                correlation = "MalformedReply";
            if (correlation == "Matched")
            {
                TryResponse(id, method, equipment, body, out var response);
                var responseJson = JsonSerializer.Serialize(response);
                if (attempt!.FirstResponseJson is null) attempt.FirstResponseJson = responseJson;
                else if (JsonNode.DeepEquals(JsonNode.Parse(attempt.FirstResponseJson), JsonNode.Parse(responseJson)))
                    correlation = "Duplicate";
                else { correlation = "ReplyConflict"; attempt.ResponseConflict = true; }
            }
            db.ShineLabCommandReplies.Add(new ShineLabCommandReplyEvidence
            {
                StrId = id, Method = method, EquipmentCode = equipment, RawJson = context.RawJson,
                ConnectionId = context.ConnectionId, ReceivedAtUtcTicks = context.ReceivedAtUtc.UtcTicks,
                TaskUuid = correlation is "Matched" or "Duplicate" or "ReplyConflict" ? attempt?.TaskUuid : null,
                Correlation = correlation
            });
            await db.SaveChangesAsync(token);
            if (attempt is not null && correlation is "Matched" or "Duplicate" or "ReplyConflict")
                await ApplyStoredAsync(repo, attempt, token);
        }
        finally { gate.Release(); }
    }

    public async Task<ShineLabTaskRecord> ReconcileAsync(string taskUuid, string method,
        string? requestId, string? unknownStage, string? error, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            var repo = scope.ServiceProvider.GetRequiredService<ShineLabTaskRepository>();
            var task = await repo.GetAsync(taskUuid, token) ?? throw new KeyNotFoundException(taskUuid);
            var activeId = method == "Config" ? task.ConfigRequestId : task.CommandRequestId;
            if (requestId is not null && activeId != requestId) return task; // Old waiter, newer operation.
            var attempt = activeId is null ? null : await db.ShineLabCommandAttempts.SingleOrDefaultAsync(a => a.StrId == activeId, token);
            if (attempt?.FirstResponseJson is not null)
                return await ApplyStoredAsync(repo, attempt, token) ?? task;
            if (unknownStage is not null)
                return await repo.MarkOutcomeUnknownAsync(taskUuid, unknownStage, error ?? "No matching reply received.", token);
            return task;
        }
        finally { gate.Release(); }
    }

    public async Task RecoverUnappliedRepliesAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            var repo = scope.ServiceProvider.GetRequiredService<ShineLabTaskRepository>();
            // Close only the crash gap between durable RX and its task update.
            // Do not replay previously applied acknowledgements to guess whether
            // an instrument kept running while MES was offline.
            var attempts = await db.ShineLabCommandAttempts.Where(a => a.TaskUuid != null && a.FirstResponseJson != null &&
                db.ShineLabTasks.Any(t => t.TaskUuid == a.TaskUuid &&
                    (a.Method == "Config" && t.ConfigRequestId == a.StrId && t.ConfigResponseJson == null ||
                     a.Method == "Command" && t.CommandRequestId == a.StrId && t.CommandResponseJson == null)))
                .ToListAsync(token);
            foreach (var attempt in attempts) await ApplyStoredAsync(repo, attempt, token);
        }
        finally { gate.Release(); }
    }

    private static async Task<ShineLabTaskRecord?> ApplyStoredAsync(ShineLabTaskRepository repo,
        ShineLabCommandAttempt attempt, CancellationToken token)
    {
        if (attempt.TaskUuid is null) return null;
        var task = await repo.GetAsync(attempt.TaskUuid, token);
        if (task is null) return null;
        var activeId = attempt.Method == "Config" ? task.ConfigRequestId : task.CommandRequestId;
        if (activeId != attempt.StrId) return task; // Historical reply must not regress newer work.
        if (attempt.ResponseConflict)
        {
            if (task.CurrentStage == "AcknowledgementConflictNeedsReview") return task;
            return await repo.MarkOutcomeUnknownAsync(task.TaskUuid, "AcknowledgementConflictNeedsReview",
                "Conflicting replies for one request; original replies retained, no automatic resend.", token);
        }
        if (attempt.FirstResponseJson is null) return task;
        var response = JsonSerializer.Deserialize<ShineLabCommandResponse>(attempt.FirstResponseJson)!;
        return attempt.Method == "Config"
            ? await repo.MarkConfigResultAsync(task.TaskUuid, response, token)
            : await repo.MarkCommandResultAsync(task.TaskUuid,
                new ShineLabCommandRequest(task.TaskUuid, attempt.Action ?? -1), response, token);
    }

    public static bool TryResponse(string id, string method, string equipment, JsonElement body,
        out ShineLabCommandResponse? response)
    {
        response = null;
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.String) return false;
        var value = result.GetString();
        if (!string.Equals(value, "Success", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(value, "Failed", StringComparison.OrdinalIgnoreCase)) return false;
        var message = body.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.String ? msg.GetString() : null;
        response = new(id, method, equipment, string.Equals(value, "Success", StringComparison.OrdinalIgnoreCase), message, body.Clone());
        return true;
    }
}
