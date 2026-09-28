using MesControlAgv.Contracts.Materials;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class DetectionStandardsViewModelTests
{
    [Fact]
    public async Task Editing_current_standard_sends_base_revision_and_creates_new_revision()
    {
        var client = new StubClient();
        var model = new DetectionStandardsViewModel(client);
        model.MethodCode = "GB/T 100";
        model.Name = "水样测试";
        model.Indicators[0].Name = "氯离子";
        model.Indicators[0].Unit = "mg/L";
        model.Indicators[0].UpperLimit = "10";

        await model.SaveAsync();
        Assert.Equal(1, model.SelectedStandard?.Revision);
        Assert.Null(client.LastSave?.BaseRevisionId);
        model.Indicators[0].UpperLimit = "5";
        await model.SaveAsync();

        Assert.Equal(2, model.SelectedStandard?.Revision);
        Assert.Equal(client.Revisions[0].RevisionId, client.LastSave?.BaseRevisionId);
        Assert.Equal(5m, client.LastSave?.Indicators[0].UpperLimit);
        Assert.True(model.IsMethodCodeLocked);
    }

    [Fact]
    public async Task Sample_assessment_previews_then_saves_once_with_selected_indicator()
    {
        var client = new StubClient();
        var standard = await client.SaveDetectionStandardAsync(new SaveDetectionStandardRequest
        {
            MethodCode = "HJ 123",
            Name = "水样测试",
            Actor = "test",
            Indicators = [new DetectionIndicatorRule { Name = "氯离子", Unit = "mg/L", UpperLimit = 5 }]
        }, CancellationToken.None);
        var sample = new SampleMaterial { SampleId = Guid.NewGuid(), Barcode = "S-123" };
        var model = new DetectionAssessmentViewModel(client, sample, "tester");
        await model.LoadAsync();
        model.SelectedStandard = standard;
        model.Measurements[0].IsSelected = true;
        model.Measurements[0].ValueText = "4";

        Assert.False(model.SaveCommand.CanExecute(null));

        await model.PreviewAsync();
        Assert.Equal(DetectionVerdict.Pass, model.Preview?.Verdict);
        Assert.Equal("合格", model.Measurements[0].PreviewVerdict);
        Assert.True(model.SaveCommand.CanExecute(null));
        model.Measurements[0].ValueText = "4.1";
        Assert.Null(model.Preview);
        Assert.Equal(string.Empty, model.Measurements[0].PreviewVerdict);
        Assert.False(model.SaveCommand.CanExecute(null));
        await model.PreviewAsync();
        await model.SaveAsync();

        Assert.True(model.IsSaved);
        Assert.False(model.SaveCommand.CanExecute(null));
        Assert.Equal(sample.SampleId, client.LastAssessment?.SampleId);
        Assert.Single(model.History);
        Assert.Equal("合格", model.History[0].VerdictText);
    }

    [Fact]
    public async Task Inflight_preview_is_discarded_when_measurement_changes()
    {
        var client = new StubClient();
        var standard = await client.SaveDetectionStandardAsync(new SaveDetectionStandardRequest
        {
            MethodCode = "HJ 124", Name = "水样测试", Actor = "test",
            Indicators = [new DetectionIndicatorRule { Name = "氯离子", Unit = "mg/L", UpperLimit = 5 }]
        }, CancellationToken.None);
        var model = new DetectionAssessmentViewModel(client,
            new SampleMaterial { SampleId = Guid.NewGuid(), Barcode = "S-124" }, "tester");
        await model.LoadAsync();
        model.SelectedStandard = standard;
        model.Measurements[0].IsSelected = true;
        model.Measurements[0].ValueText = "4";
        client.PreviewGate = new TaskCompletionSource<DetectionAssessment>();

        var pending = model.PreviewAsync();
        model.Measurements[0].ValueText = "6";
        client.PreviewGate.SetResult(client.BuildAssessment(client.PendingPreview!));
        await pending;

        Assert.Null(model.Preview);
        Assert.False(model.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Single_sample_create_inherits_existing_batch_method()
    {
        var client = new StubClient();
        var standard = await client.SaveDetectionStandardAsync(new SaveDetectionStandardRequest
        {
            MethodCode = "HJ 125", Name = "批次方法", Actor = "test",
            Indicators = [new DetectionIndicatorRule { Name = "氯离子", Unit = "mg/L", UpperLimit = 5 }]
        }, CancellationToken.None);
        var samples = new[] { new SampleMaterial
        {
            Barcode = "S-OLD", SampleBatchId = "B-ONE", DetectionMethodCode = standard.MethodCode
        } };
        var model = new CreateSampleViewModel(client, samples, "tester", () => Task.CompletedTask);
        await model.LoadAsync();
        model.BatchId = "B-ONE";
        model.Barcode = "S-NEW";

        Assert.True(model.IsMethodLocked);
        Assert.Equal(standard.MethodCode, model.SelectedStandard?.MethodCode);
        await model.SaveAsync();

        Assert.True(model.IsSaved);
        Assert.Equal("B-ONE", client.LastMaterialImport?.Samples[0].SampleBatchId);
        Assert.Null(client.LastMaterialImport?.Samples[0].DetectionMethodCode);
    }

    [Fact]
    public async Task Single_sample_create_inherits_inactive_bound_method()
    {
        var client = new StubClient();
        var samples = new[] { new SampleMaterial
        {
            Barcode = "S-OLD", SampleBatchId = "B-STOPPED", DetectionMethodCode = "HJ STOPPED"
        } };
        var model = new CreateSampleViewModel(client, samples, "tester", () => Task.CompletedTask);
        await model.LoadAsync();
        model.BatchId = "B-STOPPED";
        model.Barcode = "S-NEW";

        Assert.True(model.IsMethodLocked);
        Assert.Equal("HJ STOPPED", model.BoundMethodCode);
        Assert.Null(model.SelectedStandard);
        await model.SaveAsync();

        Assert.True(model.IsSaved);
        Assert.Null(client.LastMaterialImport?.Samples[0].DetectionMethodCode);
    }

    [Fact]
    public async Task Single_consumable_create_uses_full_receive_contract()
    {
        var client = new StubClient();
        var model = new CreateConsumableViewModel(client, [], "tester", () => Task.CompletedTask)
        {
            MaterialCode = "BUFFER-1", MaterialName = "缓冲液", LotCode = "LOT-1",
            Quantity = "5", Unit = "mL", Barcode = "LOT-BARCODE-1",
            Specification = "100mL", LocationCode = "DEFAULT",
            ManufactureDate = new DateTime(2026, 9, 1), ExpiryDate = new DateTime(2027, 9, 1)
        };

        await model.SaveAsync();

        Assert.True(model.IsSaved);
        Assert.Equal("LOT-BARCODE-1", client.LastReceive?.Barcode);
        Assert.Equal("100mL", client.LastReceive?.Specification);
        Assert.Equal(5m, client.LastReceive?.Quantity);
        Assert.Equal(2027, client.LastReceive?.ExpiryDate?.Year);
    }

    [Fact]
    public async Task Bound_sample_selects_its_method_in_assessment_dialog()
    {
        var client = new StubClient();
        var standard = await client.SaveDetectionStandardAsync(new SaveDetectionStandardRequest
        {
            MethodCode = "HJ 126", Name = "批次方法", Actor = "test",
            Indicators = [new DetectionIndicatorRule { Name = "氯离子", Unit = "mg/L", UpperLimit = 5 }]
        }, CancellationToken.None);
        var model = new DetectionAssessmentViewModel(client, new SampleMaterial
        {
            SampleId = Guid.NewGuid(), SampleBatchId = "B-TWO", DetectionMethodCode = standard.MethodCode
        }, "tester");

        await model.LoadAsync();

        Assert.Equal(standard.MethodCode, model.SelectedStandard?.MethodCode);
        Assert.False(model.IsMethodSelectionEnabled);
    }

    private sealed class StubClient : IMesClient
    {
        public List<DetectionStandardRevision> Revisions { get; } = [];
        public SaveDetectionStandardRequest? LastSave { get; private set; }
        public DetectionAssessmentRequest? LastAssessment { get; private set; }
        public DetectionAssessmentRequest? PendingPreview { get; private set; }
        public TaskCompletionSource<DetectionAssessment>? PreviewGate { get; set; }
        public MaterialImportRequest? LastMaterialImport { get; private set; }
        public ReceiveMaterialRequest? LastReceive { get; private set; }

        public Task<IReadOnlyList<DetectionStandardRevision>> GetDetectionStandardsAsync(
            string? search, bool includeHistory, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<DetectionStandardRevision>>(Revisions.Where(item =>
                (includeHistory || item.IsActive) &&
                (string.IsNullOrWhiteSpace(search) || item.MethodCode.Contains(search, StringComparison.OrdinalIgnoreCase)))
                .ToList());

        public Task<DetectionStandardRevision> SaveDetectionStandardAsync(
            SaveDetectionStandardRequest request, CancellationToken token)
        {
            LastSave = request;
            var previous = Revisions.LastOrDefault(item => item.MethodCode == request.MethodCode);
            if (previous is not null)
            {
                var index = Revisions.IndexOf(previous);
                Revisions[index] = previous with { IsActive = false };
            }
            var saved = new DetectionStandardRevision
            {
                RevisionId = Guid.NewGuid(), MethodCode = request.MethodCode, Name = request.Name,
                Revision = (previous?.Revision ?? 0) + 1, IsActive = true,
                Indicators = request.Indicators.Select(item => item with { IndicatorId = Guid.NewGuid() }).ToList()
            };
            Revisions.Add(saved);
            return Task.FromResult(saved);
        }

        public Task<IReadOnlyList<DetectionAssessment>> GetDetectionAssessmentHistoryAsync(
            Guid sampleId, CancellationToken token) => Task.FromResult<IReadOnlyList<DetectionAssessment>>([]);

        public Task<MaterialImportPreview> PreviewMaterialImportAsync(
            MaterialImportRequest request, CancellationToken token) =>
            Task.FromResult(new MaterialImportPreview { RequestId = request.RequestId, AcceptedCount = 1 });

        public Task<MaterialCommandResult<MaterialImportResult>> ImportMaterialsAsync(
            MaterialImportRequest request, CancellationToken token)
        {
            LastMaterialImport = request;
            return Task.FromResult(new MaterialCommandResult<MaterialImportResult>
            {
                RequestId = request.RequestId,
                Data = new MaterialImportResult { RequestId = request.RequestId, ImportedSamples = request.Samples.Count }
            });
        }

        public Task<MaterialCommandResult<MaterialLotInventory>> ReceiveMaterialAsync(
            ReceiveMaterialRequest request, CancellationToken token)
        {
            LastReceive = request;
            return Task.FromResult(new MaterialCommandResult<MaterialLotInventory>
            {
                RequestId = request.RequestId,
                Data = new MaterialLotInventory { MaterialCode = request.MaterialCode, LotCode = request.LotCode }
            });
        }

        public Task<DetectionAssessment> PreviewDetectionAssessmentAsync(
            DetectionAssessmentRequest request, CancellationToken token)
        {
            PendingPreview = request;
            return PreviewGate?.Task ?? Task.FromResult(Assess(request));
        }

        public Task<DetectionAssessment> SaveDetectionAssessmentAsync(
            DetectionAssessmentRequest request, CancellationToken token)
        {
            LastAssessment = request;
            return Task.FromResult(Assess(request));
        }

        public DetectionAssessment BuildAssessment(DetectionAssessmentRequest request) => Assess(request);

        private static DetectionAssessment Assess(DetectionAssessmentRequest request) => new()
        {
            RequestId = request.RequestId,
            SampleId = request.SampleId,
            RevisionId = request.RevisionId,
            Verdict = DetectionVerdict.Pass,
            Items = [new DetectionAssessmentItem
            {
                IndicatorId = request.Measurements[0].IndicatorId,
                Name = "氯离子",
                Value = request.Measurements[0].Value,
                Unit = request.Measurements[0].Unit,
                Verdict = DetectionVerdict.Pass,
                Reason = "符合合格范围"
            }]
        };

        public Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<DashboardTask>>([]);
        public Task<KpiDashboard> GetKpiDashboardAsync(DateOnly date, CancellationToken token) =>
            Task.FromResult<KpiDashboard>(null!);
        public Task<DashboardTaskDetail?> GetTaskDetailAsync(Guid taskId, CancellationToken token) =>
            Task.FromResult<DashboardTaskDetail?>(null);
        public Task<AgvDashboardSnapshot> GetAgvSnapshotAsync(CancellationToken token) =>
            Task.FromResult(new AgvDashboardSnapshot(false, "", null, null));
        public Task<DashboardTask> CreateTaskAsync(CancellationToken token) => Task.FromResult<DashboardTask>(null!);
        public Task<DashboardTask> CreateTaskAsync(int sourceStationCode, int targetStationCode,
            int priority, string? description, string? externalId, CancellationToken token) =>
            Task.FromResult<DashboardTask>(null!);
        public Task<DashboardTask> MarkArrivedAsync(Guid taskId, CancellationToken token) => Task.FromResult<DashboardTask>(null!);
        public Task<DashboardTask> ConfirmPickupAsync(Guid taskId, string operatorName, CancellationToken token) => Task.FromResult<DashboardTask>(null!);
        public Task<DashboardTask> ConfirmDropoffAsync(Guid taskId, string operatorName, CancellationToken token) => Task.FromResult<DashboardTask>(null!);
        public Task<DashboardTask> RetryAsync(Guid taskId, CancellationToken token) => Task.FromResult<DashboardTask>(null!);
        public Task<DashboardTask> RecoverAsync(Guid taskId, CancellationToken token) => Task.FromResult<DashboardTask>(null!);
        public Task<DashboardTask> CancelAsync(Guid taskId, string operatorName, CancellationToken token) => Task.FromResult<DashboardTask>(null!);
    }
}
