using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MesControlAgv.Contracts;
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

    private const string ValidDirectCsv =
        "sampleID,sampleName,sampleType,sampleLevel,processMethod,clearCalibration,cycleCount,injectionVolume,injectionVolumeUnit,position,mPos,channel,instrumentMethod,processingMethod,detectionMethod,blank,chromatographyMethod\n" +
        "LOCAL-STD-001,标准测试样,标准样,1,阴离子标准法,否,1,25,uL,1,1,A,AS18-M01,IC-P01,Normal,否,阴离子常规\n";

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
        string csv = ValidCsv,
        IMesClient? mes = null)
    {
        var viewModel = new ShineLabSequenceImportViewModel(() => handoff, () => FixedClock, mes);
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
    public void Load_projects_direct_rows_and_builds_protocol_preview_without_sending()
    {
        var csv = string.Join('\n',
            "sampleID,sampleName,sampleType,sampleLevel,processMethod,clearCalibration,cycleCount,injectionVolume,injectionVolumeUnit,position,mPos,channel,instrumentMethod,processingMethod,detectionMethod,blank,chromatographyMethod",
            "LOCAL-STD-001,Standard test,\u6807\u51c6\u6837,1,Anion standard,\u5426,1,25,uL,1,1,A,AS18-M01,IC-P01,Normal,\u5426,Anion routine");
        var viewModel = CreateLoadedViewModel(new RecordingHandoff(), csv);

        Assert.True(viewModel.HasDirectTasks);
        Assert.Empty(viewModel.DirectIssues);
        var row = Assert.Single(viewModel.DirectTasks);
        Assert.Equal("LOCAL-STD-001", row.SampleId);
        Assert.Equal("\u6807\u51c6\u6837 (1)", row.SampleType);
        Assert.Equal("1", row.Position);
        Assert.Equal("A", row.Channel);
        Assert.Equal("AS18-M01 / IC-P01", row.Methods);
        Assert.Equal("25 uL", row.InjectionVolume);
        Assert.Contains("\u5c1a\u672a\u53d1\u9001 TCP", viewModel.DirectPreviewStatus, StringComparison.Ordinal);

        using var preview = JsonDocument.Parse(viewModel.DirectProtocolPreview);
        Assert.Equal("Config", preview.RootElement.GetProperty("config").GetProperty("strMethod").GetString());
        var configBody = preview.RootElement.GetProperty("config").GetProperty("body");
        Assert.Equal(2, configBody.EnumerateObject().Count());
        Assert.DoesNotContain(configBody.EnumerateObject(), property => property.NameEquals("task_uuid"));
        Assert.Equal(6, configBody.GetProperty("sampleData")[0].EnumerateObject().Count());
        Assert.Equal(0, preview.RootElement.GetProperty("command").GetProperty("body").GetProperty("action").GetInt32());
    }

    [Fact]
    public void Selecting_a_direct_row_rebuilds_a_single_row_protocol_preview()
    {
        var csv = ValidDirectCsv +
            "LOCAL-BLANK-002,空白测试样,空白样,1,阴离子标准法,否,1,25,uL,2,2,A,AS18-M01,IC-P01,Normal,是,阴离子常规\n";
        var viewModel = CreateLoadedViewModel(new RecordingHandoff(), csv);

        Assert.Equal(2, viewModel.DirectTasks.Count);
        viewModel.SelectedDirectTask = viewModel.DirectTasks[1];

        using var preview = JsonDocument.Parse(viewModel.DirectProtocolPreview);
        var samples = preview.RootElement.GetProperty("config").GetProperty("body").GetProperty("sampleData");
        Assert.Equal(1, samples.GetArrayLength());
        Assert.Equal("LOCAL-BLANK-002", samples[0].GetProperty("sampleID").GetString());
        Assert.Equal(3, samples[0].GetProperty("type").GetInt32());
    }

    [Fact]
    public async Task Single_config_preflight_requires_confirmation_and_sends_config_only_when_device_is_idle()
    {
        var mes = new FakeMesClient([])
        {
            ShineLabDeviceStatuses =
            [
                new ShineLabDeviceStatusResponse(
                    "STN61_01", "ShineLab", true, 0, "Connected", false,
                    null, null, null, "A", null, null, null, null, null, FixedClock)
            ]
        };
        var viewModel = CreateLoadedViewModel(new RecordingHandoff(), ValidDirectCsv, mes);

        Assert.False(viewModel.CanSendSingleConfigPreflight);
        viewModel.AllowSingleConfigPreflight = true;
        Assert.True(viewModel.CanSendSingleConfigPreflight);

        await viewModel.SendSingleConfigPreflightAsync();

        var request = Assert.IsType<ShineLabTaskCreateRequest>(mes.LastShineLabTaskCreate);
        Assert.Equal("STN61_01", request.EquipmentCode);
        var sample = Assert.Single(request.SampleData);
        Assert.Equal("LOCAL-STD-001", sample.SampleId);
        Assert.Equal("1", sample.Type);
        Assert.Equal(1, sample.Position);
        Assert.Equal("A", sample.Channel);
        Assert.Equal("AS18-M01", sample.InstrumentMethod);
        Assert.Equal("IC-P01", sample.ProcessingMethod);
        Assert.Equal(request.TaskUuid, mes.LastConfiguredShineLabTaskUuid);
        Assert.Null(mes.LastShineLabCommand);
        Assert.Contains("Config Success", viewModel.DirectPreflightStatus, StringComparison.Ordinal);
        Assert.Contains("未发送 Command", viewModel.DirectPreflightStatus, StringComparison.Ordinal);
        Assert.False(viewModel.AllowSingleConfigPreflight);
    }

    [Fact]
    public async Task Single_config_preflight_stops_before_task_creation_when_device_is_offline()
    {
        var mes = new FakeMesClient([]);
        var viewModel = CreateLoadedViewModel(new RecordingHandoff(), ValidDirectCsv, mes);
        viewModel.AllowSingleConfigPreflight = true;

        await viewModel.SendSingleConfigPreflightAsync();

        Assert.Null(mes.LastShineLabTaskCreate);
        Assert.Null(mes.LastConfiguredShineLabTaskUuid);
        Assert.Contains("未在线", viewModel.DirectPreflightStatus, StringComparison.Ordinal);
        Assert.Contains("未发送 Config", viewModel.DirectPreflightStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Single_config_preflight_stops_before_task_creation_when_device_is_busy()
    {
        var mes = new FakeMesClient([])
        {
            ShineLabDeviceStatuses =
            [
                new ShineLabDeviceStatusResponse(
                    "STN61_01", "ShineLab", true, 1, "Running", true,
                    "task-active", "S-01", "Running sample", "A", 1, "Injecting", 20,
                    null, null, FixedClock)
            ]
        };
        var viewModel = CreateLoadedViewModel(new RecordingHandoff(), ValidDirectCsv, mes);
        viewModel.AllowSingleConfigPreflight = true;

        await viewModel.SendSingleConfigPreflightAsync();

        Assert.Null(mes.LastShineLabTaskCreate);
        Assert.Contains("不是空闲状态", viewModel.DirectPreflightStatus, StringComparison.Ordinal);
        Assert.Contains("未发送 Config", viewModel.DirectPreflightStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Single_config_preflight_surfaces_unexpected_config_failure_and_reuses_task_identity_on_retry()
    {
        var mes = new FakeMesClient([])
        {
            ShineLabDeviceStatuses =
            [
                new ShineLabDeviceStatusResponse(
                    "STN61_01", "ShineLab", true, 0, "Connected", false,
                    null, null, null, "A", null, null, null, null, null, FixedClock)
            ],
            ShineLabConfigureException = new FormatException("invalid Config response")
        };
        var viewModel = CreateLoadedViewModel(new RecordingHandoff(), ValidDirectCsv, mes);
        viewModel.AllowSingleConfigPreflight = true;

        await viewModel.SendSingleConfigPreflightAsync();

        var firstTaskUuid = Assert.IsType<ShineLabTaskCreateRequest>(mes.LastShineLabTaskCreate).TaskUuid;
        Assert.Contains("invalid Config response", viewModel.DirectPreflightStatus, StringComparison.Ordinal);
        Assert.False(viewModel.AllowSingleConfigPreflight);
        Assert.False(viewModel.IsDirectPreflighting);
        Assert.Null(mes.LastShineLabCommand);

        mes.ShineLabConfigureException = null;
        viewModel.AllowSingleConfigPreflight = true;
        await viewModel.SendSingleConfigPreflightAsync();

        Assert.Equal(2, mes.ShineLabTaskCreateCallCount);
        Assert.Equal(firstTaskUuid, mes.LastShineLabTaskCreate!.TaskUuid);
        Assert.Equal(firstTaskUuid, mes.LastConfiguredShineLabTaskUuid);
        Assert.Contains("Config Success", viewModel.DirectPreflightStatus, StringComparison.Ordinal);
        Assert.Null(mes.LastShineLabCommand);
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
    public void Load_reports_malformed_xlsx_xml_instead_of_crashing_the_ui()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shinelab-vm-{Guid.NewGuid():N}.xlsx");
        _temporaryFiles.Add(path);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("xl/workbook.xml");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("<workbook><sheet>");
        }
        var viewModel = new ShineLabSequenceImportViewModel(
            () => new RecordingHandoff(),
            () => FixedClock);

        viewModel.Load(path);

        Assert.False(viewModel.HasTasks);
        Assert.False(viewModel.HasDirectTasks);
        Assert.NotEmpty(viewModel.Issues);
        Assert.Contains("文件解析失败", viewModel.Status, StringComparison.Ordinal);
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
        Assert.Empty(viewModel.DirectTasks);
        Assert.Empty(viewModel.DirectIssues);
        Assert.Empty(viewModel.DirectProtocolPreview);
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
