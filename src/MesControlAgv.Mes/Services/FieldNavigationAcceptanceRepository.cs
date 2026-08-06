using System.Text.Json;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

public sealed class FieldNavigationAcceptanceRepository(MesDbContext database)
{
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

    public Task<List<FieldNavigationAcceptanceAudit>> ListAuditsAsync(Guid acceptanceId, CancellationToken cancellationToken) =>
        database.FieldNavigationAcceptanceAudits
            .Where(item => item.AcceptanceId == acceptanceId)
            .OrderBy(item => item.OccurredAtUtc)
            .ToListAsync(cancellationToken);

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
