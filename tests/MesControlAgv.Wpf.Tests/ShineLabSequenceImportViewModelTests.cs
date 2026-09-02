using System.IO;
using System.Text;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class ShineLabSequenceImportViewModelTests : IDisposable
{
    private readonly List<string> _temporaryFiles = [];

    private static readonly DateTimeOffset FixedClock =
        new(2026, 8, 25, 10, 15, 0, TimeSpan.FromHours(8));

    private const string ValidCsv =
        "样品名称,样品类型,样品等级,处理方法,清除校正,进样体积,色谱方法\n" +
        "水样-01,未知样品,1,阴离子标准法,否,25,阴离子常规\n" +
        "水样-02,未知样品,1,阴离子标准法,否,25,阴离子常规\n";

    private sealed class RecordingHandoff : IShineLabBatchHandoff
    {
        private readonly ShineLabBatchStatus _status;

        public RecordingHandoff(ShineLabBatchStatus status = ShineLabBatchStatus.Verified) => _status = status;

        public List<ShineLabBatchManifest> Submissions { get; } = [];
        public string? LastCsv { get; private set; }

        public Task<ShineLabBatchReceipt> SubmitAsync(
            ShineLabBatchManifest manifest,
            string csvContent,
            CancellationToken cancellationToken)
        {
            Submissions.Add(manifest);
            LastCsv = csvContent;
            return Task.FromResult(new ShineLabBatchReceipt
            {
                BatchId = manifest.BatchId,
                CsvSha256 = manifest.CsvSha256,
                TargetSequence = manifest.TargetSequence,
                ExpectedRows = manifest.ExpectedRows,
                ObservedRows = manifest.ExpectedRows,
                Status = _status,
                ComparisonStatus = _status == ShineLabBatchStatus.Verified ? "Match" : null,
                Error = _status == ShineLabBatchStatus.Unknown ? "导出比对未产出结果" : null
            });
        }

        public Task<ShineLabBatchReceipt?> TryReadReceiptAsync(string batchId, CancellationToken cancellationToken) =>
            Task.FromResult<ShineLabBatchReceipt?>(null);
    }

    private string WriteCsv(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"shinelab-vm-{Guid.NewGuid():N}.csv");
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(content));
        _temporaryFiles.Add(path);
        return path;
    }

    private ShineLabSequenceImportViewModel CreateLoadedViewModel(
        IShineLabBatchHandoff? handoff,
        string csv = ValidCsv)
    {
        var viewModel = new ShineLabSequenceImportViewModel(() => handoff, () => FixedClock);
        viewModel.Load(WriteCsv(csv));
        return viewModel;
    }

    [Fact]
    public void Load_projects_parsed_tasks_into_display_rows()
    {
        var viewModel = CreateLoadedViewModel(new RecordingHandoff());

        Assert.True(viewModel.HasTasks);
        Assert.Empty(viewModel.Issues);
        Assert.Equal([1, 2], viewModel.SampleTasks.Select(row => row.DisplayIndex));
        Assert.Equal("水样-01", viewModel.SampleTasks[0].SampleName);
        Assert.Equal("25 μL", viewModel.SampleTasks[0].InjectionVolume);
    }

    [Fact]
    public void Cannot_submit_until_append_is_confirmed_and_sequence_named()
    {
        var viewModel = CreateLoadedViewModel(new RecordingHandoff());
        Assert.False(viewModel.CanSubmit);

        viewModel.AllowAppend = true;
        Assert.False(viewModel.CanSubmit);

        viewModel.TargetSequence = "seq-A";
        Assert.True(viewModel.CanSubmit);
    }

    [Fact]
    public void Cannot_submit_when_the_source_file_has_issues()
    {
        var viewModel = CreateLoadedViewModel(
            new RecordingHandoff(),
            "样品名称,样品类型,进样体积\n水样-01,未知,25\n");

        viewModel.AllowAppend = true;
        viewModel.TargetSequence = "seq-A";

        Assert.False(viewModel.CanSubmit);
        Assert.NotEmpty(viewModel.Issues);
    }

    [Fact]
    public void Load_reports_parse_failures_as_issues_instead_of_throwing()
    {
        var viewModel = new ShineLabSequenceImportViewModel(() => new RecordingHandoff(), () => FixedClock);

        viewModel.Load(WriteCsv("样品名称,色谱方法\n水样-01,阴离子常规\n"));

        Assert.False(viewModel.HasTasks);
        Assert.False(viewModel.CanSubmit);
        Assert.Contains(viewModel.Issues, issue => issue.Contains("样品类型"));
    }

    [Fact]
    public async Task Submit_sends_a_manifest_that_never_requests_a_run()
    {
        var handoff = new RecordingHandoff();
        var viewModel = CreateLoadedViewModel(handoff);
        viewModel.AllowAppend = true;
        viewModel.TargetSequence = " seq-A ";
        viewModel.OperatorName = "张三";

        await viewModel.SubmitAsync();

        var manifest = Assert.Single(handoff.Submissions);
        Assert.False(manifest.AllowRun);
        Assert.True(manifest.AllowAppend);
        Assert.Equal("seq-A", manifest.TargetSequence);
        Assert.Equal("张三", manifest.CreatedBy);
        Assert.Equal(2, manifest.ExpectedRows);
        Assert.Equal("batch-20260825-101500", manifest.BatchId);
        Assert.Equal($"{manifest.BatchId}.csv", manifest.CsvFileName);
    }

    [Fact]
    public async Task Submitted_manifest_hash_matches_the_submitted_csv()
    {
        var handoff = new RecordingHandoff();
        var viewModel = CreateLoadedViewModel(handoff);
        viewModel.AllowAppend = true;
        viewModel.TargetSequence = "seq-A";

        await viewModel.SubmitAsync();

        var manifest = Assert.Single(handoff.Submissions);
        Assert.Equal(ShineLabCsvWriter.ComputeSha256(handoff.LastCsv!), manifest.CsvSha256);
        Assert.StartsWith(ShineLabCsvWriter.CanonicalHeaderLine, handoff.LastCsv);
    }

    [Fact]
    public async Task Verified_receipt_is_the_only_success_state()
    {
        var viewModel = CreateLoadedViewModel(new RecordingHandoff(ShineLabBatchStatus.Verified));
        viewModel.AllowAppend = true;
        viewModel.TargetSequence = "seq-A";

        await viewModel.SubmitAsync();

        Assert.True(viewModel.IsVerified);
        Assert.False(viewModel.RequiresManualCheck);
        Assert.Contains("导入成功", viewModel.Status);
    }

    [Theory]
    [InlineData(ShineLabBatchStatus.Submitted)]
    [InlineData(ShineLabBatchStatus.Unknown)]
    public async Task Non_verified_receipt_demands_manual_confirmation(ShineLabBatchStatus status)
    {
        var viewModel = CreateLoadedViewModel(new RecordingHandoff(status));
        viewModel.AllowAppend = true;
        viewModel.TargetSequence = "seq-A";

        await viewModel.SubmitAsync();

        Assert.False(viewModel.IsVerified);
        Assert.True(viewModel.RequiresManualCheck);
        Assert.Contains("未判定成功", viewModel.Status);
    }

    [Fact]
    public async Task Submit_without_a_configured_inbox_reports_configuration_instead_of_failing_silently()
    {
        var viewModel = CreateLoadedViewModel(handoff: null);
        viewModel.AllowAppend = true;
        viewModel.TargetSequence = "seq-A";

        await viewModel.SubmitAsync();

        Assert.Contains("SHINELAB_INBOX_PATH", viewModel.Status);
        Assert.Null(viewModel.Receipt);
    }

    [Fact]
    public async Task Submit_is_a_no_op_while_gating_conditions_are_unmet()
    {
        var handoff = new RecordingHandoff();
        var viewModel = CreateLoadedViewModel(handoff);

        await viewModel.SubmitAsync();

        Assert.Empty(handoff.Submissions);
    }

    [Fact]
    public async Task Clear_resets_append_confirmation_and_receipt()
    {
        var viewModel = CreateLoadedViewModel(new RecordingHandoff());
        viewModel.AllowAppend = true;
        viewModel.TargetSequence = "seq-A";
        await viewModel.SubmitAsync();

        viewModel.Clear();

        Assert.False(viewModel.AllowAppend);
        Assert.False(viewModel.HasTasks);
        Assert.Null(viewModel.Receipt);
        Assert.Empty(viewModel.SampleTasks);
        Assert.False(viewModel.CanSubmit);
    }

    [Fact]
    public void Csv_preview_is_rejected_before_a_valid_file_is_loaded()
    {
        var viewModel = new ShineLabSequenceImportViewModel(() => new RecordingHandoff(), () => FixedClock);

        Assert.Throws<InvalidOperationException>(() => viewModel.BuildCsvPreview());
    }

    public void Dispose()
    {
        foreach (var path in _temporaryFiles.Where(File.Exists))
        {
            File.Delete(path);
        }
    }
}
