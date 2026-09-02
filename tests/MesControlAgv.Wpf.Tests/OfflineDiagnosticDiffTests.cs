using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class OfflineDiagnosticDiffTests
{
    [Fact]
    public void Filterer_supports_status_filters_and_case_insensitive_code_search()
    {
        var items = new[]
        {
            Item("AGV_PUSH", OfflineDiagnosticDiffKind.Changed),
            Item("MAP_IDENTITY", OfflineDiagnosticDiffKind.Unchanged),
            Item("VISION_MODULE", OfflineDiagnosticDiffKind.Added),
            Item("OLD_SETTING", OfflineDiagnosticDiffKind.Removed)
        };

        Assert.Equal(4, OfflineDiagnosticDiffFilterer.Apply(items, OfflineDiagnosticDiffFilter.All, null).Count);
        Assert.Equal(3, OfflineDiagnosticDiffFilterer.Apply(items, OfflineDiagnosticDiffFilter.ChangesOnly, null).Count);
        Assert.Single(OfflineDiagnosticDiffFilterer.Apply(items, OfflineDiagnosticDiffFilter.Changed, null));
        Assert.Single(OfflineDiagnosticDiffFilterer.Apply(items, OfflineDiagnosticDiffFilter.Added, null));
        Assert.Single(OfflineDiagnosticDiffFilterer.Apply(items, OfflineDiagnosticDiffFilter.Removed, null));
        Assert.Single(OfflineDiagnosticDiffFilterer.Apply(items, OfflineDiagnosticDiffFilter.Unchanged, null));
        var searched = OfflineDiagnosticDiffFilterer.Apply(items, OfflineDiagnosticDiffFilter.All, "agv_push");
        Assert.Single(searched);
        Assert.Equal("AGV_PUSH", searched[0].Code);
    }

    [Fact]
    public void Empty_diff_report_is_explicitly_local_and_not_a_go_decision()
    {
        var report = OfflineDiagnosticDiffReport.Empty;

        Assert.False(report.HasBaseline);
        Assert.False(report.HasChanges);
        Assert.False(report.CanDetermineGo);
        Assert.Contains("GO", report.DecisionText, StringComparison.Ordinal);
    }

    private static OfflineDiagnosticDiffItem Item(string code, OfflineDiagnosticDiffKind kind) =>
        new(code, code, kind, "old", "new", "old-status", "new-status", "detail");
}
