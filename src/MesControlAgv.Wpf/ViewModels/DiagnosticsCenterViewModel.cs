using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

public sealed class DiagnosticsCenterViewModel : INotifyPropertyChanged
{
    private string _lastExportStatus = "尚未导出诊断。";
    private string _diffCodeFilter = string.Empty;
    private OfflineDiagnosticDiffFilterOption _selectedDiffFilter;
    private string _reviewerLabel = string.Empty;
    private string _diffReviewNote = string.Empty;
    private IReadOnlyList<OfflineDiagnosticDiffItem> _filteredConfigurationDiffItems = [];
    private string _snapshotDirectory = OfflineDiagnosticSnapshotLifecycle.DefaultDirectory;
    private OfflineDiagnosticSnapshotInventory _snapshotInventory = OfflineDiagnosticSnapshotInventory.Empty;
    private bool _cleanupConfirmationChecked;
    private string _cleanupConfirmationPhrase = string.Empty;
    private OfflineDiagnosticQuarantineInventory _quarantineInventory = OfflineDiagnosticQuarantineInventory.Empty;
    private bool _restoreConfirmationChecked;
    private string _restoreConfirmationPhrase = string.Empty;

    public DiagnosticsCenterViewModel(
        StartupConfigurationReport startupConfiguration,
        OfflineDiagnosticAuditTrail auditTrail)
    {
        StartupConfiguration = startupConfiguration ?? throw new ArgumentNullException(nameof(startupConfiguration));
        AuditTrail = auditTrail ?? throw new ArgumentNullException(nameof(auditTrail));
        FieldPreflightChecklist = FieldPreflightChecklistBuilder.Build(StartupConfiguration);
        ConfigurationDiff = OfflineDiagnosticDiffReport.Empty;
        DiffFilterOptions =
        [
            new("all", "全部项目", OfflineDiagnosticDiffFilter.All),
            new("changes", "仅变化", OfflineDiagnosticDiffFilter.ChangesOnly),
            new("changed", "值/状态变化", OfflineDiagnosticDiffFilter.Changed),
            new("added", "新增", OfflineDiagnosticDiffFilter.Added),
            new("removed", "移除", OfflineDiagnosticDiffFilter.Removed),
            new("unchanged", "未变化", OfflineDiagnosticDiffFilter.Unchanged)
        ];
        _selectedDiffFilter = DiffFilterOptions[0];
    }

    public StartupConfigurationReport StartupConfiguration { get; }
    public FieldPreflightChecklist FieldPreflightChecklist { get; }
    public OfflineDiagnosticDiffReport ConfigurationDiff { get; private set; }
    public IReadOnlyList<OfflineDiagnosticDiffFilterOption> DiffFilterOptions { get; }
    public IReadOnlyList<OfflineDiagnosticDiffItem> FilteredConfigurationDiffItems => _filteredConfigurationDiffItems;
    public OfflineDiagnosticAuditTrail AuditTrail { get; }
    public ReadOnlyObservableCollection<OfflineDiagnosticAuditEntry> AuditEntries => AuditTrail.Entries;
    public OfflineDiagnosticExportDocument? ImportedDocument { get; private set; }

    public OfflineDiagnosticDiffFilterOption SelectedDiffFilter
    {
        get => _selectedDiffFilter;
        set
        {
            var next = value ?? DiffFilterOptions[0];
            if (ReferenceEquals(_selectedDiffFilter, next)) return;
            _selectedDiffFilter = next;
            OnPropertyChanged();
            RefreshFilteredDiffItems();
        }
    }

    public string DiffCodeFilter
    {
        get => _diffCodeFilter;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(_diffCodeFilter, next, StringComparison.Ordinal)) return;
            _diffCodeFilter = next;
            OnPropertyChanged();
            RefreshFilteredDiffItems();
        }
    }

    public string FilteredDiffSummary => !ConfigurationDiff.HasBaseline
        ? "请先读取一份诊断基线。"
        : $"显示 {_filteredConfigurationDiffItems.Count}/{ConfigurationDiff.Items.Count} 项；变化总数 {ConfigurationDiff.ChangeCount}。";

    public string ReviewerLabel
    {
        get => _reviewerLabel;
        set => SetField(ref _reviewerLabel, value ?? string.Empty);
    }

    public string DiffReviewNote
    {
        get => _diffReviewNote;
        set => SetField(ref _diffReviewNote, value ?? string.Empty);
    }

    public string SnapshotDirectory
    {
        get => _snapshotDirectory;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(_snapshotDirectory, next, StringComparison.Ordinal)) return;
            _snapshotDirectory = next;
            OnPropertyChanged();
            // A candidate list is bound to the directory that was scanned. Any
            // path edit invalidates it and requires a fresh scan before moving.
            _snapshotInventory = OfflineDiagnosticSnapshotInventory.Empty;
            OnPropertyChanged(nameof(SnapshotInventory));
            OnPropertyChanged(nameof(CanMoveSnapshotCandidates));
            _quarantineInventory = OfflineDiagnosticQuarantineInventory.Empty;
            OnPropertyChanged(nameof(QuarantineInventory));
            OnPropertyChanged(nameof(CanRestoreSnapshotCandidates));
        }
    }

    public OfflineDiagnosticSnapshotRetentionPolicy SnapshotRetentionPolicy { get; } = new();

    public OfflineDiagnosticSnapshotInventory SnapshotInventory => _snapshotInventory;

    public bool CleanupConfirmationChecked
    {
        get => _cleanupConfirmationChecked;
        set
        {
            if (_cleanupConfirmationChecked == value) return;
            _cleanupConfirmationChecked = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanMoveSnapshotCandidates));
        }
    }

    public string CleanupConfirmationPhrase
    {
        get => _cleanupConfirmationPhrase;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(_cleanupConfirmationPhrase, next, StringComparison.Ordinal)) return;
            _cleanupConfirmationPhrase = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanMoveSnapshotCandidates));
        }
    }

    public string CleanupConfirmationHint => OfflineDiagnosticSnapshotLifecycle.CleanupConfirmationPhrase;

    public bool CanMoveSnapshotCandidates =>
        SnapshotInventory.CleanupCandidateCount > 0 &&
        CleanupConfirmationChecked &&
        string.Equals(CleanupConfirmationPhrase, CleanupConfirmationHint, StringComparison.Ordinal);

    public OfflineDiagnosticQuarantineInventory QuarantineInventory => _quarantineInventory;

    public bool RestoreConfirmationChecked
    {
        get => _restoreConfirmationChecked;
        set
        {
            if (_restoreConfirmationChecked == value) return;
            _restoreConfirmationChecked = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRestoreSnapshotCandidates));
        }
    }

    public string RestoreConfirmationPhrase
    {
        get => _restoreConfirmationPhrase;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(_restoreConfirmationPhrase, next, StringComparison.Ordinal)) return;
            _restoreConfirmationPhrase = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRestoreSnapshotCandidates));
        }
    }

    public string RestoreConfirmationHint => OfflineDiagnosticSnapshotLifecycle.RestoreConfirmationPhrase;

    public bool CanRestoreSnapshotCandidates =>
        QuarantineInventory.RestorableFileCount > 0 &&
        RestoreConfirmationChecked &&
        string.Equals(RestoreConfirmationPhrase, RestoreConfirmationHint, StringComparison.Ordinal);

    public string LastExportStatus
    {
        get => _lastExportStatus;
        private set => SetField(ref _lastExportStatus, value);
    }

    public void Export(string filePath)
    {
        OfflineDiagnosticExporter.Export(
            filePath,
            StartupConfiguration,
            AuditTrail.Snapshot(),
            baseline: ImportedDocument);
        LastExportStatus = $"已导出脱敏诊断：{Path.GetFileName(filePath)}";
        AuditTrail.Record("diagnostics", "export", "success", "脱敏诊断已导出。");
    }

    public void Clear()
    {
        AuditTrail.Clear();
        ImportedDocument = null;
        OnPropertyChanged(nameof(ImportedDocument));
        ConfigurationDiff = OfflineDiagnosticDiffReport.Empty;
        OnPropertyChanged(nameof(ConfigurationDiff));
        RefreshFilteredDiffItems();
        _snapshotInventory = OfflineDiagnosticSnapshotInventory.Empty;
        OnPropertyChanged(nameof(SnapshotInventory));
        OnPropertyChanged(nameof(CanMoveSnapshotCandidates));
        _quarantineInventory = OfflineDiagnosticQuarantineInventory.Empty;
        OnPropertyChanged(nameof(QuarantineInventory));
        OnPropertyChanged(nameof(CanRestoreSnapshotCandidates));
        CleanupConfirmationChecked = false;
        CleanupConfirmationPhrase = string.Empty;
        RestoreConfirmationChecked = false;
        RestoreConfirmationPhrase = string.Empty;
        LastExportStatus = "离线审计已清空。";
    }

    public OfflineDiagnosticImportResult Import(string filePath)
    {
        var result = OfflineDiagnosticExporter.Read(filePath);
        ImportedDocument = result.Document;
        OnPropertyChanged(nameof(ImportedDocument));
        ConfigurationDiff = result.Document is null
            ? OfflineDiagnosticDiffReport.Empty
            : OfflineDiagnosticDiffBuilder.Build(StartupConfiguration, result.Document);
        OnPropertyChanged(nameof(ConfigurationDiff));
        RefreshFilteredDiffItems();
        LastExportStatus = result.Message;
        return result;
    }

    public bool RecordDiffReview()
    {
        if (!ConfigurationDiff.HasBaseline)
        {
            LastExportStatus = "请先读取一份诊断基线，再记录差异审阅。";
            return false;
        }

        var reviewer = LimitAuditText(string.IsNullOrWhiteSpace(ReviewerLabel) ? "未填写" : ReviewerLabel.Trim());
        var note = LimitAuditText(string.IsNullOrWhiteSpace(DiffReviewNote) ? "无备注" : DiffReviewNote.Trim());
        var filter = LimitAuditText(SelectedDiffFilter.DisplayName);
        var query = LimitAuditText(string.IsNullOrWhiteSpace(DiffCodeFilter) ? "无代码筛选" : DiffCodeFilter.Trim());
        var baselineId = ImportedDocument is { } document
            ? $"schema={document.SchemaVersion};exportedAt={document.ExportedAt:O}"
            : "schema=unknown";
        AuditTrail.Record(
            "diagnostics",
            "diff-review",
            "reviewed",
            $"reviewer={reviewer}; {baselineId}; filter={filter}; query={query}; visible={FilteredConfigurationDiffItems.Count}; changes={ConfigurationDiff.ChangeCount}; note={note}");
        LastExportStatus = "已记录本次配置差异审阅（仅本地审计）。";
        return true;
    }

    public OfflineDiagnosticSnapshotInventory ScanSnapshotDirectory()
    {
        try
        {
            _snapshotInventory = OfflineDiagnosticSnapshotLifecycle.Inspect(
                SnapshotDirectory,
                SnapshotRetentionPolicy);
            OnPropertyChanged(nameof(SnapshotInventory));
            OnPropertyChanged(nameof(CanMoveSnapshotCandidates));
            var outcome = string.IsNullOrWhiteSpace(_snapshotInventory.ScanError) ? "success" : "error";
            AuditTrail.Record(
                "diagnostics",
                "snapshot-scan",
                outcome,
                $"directory={SnapshotDirectory}; files={_snapshotInventory.Items.Count}; bytes={_snapshotInventory.TotalBytes}; candidates={_snapshotInventory.CleanupCandidateCount}");
            LastExportStatus = _snapshotInventory.SummaryText;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _snapshotInventory = new OfflineDiagnosticSnapshotInventory
            {
                DirectoryDisplayPath = DiagnosticRedactor.Redact(SnapshotDirectory),
                Items = [],
                Policy = SnapshotRetentionPolicy,
                HasScan = true,
                DirectoryExists = false,
                ScanError = DiagnosticRedactor.Redact(exception.Message)
            };
            OnPropertyChanged(nameof(SnapshotInventory));
            OnPropertyChanged(nameof(CanMoveSnapshotCandidates));
            AuditTrail.Record("diagnostics", "snapshot-scan", "error", $"directory={SnapshotDirectory}; error={exception.Message}");
            LastExportStatus = _snapshotInventory.SummaryText;
        }

        return _snapshotInventory;
    }

    public OfflineDiagnosticSnapshotCleanupResult MoveSnapshotCandidatesToQuarantine()
    {
        if (!CanMoveSnapshotCandidates)
        {
            AuditTrail.Record("diagnostics", "snapshot-cleanup", "rejected", "需要勾选确认并输入完整确认短语；没有移动快照。");
            LastExportStatus = "清理未执行：请勾选确认并输入完整确认短语。";
            return OfflineDiagnosticSnapshotCleanupResult.NotConfirmed;
        }

        var result = OfflineDiagnosticSnapshotLifecycle.MoveCandidatesToQuarantine(
            SnapshotDirectory,
            SnapshotInventory.Items.Where(item => item.IsCleanupCandidate),
            CleanupConfirmationPhrase);
        AuditTrail.Record(
            "diagnostics",
            "snapshot-cleanup",
            result.HasFailures ? "partial" : "success",
            $"moved={result.MovedCount}; failures={result.Items.Count(item => !item.Moved)}; quarantine={result.QuarantineDirectoryDisplayPath}");
        CleanupConfirmationChecked = false;
        CleanupConfirmationPhrase = string.Empty;
        ScanSnapshotDirectory();
        LastExportStatus = result.SummaryText;
        return result;
    }

    public OfflineDiagnosticQuarantineInventory ScanSnapshotQuarantine()
    {
        try
        {
            _quarantineInventory = OfflineDiagnosticSnapshotLifecycle.InspectQuarantine(SnapshotDirectory);
            OnPropertyChanged(nameof(QuarantineInventory));
            OnPropertyChanged(nameof(CanRestoreSnapshotCandidates));
            AuditTrail.Record(
                "diagnostics",
                "snapshot-quarantine-scan",
                string.IsNullOrWhiteSpace(_quarantineInventory.ScanError) ? "success" : "error",
                $"directory={SnapshotDirectory}; batches={_quarantineInventory.Items.Count}; restorable={_quarantineInventory.RestorableFileCount}");
            LastExportStatus = _quarantineInventory.SummaryText;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _quarantineInventory = new OfflineDiagnosticQuarantineInventory
            {
                DirectoryDisplayPath = DiagnosticRedactor.Redact(Path.Combine(SnapshotDirectory, OfflineDiagnosticSnapshotLifecycle.QuarantineDirectoryName)),
                Items = [],
                HasScan = true,
                DirectoryExists = false,
                ScanError = DiagnosticRedactor.Redact(exception.Message)
            };
            OnPropertyChanged(nameof(QuarantineInventory));
            OnPropertyChanged(nameof(CanRestoreSnapshotCandidates));
            AuditTrail.Record("diagnostics", "snapshot-quarantine-scan", "error", $"directory={SnapshotDirectory}; error={exception.Message}");
            LastExportStatus = _quarantineInventory.SummaryText;
        }

        return _quarantineInventory;
    }

    public OfflineDiagnosticSnapshotRestoreResult RestoreSnapshotCandidates()
    {
        if (!CanRestoreSnapshotCandidates)
        {
            AuditTrail.Record("diagnostics", "snapshot-restore", "rejected", "需要先扫描回收目录、勾选确认并输入完整恢复确认短语；没有恢复快照。");
            LastExportStatus = "恢复未执行：请先扫描回收目录并完成确认。";
            return OfflineDiagnosticSnapshotRestoreResult.NotConfirmed;
        }

        var result = OfflineDiagnosticSnapshotLifecycle.RestoreCandidatesFromQuarantine(
            SnapshotDirectory,
            QuarantineInventory.Items.SelectMany(item => item.Files).Where(file => file.IsRestorable),
            RestoreConfirmationPhrase);
        AuditTrail.Record(
            "diagnostics",
            "snapshot-restore",
            result.HasFailures ? "partial" : "success",
            $"restored={result.RestoredCount}; failures={result.Items.Count(item => !item.Restored)}");
        RestoreConfirmationChecked = false;
        RestoreConfirmationPhrase = string.Empty;
        ScanSnapshotQuarantine();
        ScanSnapshotDirectory();
        LastExportStatus = result.SummaryText;
        return result;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void RefreshFilteredDiffItems()
    {
        _filteredConfigurationDiffItems = OfflineDiagnosticDiffFilterer.Apply(
            ConfigurationDiff.Items,
            SelectedDiffFilter.Filter,
            DiffCodeFilter);
        OnPropertyChanged(nameof(FilteredConfigurationDiffItems));
        OnPropertyChanged(nameof(FilteredDiffSummary));
    }

    private static string LimitAuditText(string value, int maxLength = 240) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
