using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Registers a material write request before its line-level inventory records
/// are written. The request fingerprint is the idempotency boundary; line keys
/// only distinguish the individual rows produced by one accepted request.
/// </summary>
public sealed class MaterialOperationCoordinator
{
    public async Task<MaterialOperationExecution<T>> ExecuteAsync<T>(
        MesDbContext database,
        Guid requestId,
        string operationKind,
        string fingerprint,
        string actor,
        Func<Task<T>> mutation,
        Func<T, string> serialize,
        Func<string, T> deserialize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(serialize);
        ArgumentNullException.ThrowIfNull(deserialize);
        Validate(requestId, operationKind, fingerprint, actor);
        operationKind = operationKind.Trim();
        fingerprint = fingerprint.Trim();

        var ownsTransaction = database.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await database.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var existing = await database.MaterialOperations
            .SingleOrDefaultAsync(operation => operation.RequestId == requestId, cancellationToken);
        if (existing is not null)
        {
            EnsureSameRequest(existing, operationKind, fingerprint, requestId);
            if (!string.Equals(existing.Outcome, "Completed", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Material request '{requestId}' is not replayable because its outcome is '{existing.Outcome}'.");
            }

            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);
            return new MaterialOperationExecution<T>(
                deserialize(existing.ResultJson),
                IsReplay: true);
        }

        var operation = new MaterialOperationRecord
        {
            RequestId = requestId,
            OperationKind = operationKind.Trim(),
            Fingerprint = fingerprint.Trim(),
            Outcome = "Pending",
            ResultJson = "{}",
            Actor = actor.Trim(),
            CreatedAtUtc = DateTime.UtcNow
        };
        database.MaterialOperations.Add(operation);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (!ownsTransaction)
                throw;
            database.ChangeTracker.Clear();
            await transaction!.RollbackAsync(cancellationToken);
            await using var replayTransaction =
                await database.Database.BeginTransactionAsync(cancellationToken);
            var concurrent = await database.MaterialOperations
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.RequestId == requestId, cancellationToken);
            if (concurrent is null) throw;
            EnsureSameRequest(concurrent, operationKind, fingerprint, requestId);
            if (!string.Equals(concurrent.Outcome, "Completed", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Material request '{requestId}' is not replayable because its outcome is '{concurrent.Outcome}'.");
            }

            await replayTransaction.RollbackAsync(cancellationToken);
            return new MaterialOperationExecution<T>(
                deserialize(concurrent.ResultJson),
                IsReplay: true);
        }

        try
        {
            var result = await mutation();
            operation.Outcome = "Completed";
            operation.ResultJson = serialize(result);
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return new MaterialOperationExecution<T>(result, IsReplay: false);
        }
        catch
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                finally
                {
                    // Do not leave Added/Modified entities from a rolled-back
                    // request in a scoped context that may be reused by a caller.
                    database.ChangeTracker.Clear();
                }
            }

            throw;
        }
    }

    public async Task<MaterialOperationRecord> GetOrAddAsync(
        MesDbContext database,
        Guid requestId,
        string operationKind,
        string fingerprint,
        string actor,
        CancellationToken cancellationToken)
    {
        Validate(requestId, operationKind, fingerprint, actor);
        operationKind = operationKind.Trim();
        fingerprint = fingerprint.Trim();

        var existing = await database.MaterialOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(operation => operation.RequestId == requestId, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.OperationKind?.Trim(), operationKind, StringComparison.Ordinal) ||
                !string.Equals(existing.Fingerprint?.Trim(), fingerprint, StringComparison.Ordinal))
            {
                throw new MaterialRequestIdReusedException(requestId);
            }

            return existing;
        }

        var operation = new MaterialOperationRecord
        {
            RequestId = requestId,
            OperationKind = operationKind.Trim(),
            Fingerprint = fingerprint.Trim(),
            Outcome = "Pending",
            ResultJson = "{}",
            Actor = actor.Trim(),
            CreatedAtUtc = DateTime.UtcNow
        };
        database.MaterialOperations.Add(operation);
        return operation;
    }

    private static void Validate(
        Guid requestId,
        string operationKind,
        string fingerprint,
        string actor)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("A request id is required.", nameof(requestId));
        if (string.IsNullOrWhiteSpace(operationKind))
            throw new ArgumentException("An operation kind is required.", nameof(operationKind));
        if (string.IsNullOrWhiteSpace(fingerprint))
            throw new ArgumentException("A request fingerprint is required.", nameof(fingerprint));
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("An actor is required.", nameof(actor));
    }

    private static void EnsureSameRequest(
        MaterialOperationRecord existing,
        string operationKind,
        string fingerprint,
        Guid requestId)
    {
        if (!string.Equals(existing.OperationKind?.Trim(), operationKind.Trim(), StringComparison.Ordinal) ||
            !string.Equals(existing.Fingerprint?.Trim(), fingerprint.Trim(), StringComparison.Ordinal))
        {
            throw new MaterialRequestIdReusedException(requestId);
        }
    }
}

public sealed record MaterialOperationExecution<T>(T Value, bool IsReplay);

public sealed class MaterialRequestIdReusedException : InvalidOperationException
{
    public MaterialRequestIdReusedException(Guid requestId)
        : base($"Material request id '{requestId}' was already used with a different operation or payload.")
    {
        RequestId = requestId;
    }

    public Guid RequestId { get; }
}
