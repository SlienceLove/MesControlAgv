using System.Text.Json;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class OfflineDiagnosticAuditTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "MesControlAgv.OfflineDiagnosticAuditTests",
        Guid.NewGuid().ToString("N"));

    public OfflineDiagnosticAuditTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Redactor_removes_urls_addresses_ports_paths_and_credentials()
    {
        var redacted = DiagnosticRedactor.Redact(
            "url=http://192.168.1.2:5041/api host=controller.local COM3 " +
            "path=\"D:\\site\\capture\\raw.json\" token=top-secret password:abc123");

        Assert.DoesNotContain("192.168.1.2", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("controller.local", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("COM3", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("D:\\site", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("top-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
        Assert.Contains("[URL]", redacted, StringComparison.Ordinal);
        Assert.Contains("[PATH]", redacted, StringComparison.Ordinal);
        Assert.Contains("[PORT]", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_trail_is_bounded_and_redacts_before_storing_entries()
    {
        var trail = new OfflineDiagnosticAuditTrail(capacity: 2);
        trail.Record("one", "refresh", "error", "token=first");
        trail.Record("two", "refresh", "error", "host=192.168.1.2");
        trail.Record("three", "retry", "ready", "ok");

        Assert.Equal(2, trail.Entries.Count);
        Assert.DoesNotContain(trail.Entries, entry => entry.Category == "one");
        Assert.DoesNotContain("192.168.1.2", trail.Entries[0].Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", trail.Entries[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Exporter_writes_atomic_redacted_json_without_bom()
    {
        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            RuntimeMode = "physical",
            MesBaseUrl = "http://192.168.1.11:5045/",
            AdapterBaseUrl = "http://192.168.1.12:5041/"
        });
        var trail = new OfflineDiagnosticAuditTrail();
        trail.Record("readiness", "refresh", "error", "endpoint=http://192.168.1.2/api token=secret");
        var path = Path.Combine(_directory, "diagnostics.json");

        OfflineDiagnosticExporter.Export(
            path,
            report,
            trail.Snapshot(),
            new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.FromHours(8)));

        var bytes = File.ReadAllBytes(path);
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal("mes.offline-diagnostics/1.0", document.RootElement.GetProperty("schemaVersion").GetString());
        var fieldPreflight = document.RootElement.GetProperty("fieldPreflight");
        Assert.Equal(OfflineDiagnosticVersions.FieldPreflightSchema, fieldPreflight.GetProperty("schemaVersion").GetString());
        Assert.False(fieldPreflight.GetProperty("canDetermineGo").GetBoolean());
        Assert.Contains(
            fieldPreflight.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "CONTROL_OWNER");
        Assert.DoesNotContain("192.168.1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
        Assert.Contains("[URL]", json, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));

        var imported = OfflineDiagnosticExporter.Read(path);
        Assert.True(imported.IsAccepted);
        Assert.False(imported.NeedsUpgrade);
        Assert.NotNull(imported.Document);
        Assert.Equal(OfflineDiagnosticVersions.RuleSchema, imported.Document.DiagnosticRuleVersion);
        Assert.NotNull(imported.Document.FieldPreflight);
        Assert.False(imported.Document.FieldPreflight.CanDetermineGo);
        Assert.Equal(OfflineDiagnosticVersions.FieldPreflightSchema, imported.Document.FieldPreflight.SchemaVersion);
        Assert.Null(imported.Document.ConfigurationDiff);
    }

    [Fact]
    public void Exporter_includes_a_redacted_configuration_diff_without_deriving_go()
    {
        var baselineReport = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory,
            RuntimeMode = "simulator",
            ManageLocalServices = "false"
        });
        var baselinePath = Path.Combine(_directory, "baseline.json");
        OfflineDiagnosticExporter.Export(baselinePath, baselineReport, [], DateTimeOffset.UtcNow);
        var baseline = OfflineDiagnosticExporter.Read(baselinePath).Document;
        Assert.NotNull(baseline);

        var currentReport = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory,
            RuntimeMode = "physical",
            ManageLocalServices = "false",
            AdapterConfigurationPath = Path.Combine(_directory, "adapter", "appsettings.json")
        });
        var currentPath = Path.Combine(_directory, "current.json");
        OfflineDiagnosticExporter.Export(
            currentPath,
            currentReport,
            [],
            DateTimeOffset.UtcNow,
            baseline);

        var json = File.ReadAllText(currentPath);
        using var document = JsonDocument.Parse(json);
        var diff = document.RootElement.GetProperty("configurationDiff");
        Assert.Equal(OfflineDiagnosticVersions.DiffSchema, diff.GetProperty("schemaVersion").GetString());
        Assert.True(diff.GetProperty("hasChanges").GetBoolean());
        Assert.False(diff.GetProperty("canDetermineGo").GetBoolean());
        Assert.Contains(
            diff.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "RUNTIME_MODE" &&
                    item.GetProperty("kind").GetString() == nameof(OfflineDiagnosticDiffKind.Changed));
        Assert.DoesNotContain(_directory, json, StringComparison.OrdinalIgnoreCase);

        var imported = OfflineDiagnosticExporter.Read(currentPath);
        Assert.True(imported.IsAccepted);
        Assert.NotNull(imported.Document?.ConfigurationDiff);
        Assert.False(imported.Document.ConfigurationDiff.CanDetermineGo);
    }

    [Fact]
    public void Diff_builder_is_stable_for_the_same_exported_snapshot()
    {
        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory,
            RuntimeMode = "simulator",
            ManageLocalServices = "false"
        });
        var path = Path.Combine(_directory, "same.json");
        OfflineDiagnosticExporter.Export(path, report, []);
        var baseline = OfflineDiagnosticExporter.Read(path).Document;
        Assert.NotNull(baseline);

        var diff = OfflineDiagnosticDiffBuilder.Build(report, baseline);

        Assert.True(diff.HasBaseline);
        Assert.False(diff.HasChanges);
        Assert.Equal(0, diff.ChangeCount);
        Assert.False(diff.CanDetermineGo);
        Assert.NotEmpty(diff.Items);
        Assert.All(diff.Items, item => Assert.Equal(OfflineDiagnosticDiffKind.Unchanged, item.Kind));
    }

    [Fact]
    public void Diagnostics_view_model_recomputes_diff_on_import_and_clears_it_locally()
    {
        var baselineReport = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory,
            RuntimeMode = "simulator",
            ManageLocalServices = "false"
        });
        var path = Path.Combine(_directory, "view-model-baseline.json");
        OfflineDiagnosticExporter.Export(path, baselineReport, []);

        var currentReport = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = _directory,
            RuntimeMode = "physical",
            ManageLocalServices = "false"
        });
        var viewModel = new DiagnosticsCenterViewModel(currentReport, new OfflineDiagnosticAuditTrail());

        var result = viewModel.Import(path);

        Assert.True(result.IsAccepted);
        Assert.True(viewModel.ConfigurationDiff.HasBaseline);
        Assert.True(viewModel.ConfigurationDiff.HasChanges);
        Assert.False(viewModel.ConfigurationDiff.CanDetermineGo);

        viewModel.SelectedDiffFilter = Assert.Single(viewModel.DiffFilterOptions.Where(option => option.Filter == OfflineDiagnosticDiffFilter.ChangesOnly));
        Assert.NotEmpty(viewModel.FilteredConfigurationDiffItems);
        Assert.All(viewModel.FilteredConfigurationDiffItems, item => Assert.True(item.IsChange));
        viewModel.DiffCodeFilter = "RUNTIME_MODE";
        Assert.All(viewModel.FilteredConfigurationDiffItems, item => Assert.Contains("RUNTIME_MODE", item.Code, StringComparison.Ordinal));
        viewModel.ReviewerLabel = "operator token=should-hide";
        viewModel.DiffReviewNote = "note path=\\server\u005cshare and host=192.168.1.2";
        Assert.True(viewModel.RecordDiffReview());
        var review = Assert.Single(viewModel.AuditEntries.Where(entry => entry.Action == "diff-review"));
        Assert.DoesNotContain("should-hide", review.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.1.2", review.Message, StringComparison.Ordinal);

        viewModel.Clear();

        Assert.False(viewModel.ConfigurationDiff.HasBaseline);
        Assert.Empty(viewModel.ConfigurationDiff.Items);
    }

    [Fact]
    public void Reader_accepts_legacy_files_with_an_upgrade_notice_and_rejects_unknown_major_versions()
    {
        var legacyPath = Path.Combine(_directory, "legacy.json");
        File.WriteAllText(legacyPath, "{\"startup\":{},\"audit\":[]}");
        var legacy = OfflineDiagnosticExporter.Read(legacyPath);

        var unknownPath = Path.Combine(_directory, "unknown.json");
        File.WriteAllText(unknownPath, "{\"schemaVersion\":\"mes.offline-diagnostics/2.0\"}");
        var unknown = OfflineDiagnosticExporter.Read(unknownPath);

        Assert.True(legacy.IsAccepted);
        Assert.True(legacy.NeedsUpgrade);
        Assert.Contains("旧版", legacy.Message, StringComparison.Ordinal);
        Assert.False(unknown.IsAccepted);
        Assert.False(unknown.NeedsUpgrade);
    }

    [Fact]
    public void Reader_rejects_an_imported_snapshot_that_claims_field_go()
    {
        var path = Path.Combine(_directory, "claims-go.json");
        File.WriteAllText(
            path,
            """
            {
              "schemaVersion": "mes.offline-diagnostics/1.0",
              "diagnosticRuleVersion": "mes.startup-diagnostics/1.0",
              "fieldPreflight": {
                "schemaVersion": "mes.field-preflight/1.0",
                "canDetermineGo": true,
                "items": []
              },
              "audit": []
            }
            """);

        var result = OfflineDiagnosticExporter.Read(path);

        Assert.False(result.IsAccepted);
        Assert.Contains("GO", result.Message, StringComparison.Ordinal);
        Assert.Null(result.Document);
    }

    [Fact]
    public void Reader_keeps_additive_current_schema_compatible_when_diff_sections_are_absent()
    {
        var path = Path.Combine(_directory, "additive-legacy.json");
        File.WriteAllText(
            path,
            """
            {
              "schemaVersion": "mes.offline-diagnostics/1.0",
              "diagnosticRuleVersion": "mes.startup-diagnostics/1.0",
              "startup": {},
              "audit": []
            }
            """);

        var result = OfflineDiagnosticExporter.Read(path);

        Assert.True(result.IsAccepted);
        Assert.False(result.NeedsUpgrade);
        Assert.Contains("未包含", result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.Document);
        Assert.Null(result.Document.FieldPreflight);
        Assert.Null(result.Document.ConfigurationDiff);
    }

    [Fact]
    public async Task Main_view_model_records_failure_and_user_retry_transitions()
    {
        var client = new FakeMesClient([]) { ReadinessException = new InvalidOperationException("host=192.168.1.2") };
        var trail = new OfflineDiagnosticAuditTrail();
        using var viewModel = new MainViewModel(client, diagnosticAudit: trail);

        await viewModel.Readiness.RefreshAsync();
        client.ReadinessException = null;
        await viewModel.Readiness.RefreshAsync();

        Assert.Contains(trail.Entries, entry => entry.Category == "readiness" && entry.Outcome == "Error");
        Assert.Contains(trail.Entries, entry => entry.Category == "readiness" && entry.Action == "retry");
        Assert.DoesNotContain(trail.Entries, entry => entry.Message.Contains("192.168.1.2", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
