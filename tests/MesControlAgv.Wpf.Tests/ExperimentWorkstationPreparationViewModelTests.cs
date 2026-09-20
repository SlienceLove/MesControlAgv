using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed partial class ExperimentWorkstationPreparationViewModelTests
{
    [Fact]
    public void Clear_source_command_is_stable_and_notifies_as_a_source_is_selected_and_cleared()
    {
        var source = new WorkstationTemplateSourceChoice("SRC", 1, 1);
        var binding = new WorkstationPreparationBottleBindingViewModel(2, [], [source], null, null, () => { });
        var command = binding.ClearSourceCommand;
        var notifications = 0;
        command.CanExecuteChanged += (_, _) => notifications++;
        Assert.Same(command, binding.ClearSourceCommand);
        Assert.False(command.CanExecute(null));
        binding.SelectedSource = source;
        Assert.True(command.CanExecute(null));
        Assert.Equal(1, notifications);
        command.Execute(null);
        Assert.False(command.CanExecute(null));
        Assert.Equal(2, notifications);
    }

    [Fact]
    public async Task Configured_job_stays_legacy_until_operator_explicitly_enters_template_mode_and_requires_unique_sources()
    {
        var client = new PreparationClient();
        var vm = CreateViewModel(client);
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
    public async Task Sequential_source_selections_raise_save_command_changes_and_duplicate_sources_are_rejected_without_writes()
    {
        var client = new PreparationClient { Template = TwoSourceTemplate() };
        var vm = CreateViewModel(client);
        var (job, schedule, workflow, verification, samples) = Fixture();
        await vm.LoadAsync(job, schedule, workflow, verification, samples, CancellationToken.None);
        await vm.BeginTemplateModeAsync();
        vm.BottleBindings[0].SelectedSample = samples[0];
        vm.BottleBindings[1].SelectedSample = samples[0];
        var notifications = 0;
        vm.SaveCommand.CanExecuteChanged += (_, _) => notifications++;

        vm.BottleBindings[0].SelectedSource = vm.SourceKeys[0];
        var afterFirstSelection = notifications;
        vm.BottleBindings[1].SelectedSource = vm.SourceKeys[1];

        Assert.True(afterFirstSelection > 0);
        Assert.True(notifications > afterFirstSelection);
        Assert.True(vm.SaveCommand.CanExecute(null));
        vm.BottleBindings[1].SelectedSource = vm.SourceKeys[0];
        Assert.False(vm.SaveCommand.CanExecute(null));
        await vm.SaveAsync();
        Assert.Equal(0, client.PrepareCalls);
        Assert.Equal("S-1 / BC-1", vm.Transfers[0].SourceIdentity);
        Assert.Equal("Unbound source", vm.Transfers[1].SourceIdentity);
    }

    [Fact]
    public async Task Valid_prepared_snapshot_allows_import_and_each_identity_revision_or_hash_drift_blocks_it_without_side_effects()
    {
        var client = new PreparationClient();
        var vm = CreateViewModel(client);
        var (job, schedule, workflow, verification, samples) = Fixture();
        client.CurrentByJob[job.JobId] = Prepared(ExperimentWorkstationPreparationStatus.Prepared, job, verification);

        await vm.LoadAsync(job, schedule, workflow, verification, samples, CancellationToken.None);
        Assert.True(vm.CanImport);
        foreach (var drifted in new[]
                 {
                     verification with { VerificationId = Guid.NewGuid() },
                     verification with { Revision = verification.Revision + 1 },
                     verification with { SnapshotHash = verification.SnapshotHash + "-changed" }
                 })
        {
            await vm.LoadAsync(job, schedule, workflow, drifted, samples, CancellationToken.None);
            Assert.False(vm.IsVerificationCurrent);
            Assert.False(vm.CanImport);
        }
        Assert.Equal(0, client.PrepareCalls);
        Assert.Equal(0, client.ImportCalls);
        Assert.Equal(0, client.StartOrAdmitCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_preparation_uses_persisted_source_identity_when_current_sample_projection_is_absent_or_changed(bool changedProjection)
    {
        var client = new PreparationClient();
        var vm = CreateViewModel(client);
        var (job, schedule, workflow, verification, samples) = Fixture();
        client.CurrentByJob[job.JobId] = Prepared(
            ExperimentWorkstationPreparationStatus.Imported, job, verification,
            sourceBusinessId: "S-SAVED", sourceBarcode: "BC-SAVED");
        IReadOnlyList<ExperimentSampleVerificationRowViewModel> projection = changedProjection
            ? [new ExperimentSampleVerificationRowViewModel(
                verification.Rows[0],
                new ExperimentSample { SampleId = verification.Rows[0].SampleId, BusinessSampleId = "S-CURRENT", Barcode = "BC-CURRENT" },
                [])]
            : [];

        await vm.LoadAsync(job with { Status = ExperimentJobStatus.Completed }, schedule, workflow, verification, projection, CancellationToken.None);

        Assert.Equal("S-SAVED / BC-SAVED", Assert.Single(vm.Transfers).SourceIdentity);
        Assert.False(vm.CanMutate);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.ImportCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_and_import_send_exactly_one_write_each_and_public_methods_recheck_eligibility()
    {
        var client = new PreparationClient();
        var vm = CreateViewModel(client);
        var (job, schedule, workflow, verification, samples) = Fixture();
        await vm.LoadAsync(job, schedule, workflow, verification, samples, CancellationToken.None);
        await vm.SaveAsync();
        await vm.ImportAsync();
        Assert.Equal(0, client.PrepareCalls);
        Assert.Equal(0, client.ImportCalls);

        await vm.BeginTemplateModeAsync();
        BindSingleSource(vm, samples[0]);
        var saveWrite = client.HoldNextPrepare();
        var save = vm.SaveAsync();
        await saveWrite.Requested.Task;
        Assert.Equal(job.JobId, client.LastJobId);
        Assert.Equal(1, client.PrepareCalls);
        Assert.Equal(verification.Revision, client.LastPrepare!.VerificationRevision);
        Assert.Equal(verification.SnapshotHash, client.LastPrepare.VerificationSnapshotHash);
        Assert.Equal("operator", client.LastPrepare.Actor);
        Assert.Equal("reason", client.LastPrepare.Reason);
        Assert.Equal("WS", client.LastPrepare.DeviceId);
        Assert.Equal("TASK", client.LastPrepare.SourceTaskNo);
        Assert.Single(client.LastPrepare.Transfers!);
        Assert.Equal(2, client.LastPrepare.BottleBindings.Count);
        Assert.NotNull(client.LastPrepare.BottleBindings[0].TemplateSource);
        Assert.Null(client.LastPrepare.BottleBindings[1].TemplateSource);
        saveWrite.Gate.SetResult(Prepared(ExperimentWorkstationPreparationStatus.Prepared, job, verification));
        await save;

        var importWrite = client.HoldNextImport();
        var import = vm.ImportAsync();
        await importWrite.Requested.Task;
        Assert.Equal(1, client.ImportCalls);
        importWrite.Gate.SetResult(vm.Preparation! with { Status = ExperimentWorkstationPreparationStatus.Imported });
        await import;
        Assert.Equal(0, client.StartOrAdmitCalls);
    }

    [Fact]
    public async Task Command_write_failures_are_visible_and_do_not_escape_the_async_command_path()
    {
        var client = new PreparationClient();
        var vm = CreateViewModel(client);
        var (job, schedule, workflow, verification, samples) = Fixture();
        await vm.LoadAsync(job, schedule, workflow, verification, samples, CancellationToken.None);
        await vm.BeginTemplateModeAsync();
        BindSingleSource(vm, samples[0]);
        var failedSave = client.HoldNextPrepare();
        var saveMessage = MessageContaining(vm, "save failed");

        vm.SaveCommand.Execute(null);
        await failedSave.Requested.Task;
        failedSave.Gate.SetException(new InvalidOperationException("save failed"));
        await saveMessage;
        Assert.Contains("save failed", vm.Message, StringComparison.Ordinal);

        client.CurrentByJob[job.JobId] = Prepared(ExperimentWorkstationPreparationStatus.Prepared, job, verification);
        await vm.LoadAsync(job, schedule, workflow, verification, samples, CancellationToken.None);
        var failedImport = client.HoldNextImport();
        var importMessage = MessageContaining(vm, "import failed");
        vm.ImportCommand.Execute(null);
        await failedImport.Requested.Task;
        failedImport.Gate.SetException(new InvalidOperationException("import failed"));
        await importMessage;
        Assert.Contains("import failed", vm.Message, StringComparison.Ordinal);
        Assert.False(vm.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delayed_save_from_A_after_A_to_B_to_A_cannot_replace_newer_state_or_clear_its_busy_flag(bool staleFailure)
    {
        var client = new PreparationClient();
        var vm = CreateViewModel(client);
        var a = Fixture();
        var b = Fixture();
        await vm.LoadAsync(a.Job, a.Schedule, a.Workflow, a.Verification, a.Samples, CancellationToken.None);
        await vm.BeginTemplateModeAsync();
        BindSingleSource(vm, a.Samples[0]);
        var oldWrite = client.HoldNextPrepare();
        var oldSave = vm.SaveAsync();
        await oldWrite.Requested.Task;

        await vm.LoadAsync(b.Job, b.Schedule, b.Workflow, b.Verification, b.Samples, CancellationToken.None);
        await vm.LoadAsync(a.Job, a.Schedule, a.Workflow, a.Verification, a.Samples, CancellationToken.None);
        await vm.BeginTemplateModeAsync();
        BindSingleSource(vm, a.Samples[0]);
        var newWrite = client.HoldNextPrepare();
        var newSave = vm.SaveAsync();
        await newWrite.Requested.Task;
        var currentMessage = vm.Message;
        Assert.False(vm.CanEdit);

        if (staleFailure) oldWrite.Gate.SetException(new InvalidOperationException("stale save failure"));
        else oldWrite.Gate.SetResult(Prepared(ExperimentWorkstationPreparationStatus.Prepared, a.Job, a.Verification));
        await oldSave;
        Assert.True(vm.IsBusy);
        Assert.Null(vm.Preparation);
        Assert.Equal(currentMessage, vm.Message);

        var newestId = Guid.NewGuid();
        newWrite.Gate.SetResult(Prepared(ExperimentWorkstationPreparationStatus.Prepared, a.Job, a.Verification, newestId));
        await newSave;
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanEdit);
        Assert.Equal(newestId, vm.Preparation!.PreparationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delayed_import_from_A_after_A_to_B_to_A_cannot_replace_newer_state_or_clear_its_busy_flag(bool staleFailure)
    {
        var client = new PreparationClient();
        var vm = CreateViewModel(client);
        var a = Fixture();
        var b = Fixture();
        var oldA = Prepared(ExperimentWorkstationPreparationStatus.Prepared, a.Job, a.Verification, Guid.NewGuid());
        client.CurrentByJob[a.Job.JobId] = oldA;
        client.CurrentByJob[b.Job.JobId] = Prepared(ExperimentWorkstationPreparationStatus.Prepared, b.Job, b.Verification);
        await vm.LoadAsync(a.Job, a.Schedule, a.Workflow, a.Verification, a.Samples, CancellationToken.None);
        var oldWrite = client.HoldNextImport();
        var oldImport = vm.ImportAsync();
        await oldWrite.Requested.Task;

        await vm.LoadAsync(b.Job, b.Schedule, b.Workflow, b.Verification, b.Samples, CancellationToken.None);
        var newA = Prepared(ExperimentWorkstationPreparationStatus.Prepared, a.Job, a.Verification, Guid.NewGuid());
        client.CurrentByJob[a.Job.JobId] = newA;
        await vm.LoadAsync(a.Job, a.Schedule, a.Workflow, a.Verification, a.Samples, CancellationToken.None);
        var newWrite = client.HoldNextImport();
        var newImport = vm.ImportAsync();
        await newWrite.Requested.Task;
        var currentMessage = vm.Message;
        Assert.False(vm.CanEdit);

        if (staleFailure) oldWrite.Gate.SetException(new InvalidOperationException("stale import failure"));
        else oldWrite.Gate.SetResult(oldA with { Status = ExperimentWorkstationPreparationStatus.Imported });
        await oldImport;
        Assert.True(vm.IsBusy);
        Assert.Equal(newA.PreparationId, vm.Preparation!.PreparationId);
        Assert.Equal(currentMessage, vm.Message);
        newWrite.Gate.SetResult(newA with { Status = ExperimentWorkstationPreparationStatus.Imported });
        await newImport;
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanEdit);
        Assert.Equal(ExperimentWorkstationPreparationStatus.Imported, vm.Preparation!.Status);
    }

    private static ExperimentWorkstationPreparationViewModel CreateViewModel(PreparationClient client) =>
        new(client, () => "operator", () => "reason");

    private static void BindSingleSource(ExperimentWorkstationPreparationViewModel vm, ExperimentSampleVerificationRowViewModel sample)
    {
        vm.BottleBindings[0].SelectedSample = sample;
        vm.BottleBindings[1].SelectedSample = sample;
        vm.BottleBindings[0].SelectedSource = vm.SourceKeys[0];
    }

    private static Task MessageContaining(ExperimentWorkstationPreparationViewModel vm, string expected)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += Handler;
        return completion.Task;
        void Handler(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(vm.Message) && vm.Message.Contains(expected, StringComparison.Ordinal))
            {
                vm.PropertyChanged -= Handler;
                completion.TrySetResult();
            }
        }
    }

    private static (ExperimentJob Job, ScheduleEntry Schedule, WorkflowVersion Workflow, ExperimentSampleVerification Verification, IReadOnlyList<ExperimentSampleVerificationRowViewModel> Samples) Fixture()
    {
        var job = new ExperimentJob { JobId = Guid.NewGuid(), Status = ExperimentJobStatus.Scheduled };
        var schedule = new ScheduleEntry { ExperimentJobId = job.JobId, Status = ScheduleEntryStatus.Scheduled };
        var workflow = new WorkflowVersion { Definition = new WorkflowDefinition { Nodes = [new WorkflowNode { NodeTypeId = WorkflowGraphNodeTypeIds.SampleWorkstationExecuteExistingTask, Configuration = new Dictionary<string, string?> { [WorkflowNodeConfigurationKeys.DeviceId] = "WS", [WorkflowNodeConfigurationKeys.TaskNo] = "TASK" } }] } };
        var sample = new ExperimentSample { SampleId = Guid.NewGuid(), BusinessSampleId = "S-1", Barcode = "BC-1" };
        var row = new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = sample.SampleId, BusinessSampleId = sample.BusinessSampleId, SampleBarcode = sample.Barcode };
        var verification = new ExperimentSampleVerification { VerificationId = Guid.NewGuid(), ExperimentJobId = job.JobId, Revision = 1, Status = ExperimentSampleVerificationStatus.Verified, SnapshotHash = "HASH", Rows = [row] };
        return (job, schedule, workflow, verification, [new ExperimentSampleVerificationRowViewModel(row, sample, [])]);
    }

    private static SampleWorkstationTaskTemplate TwoSourceTemplate() => new(
        "TASK", "two sources",
        [
            new SampleWorkstationTransferRow("L1", "T1", 1, 1, "SRC", 1, 1, "OUT", 2, 2, 10),
            new SampleWorkstationTransferRow("L2", "T2", 1, 2, "SRC", 1, 2, "OUT", 2, 3, 20)
        ]);

    private static ExperimentWorkstationPreparation Prepared(
        ExperimentWorkstationPreparationStatus status,
        ExperimentJob job,
        ExperimentSampleVerification verification,
        Guid? preparationId = null,
        string sourceBusinessId = "S-1",
        string sourceBarcode = "BC-1")
    {
        var transfer = new SampleWorkstationTransferRow("L", "T", 1, 1, "SRC", 1, 1, "OUT", 2, 2, 10);
        var source = new WorkstationTemplateSourceKey { Module = transfer.SourceModule, X = transfer.SourceX, Y = transfer.SourceY };
        var sampleId = verification.Rows.FirstOrDefault()?.SampleId ?? Guid.NewGuid();
        var rowId = verification.Rows.FirstOrDefault()?.RowId ?? Guid.NewGuid();
        return new ExperimentWorkstationPreparation
        {
            PreparationId = preparationId ?? Guid.NewGuid(),
            ExperimentJobId = job.JobId,
            Revision = 3,
            VerificationId = verification.VerificationId,
            VerificationRevision = verification.Revision,
            VerificationSnapshotHash = verification.SnapshotHash,
            Status = status,
            Payload = new ExperimentWorkstationPreparationPayload
            {
                Transfers = [new PreparedWorkstationTransfer { Order = 1, Transfer = transfer, BottleNumber = 1, VerificationRowId = rowId, SourceSampleId = sampleId, SourceBusinessSampleId = sourceBusinessId, SourceSampleBarcode = sourceBarcode }],
                BottleBindings =
                [
                    new PreparedWorkstationBottleBinding { BottleNumber = 1, IsUsedByTemplate = true, TemplateSource = source, VerificationRowId = rowId, SampleId = sampleId, BusinessSampleId = sourceBusinessId, SampleBarcode = sourceBarcode },
                    new PreparedWorkstationBottleBinding { BottleNumber = 2, IsUsedByTemplate = false, VerificationRowId = rowId, SampleId = sampleId, BusinessSampleId = sourceBusinessId, SampleBarcode = sourceBarcode }
                ]
            }
        };
    }

    private sealed class PendingWrite<T>
    {
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<T> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PreparationClient : IMesClient
    {
        private readonly Queue<PendingWrite<ExperimentWorkstationPreparation>> _prepareWrites = [];
        private readonly Queue<PendingWrite<ExperimentWorkstationPreparation>> _importWrites = [];
        public Dictionary<Guid, ExperimentWorkstationPreparation?> CurrentByJob { get; } = [];
        public SampleWorkstationTaskTemplate Template { get; set; } = new("TASK", "template", [new SampleWorkstationTransferRow("L", "T", 1, 1, "SRC", 1, 1, "OUT", 2, 2, 10)]);
        public int TemplateReads { get; private set; }
        public int PrepareCalls { get; private set; }
        public int ImportCalls { get; private set; }
        public int StartOrAdmitCalls { get; private set; }
        public Guid LastJobId { get; private set; }
        public PrepareExperimentWorkstationTaskRequest? LastPrepare { get; private set; }

        public PendingWrite<ExperimentWorkstationPreparation> HoldNextPrepare()
        {
            var pending = new PendingWrite<ExperimentWorkstationPreparation>();
            _prepareWrites.Enqueue(pending);
            return pending;
        }
        public PendingWrite<ExperimentWorkstationPreparation> HoldNextImport()
        {
            var pending = new PendingWrite<ExperimentWorkstationPreparation>();
            _importWrites.Enqueue(pending);
            return pending;
        }
        public Task<ExperimentWorkstationPreparation?> GetCurrentExperimentWorkstationPreparationAsync(Guid jobId, CancellationToken ct) =>
            Task.FromResult(CurrentByJob.GetValueOrDefault(jobId));
        public Task<SampleWorkstationTemplateResponse> GetSampleWorkstationTemplateAsync(string deviceId, string taskNo, CancellationToken ct)
        {
            TemplateReads++;
            return Task.FromResult(new SampleWorkstationTemplateResponse(deviceId, "t.xlsx", [], "h", Template, DateTimeOffset.UtcNow));
        }
        public Task<ExperimentWorkstationPreparation> PrepareExperimentWorkstationTaskAsync(Guid jobId, PrepareExperimentWorkstationTaskRequest request, CancellationToken ct)
        {
            PrepareCalls++;
            LastJobId = jobId;
            LastPrepare = request;
            var pending = _prepareWrites.Dequeue();
            pending.Requested.TrySetResult();
            return pending.Gate.Task;
        }
        public Task<ExperimentWorkstationPreparation> ImportExperimentWorkstationTaskAsync(Guid jobId, Guid preparationId, ImportExperimentWorkstationTaskRequest request, CancellationToken ct)
        {
            ImportCalls++;
            var pending = _importWrites.Dequeue();
            pending.Requested.TrySetResult();
            return pending.Gate.Task;
        }
        public Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DashboardTask>>([]);
        public Task<KpiDashboard> GetKpiDashboardAsync(DateOnly d, CancellationToken ct) => throw new NotSupportedException();
        public Task<DashboardTaskDetail?> GetTaskDetailAsync(Guid id, CancellationToken ct) => Task.FromResult<DashboardTaskDetail?>(null);
        public Task<AgvDashboardSnapshot> GetAgvSnapshotAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<DashboardTask> CreateTaskAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<DashboardTask> CreateTaskAsync(int a, int b, int c, string? d, string? e, CancellationToken ct) => throw new NotSupportedException();
        public Task<DashboardTask> MarkArrivedAsync(Guid a, CancellationToken ct) => throw new NotSupportedException();
        public Task<DashboardTask> ConfirmPickupAsync(Guid a, string b, CancellationToken ct) => throw new NotSupportedException();
        public Task<DashboardTask> ConfirmDropoffAsync(Guid a, string b, CancellationToken ct) => throw new NotSupportedException();
        public Task<DashboardTask> RetryAsync(Guid a, CancellationToken ct) => throw new NotSupportedException();
        public Task<DashboardTask> RecoverAsync(Guid a, CancellationToken ct) => throw new NotSupportedException();
        public Task<DashboardTask> CancelAsync(Guid a, string b, CancellationToken ct) => throw new NotSupportedException();
    }
}
