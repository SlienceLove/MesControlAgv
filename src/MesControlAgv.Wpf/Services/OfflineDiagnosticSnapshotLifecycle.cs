using System.IO;
using System.Text.Json;

namespace MesControlAgv.Wpf.Services;

public enum OfflineDiagnosticSnapshotValidationState
{
    Current,
    NeedsUpgrade,
    Unsupported,
    Unreadable
}

public sealed record OfflineDiagnosticSnapshotRetentionPolicy(
    int RetainDays = 30,
    int MinimumKeepFiles = 3,
    int MaxFiles = 100,
    long MaxTotalBytes = 50 * 1024 * 1024,
    int MaxScanFiles = 1000)
{
    public void Validate()
    {
        if (RetainDays < 1) throw new ArgumentOutOfRangeException(nameof(RetainDays));
        if (MinimumKeepFiles < 1) throw new ArgumentOutOfRangeException(nameof(MinimumKeepFiles));
        if (MaxFiles < MinimumKeepFiles) throw new ArgumentOutOfRangeException(nameof(MaxFiles));
        if (MaxTotalBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxTotalBytes));
        if (MaxScanFiles < MaxFiles) throw new ArgumentOutOfRangeException(nameof(MaxScanFiles));
    }

    public string Description =>
        $"保留 {RetainDays} 天 / 至少保留最新 {MinimumKeepFiles} 个 / 上限 {MaxFiles} 个 / {FormatBytes(MaxTotalBytes)}";

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.##} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):0.##} MB";
        return $"{bytes / (1024d * 1024 * 1024):0.##} GB";
    }
}

public sealed record OfflineDiagnosticSnapshotEntry(
    string FileName,
    DateTimeOffset LastWriteAt,
    long SizeBytes,
    OfflineDiagnosticSnapshotValidationState ValidationState,
    bool IsCleanupCandidate,
    string CleanupReason)
{
    public string LastWriteAtText => LastWriteAt == DateTimeOffset.MinValue
        ? "未知"
        : LastWriteAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string SizeText => OfflineDiagnosticSnapshotRetentionPolicy.FormatBytes(SizeBytes);
    public string ValidationStatusText => ValidationState switch
    {
        OfflineDiagnosticSnapshotValidationState.Current => "当前格式",
        OfflineDiagnosticSnapshotValidationState.NeedsUpgrade => "需升级",
        OfflineDiagnosticSnapshotValidationState.Unsupported => "不支持",
        OfflineDiagnosticSnapshotValidationState.Unreadable => "无法读取",
        _ => "未知"
    };

    public string RetentionStatusText => IsCleanupCandidate ? "清理候选（需人工确认）" : "保留";
}

public sealed record OfflineDiagnosticSnapshotInventory
{
    public static OfflineDiagnosticSnapshotInventory Empty { get; } = new()
    {
        DirectoryDisplayPath = string.Empty,
        Items = [],
        Policy = new OfflineDiagnosticSnapshotRetentionPolicy()
    };

    public required string DirectoryDisplayPath { get; init; }
    public required IReadOnlyList<OfflineDiagnosticSnapshotEntry> Items { get; init; }
    public required OfflineDiagnosticSnapshotRetentionPolicy Policy { get; init; }
    public bool HasScan { get; init; }
    public bool DirectoryExists { get; init; }
    public bool IsScanTruncated { get; init; }
    public string? ScanError { get; init; }
    public long TotalBytes => Items.Sum(item => item.SizeBytes);
    public int CleanupCandidateCount => Items.Count(item => item.IsCleanupCandidate);
    public bool HasUnreadableItems => Items.Any(item => item.ValidationState == OfflineDiagnosticSnapshotValidationState.Unreadable);
    public string TotalSizeText => OfflineDiagnosticSnapshotRetentionPolicy.FormatBytes(TotalBytes);
    public string SummaryText
    {
        get
        {
            if (!HasScan) return "尚未扫描目录。";
            if (!DirectoryExists) return "目录不存在；不会自动创建或写入。";
            if (!string.IsNullOrWhiteSpace(ScanError)) return $"扫描未完成：{ScanError}";
            var suffix = IsScanTruncated ? " 已达到扫描上限，未展示全部文件。" : string.Empty;
            return $"共 {Items.Count} 个诊断快照、{TotalSizeText}；{CleanupCandidateCount} 个清理候选。{suffix}";
        }
    }

    public string BoundaryText => "仅扫描 mes-offline-diagnostics*.json；清理只提供预览，不自动删除、上传或覆盖。";
}

public sealed record OfflineDiagnosticSnapshotMoveItem(
    string FileName,
    bool Moved,
    string Detail);

public sealed record OfflineDiagnosticSnapshotCleanupResult
{
    public static OfflineDiagnosticSnapshotCleanupResult NotConfirmed { get; } = new()
    {
        ConfirmationAccepted = false,
        Items = [],
        QuarantineDirectoryDisplayPath = string.Empty,
        SummaryText = "未确认；没有移动任何快照。"
    };

    public required bool ConfirmationAccepted { get; init; }
    public required IReadOnlyList<OfflineDiagnosticSnapshotMoveItem> Items { get; init; }
    public required string QuarantineDirectoryDisplayPath { get; init; }
    public bool HasFailures => Items.Any(item => !item.Moved);
    public int MovedCount => Items.Count(item => item.Moved);
    public string SummaryText { get; init; } = string.Empty;
}

public enum OfflineDiagnosticQuarantineValidationState
{
    Valid,
    Invalid,
    Unreadable
}

public sealed record OfflineDiagnosticQuarantineFileEntry(
    string BatchDirectoryName,
    string OriginalFileName,
    string QuarantineFileName,
    long SizeBytes,
    bool IsRestorable,
    string RestoreReason)
{
    public string SizeText => OfflineDiagnosticSnapshotRetentionPolicy.FormatBytes(SizeBytes);
}

public sealed record OfflineDiagnosticQuarantineEntry(
    string BatchDirectoryName,
    DateTimeOffset? MovedAt,
    OfflineDiagnosticQuarantineValidationState ValidationState,
    IReadOnlyList<OfflineDiagnosticQuarantineFileEntry> Files,
    string ValidationMessage)
{
    public int RestorableFileCount => Files.Count(file => file.IsRestorable);
    public string ValidationStatusText => ValidationState switch
    {
        OfflineDiagnosticQuarantineValidationState.Valid => "manifest 一致",
        OfflineDiagnosticQuarantineValidationState.Invalid => "manifest 不一致",
        OfflineDiagnosticQuarantineValidationState.Unreadable => "无法读取",
        _ => "未知"
    };
}

public sealed record OfflineDiagnosticQuarantineInventory
{
    public static OfflineDiagnosticQuarantineInventory Empty { get; } = new()
    {
        DirectoryDisplayPath = string.Empty,
        Items = [],
        HasScan = false
    };

    public required string DirectoryDisplayPath { get; init; }
    public required IReadOnlyList<OfflineDiagnosticQuarantineEntry> Items { get; init; }
    public bool HasScan { get; init; }
    public bool DirectoryExists { get; init; }
    public bool IsScanTruncated { get; init; }
    public string? ScanError { get; init; }
    public int RestorableFileCount => Items.Sum(item => item.RestorableFileCount);
    public string SummaryText
    {
        get
        {
            if (!HasScan) return "尚未扫描回收目录。";
            if (!DirectoryExists) return "回收目录不存在；不会自动创建或写入。";
            if (!string.IsNullOrWhiteSpace(ScanError)) return $"扫描未完成：{ScanError}";
            var suffix = IsScanTruncated ? " 已达到扫描上限。" : string.Empty;
            return $"共 {Items.Count} 个回收批次、{RestorableFileCount} 个可恢复文件。{suffix}";
        }
    }
}

public sealed record OfflineDiagnosticSnapshotRestoreItem(
    string BatchDirectoryName,
    string OriginalFileName,
    bool Restored,
    string Detail);

public sealed record OfflineDiagnosticSnapshotRestoreResult
{
    public static OfflineDiagnosticSnapshotRestoreResult NotConfirmed { get; } = new()
    {
        ConfirmationAccepted = false,
        Items = [],
        SummaryText = "未确认；没有恢复任何快照。"
    };

    public required bool ConfirmationAccepted { get; init; }
    public required IReadOnlyList<OfflineDiagnosticSnapshotRestoreItem> Items { get; init; }
    public bool HasFailures => Items.Any(item => !item.Restored);
    public int RestoredCount => Items.Count(item => item.Restored);
    public string SummaryText { get; init; } = string.Empty;
}

/// <summary>
/// Reads local diagnostic snapshot files and produces a bounded retention preview.
/// No method in this type mutates the directory.
/// </summary>
public static class OfflineDiagnosticSnapshotLifecycle
{
    public const string DefaultDirectoryName = "offline-diagnostics";
    public const string FilePattern = "mes-offline-diagnostics*.json";
    public const string QuarantineDirectoryName = ".offline-diagnostics-recycle";
    public const string CleanupConfirmationPhrase = "MOVE-OFFLINE-DIAGNOSTIC-CANDIDATES";
    public const string RestoreConfirmationPhrase = "RESTORE-OFFLINE-DIAGNOSTIC-CANDIDATES";

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MesControlAgv",
        DefaultDirectoryName);

    public static OfflineDiagnosticSnapshotInventory Inspect(
        string directory,
        OfflineDiagnosticSnapshotRetentionPolicy? policy = null,
        DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        policy ??= new OfflineDiagnosticSnapshotRetentionPolicy();
        policy.Validate();
        var fullDirectory = Path.GetFullPath(directory);
        var currentTime = now ?? DateTimeOffset.Now;
        if (!Directory.Exists(fullDirectory))
        {
            return new OfflineDiagnosticSnapshotInventory
            {
                DirectoryDisplayPath = DiagnosticRedactor.Redact(fullDirectory),
                Items = [],
                Policy = policy,
                HasScan = true,
                DirectoryExists = false
            };
        }

        List<FileInfo> files;
        try
        {
            files = Directory.EnumerateFiles(fullDirectory, FilePattern, SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.Name, StringComparer.Ordinal)
                .Take(policy.MaxScanFiles + 1)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new OfflineDiagnosticSnapshotInventory
            {
                DirectoryDisplayPath = DiagnosticRedactor.Redact(fullDirectory),
                Items = [],
                Policy = policy,
                HasScan = true,
                DirectoryExists = true,
                ScanError = DiagnosticRedactor.Redact(exception.Message)
            };
        }

        var truncated = files.Count > policy.MaxScanFiles;
        if (truncated) files.RemoveAt(files.Count - 1);
        var provisional = files.Select(file => CreateEntry(file)).ToList();
        var candidateFlags = CalculateCandidates(provisional, policy, currentTime);
        var items = provisional
            .Select((item, index) => item with
            {
                IsCleanupCandidate = candidateFlags[index].IsCandidate,
                CleanupReason = candidateFlags[index].Reason
            })
            .ToArray();
        return new OfflineDiagnosticSnapshotInventory
        {
            DirectoryDisplayPath = DiagnosticRedactor.Redact(fullDirectory),
            Items = items,
            Policy = policy,
            HasScan = true,
            DirectoryExists = true,
            IsScanTruncated = truncated
        };
    }

    /// <summary>
    /// Moves only explicitly confirmed candidates into a recoverable quarantine
    /// directory. It never permanently deletes a snapshot.
    /// </summary>
    public static OfflineDiagnosticSnapshotCleanupResult MoveCandidatesToQuarantine(
        string directory,
        IEnumerable<OfflineDiagnosticSnapshotEntry> candidates,
        string confirmationPhrase,
        DateTimeOffset? movedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(candidates);
        if (!string.Equals(confirmationPhrase, CleanupConfirmationPhrase, StringComparison.Ordinal))
        {
            return OfflineDiagnosticSnapshotCleanupResult.NotConfirmed;
        }

        var fullDirectory = Path.GetFullPath(directory);
        var requested = candidates
            .Where(item => item.IsCleanupCandidate)
            .GroupBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.FileName, StringComparer.Ordinal)
            .ToArray();
        if (requested.Length == 0)
        {
            return new OfflineDiagnosticSnapshotCleanupResult
            {
                ConfirmationAccepted = true,
                Items = [],
                QuarantineDirectoryDisplayPath = DiagnosticRedactor.Redact(Path.Combine(fullDirectory, QuarantineDirectoryName)),
                SummaryText = "已确认，但没有可移动的清理候选。"
            };
        }

        var timestamp = movedAt ?? DateTimeOffset.Now;
        var quarantineDirectory = Path.Combine(
            fullDirectory,
            QuarantineDirectoryName,
            timestamp.ToLocalTime().ToString("yyyyMMdd-HHmmss"));
        var quarantineDisplayPath = DiagnosticRedactor.Redact(quarantineDirectory);
        var movedItems = new List<OfflineDiagnosticSnapshotMoveItem>(requested.Length);
        var manifestItems = new List<object>();
        try
        {
            Directory.CreateDirectory(quarantineDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new OfflineDiagnosticSnapshotCleanupResult
            {
                ConfirmationAccepted = true,
                Items = requested.Select(item => new OfflineDiagnosticSnapshotMoveItem(
                    item.FileName,
                    false,
                    $"无法创建回收目录：{DiagnosticRedactor.Redact(exception.Message)}")).ToArray(),
                QuarantineDirectoryDisplayPath = quarantineDisplayPath,
                SummaryText = "已确认，但回收目录创建失败；没有快照被移动。"
            };
        }

        foreach (var candidate in requested)
        {
            string sourcePath;
            try
            {
                sourcePath = ResolveSnapshotPath(fullDirectory, candidate.FileName);
            }
            catch (ArgumentException exception)
            {
                movedItems.Add(new OfflineDiagnosticSnapshotMoveItem(
                    candidate.FileName,
                    false,
                    DiagnosticRedactor.Redact(exception.Message)));
                continue;
            }

            try
            {
                var sourceInfo = new FileInfo(sourcePath);
                if (!sourceInfo.Exists)
                {
                    movedItems.Add(new OfflineDiagnosticSnapshotMoveItem(candidate.FileName, false, "源文件不存在，候选已过期。"));
                    continue;
                }

                if (sourceInfo.Length != candidate.SizeBytes ||
                    new DateTimeOffset(sourceInfo.LastWriteTimeUtc, TimeSpan.Zero) != candidate.LastWriteAt)
                {
                    movedItems.Add(new OfflineDiagnosticSnapshotMoveItem(candidate.FileName, false, "源文件已变化，拒绝移动过期候选。"));
                    continue;
                }

                var destinationName = $"{Path.GetFileNameWithoutExtension(candidate.FileName)}-{Guid.NewGuid():N}{Path.GetExtension(candidate.FileName)}";
                var destinationPath = Path.Combine(quarantineDirectory, destinationName);
                File.Move(sourcePath, destinationPath, overwrite: false);
                movedItems.Add(new OfflineDiagnosticSnapshotMoveItem(candidate.FileName, true, "已移动到可恢复回收目录。"));
                manifestItems.Add(new
                {
                    originalFile = candidate.FileName,
                    quarantineFile = destinationName,
                    sizeBytes = candidate.SizeBytes,
                    cleanupReason = DiagnosticRedactor.Redact(candidate.CleanupReason)
                });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                movedItems.Add(new OfflineDiagnosticSnapshotMoveItem(
                    candidate.FileName,
                    false,
                    $"移动失败：{DiagnosticRedactor.Redact(exception.Message)}"));
            }
        }

        if (manifestItems.Count > 0)
        {
            try
            {
                var manifestPath = Path.Combine(quarantineDirectory, "manifest.json");
                var manifest = new
                {
                    schemaVersion = OfflineDiagnosticVersions.QuarantineSchema,
                    movedAt = timestamp,
                    sourceDirectory = DiagnosticRedactor.Redact(fullDirectory),
                    files = manifestItems
                };
                File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                movedItems.Add(new OfflineDiagnosticSnapshotMoveItem(
                    "manifest.json",
                    false,
                    $"回收清单写入失败：{DiagnosticRedactor.Redact(exception.Message)}"));
            }
        }

        var movedCount = movedItems.Count(item => item.Moved);
        var failureCount = movedItems.Count - movedCount;
        return new OfflineDiagnosticSnapshotCleanupResult
        {
            ConfirmationAccepted = true,
            Items = movedItems,
            QuarantineDirectoryDisplayPath = quarantineDisplayPath,
            SummaryText = failureCount == 0
                ? $"已将 {movedCount} 个候选移动到可恢复回收目录。"
                : $"已移动 {movedCount} 个候选，{failureCount} 个候选未移动；请人工复核。"
        };
    }

    /// <summary>
    /// Reads recoverable quarantine batches without creating directories or
    /// changing any file. Every manifest entry is checked against the current
    /// quarantine file before it is offered for restoration.
    /// </summary>
    public static OfflineDiagnosticQuarantineInventory InspectQuarantine(
        string directory,
        int maxScanBatches = 100)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (maxScanBatches is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maxScanBatches));

        var fullDirectory = Path.GetFullPath(directory);
        var quarantineRoot = Path.Combine(fullDirectory, QuarantineDirectoryName);
        if (!Directory.Exists(quarantineRoot))
        {
            return new OfflineDiagnosticQuarantineInventory
            {
                DirectoryDisplayPath = DiagnosticRedactor.Redact(quarantineRoot),
                Items = [],
                HasScan = true,
                DirectoryExists = false
            };
        }

        try
        {
            var batches = Directory.EnumerateDirectories(quarantineRoot, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new DirectoryInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ThenBy(info => info.Name, StringComparer.Ordinal)
                .Take(maxScanBatches + 1)
                .ToList();
            var truncated = batches.Count > maxScanBatches;
            if (truncated) batches.RemoveAt(batches.Count - 1);
            return new OfflineDiagnosticQuarantineInventory
            {
                DirectoryDisplayPath = DiagnosticRedactor.Redact(quarantineRoot),
                Items = batches.Select(batch => InspectQuarantineBatch(fullDirectory, batch)).ToArray(),
                HasScan = true,
                DirectoryExists = true,
                IsScanTruncated = truncated
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new OfflineDiagnosticQuarantineInventory
            {
                DirectoryDisplayPath = DiagnosticRedactor.Redact(quarantineRoot),
                Items = [],
                HasScan = true,
                DirectoryExists = true,
                ScanError = DiagnosticRedactor.Redact(exception.Message)
            };
        }
    }

    /// <summary>
    /// Restores only entries from a prior, consistent quarantine scan after an
    /// explicit confirmation. Existing destination files are never overwritten.
    /// </summary>
    public static OfflineDiagnosticSnapshotRestoreResult RestoreCandidatesFromQuarantine(
        string directory,
        IEnumerable<OfflineDiagnosticQuarantineFileEntry> candidates,
        string confirmationPhrase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(candidates);
        if (!string.Equals(confirmationPhrase, RestoreConfirmationPhrase, StringComparison.Ordinal))
            return OfflineDiagnosticSnapshotRestoreResult.NotConfirmed;

        var fullDirectory = Path.GetFullPath(directory);
        var requested = candidates
            .Where(item => item.IsRestorable)
            .GroupBy(item => $"{item.BatchDirectoryName}\u001f{item.OriginalFileName}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.BatchDirectoryName, StringComparer.Ordinal)
            .ThenBy(item => item.OriginalFileName, StringComparer.Ordinal)
            .ToArray();
        if (requested.Length == 0)
        {
            return new OfflineDiagnosticSnapshotRestoreResult
            {
                ConfirmationAccepted = true,
                Items = [],
                SummaryText = "已确认，但没有可恢复的快照。"
            };
        }

        var results = new List<OfflineDiagnosticSnapshotRestoreItem>(requested.Length);
        foreach (var candidate in requested)
        {
            try
            {
                var batch = ResolveQuarantineBatchPath(fullDirectory, candidate.BatchDirectoryName);
                var source = ResolveQuarantineFilePath(batch, candidate.QuarantineFileName);
                var destination = ResolveSnapshotPath(fullDirectory, candidate.OriginalFileName);
                if (!IsManifestEntryCurrent(batch, candidate))
                {
                    results.Add(new(candidate.BatchDirectoryName, candidate.OriginalFileName, false, "manifest 已变化，拒绝恢复过期预览。"));
                    continue;
                }
                var sourceInfo = new FileInfo(source);
                if (!sourceInfo.Exists)
                {
                    results.Add(new(candidate.BatchDirectoryName, candidate.OriginalFileName, false, "回收文件不存在，拒绝恢复。"));
                    continue;
                }
                if (sourceInfo.Length != candidate.SizeBytes)
                {
                    results.Add(new(candidate.BatchDirectoryName, candidate.OriginalFileName, false, "回收文件已变化，拒绝恢复过期预览。"));
                    continue;
                }
                if (File.Exists(destination))
                {
                    results.Add(new(candidate.BatchDirectoryName, candidate.OriginalFileName, false, "目标快照已存在，禁止覆盖。"));
                    continue;
                }

                File.Move(source, destination, overwrite: false);
                results.Add(new(candidate.BatchDirectoryName, candidate.OriginalFileName, true, "已恢复到诊断快照目录。"));
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                results.Add(new(
                    candidate.BatchDirectoryName,
                    candidate.OriginalFileName,
                    false,
                    DiagnosticRedactor.Redact(exception.Message)));
            }
        }

        UpdateManifestsAfterRestore(fullDirectory, results);
        var restored = results.Count(item => item.Restored);
        var failures = results.Count - restored;
        return new OfflineDiagnosticSnapshotRestoreResult
        {
            ConfirmationAccepted = true,
            Items = results,
            SummaryText = failures == 0
                ? $"已恢复 {restored} 个诊断快照。"
                : $"已恢复 {restored} 个快照，{failures} 个未恢复；请人工复核。"
        };
    }

    private static OfflineDiagnosticQuarantineEntry InspectQuarantineBatch(
        string rootDirectory,
        DirectoryInfo batch)
    {
        var manifestPath = Path.Combine(batch.FullName, "manifest.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var schema) ||
                !string.Equals(schema.GetString(), OfflineDiagnosticVersions.QuarantineSchema, StringComparison.Ordinal))
            {
                return new OfflineDiagnosticQuarantineEntry(
                    batch.Name,
                    null,
                    OfflineDiagnosticQuarantineValidationState.Invalid,
                    [],
                    "manifest schema 不受支持。");
            }

            DateTimeOffset? movedAt = null;
            if (root.TryGetProperty("movedAt", out var movedAtElement) &&
                movedAtElement.TryGetDateTimeOffset(out var parsedMovedAt))
            {
                movedAt = parsedMovedAt;
            }

            if (!root.TryGetProperty("files", out var filesElement) ||
                filesElement.ValueKind != JsonValueKind.Array)
            {
                return new OfflineDiagnosticQuarantineEntry(
                    batch.Name,
                    movedAt,
                    OfflineDiagnosticQuarantineValidationState.Invalid,
                    [],
                    "manifest 缺少 files 数组。 ");
            }

            var entries = new List<OfflineDiagnosticQuarantineFileEntry>();
            var seenOriginals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in filesElement.EnumerateArray())
            {
                var original = ReadManifestString(element, "originalFile");
                var quarantine = ReadManifestString(element, "quarantineFile");
                var size = element.TryGetProperty("sizeBytes", out var sizeElement) &&
                           sizeElement.TryGetInt64(out var parsedSize)
                    ? parsedSize
                    : -1;
                var reason = "";
                var restorable = false;
                try
                {
                    if (!seenOriginals.Add(original)) throw new ArgumentException("manifest 包含重复的原始文件名。");
                    _ = ResolveSnapshotPath(rootDirectory, original);
                    var source = ResolveQuarantineFilePath(batch.FullName, quarantine);
                    var info = new FileInfo(source);
                    if (size < 0) throw new ArgumentException("manifest 文件大小无效。");
                    if (!info.Exists) throw new FileNotFoundException("回收文件不存在。");
                    if (info.Length != size) throw new IOException("回收文件大小与 manifest 不一致。");
                    var destination = ResolveSnapshotPath(rootDirectory, original);
                    if (File.Exists(destination)) throw new IOException("目标快照已存在，禁止覆盖。");
                    restorable = true;
                    reason = "可恢复。";
                }
                catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or FileNotFoundException)
                {
                    reason = DiagnosticRedactor.Redact(exception.Message);
                }

                entries.Add(new OfflineDiagnosticQuarantineFileEntry(
                    batch.Name,
                    original,
                    quarantine,
                    Math.Max(0, size),
                    restorable,
                    reason));
            }

            var invalid = entries.Any(entry => !entry.IsRestorable);
            if (invalid)
            {
                entries = entries
                    .Select(entry => entry with
                    {
                        IsRestorable = false,
                        RestoreReason = "manifest 或回收文件不一致；请修复并重新扫描后再恢复。"
                    })
                    .ToList();
            }
            return new OfflineDiagnosticQuarantineEntry(
                batch.Name,
                movedAt,
                invalid
                    ? OfflineDiagnosticQuarantineValidationState.Invalid
                    : OfflineDiagnosticQuarantineValidationState.Valid,
                entries,
                invalid ? "manifest 或回收文件不一致；仅可恢复条目会被提供。" : "manifest 与回收文件一致。");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or JsonException)
        {
            return new OfflineDiagnosticQuarantineEntry(
                batch.Name,
                null,
                exception is ArgumentException
                    ? OfflineDiagnosticQuarantineValidationState.Invalid
                    : OfflineDiagnosticQuarantineValidationState.Unreadable,
                [],
                $"无法读取 manifest：{DiagnosticRedactor.Redact(exception.Message)}");
        }
    }

    private static string ReadManifestString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException($"manifest 字段 {propertyName} 无效。");
        }

        return value.GetString()!.Trim();
    }

    private static string ResolveQuarantineBatchPath(string rootDirectory, string batchDirectoryName)
    {
        if (string.IsNullOrWhiteSpace(batchDirectoryName) ||
            batchDirectoryName.Contains(Path.DirectorySeparatorChar) ||
            batchDirectoryName.Contains(Path.AltDirectorySeparatorChar) ||
            batchDirectoryName is "." or "..")
        {
            throw new ArgumentException("回收批次目录名无效，已拒绝路径穿越。", nameof(batchDirectoryName));
        }

        var quarantineRoot = Path.GetFullPath(Path.Combine(rootDirectory, QuarantineDirectoryName));
        var rootPrefix = quarantineRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(quarantineRoot, batchDirectoryName));
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), quarantineRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("回收批次必须位于回收目录的顶层。", nameof(batchDirectoryName));
        }

        return path;
    }

    private static string ResolveQuarantineFilePath(string batchDirectory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Contains(Path.DirectorySeparatorChar) ||
            fileName.Contains(Path.AltDirectorySeparatorChar) ||
            fileName is "." or ".." ||
            string.Equals(fileName, "manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("回收文件名无效，已拒绝路径穿越。", nameof(fileName));
        }

        var batchRoot = Path.GetFullPath(batchDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(batchDirectory, fileName));
        if (!path.StartsWith(batchRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), batchRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("回收文件必须位于批次目录的顶层。", nameof(fileName));
        }

        return path;
    }

    private static void UpdateManifestsAfterRestore(
        string rootDirectory,
        IReadOnlyList<OfflineDiagnosticSnapshotRestoreItem> results)
    {
        foreach (var group in results.Where(item => item.Restored).GroupBy(item => item.BatchDirectoryName, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var batch = ResolveQuarantineBatchPath(rootDirectory, group.Key);
                var manifestPath = Path.Combine(batch, "manifest.json");
                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = document.RootElement;
                if (!root.TryGetProperty("files", out var filesElement) || filesElement.ValueKind != JsonValueKind.Array) continue;
                var restoredNames = group.Select(item => item.OriginalFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var remaining = filesElement.EnumerateArray()
                    .Where(item => !item.TryGetProperty("originalFile", out var original) ||
                                   !restoredNames.Contains(original.GetString() ?? string.Empty))
                    .Select(item => item.Clone())
                    .ToArray();
                var payload = new
                {
                    schemaVersion = OfflineDiagnosticVersions.QuarantineSchema,
                    movedAt = root.TryGetProperty("movedAt", out var movedAt) ? movedAt.GetString() : null,
                    sourceDirectory = root.TryGetProperty("sourceDirectory", out var sourceDirectory) ? sourceDirectory.GetString() : "[PATH]",
                    files = remaining,
                    restoredAt = DateTimeOffset.Now,
                    restoredFiles = group.Select(item => item.OriginalFileName).ToArray()
                };
                File.WriteAllText(manifestPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                // The file movement result remains authoritative; a subsequent
                // scan will surface the manifest mismatch for manual review.
            }
        }
    }

    private static bool IsManifestEntryCurrent(
        string batchDirectory,
        OfflineDiagnosticQuarantineFileEntry candidate)
    {
        var manifestPath = Path.Combine(batchDirectory, "manifest.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var schema) ||
            !string.Equals(schema.GetString(), OfflineDiagnosticVersions.QuarantineSchema, StringComparison.Ordinal) ||
            !root.TryGetProperty("files", out var files) ||
            files.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return files.EnumerateArray().Any(item =>
            string.Equals(ReadManifestString(item, "originalFile"), candidate.OriginalFileName, StringComparison.Ordinal) &&
            string.Equals(ReadManifestString(item, "quarantineFile"), candidate.QuarantineFileName, StringComparison.Ordinal) &&
            item.TryGetProperty("sizeBytes", out var size) &&
            size.TryGetInt64(out var parsedSize) &&
            parsedSize == candidate.SizeBytes);
    }

    private static string ResolveSnapshotPath(string directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Contains(Path.DirectorySeparatorChar) ||
            fileName.Contains(Path.AltDirectorySeparatorChar) ||
            fileName is "." or ".." ||
            !fileName.StartsWith("mes-offline-diagnostics", StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("清理候选文件名无效，已拒绝路径穿越。", nameof(fileName));
        }

        var directoryRoot = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!path.StartsWith(directoryRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), directoryRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("清理候选必须位于扫描目录的顶层。", nameof(fileName));
        }

        return path;
    }

    private static OfflineDiagnosticSnapshotEntry CreateEntry(FileInfo file)
    {
        try
        {
            file.Refresh();
            var sizeBytes = file.Length;
            var lastWriteAt = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            var validation = OfflineDiagnosticExporter.Read(file.FullName);
            var state = !validation.IsAccepted
                ? OfflineDiagnosticSnapshotValidationState.Unsupported
                : validation.NeedsUpgrade
                    ? OfflineDiagnosticSnapshotValidationState.NeedsUpgrade
                    : OfflineDiagnosticSnapshotValidationState.Current;
            return new OfflineDiagnosticSnapshotEntry(
                file.Name,
                lastWriteAt,
                sizeBytes,
                state,
                false,
                state switch
                {
                    OfflineDiagnosticSnapshotValidationState.Unsupported => "格式不受支持，先人工确认，不纳入自动清理候选。",
                    OfflineDiagnosticSnapshotValidationState.NeedsUpgrade => "旧格式可读取，建议重新导出后再评估保留。",
                    _ => string.Empty
                });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new OfflineDiagnosticSnapshotEntry(
                file.Name,
                DateTimeOffset.MinValue,
                0,
                OfflineDiagnosticSnapshotValidationState.Unreadable,
                false,
                $"无法读取文件，需人工确认：{DiagnosticRedactor.Redact(exception.Message)}");
        }
    }

    private static IReadOnlyList<(bool IsCandidate, string Reason)> CalculateCandidates(
        IReadOnlyList<OfflineDiagnosticSnapshotEntry> items,
        OfflineDiagnosticSnapshotRetentionPolicy policy,
        DateTimeOffset now)
    {
        var results = items.Select(_ => (IsCandidate: false, Reason: string.Empty)).ToArray();
        var eligible = items
            .Select((item, index) => (item, index))
            .Where(pair => pair.item.ValidationState is OfflineDiagnosticSnapshotValidationState.Current or OfflineDiagnosticSnapshotValidationState.NeedsUpgrade)
            .ToArray();
        var protectedIndexes = eligible.Take(policy.MinimumKeepFiles).Select(pair => pair.index).ToHashSet();
        var totalBytes = items.Sum(item => item.SizeBytes);
        var eligibleCount = eligible.Length;
        var expiration = now - TimeSpan.FromDays(policy.RetainDays);

        foreach (var pair in eligible.Reverse())
        {
            var reasons = new List<string>();
            if (pair.item.LastWriteAt < expiration) reasons.Add("已超过保留期限");
            if (eligibleCount > policy.MaxFiles && !protectedIndexes.Contains(pair.index))
            {
                reasons.Add("超过文件数量上限");
                eligibleCount--;
            }
            if (totalBytes > policy.MaxTotalBytes && !protectedIndexes.Contains(pair.index))
            {
                reasons.Add("超过容量上限");
                totalBytes -= pair.item.SizeBytes;
            }

            if (reasons.Count > 0 && !protectedIndexes.Contains(pair.index))
            {
                results[pair.index] = (true, string.Join("；", reasons));
            }
        }

        foreach (var pair in items.Select((item, index) => (item, index)))
        {
            if (pair.item.ValidationState is OfflineDiagnosticSnapshotValidationState.Unsupported or OfflineDiagnosticSnapshotValidationState.Unreadable)
            {
                results[pair.index] = (false, pair.item.CleanupReason);
            }
            else if (protectedIndexes.Contains(pair.index))
            {
                results[pair.index] = (false, "最近快照保护，不纳入清理候选。");
            }
        }

        return results;
    }
}
