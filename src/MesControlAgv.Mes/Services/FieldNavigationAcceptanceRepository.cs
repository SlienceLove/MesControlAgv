using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

public sealed class FieldNavigationAcceptanceRepository(MesDbContext database)
{
    public MesDbContext Database => database;

    public async Task CreateAsync(FieldNavigationAcceptance acceptance, object auditDetails, CancellationToken cancellationToken)
    {
        database.FieldNavigationAcceptances.Add(acceptance);
        AddAudit(acceptance.Id, "Created", auditDetails);
        await database.SaveChangesAsync(cancellationToken);
    }

    public Task<FieldNavigationAcceptance?> GetAsync(Guid acceptanceId, CancellationToken cancellationToken) =>
        database.FieldNavigationAcceptances.SingleOrDefaultAsync(item => item.Id == acceptanceId, cancellationToken);

    public Task<FieldNavigationAcceptance?> GetByPermitIdAsync(string permitId, CancellationToken cancellationToken) =>
        database.FieldNavigationAcceptances.SingleOrDefaultAsync(item => item.PermitId == permitId, cancellationToken);

    public Task<FieldNavigationAcceptance?> GetByWorkflowNodeExecutionIdAsync(
        Guid nodeExecutionId,
        CancellationToken cancellationToken) =>
        database.FieldNavigationAcceptances.SingleOrDefaultAsync(
            item => item.WorkflowNodeExecutionId == nodeExecutionId,
            cancellationToken);

    public async Task<IReadOnlyList<FieldNavigationAcceptance>> ListForWorkflowRunAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        var records = await database.FieldNavigationAcceptances
            .AsNoTracking()
            .Where(item => item.WorkflowRunId == workflowRunId)
            .ToListAsync(cancellationToken);
        return records
            .OrderBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.Id)
            .ToArray();
    }

    public async Task LinkDeviceOperationAsync(
        FieldNavigationAcceptance acceptance,
        Guid workflowDeviceOperationId,
        CancellationToken cancellationToken)
    {
        if (acceptance.WorkflowDeviceOperationId is { } existing && existing != workflowDeviceOperationId)
        {
            throw new InvalidOperationException(
                "The field-navigation acceptance is already linked to another workflow device operation.");
        }

        acceptance.WorkflowDeviceOperationId = workflowDeviceOperationId;
        await SaveWithAuditAsync(acceptance, "WorkflowDeviceOperationLinked", new
        {
            acceptance.WorkflowRunId,
            acceptance.WorkflowNodeExecutionId,
            workflowDeviceOperationId
        }, cancellationToken);
    }

    public async Task<List<FieldNavigationAcceptanceAudit>> ListAuditsAsync(Guid acceptanceId, CancellationToken cancellationToken)
    {
        // SQLite cannot reliably translate ordering/comparison of DateTimeOffset.
        // Load the small audit set and order it in memory instead of returning
        // HTTP 500 from the acceptance detail endpoint.
        var audits = await database.FieldNavigationAcceptanceAudits
            .Where(item => item.AcceptanceId == acceptanceId)
            .ToListAsync(cancellationToken);
        return audits.OrderBy(item => item.OccurredAtUtc).ToList();
    }

    public async Task<List<FieldNavigationAcceptance>> ListInFlightAsync(CancellationToken cancellationToken)
    {
        var records = await database.FieldNavigationAcceptances
            .Where(item => item.Status == FieldNavigationAcceptanceStatuses.Authorized
                || item.Status == FieldNavigationAcceptanceStatuses.Dispatching
                || item.Status == FieldNavigationAcceptanceStatuses.Accepted
                || item.Status == FieldNavigationAcceptanceStatuses.Moving
                || item.Status == FieldNavigationAcceptanceStatuses.Unknown)
            .ToListAsync(cancellationToken);
        return records.OrderBy(item => item.UpdatedAtUtc).ToList();
    }

    public async Task SaveWithAuditAsync(
        FieldNavigationAcceptance acceptance,
        string eventType,
        object auditDetails,
        CancellationToken cancellationToken)
    {
        acceptance.UpdatedAtUtc = DateTimeOffset.UtcNow;
        AddAudit(acceptance.Id, eventType, auditDetails);
        await database.SaveChangesAsync(cancellationToken);
    }

    private void AddAudit(Guid acceptanceId, string eventType, object details) =>
        database.FieldNavigationAcceptanceAudits.Add(new FieldNavigationAcceptanceAudit
        {
            AcceptanceId = acceptanceId,
            EventType = eventType,
            DetailsJson = JsonSerializer.Serialize(details)
        });
}
