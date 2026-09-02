using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class OfflineDiagnosticSnapshotLifecycleTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "MesControlAgv.OfflineDiagnosticSnapshotLifecycleTests",
        Guid.NewGuid().ToString("N"));

    public OfflineDiagnosticSnapshotLifecycleTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Missing_directory_is_reported_without_creating_or_writing_it()
    {
        var missing = Path.Combine(_directory, "missing");
        var inventory = OfflineDiagnosticSnapshotLifecycle.Inspect(missing);

        Assert.True(inventory.HasScan);
        Assert.False(inventory.DirectoryExists);
        Assert.Empty(inventory.Items);
        Assert.Contains("目录不存在", inventory.SummaryText, StringComparison.Ordinal);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void Scan_classifies_snapshots_and_keeps_invalid_files_out_of_cleanup_candidates()
    {
        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory,
            RuntimeMode = "simulator",
            ManageLocalServices = "false"
        });
        WriteValidSnapshot("mes-offline-diagnostics-recent.json", report, DateTimeOffset.Parse("2026-08-30T10:00:00+08:00"));
        WriteValidSnapshot("mes-offline-diagnostics-old.json", report, DateTimeOffset.Parse("2026-07-01T10:00:00+08:00"));
        File.WriteAllText(Path.Combine(_directory, "mes-offline-diagnostics-legacy.json"), "{\"startup\":{},\"audit\":[]}");
        File.WriteAllText(Path.Combine(_directory, "mes-offline-diagnostics-unknown.json"), "{\"schemaVersion\":\"mes.offline-diagnostics/9.0\"}");

        var inventory = OfflineDiagnosticSnapshotLifecycle.Inspect(
            _directory,
            new OfflineDiagnosticSnapshotRetentionPolicy(RetainDays: 30, MinimumKeepFiles: 1, MaxFiles: 10),
            DateTimeOffset.Parse("2026-08-30T12:00:00+08:00"));

        Assert.Equal(4, inventory.Items.Count);
        Assert.Equal(1, inventory.CleanupCandidateCount);
        Assert.Equal(
            OfflineDiagnosticSnapshotValidationState.Current,
            Item(inventory, "mes-offline-diagnostics-recent.json").ValidationState);
        Assert.Equal(
            OfflineDiagnosticSnapshotValidationState.NeedsUpgrade,
            Item(inventory, "mes-offline-diagnostics-legacy.json").ValidationState);
        Assert.Equal(
            OfflineDiagnosticSnapshotValidationState.Unsupported,
            Item(inventory, "mes-offline-diagnostics-unknown.json").ValidationState);
        Assert.True(Item(inventory, "mes-offline-diagnostics-old.json").IsCleanupCandidate);
        Assert.False(Item(inventory, "mes-offline-diagnostics-unknown.json").IsCleanupCandidate);
        Assert.Equal(4, Directory.GetFiles(_directory, "mes-offline-diagnostics*.json").Length);
    }

    [Fact]
    public void Retention_preview_protects_newest_files_and_applies_count_and_size_limits()
    {
        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory,
            RuntimeMode = "simulator",
            ManageLocalServices = "false"
        });
        for (var index = 0; index < 5; index++)
        {
            WriteValidSnapshot(
                $"mes-offline-diagnostics-{index}.json",
                report,
                DateTimeOffset.Parse("2026-08-30T10:00:00+08:00").AddMinutes(index));
        }

        var inventory = OfflineDiagnosticSnapshotLifecycle.Inspect(
            _directory,
            new OfflineDiagnosticSnapshotRetentionPolicy(
                RetainDays: 365,
                MinimumKeepFiles: 2,
                MaxFiles: 3,
                MaxTotalBytes: 1),
            DateTimeOffset.Parse("2026-08-30T12:00:00+08:00"));

        Assert.Equal(5, inventory.Items.Count);
        Assert.Equal(3, inventory.CleanupCandidateCount);
        Assert.False(Item(inventory, "mes-offline-diagnostics-4.json").IsCleanupCandidate);
        Assert.False(Item(inventory, "mes-offline-diagnostics-3.json").IsCleanupCandidate);
        Assert.True(Item(inventory, "mes-offline-diagnostics-1.json").IsCleanupCandidate);
        Assert.True(Item(inventory, "mes-offline-diagnostics-0.json").IsCleanupCandidate);
        Assert.True(Item(inventory, "mes-offline-diagnostics-2.json").IsCleanupCandidate);
        Assert.All(inventory.Items, item => Assert.DoesNotContain("\\", item.FileName, StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_policy_is_rejected_before_any_directory_scan()
    {
        var policy = new OfflineDiagnosticSnapshotRetentionPolicy(RetainDays: 0);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OfflineDiagnosticSnapshotLifecycle.Inspect(_directory, policy));
    }

    [Fact]
    public void Diagnostics_view_model_scans_locally_and_records_only_a_redacted_summary()
    {
        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory,
            RuntimeMode = "simulator",
            ManageLocalServices = "false"
        });
        WriteValidSnapshot("mes-offline-diagnostics-vm.json", report, DateTimeOffset.UtcNow);
        var audit = new OfflineDiagnosticAuditTrail();
        var viewModel = new MesControlAgv.Wpf.ViewModels.DiagnosticsCenterViewModel(report, audit)
        {
            SnapshotDirectory = _directory
        };

        var inventory = viewModel.ScanSnapshotDirectory();

        Assert.True(inventory.HasScan);
        Assert.Single(inventory.Items);
        var entry = Assert.Single(audit.Entries.Where(item => item.Action == "snapshot-scan"));
        Assert.DoesNotContain(_directory, entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("files=1", entry.Message, StringComparison.Ordinal);

        viewModel.SnapshotDirectory = Path.Combine(_directory, "other");
        Assert.False(viewModel.SnapshotInventory.HasScan);
        Assert.False(viewModel.CanMoveSnapshotCandidates);
    }

    [Fact]
    public void Cleanup_requires_exact_confirmation_and_moves_candidates_to_recoverable_quarantine()
    {
        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory,
            RuntimeMode = "simulator",
            ManageLocalServices = "false"
        });
        var oldPath = Path.Combine(_directory, "mes-offline-diagnostics-old.json");
        WriteValidSnapshot("mes-offline-diagnostics-old.json", report, DateTimeOffset.Parse("2026-07-01T10:00:00+08:00"));
        WriteValidSnapshot("mes-offline-diagnostics-new.json", report, DateTimeOffset.Parse("2026-08-30T10:00:00+08:00"));
        var inventory = OfflineDiagnosticSnapshotLifecycle.Inspect(
            _directory,
            new OfflineDiagnosticSnapshotRetentionPolicy(RetainDays: 30, MinimumKeepFiles: 1, MaxFiles: 10),
            DateTimeOffset.Parse("2026-08-30T12:00:00+08:00"));
        var candidate = Item(inventory, "mes-offline-diagnostics-old.json");

        var rejected = OfflineDiagnosticSnapshotLifecycle.MoveCandidatesToQuarantine(
            _directory,
            [candidate],
            "wrong-confirmation");
        Assert.False(rejected.ConfirmationAccepted);
        Assert.True(File.Exists(oldPath));

        var moved = OfflineDiagnosticSnapshotLifecycle.MoveCandidatesToQuarantine(
            _directory,
            [candidate],
            OfflineDiagnosticSnapshotLifecycle.CleanupConfirmationPhrase,
            DateTimeOffset.Parse("2026-08-30T12:30:00+08:00"));

        Assert.True(moved.ConfirmationAccepted);
        Assert.Equal(1, moved.MovedCount);
        Assert.False(moved.HasFailures);
        Assert.False(File.Exists(oldPath));
        var quarantineFiles = Directory.GetFiles(
            Path.Combine(_directory, OfflineDiagnosticSnapshotLifecycle.QuarantineDirectoryName),
            "*",
            SearchOption.AllDirectories);
        Assert.Contains(quarantineFiles, path => Path.GetFileName(path) == "manifest.json");
        Assert.DoesNotContain(_directory, moved.QuarantineDirectoryDisplayPath, StringComparison.OrdinalIgnoreCase);
        var manifestPath = quarantineFiles.Single(path => Path.GetFileName(path) == "manifest.json");
        var manifest = File.ReadAllText(manifestPath);
        Assert.Contains(OfflineDiagnosticVersions.QuarantineSchema, manifest, StringComparison.Ordinal);
        Assert.DoesNotContain(_directory, manifest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cleanup_rejects_stale_and_path_traversal_candidates_without_touching_files()
    {
        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory,
            RuntimeMode = "simulator",
            ManageLocalServices = "false"
        });
        var path = Path.Combine(_directory, "mes-offline-diagnostics-stale.json");
        WriteValidSnapshot("mes-offline-diagnostics-stale.json", report, DateTimeOffset.Parse("2026-07-01T10:00:00+08:00"));
        WriteValidSnapshot("mes-offline-diagnostics-stale-new.json", report, DateTimeOffset.Parse("2026-08-30T10:00:00+08:00"));
        var inventory = OfflineDiagnosticSnapshotLifecycle.Inspect(
            _directory,
            new OfflineDiagnosticSnapshotRetentionPolicy(RetainDays: 1, MinimumKeepFiles: 1, MaxFiles: 10),
            DateTimeOffset.Parse("2026-08-30T12:00:00+08:00"));
        var candidate = Item(inventory, "mes-offline-diagnostics-stale.json");
        File.AppendAllText(path, "stale");

        var stale = OfflineDiagnosticSnapshotLifecycle.MoveCandidatesToQuarantine(
            _directory,
            [candidate],
            OfflineDiagnosticSnapshotLifecycle.CleanupConfirmationPhrase);
        var traversal = OfflineDiagnosticSnapshotLifecycle.MoveCandidatesToQuarantine(
            _directory,
            [new OfflineDiagnosticSnapshotEntry("..\\outside.json", DateTimeOffset.UtcNow, 1, OfflineDiagnosticSnapshotValidationState.Current, true, "test")],
            OfflineDiagnosticSnapshotLifecycle.CleanupConfirmationPhrase);

        Assert.True(stale.HasFailures);
        Assert.True(File.Exists(path));
        Assert.True(traversal.HasFailures);
        Assert.Contains("路径穿越", traversal.Items[0].Detail, StringComparison.Ordinal);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void View_model_requires_checkbox_and_phrase_then_resets_confirmation_after_move()
    {
        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory,
            RuntimeMode = "simulator",
            ManageLocalServices = "false"
        });
        WriteValidSnapshot("mes-offline-diagnostics-vm-old.json", report, DateTimeOffset.Parse("2026-07-01T10:00:00+08:00"));
        WriteValidSnapshot("mes-offline-diagnostics-vm-old-2.json", report, DateTimeOffset.Parse("2026-07-02T10:00:00+08:00"));
        WriteValidSnapshot("mes-offline-diagnostics-vm-old-3.json", report, DateTimeOffset.Parse("2026-07-03T10:00:00+08:00"));
        WriteValidSnapshot("mes-offline-diagnostics-vm-new.json", report, DateTimeOffset.Parse("2026-08-30T10:00:00+08:00"));
        var viewModel = new MesControlAgv.Wpf.ViewModels.DiagnosticsCenterViewModel(report, new OfflineDiagnosticAuditTrail())
        {
            SnapshotDirectory = _directory
        };
        viewModel.ScanSnapshotDirectory();

        var rejected = viewModel.MoveSnapshotCandidatesToQuarantine();
        Assert.False(rejected.ConfirmationAccepted);
        Assert.True(viewModel.SnapshotInventory.CleanupCandidateCount > 0);

        viewModel.CleanupConfirmationChecked = true;
        viewModel.CleanupConfirmationPhrase = viewModel.CleanupConfirmationHint;
        var moved = viewModel.MoveSnapshotCandidatesToQuarantine();

        Assert.True(moved.ConfirmationAccepted);
        Assert.False(viewModel.CleanupConfirmationChecked);
        Assert.Equal(string.Empty, viewModel.CleanupConfirmationPhrase);
        Assert.False(viewModel.CanMoveSnapshotCandidates);
        Assert.Equal(0, viewModel.SnapshotInventory.CleanupCandidateCount);
    }

    [Fact]
    public void Quarantine_scan_previews_manifest_and_restore_requires_separate_confirmation()
    {
        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory,
            RuntimeMode = "simulator",
            ManageLocalServices = "false"
        });
        var oldPath = Path.Combine(_directory, "mes-offline-diagnostics-restore.json");
        WriteValidSnapshot("mes-offline-diagnostics-restore.json", report, DateTimeOffset.Parse("2026-07-01T10:00:00Z"));
        WriteValidSnapshot("mes-offline-diagnostics-keep.json", report, DateTimeOffset.Parse("2026-08-30T10:00:00Z"));
        var inventory = OfflineDiagnosticSnapshotLifecycle.Inspect(
            _directory,
            new OfflineDiagnosticSnapshotRetentionPolicy(RetainDays: 30, MinimumKeepFiles: 1),
            DateTimeOffset.Parse("2026-08-30T12:00:00Z"));
        var candidate = Item(inventory, "mes-offline-diagnostics-restore.json");
        var moved = OfflineDiagnosticSnapshotLifecycle.MoveCandidatesToQuarantine(
            _directory,
            [candidate],
            OfflineDiagnosticSnapshotLifecycle.CleanupConfirmationPhrase,
            DateTimeOffset.Parse("2026-08-30T12:30:00Z"));
        Assert.Equal(1, moved.MovedCount);

        var quarantine = OfflineDiagnosticSnapshotLifecycle.InspectQuarantine(_directory);
        var batch = Assert.Single(quarantine.Items);
        var restoreCandidate = Assert.Single(batch.Files);
        Assert.Equal(OfflineDiagnosticQuarantineValidationState.Valid, batch.ValidationState);
        Assert.True(restoreCandidate.IsRestorable);

        var rejected = OfflineDiagnosticSnapshotLifecycle.RestoreCandidatesFromQuarantine(
            _directory,
            [restoreCandidate],
            "wrong-confirmation");
        Assert.False(rejected.ConfirmationAccepted);
        Assert.False(File.Exists(oldPath));

        var restored = OfflineDiagnosticSnapshotLifecycle.RestoreCandidatesFromQuarantine(
            _directory,
            [restoreCandidate],
            OfflineDiagnosticSnapshotLifecycle.RestoreConfirmationPhrase);
        Assert.True(restored.ConfirmationAccepted);
        Assert.Equal(1, restored.RestoredCount);
        Assert.True(File.Exists(oldPath));

        var after = OfflineDiagnosticSnapshotLifecycle.InspectQuarantine(_directory);
        Assert.Equal(0, after.RestorableFileCount);
        Assert.Contains(
            "restoredFiles",
            File.ReadAllText(Path.Combine(
                _directory,
                OfflineDiagnosticSnapshotLifecycle.QuarantineDirectoryName,
                batch.BatchDirectoryName,
                "manifest.json")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Quarantine_manifest_mismatch_is_not_offered_for_restore()
    {
        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory,
            RuntimeMode = "simulator",
            ManageLocalServices = "false"
        });
        WriteValidSnapshot("mes-offline-diagnostics-tampered.json", report, DateTimeOffset.Parse("2026-07-01T10:00:00Z"));
        WriteValidSnapshot("mes-offline-diagnostics-tampered-keep.json", report, DateTimeOffset.Parse("2026-08-30T10:00:00Z"));
        var inventory = OfflineDiagnosticSnapshotLifecycle.Inspect(
            _directory,
            new OfflineDiagnosticSnapshotRetentionPolicy(RetainDays: 30, MinimumKeepFiles: 1),
            DateTimeOffset.Parse("2026-08-30T12:00:00Z"));
        var candidate = Item(inventory, "mes-offline-diagnostics-tampered.json");
        var moved = OfflineDiagnosticSnapshotLifecycle.MoveCandidatesToQuarantine(
            _directory,
            [candidate],
            OfflineDiagnosticSnapshotLifecycle.CleanupConfirmationPhrase,
            DateTimeOffset.Parse("2026-08-30T12:30:00Z"));
        var batch = Assert.Single(OfflineDiagnosticSnapshotLifecycle.InspectQuarantine(_directory).Items);
        var manifestPath = Path.Combine(
            _directory,
            OfflineDiagnosticSnapshotLifecycle.QuarantineDirectoryName,
            batch.BatchDirectoryName,
            "manifest.json");
        var manifest = File.ReadAllText(manifestPath).Replace(
            $"\"sizeBytes\": {candidate.SizeBytes}",
            $"\"sizeBytes\": {candidate.SizeBytes + 1}",
            StringComparison.Ordinal);
        File.WriteAllText(manifestPath, manifest);

        var inspected = Assert.Single(OfflineDiagnosticSnapshotLifecycle.InspectQuarantine(_directory).Items);
        Assert.Equal(OfflineDiagnosticQuarantineValidationState.Invalid, inspected.ValidationState);
        var file = Assert.Single(inspected.Files);
        Assert.False(file.IsRestorable);
        var result = OfflineDiagnosticSnapshotLifecycle.RestoreCandidatesFromQuarantine(
            _directory,
            [file],
            OfflineDiagnosticSnapshotLifecycle.RestoreConfirmationPhrase);
        Assert.Equal(0, result.RestoredCount);
    }

    [Fact]
    public void View_model_requires_restore_confirmation_and_rescans_after_recovery()
    {
        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory,
            RuntimeMode = "simulator",
            ManageLocalServices = "false"
        });
        var restorePath = Path.Combine(_directory, "mes-offline-diagnostics-vm-restore.json");
        WriteValidSnapshot("mes-offline-diagnostics-vm-restore.json", report, DateTimeOffset.Parse("2026-07-01T10:00:00Z"));
        WriteValidSnapshot("mes-offline-diagnostics-vm-keep-1.json", report, DateTimeOffset.Parse("2026-08-30T10:00:00Z"));
        WriteValidSnapshot("mes-offline-diagnostics-vm-keep-2.json", report, DateTimeOffset.Parse("2026-08-30T10:01:00Z"));
        var inventory = OfflineDiagnosticSnapshotLifecycle.Inspect(
            _directory,
            new OfflineDiagnosticSnapshotRetentionPolicy(RetainDays: 30, MinimumKeepFiles: 2),
            DateTimeOffset.Parse("2026-08-30T12:00:00Z"));
        var candidate = Item(inventory, "mes-offline-diagnostics-vm-restore.json");
        OfflineDiagnosticSnapshotLifecycle.MoveCandidatesToQuarantine(
            _directory,
            [candidate],
            OfflineDiagnosticSnapshotLifecycle.CleanupConfirmationPhrase,
            DateTimeOffset.Parse("2026-08-30T12:30:00Z"));

        var viewModel = new MesControlAgv.Wpf.ViewModels.DiagnosticsCenterViewModel(report, new OfflineDiagnosticAuditTrail())
        {
            SnapshotDirectory = _directory
        };
        viewModel.ScanSnapshotQuarantine();
        Assert.Equal(1, viewModel.QuarantineInventory.RestorableFileCount);
        Assert.False(viewModel.RestoreSnapshotCandidates().ConfirmationAccepted);

        viewModel.RestoreConfirmationChecked = true;
        viewModel.RestoreConfirmationPhrase = viewModel.RestoreConfirmationHint;
        var restored = viewModel.RestoreSnapshotCandidates();

        Assert.Equal(1, restored.RestoredCount);
        Assert.True(File.Exists(restorePath));
        Assert.Equal(0, viewModel.QuarantineInventory.RestorableFileCount);
        Assert.False(viewModel.RestoreConfirmationChecked);
        Assert.Equal(string.Empty, viewModel.RestoreConfirmationPhrase);
    }

    private void WriteValidSnapshot(string fileName, StartupConfigurationReport report, DateTimeOffset lastWriteAt)
    {
        var path = Path.Combine(_directory, fileName);
        OfflineDiagnosticExporter.Export(path, report, []);
        File.SetLastWriteTimeUtc(path, lastWriteAt.UtcDateTime);
    }

    private static OfflineDiagnosticSnapshotEntry Item(OfflineDiagnosticSnapshotInventory inventory, string fileName) =>
        Assert.Single(inventory.Items.Where(item => item.FileName == fileName));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
