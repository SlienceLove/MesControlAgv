using System.Security.Cryptography;
using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;

namespace MesControlAgv.Mes.Services;

/// <summary>Explicit import only. Never initializes or starts a workstation.</summary>
public sealed class WorkstationBatchImportService(
    ISampleWorkstationReader reader, ISampleWorkstationCapabilityReader capabilities,
    ISampleWorkstationTaskImporter importer, IConfiguration configuration, IHostEnvironment? environment = null)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private string JournalDirectory => Path.GetFullPath(configuration["WorkstationBatch:JournalDirectory"]
        ?? Path.Combine(environment?.ContentRootPath ?? AppContext.BaseDirectory, "data", "workstation-batches"));

    public async Task<WorkstationBatchResult> ImportAsync(string deviceId, WorkstationBatchRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 128 || request.RequestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Actor) || request.Actor.Length > 128) throw new ArgumentException("需要有效设备、请求编号和操作人。");
        WorkstationBatchWorkbook.Validate(request.Tasks);
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { DeviceId = deviceId, request.Actor, request.Tasks })));
        await Gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(JournalDirectory);
            // Shared persistent content root: a second MES process must not race this journal.
            using var journalLock = new FileStream(Path.Combine(JournalDirectory, ".lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var path = Path.Combine(JournalDirectory, request.RequestId.ToString("N") + ".json");
            if (File.Exists(path))
            {
                var previous = Read(path);
                if (previous.PayloadHash != hash) throw new InvalidOperationException("请求编号已用于不同内容，请核对导入记录。");
                return previous with { Items = previous.Items.Select(i => i.Status == "Importing" ? i with { Status = "Unknown", Message = "上次导入中断，必须人工核对；不会重新下发。" } : i).ToArray() };
            }
            foreach (var file in Directory.EnumerateFiles(JournalDirectory, "*.json"))
            {
                var previous = Read(file);
                if (string.Equals(previous.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) &&
                    previous.Items.Any(i => i.Status is "Importing" or "Unknown"))
                    throw new InvalidOperationException("此设备有结果不确定的批次。请先人工核对，禁止重复导入。");
            }
            // Keep historical request replay above this new-import gate. Never rewrite old identities.
            foreach (var task in request.Tasks) WorkstationTaskNumber.EnsureSafeForImport(task.TaskNo);
            var caps = await capabilities.GetCapabilitiesAsync(deviceId, cancellationToken);
            if (caps.DeviceId != deviceId || !caps.Enabled || !caps.ControlEnabled || !caps.TaskImportSupported)
                throw new InvalidOperationException("设备未开放任务导入；可继续编辑或导出本地模板。");
            if (caps.TemplateVolumeUnit is not { } fileVolumeUnit || !Enum.IsDefined(fileVolumeUnit))
                throw new InvalidOperationException("设备网关未提供有效的厂家模板体积单位，请升级或核对 Adapter 配置；未执行导入。");
            await EnsureIdleAsync(deviceId, cancellationToken);
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var startNo = 1;
            for (var page = 0; ; page++)
            {
                if (page == 100) throw new InvalidOperationException("任务列表过大，无法完整检查重复编号；未执行导入。");
                var tasks = await reader.GetTasksAsync(deviceId, new SampleWorkstationTaskQuery(StartNo: startNo, RecordNum: 100), cancellationToken);
                if (tasks.Count == 0) break;
                var previousCount = existing.Count;
                foreach (var task in tasks) existing.Add(task.TaskNo);
                if (existing.Count == previousCount) throw new InvalidOperationException("任务列表分页未推进，无法排除重复编号；未执行导入。");
                startNo += tasks.Count;
            }
            var duplicate = request.Tasks.FirstOrDefault(t => existing.Contains(t.TaskNo));
            if (duplicate is not null) throw new InvalidOperationException($"任务 {duplicate.TaskNo} 已存在；不会覆盖，请使用新编号。");
            var items = request.Tasks.Select(t => new WorkstationBatchItem(t.TaskNo, t.TaskName, "Pending", null)).ToArray();
            WorkstationBatchResult Snapshot() => new(request.RequestId, deviceId, hash, items.ToArray(), DateTimeOffset.UtcNow, request.Actor)
            { FileVolumeUnit = fileVolumeUnit };
            Save(path, Snapshot());
            for (var i = 0; i < request.Tasks.Count; i++)
            {
                // All preparation and read checks happen before the durable write-intent marker.
                byte[] bytes;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await EnsureIdleAsync(deviceId, cancellationToken);
                    bytes = SampleWorkstationTemplateFile.Write(request.Tasks[i], fileVolumeUnit);
                }
                catch (Exception ex)
                {
                    items[i] = items[i] with { Status = "NotImported", Message = ex.Message };
                    Save(path, Snapshot()); return Snapshot();
                }
                items[i] = items[i] with { Status = "Importing", Message = "已记录导入意图，正在等待回读。" };
                Save(path, Snapshot());
                try
                {
                    using var stream = new MemoryStream(bytes, writable: false);
                    var imported = await importer.ImportTasksAsync(deviceId, request.Tasks[i].TaskNo + ".xlsx", stream, cancellationToken, fileVolumeUnit);
                    if (!imported.ReadbackVerified || imported.Template is null || imported.DeviceId != deviceId ||
                        imported.FileVolumeUnit != fileVolumeUnit ||
                        !SampleWorkstationTemplateFile.SameContent(request.Tasks[i], imported.Template))
                        throw new InvalidOperationException("导入后回读内容不一致。");
                    items[i] = items[i] with { Status = "Imported", Message = "已导入并回读验证；未启动。" };
                }
                catch (Exception ex)
                {
                    items[i] = items[i] with { Status = "Unknown", Message = "导入结果不确定，停止后续任务。" + ex.Message };
                    Save(path, Snapshot()); return Snapshot();
                }
                Save(path, Snapshot());
            }
            return Snapshot();
        }
        finally { Gate.Release(); }
    }

    private async Task EnsureIdleAsync(string deviceId, CancellationToken token)
    {
        var status = await reader.GetStatusAsync(deviceId, token);
        if (status.DeviceId != deviceId || !status.Online || status.State != SampleWorkstationDeviceState.Idle)
            throw new InvalidOperationException("工作站须在线且空闲后才能导入。");
    }
    private static WorkstationBatchResult Read(string path) => JsonSerializer.Deserialize<WorkstationBatchResult>(File.ReadAllBytes(path))
        ?? throw new InvalidOperationException("导入日志无法读取，请人工核对。");
    private static void Save(string path, WorkstationBatchResult result)
    {
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, result);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
}
