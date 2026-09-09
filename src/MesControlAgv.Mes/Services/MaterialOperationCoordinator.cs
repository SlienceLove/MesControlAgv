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
    public async Task<MaterialOperationRecord> GetOrAddAsync(
        MesDbContext database,
        Guid requestId,
        string operationKind,
        string fingerprint,
        string actor,
        CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("A request id is required.", nameof(requestId));
        if (string.IsNullOrWhiteSpace(operationKind))
            throw new ArgumentException("An operation kind is required.", nameof(operationKind));
        if (string.IsNullOrWhiteSpace(fingerprint))
            throw new ArgumentException("A request fingerprint is required.", nameof(fingerprint));
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("An actor is required.", nameof(actor));

        var existing = await database.MaterialOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(operation => operation.RequestId == requestId, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.OperationKind, operationKind, StringComparison.Ordinal) ||
                !string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
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
}

public sealed class MaterialRequestIdReusedException : InvalidOperationException
{
    public MaterialRequestIdReusedException(Guid requestId)
        : base($"Material request id '{requestId}' was already used with a different operation or payload.")
    {
        RequestId = requestId;
    }

    public Guid RequestId { get; }
}
