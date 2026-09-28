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

    private sealed class StubClient : IMesClient
    {
        public List<DetectionStandardRevision> Revisions { get; } = [];
        public SaveDetectionStandardRequest? LastSave { get; private set; }
        public DetectionAssessmentRequest? LastAssessment { get; private set; }
        public DetectionAssessmentRequest? PendingPreview { get; private set; }
        public TaskCompletionSource<DetectionAssessment>? PreviewGate { get; set; }

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
