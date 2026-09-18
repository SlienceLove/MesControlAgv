using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class ExperimentWorkstationPreparationViewModelTests
{
    [Fact]
    public async Task Configured_job_stays_legacy_until_operator_explicitly_enters_template_mode_and_requires_unique_sources()
    {
        var client = new PreparationClient();
        var vm = new ExperimentWorkstationPreparationViewModel(client, () => "operator", () => "reason");
        var (job, schedule, workflow, verification, samples) = Fixture();
        await vm.LoadAsync(job, schedule, workflow, verification, samples, CancellationToken.None);
        Assert.False(vm.RequiresPreparation);
        Assert.False(vm.IsTemplateMode);
        Assert.Equal(0, client.TemplateReads);
        await vm.BeginTemplateModeAsync();
        Assert.True(vm.RequiresPreparation);
        Assert.True(vm.IsTemplateMode);
        Assert.Equal(1, client.TemplateReads);
        Assert.False(vm.CanSave);
        vm.BottleBindings[0].SelectedSample = samples[0];
        vm.BottleBindings[1].SelectedSample = samples[0];
        vm.BottleBindings[0].SelectedSource = vm.SourceKeys[0];
        vm.BottleBindings[1].SelectedSource = vm.SourceKeys[0];
        Assert.False(vm.CanSave);
        vm.BottleBindings[1].SelectedSource = null;
        Assert.True(vm.CanSave);
        Assert.Contains("Unused barcode slot", vm.BottleBindings[1].SourceUseStatus, StringComparison.Ordinal);
        Assert.Equal("S-1 / BC-1", vm.Transfers[0].SourceIdentity);
    }

    [Fact]
    public async Task Existing_preparation_is_read_only_provenance_for_terminal_job_and_snapshot_drift_blocks_import()
    {
        var client = new PreparationClient { Current = Prepared(ExperimentWorkstationPreparationStatus.Prepared) };
        var vm = new ExperimentWorkstationPreparationViewModel(client, () => "operator", () => "reason");
        var (job, schedule, workflow, verification, samples) = Fixture();
        await vm.LoadAsync(job with { Status = ExperimentJobStatus.Completed }, schedule, workflow, verification, samples, CancellationToken.None);
        Assert.NotNull(vm.Preparation);
        Assert.False(vm.CanMutate);
        Assert.False(vm.CanImport);
        await vm.LoadAsync(job, schedule, workflow, verification with { Revision = 2 }, samples, CancellationToken.None);
        Assert.False(vm.IsVerificationCurrent);
        Assert.False(vm.CanImport);
    }

    private static (ExperimentJob, ScheduleEntry, WorkflowVersion, ExperimentSampleVerification, IReadOnlyList<ExperimentSampleVerificationRowViewModel>) Fixture()
    {
        var job = new ExperimentJob { JobId = Guid.NewGuid(), Status = ExperimentJobStatus.Scheduled };
        var schedule = new ScheduleEntry { ExperimentJobId = job.JobId, Status = ScheduleEntryStatus.Scheduled };
        var workflow = new WorkflowVersion { Definition = new WorkflowDefinition { Nodes = [new WorkflowNode { NodeTypeId = WorkflowGraphNodeTypeIds.SampleWorkstationExecuteExistingTask, Configuration = new Dictionary<string, string?> { [WorkflowNodeConfigurationKeys.DeviceId] = "WS", [WorkflowNodeConfigurationKeys.TaskNo] = "TASK" } }] } };
        var sample = new ExperimentSample { SampleId = Guid.NewGuid(), BusinessSampleId = "S-1", Barcode = "BC-1" };
        var row = new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = sample.SampleId, BusinessSampleId = sample.BusinessSampleId, SampleBarcode = sample.Barcode };
        var verification = new ExperimentSampleVerification { VerificationId = Guid.NewGuid(), ExperimentJobId = job.JobId, Revision = 1, Status = ExperimentSampleVerificationStatus.Verified, SnapshotHash = "HASH", Rows = [row] };
        return (job, schedule, workflow, verification, [new ExperimentSampleVerificationRowViewModel(row, sample, [])]);
    }
    private static ExperimentWorkstationPreparation Prepared(ExperimentWorkstationPreparationStatus status) => new() { PreparationId = Guid.NewGuid(), Revision = 3, VerificationId = Guid.NewGuid(), VerificationRevision = 1, VerificationSnapshotHash = "HASH", Status = status };
    private sealed class PreparationClient : IMesClient
    {
        public int TemplateReads { get; private set; } public ExperimentWorkstationPreparation? Current { get; set; }
        public Task<ExperimentWorkstationPreparation?> GetCurrentExperimentWorkstationPreparationAsync(Guid jobId, CancellationToken ct) => Task.FromResult(Current);
        public Task<SampleWorkstationTemplateResponse> GetSampleWorkstationTemplateAsync(string deviceId, string taskNo, CancellationToken ct) { TemplateReads++; return Task.FromResult(new SampleWorkstationTemplateResponse(deviceId, "t.xlsx", [], "h", new SampleWorkstationTaskTemplate(taskNo, "t", [new SampleWorkstationTransferRow("L", "T", 1, 1, "SRC", 1, 1, "OUT", 2, 2, 10)]), DateTimeOffset.UtcNow)); }
        public Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DashboardTask>>([]);
        public Task<KpiDashboard> GetKpiDashboardAsync(DateOnly d, CancellationToken ct) => throw new NotSupportedException();
        public Task<DashboardTaskDetail?> GetTaskDetailAsync(Guid id, CancellationToken ct) => Task.FromResult<DashboardTaskDetail?>(null);
        public Task<AgvDashboardSnapshot> GetAgvSnapshotAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<DashboardTask> CreateTaskAsync(CancellationToken ct) => throw new NotSupportedException(); public Task<DashboardTask> CreateTaskAsync(int a,int b,int c,string? d,string? e,CancellationToken ct)=>throw new NotSupportedException(); public Task<DashboardTask> MarkArrivedAsync(Guid a,CancellationToken ct)=>throw new NotSupportedException(); public Task<DashboardTask> ConfirmPickupAsync(Guid a,string b,CancellationToken ct)=>throw new NotSupportedException(); public Task<DashboardTask> ConfirmDropoffAsync(Guid a,string b,CancellationToken ct)=>throw new NotSupportedException(); public Task<DashboardTask> RetryAsync(Guid a,CancellationToken ct)=>throw new NotSupportedException(); public Task<DashboardTask> RecoverAsync(Guid a,CancellationToken ct)=>throw new NotSupportedException(); public Task<DashboardTask> CancelAsync(Guid a,string b,CancellationToken ct)=>throw new NotSupportedException();
    }
}
