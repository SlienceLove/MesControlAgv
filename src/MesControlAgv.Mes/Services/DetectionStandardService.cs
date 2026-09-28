using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MesControlAgv.Contracts.Materials;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

public sealed class DetectionStandardService(MesDbContext database)
{
    public async Task<IReadOnlyList<DetectionStandardRevision>> ListAsync(
        string? search, bool includeHistory, CancellationToken cancellationToken)
    {
        var query = database.DetectionStandardRevisions.AsNoTracking().Include(item => item.Indicators).AsQueryable();
        if (!includeHistory) query = query.Where(item => item.IsActive);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var upper = term.ToUpperInvariant();
            query = query.Where(item => item.MethodCode.Contains(upper) || item.Name.ToUpper().Contains(upper));
        }
        var records = await query.OrderBy(item => item.MethodCode).ThenByDescending(item => item.Revision)
            .ToListAsync(cancellationToken);
        return records.Select(ToContract).ToList();
    }

    public async Task<DetectionStandardRevision?> GetAsync(
        string methodCode, int? revision, CancellationToken cancellationToken)
    {
        var code = NormalizeCode(methodCode);
        var query = database.DetectionStandardRevisions.AsNoTracking().Include(item => item.Indicators)
            .Where(item => item.MethodCode == code);
        query = revision.HasValue
            ? query.Where(item => item.Revision == revision.Value)
            : query.Where(item => item.IsActive);
        var record = await query.SingleOrDefaultAsync(cancellationToken);
        return record is null ? null : ToContract(record);
    }

    public async Task<DetectionStandardRevision> SaveAsync(
        SaveDetectionStandardRequest request, CancellationToken cancellationToken)
    {
        var code = NormalizeCode(request.MethodCode);
        var name = Require(request.Name, "标准名称", 256);
        var actor = Require(request.Actor, "操作人", 256);
        var category = Optional(request.Category, 128);
        if (request.Indicators is null || request.Indicators.Count == 0)
            throw new ArgumentException("至少需要一个检测指标。");
        if (request.Indicators.Count > 100) throw new ArgumentException("单个方法最多配置 100 个指标。");

        var normalized = request.Indicators.Select(item => new DetectionIndicatorRule
        {
            IndicatorId = Guid.NewGuid(),
            Name = Require(item.Name, "指标名称", 128),
            Unit = Require(item.Unit, "指标单位", 64),
            DetectionLimit = item.DetectionLimit,
            LowerLimit = item.LowerLimit,
            UpperLimit = item.UpperLimit
        }).ToList();
        if (normalized.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Count)
            throw new ArgumentException("同一方法中的指标名称不能重复。");
        if (normalized.Any(item => item.DetectionLimit < 0 ||
            item.LowerLimit.HasValue && item.UpperLimit.HasValue && item.LowerLimit > item.UpperLimit))
            throw new ArgumentException("检出限不能为负，合格下限不能大于上限。");

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var previous = await database.DetectionStandardRevisions
            .Where(item => item.MethodCode == code)
            .OrderByDescending(item => item.Revision)
            .FirstOrDefaultAsync(cancellationToken);
        if (previous is { IsActive: true } && request.BaseRevisionId != previous.RevisionId)
            throw new InvalidOperationException("标准已被其他用户更新，请刷新后从当前修订重新编辑。");
        if (previous is not { IsActive: true } && request.BaseRevisionId.HasValue)
            throw new InvalidOperationException("基础修订已失效，请刷新标准列表。");
        if (previous is { IsActive: true })
        {
            previous.IsActive = false;
            await database.SaveChangesAsync(cancellationToken);
        }

        var revision = new DetectionStandardRevisionRecord
        {
            RevisionId = Guid.NewGuid(),
            MethodCode = code,
            Name = name,
            Category = category,
            Revision = (previous?.Revision ?? 0) + 1,
            IsActive = true,
            CreatedBy = actor,
            CreatedAtUtc = DateTime.UtcNow,
            Indicators = normalized.Select(item => new DetectionIndicatorRuleRecord
            {
                IndicatorId = item.IndicatorId,
                Name = item.Name,
                Unit = item.Unit,
                DetectionLimit = item.DetectionLimit,
                LowerLimit = item.LowerLimit,
                UpperLimit = item.UpperLimit
            }).ToList()
        };
        database.DetectionStandardRevisions.Add(revision);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToContract(revision);
    }

    public async Task<DetectionStandardRevision> DeactivateAsync(
        string methodCode, Guid revisionId, CancellationToken cancellationToken)
    {
        var code = NormalizeCode(methodCode);
        if (revisionId == Guid.Empty) throw new ArgumentException("修订 ID 不能为空。");
        var current = await database.DetectionStandardRevisions.AsNoTracking().Include(item => item.Indicators)
            .SingleOrDefaultAsync(item => item.MethodCode == code && item.RevisionId == revisionId, cancellationToken)
            ?? throw new KeyNotFoundException("检测标准修订不存在。");
        var changed = await database.DetectionStandardRevisions
            .Where(item => item.MethodCode == code && item.RevisionId == revisionId && item.IsActive)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false), cancellationToken);
        if (changed != 1) throw new InvalidOperationException("标准已被其他用户更新，请刷新后重试。");
        current.IsActive = false;
        return ToContract(current);
    }

    public async Task<DetectionAssessment> PreviewAsync(
        DetectionAssessmentRequest request, CancellationToken cancellationToken)
    {
        var (sample, revision, items) = await PrepareAsync(request, cancellationToken);
        return CreateAssessment(request, sample, revision, items);
    }

    public async Task<DetectionAssessment> AssessAsync(
        DetectionAssessmentRequest request, CancellationToken cancellationToken)
    {
        if (request.RequestId == Guid.Empty) throw new ArgumentException("请求 ID 不能为空。");
        var fingerprint = Fingerprint(request);
        var existing = await database.DetectionAssessments.AsNoTracking().Include(item => item.Items)
            .SingleOrDefaultAsync(item => item.RequestId == request.RequestId, cancellationToken);
        if (existing is not null)
        {
            if (existing.Fingerprint != fingerprint) throw new InvalidOperationException("请求 ID 已用于不同的检测记录。");
            return ToContract(existing);
        }

        var (sample, revision, items) = await PrepareAsync(request, cancellationToken);
        var assessment = CreateAssessment(request, sample, revision, items);
        database.DetectionAssessments.Add(new DetectionAssessmentRecord
        {
            RequestId = request.RequestId,
            Fingerprint = fingerprint,
            SampleId = sample.SampleId,
            SampleBarcode = sample.Barcode,
            ExperimentJobId = sample.BoundExperimentJobId,
            RevisionId = revision.RevisionId,
            MethodCode = revision.MethodCode,
            Revision = revision.Revision,
            Actor = assessment.Actor,
            AssessedAtUtc = assessment.AssessedAt.UtcDateTime,
            Verdict = assessment.Verdict.ToString(),
            Items = items.Select(item => new DetectionAssessmentItemRecord
            {
                Id = Guid.NewGuid(),
                IndicatorId = item.IndicatorId,
                Name = item.Name,
                Value = item.Value,
                Unit = item.Unit,
                DetectionLimit = item.DetectionLimit,
                LowerLimit = item.LowerLimit,
                UpperLimit = item.UpperLimit,
                Verdict = item.Verdict.ToString(),
                Reason = item.Reason
            }).ToList()
        });
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            var concurrent = await database.DetectionAssessments.AsNoTracking().Include(item => item.Items)
                .SingleOrDefaultAsync(item => item.RequestId == request.RequestId, cancellationToken);
            if (concurrent is null) throw;
            if (concurrent.Fingerprint != fingerprint)
                throw new InvalidOperationException("请求 ID 已用于不同的检测记录。");
            return ToContract(concurrent);
        }
        return assessment;
    }

    public async Task<IReadOnlyList<DetectionAssessment>> HistoryAsync(
        Guid sampleId, CancellationToken cancellationToken)
    {
        if (!await database.SampleMaterials.AnyAsync(item => item.SampleId == sampleId, cancellationToken))
            throw new KeyNotFoundException("样品不存在。");
        var records = await database.DetectionAssessments.AsNoTracking().Include(item => item.Items)
            .Where(item => item.SampleId == sampleId)
            .OrderByDescending(item => item.AssessedAtUtc)
            .ToListAsync(cancellationToken);
        return records.Select(ToContract).ToList();
    }

    public static DetectionAssessmentItem Evaluate(DetectionIndicatorRule rule, DetectionMeasurement measurement)
    {
        if (!string.Equals(rule.Unit, measurement.Unit?.Trim(), StringComparison.Ordinal))
            throw new ArgumentException($"指标“{rule.Name}”的单位必须为 {rule.Unit}。");
        var belowDetectionLimit = rule.DetectionLimit.HasValue && measurement.Value < rule.DetectionLimit.Value;
        var verdict = belowDetectionLimit || !rule.LowerLimit.HasValue && !rule.UpperLimit.HasValue
            ? DetectionVerdict.Indeterminate
            : rule.LowerLimit.HasValue && measurement.Value < rule.LowerLimit.Value ||
              rule.UpperLimit.HasValue && measurement.Value > rule.UpperLimit.Value
                ? DetectionVerdict.Fail
                : DetectionVerdict.Pass;
        return new DetectionAssessmentItem
        {
            IndicatorId = rule.IndicatorId,
            Name = rule.Name,
            Value = measurement.Value,
            Unit = rule.Unit,
            DetectionLimit = rule.DetectionLimit,
            LowerLimit = rule.LowerLimit,
            UpperLimit = rule.UpperLimit,
            Verdict = verdict,
            Reason = belowDetectionLimit ? "低于检出限" :
                !rule.LowerLimit.HasValue && !rule.UpperLimit.HasValue ? "未配置合格限值" :
                verdict == DetectionVerdict.Fail ? "超出合格范围" : "符合合格范围"
        };
    }

    private async Task<(SampleMaterialRecord Sample, DetectionStandardRevisionRecord Revision,
        List<DetectionAssessmentItem> Items)> PrepareAsync(
        DetectionAssessmentRequest request, CancellationToken cancellationToken)
    {
        var actor = Require(request.Actor, "操作人", 256);
        _ = actor;
        var sample = await database.SampleMaterials.AsNoTracking()
            .SingleOrDefaultAsync(item => item.SampleId == request.SampleId, cancellationToken)
            ?? throw new KeyNotFoundException("样品不存在。");
        var revision = await database.DetectionStandardRevisions.AsNoTracking().Include(item => item.Indicators)
            .SingleOrDefaultAsync(item => item.RevisionId == request.RevisionId, cancellationToken)
            ?? throw new KeyNotFoundException("检测标准修订不存在。");
        var batchMethod = await database.SampleBatchDetectionMethods.AsNoTracking()
            .SingleOrDefaultAsync(item => item.SampleBatchId == sample.SampleBatchId, cancellationToken);
        if (batchMethod is not null && batchMethod.MethodCode != revision.MethodCode)
            throw new InvalidOperationException("样品批次已绑定其他检测方法。");
        if (!revision.IsActive) throw new InvalidOperationException("检测标准修订已停用，请重新选择当前版本。");
        if (request.Measurements is null || request.Measurements.Count is 0 or > 100)
            throw new ArgumentException("至少选择一个、至多 100 个指标。");

        var rules = revision.Indicators.ToDictionary(item => item.IndicatorId);
        var seen = new HashSet<Guid>();
        var items = new List<DetectionAssessmentItem>(request.Measurements.Count);
        foreach (var measurement in request.Measurements)
        {
            if (!seen.Add(measurement.IndicatorId)) throw new ArgumentException("同一指标不能重复提交。");
            if (!rules.TryGetValue(measurement.IndicatorId, out var record))
                throw new ArgumentException("选中的指标不属于该检测标准修订。");
            items.Add(Evaluate(ToContract(record), measurement));
        }
        return (sample, revision, items);
    }

    private static DetectionAssessment CreateAssessment(
        DetectionAssessmentRequest request,
        SampleMaterialRecord sample,
        DetectionStandardRevisionRecord revision,
        IReadOnlyList<DetectionAssessmentItem> items) => new()
    {
        RequestId = request.RequestId,
        SampleId = sample.SampleId,
        SampleBarcode = sample.Barcode,
        ExperimentJobId = sample.BoundExperimentJobId,
        RevisionId = revision.RevisionId,
        MethodCode = revision.MethodCode,
        Revision = revision.Revision,
        Actor = request.Actor.Trim(),
        AssessedAt = DateTimeOffset.UtcNow,
        Verdict = items.Any(item => item.Verdict == DetectionVerdict.Fail) ? DetectionVerdict.Fail :
            items.All(item => item.Verdict == DetectionVerdict.Pass) ? DetectionVerdict.Pass : DetectionVerdict.Indeterminate,
        Items = items
    };

    private static string Fingerprint(DetectionAssessmentRequest request)
    {
        var content = JsonSerializer.Serialize(new
        {
            request.SampleId,
            request.RevisionId,
            Actor = request.Actor.Trim(),
            Measurements = (request.Measurements ?? []).OrderBy(item => item.IndicatorId)
                .Select(item => new { item.IndicatorId, item.Value, Unit = item.Unit?.Trim() ?? string.Empty })
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private static DetectionStandardRevision ToContract(DetectionStandardRevisionRecord item) => new()
    {
        RevisionId = item.RevisionId,
        MethodCode = item.MethodCode,
        Name = item.Name,
        Category = item.Category,
        Revision = item.Revision,
        IsActive = item.IsActive,
        CreatedBy = item.CreatedBy,
        CreatedAt = new DateTimeOffset(item.CreatedAtUtc, TimeSpan.Zero),
        Indicators = item.Indicators.Select(ToContract).OrderBy(rule => rule.Name).ToList()
    };

    private static DetectionIndicatorRule ToContract(DetectionIndicatorRuleRecord item) => new()
    {
        IndicatorId = item.IndicatorId,
        Name = item.Name,
        Unit = item.Unit,
        DetectionLimit = item.DetectionLimit,
        LowerLimit = item.LowerLimit,
        UpperLimit = item.UpperLimit
    };

    private static DetectionAssessment ToContract(DetectionAssessmentRecord item) => new()
    {
        RequestId = item.RequestId,
        SampleId = item.SampleId,
        SampleBarcode = item.SampleBarcode,
        ExperimentJobId = item.ExperimentJobId,
        RevisionId = item.RevisionId,
        MethodCode = item.MethodCode,
        Revision = item.Revision,
        Actor = item.Actor,
        AssessedAt = new DateTimeOffset(item.AssessedAtUtc, TimeSpan.Zero),
        Verdict = Enum.Parse<DetectionVerdict>(item.Verdict),
        Items = item.Items.Select(value => new DetectionAssessmentItem
        {
            IndicatorId = value.IndicatorId,
            Name = value.Name,
            Value = value.Value,
            Unit = value.Unit,
            DetectionLimit = value.DetectionLimit,
            LowerLimit = value.LowerLimit,
            UpperLimit = value.UpperLimit,
            Verdict = Enum.Parse<DetectionVerdict>(value.Verdict),
            Reason = value.Reason
        }).OrderBy(value => value.Name).ToList()
    };

    private static string NormalizeCode(string code) => Require(code, "方法编号", 128).ToUpperInvariant();

    private static string Require(string? value, string label, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 || normalized.Length > maxLength)
            throw new ArgumentException($"{label}不能为空且不能超过 {maxLength} 个字符。");
        return normalized;
    }

    private static string? Optional(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (normalized?.Length > maxLength) throw new ArgumentException($"分类不能超过 {maxLength} 个字符。");
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
