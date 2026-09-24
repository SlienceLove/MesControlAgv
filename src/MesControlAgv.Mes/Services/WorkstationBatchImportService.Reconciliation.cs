using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MesControlAgv.Contracts;

namespace MesControlAgv.Mes.Services;

public sealed partial class WorkstationBatchImportService
{
    public const string CreatedWithReadbackException = "CreatedWithReadbackException";
    private string ReconciliationDirectory => Path.Combine(JournalDirectory, "reconciliations");
    private static bool NeedsReview(WorkstationBatchItem item) => item.Status is "Unknown" or "Importing";

    public async Task<IReadOnlyList<WorkstationBatchReviewItem>> GetReviewItemsAsync(string deviceId, CancellationToken token)
    {
        ValidateReviewDevice(deviceId);
        await Gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(JournalDirectory);
            using var journalLock = OpenReviewLock();
            var result = new List<WorkstationBatchReviewItem>();
            foreach (var file in Directory.EnumerateFiles(JournalDirectory, "*.json").Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var (batch, hash) = ReadJournalSnapshot(file);
                if (batch.DeviceId != deviceId) continue;
                foreach (var item in batch.Items.Where(NeedsReview))
                    result.Add(new(batch.RequestId, batch.DeviceId, item.TaskNo, item.TaskName,
                        item.Status, item.Message, batch.PayloadHash, hash, ReadReconciliation(batch, item, hash)));
            }
            return result;
        }
        finally { Gate.Release(); }
    }

    public async Task<WorkstationBatchReconciliation> ReconcileCreatedAsync(string deviceId, Guid requestId,
        WorkstationBatchReconcileRequest request, CancellationToken token)
    {
        ValidateReviewDevice(deviceId);
        if (requestId == Guid.Empty) throw new ArgumentException("需要原始批次请求编号。");
        request = NormalizeConfirmation(request);
        await Gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(JournalDirectory);
            using var journalLock = OpenReviewLock();
            var path = Path.Combine(JournalDirectory, requestId.ToString("N") + ".json");
            if (!File.Exists(path)) throw new KeyNotFoundException("未找到原始导入记录；不能仅凭任务编号解除阻断。");
            var (batch, journalHash) = ReadJournalSnapshot(path);
            if (batch.DeviceId != deviceId || batch.PayloadHash != request.ExpectedPayloadHash || journalHash != request.ExpectedJournalSha256)
                throw new InvalidOperationException("原记录身份或内容已变化，请刷新后重新核对。");
            var items = batch.Items.Where(i => i.TaskNo == request.TaskNo).ToArray();
            if (items.Length != 1 || !NeedsReview(items[0]))
                throw new InvalidOperationException("只能人工核对原记录中的单条Unknown/Importing任务。");
            var item = items[0];
            var existing = ReadReconciliation(batch, item, journalHash);
            if (existing is not null)
            {
                if (existing.Confirmation != request) throw new InvalidOperationException("此任务已有人工结案记录，不可覆盖；请刷新核对。");
                return existing; // Same confirmation retry: no device reads/writes and no audit overwrite.
            }
            if (Directory.Exists(ReconciliationDirectory))
                foreach (var file in Directory.EnumerateFiles(ReconciliationDirectory, "*.json"))
                    if (ReadAuditFile(file).Confirmation.ConfirmationId == request.ConfirmationId)
                        throw new InvalidOperationException("确认操作号已用于其他核对记录。");

            var status = await reader.GetStatusAsync(deviceId, token);
            if (!IsFreshIdle(status, deviceId, DateTimeOffset.UtcNow))
                throw new InvalidOperationException("人工核对须取得工作站当前在线空闲状态；未解除阻断。");
            var task = await reader.GetTaskDetailsAsync(deviceId, item.TaskNo, token);
            if (task.TaskNo != item.TaskNo || task.TaskName != item.TaskName || task.State != SampleWorkstationTaskState.Waiting)
                throw new InvalidOperationException("设备中的任务编号、名称或等待状态不匹配；未解除阻断。");
            status = await reader.GetStatusAsync(deviceId, token);
            var confirmedAt = DateTimeOffset.UtcNow;
            if (!IsFreshIdle(status, deviceId, confirmedAt))
                throw new InvalidOperationException("查询任务期间设备状态变化或已过期，未解除阻断。");
            // The operator attests the transfer details; this is NOT automatic workbook verification.
            var audit = new WorkstationBatchReconciliation(batch.RequestId, deviceId, item.TaskNo, item.TaskName,
                item.Status, item.Message, batch.PayloadHash, journalHash, request, task, status,
                confirmedAt, CreatedWithReadbackException);
            token.ThrowIfCancellationRequested();
            if (ReadJournalSnapshot(path).Hash != journalHash) throw new InvalidOperationException("核对过程中原记录发生变化，未解除阻断。");
            SaveReconciliation(audit);
            return audit;
        }
        finally { Gate.Release(); }
    }

    private FileStream OpenReviewLock() => new(Path.Combine(JournalDirectory, ".lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static void ValidateReviewDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 128) throw new ArgumentException("需要有效设备编号。");
    }

    private static WorkstationBatchReconcileRequest NormalizeConfirmation(WorkstationBatchReconcileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ConfirmationId == Guid.Empty || string.IsNullOrWhiteSpace(request.TaskNo) || request.TaskNo.Length > 128 ||
            !IsHash(request.ExpectedPayloadHash) || !IsHash(request.ExpectedJournalSha256) ||
            string.IsNullOrWhiteSpace(request.Actor) || request.Actor.Length > 128 ||
            string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 1000 ||
            !request.ConfirmTaskCreated || !request.ConfirmTransfersChecked)
            throw new ArgumentException("请提供有效操作号、原记录指纹、操作人、核对原因，并明确确认任务已创建及分液明细已检查。");
        return request with { Actor = request.Actor.Trim(), Reason = request.Reason.Trim(),
            ExpectedPayloadHash = request.ExpectedPayloadHash.ToUpperInvariant(), ExpectedJournalSha256 = request.ExpectedJournalSha256.ToUpperInvariant() };
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsFreshIdle(SampleWorkstationStatusResponse? status, string deviceId, DateTimeOffset at) =>
        status is not null && status.DeviceId == deviceId && status.Online && status.State == SampleWorkstationDeviceState.Idle &&
        status.ObservedAtUtc >= at.AddMinutes(-1) && status.ObservedAtUtc <= at.AddSeconds(10);

    private static (WorkstationBatchResult Batch, string Hash) ReadJournalSnapshot(string path)
    {
        var bytes = File.ReadAllBytes(path);
        WorkstationBatchResult batch;
        try { batch = JsonSerializer.Deserialize<WorkstationBatchResult>(bytes)
            ?? throw new InvalidOperationException("原导入日志无法读取，禁止解除阻断。"); }
        catch (JsonException ex) { throw new InvalidOperationException("原导入日志损坏，禁止解除阻断。", ex); }
        if (batch.RequestId == Guid.Empty || Path.GetFileName(path) != batch.RequestId.ToString("N") + ".json" ||
            string.IsNullOrWhiteSpace(batch.DeviceId) || batch.DeviceId.Length > 128 ||
            batch.Items is null || batch.Items.Count is < 1 or > 100 || !IsHash(batch.PayloadHash) ||
            batch.Items.Any(item => item is null || string.IsNullOrWhiteSpace(item.TaskNo) || string.IsNullOrWhiteSpace(item.TaskName) ||
                item.Status is not ("Pending" or "Importing" or "Unknown" or "Imported" or "NotImported")) ||
            batch.Items.Select(item => item.TaskNo).Distinct(StringComparer.OrdinalIgnoreCase).Count() != batch.Items.Count)
            throw new InvalidOperationException("原导入日志身份无效，禁止解除阻断。");
        return (batch, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private string ReconciliationPath(Guid requestId, string taskNo) => Path.Combine(ReconciliationDirectory,
        requestId.ToString("N") + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(taskNo))) + ".json");

    private static WorkstationBatchReconciliation ReadAuditFile(string path)
    {
        try
        {
            var audit = JsonSerializer.Deserialize<WorkstationBatchReconciliation>(File.ReadAllBytes(path))
                ?? throw new InvalidOperationException("人工核对审计为空，禁止解除阻断。");
            if (audit.RequestId == Guid.Empty || audit.Confirmation is null || audit.DeviceTask is null || audit.DeviceStatus is null ||
                audit.Outcome != CreatedWithReadbackException || !IsHash(audit.JournalSha256) || !IsHash(audit.PayloadHash))
                throw new InvalidOperationException("人工核对审计字段无效，禁止解除阻断。");
            _ = NormalizeConfirmation(audit.Confirmation);
            return audit;
        }
        catch (JsonException ex) { throw new InvalidOperationException("人工核对审计损坏，禁止解除阻断。", ex); }
        catch (ArgumentException ex) { throw new InvalidOperationException("人工核对确认内容无效，禁止解除阻断。", ex); }
    }

    private WorkstationBatchReconciliation? ReadReconciliation(WorkstationBatchResult batch, WorkstationBatchItem item, string hash)
    {
        var path = ReconciliationPath(batch.RequestId, item.TaskNo);
        if (!File.Exists(path)) return null;
        var audit = ReadAuditFile(path);
        var confirmation = NormalizeConfirmation(audit.Confirmation);
        if (audit.RequestId != batch.RequestId || audit.DeviceId != batch.DeviceId || audit.TaskNo != item.TaskNo ||
            audit.TaskName != item.TaskName || audit.OriginalStatus != item.Status || audit.OriginalMessage != item.Message ||
            audit.PayloadHash != batch.PayloadHash || audit.JournalSha256 != hash ||
            confirmation.TaskNo != item.TaskNo || confirmation.ExpectedPayloadHash != batch.PayloadHash || confirmation.ExpectedJournalSha256 != hash ||
            audit.Outcome != CreatedWithReadbackException || audit.ConfirmedAtUtc == default || audit.ConfirmedAtUtc > DateTimeOffset.UtcNow.AddSeconds(10) ||
            audit.DeviceTask is null || audit.DeviceTask.TaskNo != item.TaskNo || audit.DeviceTask.TaskName != item.TaskName ||
            audit.DeviceTask.State != SampleWorkstationTaskState.Waiting || !IsFreshIdle(audit.DeviceStatus, batch.DeviceId, audit.ConfirmedAtUtc))
            throw new InvalidOperationException("人工核对审计与原记录不符或已过期，禁止解除阻断。");
        return audit;
    }

    private void SaveReconciliation(WorkstationBatchReconciliation audit)
    {
        Directory.CreateDirectory(ReconciliationDirectory);
        var path = ReconciliationPath(audit.RequestId, audit.TaskNo);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, audit); stream.Flush(flushToDisk: true); }
        File.Move(temporary, path, overwrite: false); // Append-only final audit; original journal never changes.
    }
}
