using MesControlAgv.Mes.Data;
using MesControlAgv.Reporting;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

public sealed class LabReportService(MesDbContext db)
{
    public async Task<ReportSnapshot> ReadAsync(ReportQuery query, CancellationToken token)
    {
        ReportRules.ValidateQuery(query);
        var from = query.From.UtcDateTime; var to = query.To.UtcDateTime;
        var rows = db.ExperimentJobs.AsNoTracking().Where(j => j.CreatedAtUtc >= from && j.CreatedAtUtc < to);
        if (!string.IsNullOrWhiteSpace(query.Batch)) rows = rows.Where(j => j.SampleBatchId == query.Batch);
        var jobs = await rows.OrderBy(j => j.CreatedAtUtc).ThenBy(j => j.JobId)
            .Take(ReportRules.MaxRows + 1).ToArrayAsync(token);
        if (jobs.Length > ReportRules.MaxRows) throw new ArgumentException("任务超过 10,000 条，请缩小时间或批次范围。");
        static DateTimeOffset Utc(DateTime date) => new(DateTime.SpecifyKind(date, DateTimeKind.Utc));
        return new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, query,
            jobs.Select(j => new ReportJob(j.JobId.ToString(), j.SampleId ?? "", j.SampleBatchId, j.Status,
                Utc(j.CreatedAtUtc), j.StartedAtUtc.HasValue ? Utc(j.StartedAtUtc.Value) : null,
                j.CompletedAtUtc.HasValue ? Utc(j.CompletedAtUtc.Value) : null, j.LastError)).ToArray(), [],
            ["MES 实验任务快照；未自动采集仪器测量值，测量结果请通过标准模板导入。"]);
    }
}
