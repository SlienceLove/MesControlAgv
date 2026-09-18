using System.Collections.ObjectModel;
using System.Windows.Input;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.Infrastructure;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>Selection-scoped editor for the read-only workstation template and its explicit MES preparation.</summary>
public sealed class ExperimentWorkstationPreparationViewModel : ExperimentBindableObject
{
    private readonly IMesClient _mes;
    private readonly Func<string> _actor;
    private readonly Func<string> _reason;
    private readonly AsyncCommand _saveCommand;
    private readonly AsyncCommand _importCommand;
    private readonly RelayCommand _beginTemplateModeCommand;
    private Guid? _jobId;
    private int _generation;
    private bool _isBusy;
    private bool _isApplicable;
    private bool _isRequired;
    private bool _isResolved = true;
    private bool _isDirty;
    private bool _isTemplateMode;
    private bool _canMutate;
    private string _deviceId = string.Empty;
    private string _sourceTaskNo = string.Empty;
    private string _message = "Select a scheduled workstation task.";
    private ExperimentWorkstationPreparation? _preparation;
    private ExperimentSampleVerification? _verification;
    private Guid? _verificationId;

    public ExperimentWorkstationPreparationViewModel(IMesClient mes, Func<string> actor, Func<string> reason)
    {
        _mes = mes;
        _actor = actor;
        _reason = reason;
        _saveCommand = new AsyncCommand(SaveAsync, () => CanSave);
        _importCommand = new AsyncCommand(ImportAsync, () => CanImport);
        _beginTemplateModeCommand = new RelayCommand(() => _ = BeginTemplateModeAsync(), () => IsApplicable && CanMutate && !IsBusy && !BlocksNewTemplateMode && !IsTemplateMode);
    }

    public ObservableCollection<WorkstationPreparationTransferRowViewModel> Transfers { get; } = [];
    public ObservableCollection<WorkstationPreparationBottleBindingViewModel> BottleBindings { get; } = [];
    public ICommand SaveCommand => _saveCommand;
    public ICommand ImportCommand => _importCommand;
    public bool IsBusy { get => _isBusy; private set { if (SetField(ref _isBusy, value)) RaiseStates(); } }
    public bool IsApplicable { get => _isApplicable; private set { if (SetField(ref _isApplicable, value)) RaiseStates(); } }
    public bool IsTemplateMode { get => _isTemplateMode; private set { if (SetField(ref _isTemplateMode, value)) RaiseStates(); } }
    public bool CanMutate { get => _canMutate; private set { if (SetField(ref _canMutate, value)) RaiseStates(); } }
    public bool RequiresPreparation { get => _isRequired; private set { if (SetField(ref _isRequired, value)) RaiseStates(); } }
    public bool IsResolved { get => _isResolved; private set { if (SetField(ref _isResolved, value)) RaiseStates(); } }
    public bool IsDirty { get => _isDirty; private set { if (SetField(ref _isDirty, value)) RaiseStates(); } }
    public string DeviceId { get => _deviceId; private set => SetField(ref _deviceId, value); }
    public string SourceTaskNo { get => _sourceTaskNo; private set => SetField(ref _sourceTaskNo, value); }
    public string Message { get => _message; private set => SetField(ref _message, value); }
    public ExperimentWorkstationPreparation? Preparation { get => _preparation; private set { if (SetField(ref _preparation, value)) { OnPropertyChanged(nameof(PreparationStatus)); RaiseStates(); } } }
    public string PreparationStatus => Preparation is null ? "Not prepared" : $"{Preparation.Status} / rev {Preparation.Revision}";
    public bool IsVerificationCurrent => Preparation is null || (_verification?.Status == ExperimentSampleVerificationStatus.Verified && Preparation.VerificationId == _verificationId && Preparation.VerificationRevision == _verification.Revision && string.Equals(Preparation.VerificationSnapshotHash, _verification.SnapshotHash, StringComparison.Ordinal));
    public bool CanSave => IsApplicable && IsTemplateMode && CanMutate && !IsBusy && _verification?.Status == ExperimentSampleVerificationStatus.Verified && !string.IsNullOrWhiteSpace(DeviceId) && !string.IsNullOrWhiteSpace(SourceTaskNo) && BottleBindings.Count == 2 && BottleBindings.All(binding => binding.SelectedSample is not null) && BottleBindings.Count(binding => binding.SelectedSource is not null) == SourceKeys.Count && BottleBindings.Select(binding => binding.SelectedSource).Where(source => source is not null).Distinct().Count() == SourceKeys.Count;
    public bool CanImport => IsApplicable && IsTemplateMode && CanMutate && !IsBusy && !IsDirty && IsVerificationCurrent && Preparation?.Status == ExperimentWorkstationPreparationStatus.Prepared;
    public bool BlocksNewTemplateMode => IsDirty || _verification?.Status != ExperimentSampleVerificationStatus.Verified || Preparation?.Status is ExperimentWorkstationPreparationStatus.Prepared or ExperimentWorkstationPreparationStatus.Importing or ExperimentWorkstationPreparationStatus.Unknown;
    public ObservableCollection<WorkstationTemplateSourceChoice> SourceKeys { get; } = [];
    public ObservableCollection<ExperimentSampleVerificationRowViewModel> VerifiedSamples { get; } = [];
    public ICommand BeginTemplateModeCommand => _beginTemplateModeCommand;

    public async Task LoadAsync(ExperimentJob? job, ScheduleEntry? schedule, WorkflowVersion? workflow, ExperimentSampleVerification? verification, IReadOnlyList<ExperimentSampleVerificationRowViewModel> samples, CancellationToken cancellationToken)
    {
        var generation = ++_generation;
        Reset();
        _jobId = job?.JobId;
        _verification = verification;
        _verificationId = verification?.VerificationId;
        foreach (var sample in samples) VerifiedSamples.Add(sample);
        var node = workflow?.Definition.Nodes.SingleOrDefault(item => string.Equals(item.NodeTypeId, WorkflowGraphNodeTypeIds.SampleWorkstationExecuteExistingTask, StringComparison.Ordinal));
        if (job is null || node is null)
        {
            Message = "Workstation preparation requires exactly one scheduled workstation task.";
            IsResolved = true;
            return;
        }
        DeviceId = Read(node, WorkflowNodeConfigurationKeys.DeviceId);
        SourceTaskNo = Read(node, WorkflowNodeConfigurationKeys.TaskNo);
        if (string.IsNullOrWhiteSpace(DeviceId) || string.IsNullOrWhiteSpace(SourceTaskNo))
        {
            // Existing workflows that only have the legacy execute-existing-task node
            // retain their historical admission path. A configured node opts into the
            // explicit preparation lifecycle.
            Message = "Legacy workstation node has no preparation template configuration.";
            IsResolved = true;
            return;
        }
        IsApplicable = true;
        CanMutate = job.Status == ExperimentJobStatus.Scheduled && schedule?.Status == ScheduleEntryStatus.Scheduled;
        try
        {
            var current = await _mes.GetCurrentExperimentWorkstationPreparationAsync(job.JobId, cancellationToken);
            if (generation != _generation || _jobId != job.JobId) return;
            Preparation = current;
            if (current is not null) { RequiresPreparation = true; IsTemplateMode = true; ApplyPrepared(current); }
            if (generation == _generation) Message = current is null ? "Legacy mode: explicitly choose new template preparation when required." : $"Current preparation: {PreparationStatus}.";
        }
        catch (Exception exception)
        {
            if (generation == _generation && _jobId == job.JobId) Message = exception.Message;
        }
        finally { if (generation == _generation) IsResolved = true; }
    }

    public async Task LoadTemplateAsync() => await LoadTemplateAsync(_generation, CancellationToken.None);

    public async Task BeginTemplateModeAsync()
    {
        var generation = _generation;
        if (!IsApplicable || !CanMutate || BlocksNewTemplateMode || IsTemplateMode) return;
        RequiresPreparation = true;
        IsTemplateMode = true;
        try { await LoadTemplateAsync(generation, CancellationToken.None); }
        catch (Exception exception) { if (generation == _generation) Message = $"Template read failed; legacy admission remains unavailable in preparation mode. {exception.Message}"; }
    }

    private async Task LoadTemplateAsync(int generation, CancellationToken cancellationToken)
    {
        if (!IsTemplateMode || (BlocksNewTemplateMode && Preparation is not null)) { Message = "Unsaved edits, verification drift, or a non-imported preparation blocks new template mode."; return; }
        var template = await _mes.GetSampleWorkstationTemplateAsync(DeviceId, SourceTaskNo, cancellationToken);
        if (generation != _generation) return;
        Transfers.Clear(); SourceKeys.Clear(); BottleBindings.Clear();
        foreach (var row in template.Template.Transfers) Transfers.Add(new WorkstationPreparationTransferRowViewModel(row, MarkDirty));
        foreach (var source in template.Template.Transfers.Select(row => new WorkstationTemplateSourceChoice(row.SourceModule, row.SourceX, row.SourceY)).Distinct()) SourceKeys.Add(source);
        if (SourceKeys.Count is < 1 or > 2) throw new InvalidOperationException("The workstation template must use one or two explicit sources.");
        AddBottle(1); AddBottle(2); IsDirty = false; RaiseStates();
    }

    public async Task SaveAsync()
    {
        if (_jobId is not { } jobId || _verification is null) return;
        var generation = _generation;
        var verification = _verification;
        IsBusy = true;
        try
        {
            var prepared = await _mes.PrepareExperimentWorkstationTaskAsync(jobId, new PrepareExperimentWorkstationTaskRequest
            {
                RequestId = Guid.NewGuid(), Actor = _actor(), Reason = _reason(), DeviceId = DeviceId, SourceTaskNo = SourceTaskNo,
                VerificationRevision = verification.Revision, VerificationSnapshotHash = verification.SnapshotHash,
                Transfers = Transfers.Select(row => row.ToContract()).ToArray(),
                BottleBindings = BottleBindings.Select(binding => new PrepareWorkstationBottleBinding { BottleNumber = binding.BottleNumber, SampleId = binding.SelectedSample!.SampleId, TemplateSource = binding.SelectedSource?.ToContract() }).ToArray()
            }, CancellationToken.None);
            if (generation != _generation || _jobId != jobId || !ReferenceEquals(verification, _verification)) return;
            Preparation = prepared; ApplyPrepared(prepared); IsDirty = false; Message = "Preparation saved; it has not been imported or started.";
        }
        catch (Exception exception) { if (generation == _generation && _jobId == jobId) Message = $"Preparation was not saved. Retry only after resolving: {exception.Message}"; }
        finally { if (generation == _generation && _jobId == jobId) IsBusy = false; }
    }

    public async Task ImportAsync()
    {
        if (_jobId is not { } jobId || Preparation is null) return;
        var preparationId = Preparation.PreparationId;
        var revision = Preparation.Revision;
        var generation = _generation;
        IsBusy = true;
        try
        {
            var imported = await _mes.ImportExperimentWorkstationTaskAsync(jobId, preparationId, new ImportExperimentWorkstationTaskRequest { RequestId = Guid.NewGuid(), Actor = _actor(), Reason = _reason() }, CancellationToken.None);
            if (generation != _generation || _jobId != jobId || Preparation?.PreparationId != preparationId || Preparation.Revision != revision) return;
            Preparation = imported; ApplyPrepared(imported); Message = imported.Status == ExperimentWorkstationPreparationStatus.Imported ? "Template imported; no task was started." : $"Import requires reconciliation: {imported.Status}.";
        }
        catch (Exception exception) { if (generation == _generation && _jobId == jobId && Preparation?.PreparationId == preparationId && Preparation.Revision == revision) Message = $"Import outcome was not accepted. Do not automatically retry; reconcile the instrument state. {exception.Message}"; }
        finally { if (generation == _generation && _jobId == jobId && Preparation?.PreparationId == preparationId && Preparation.Revision == revision) IsBusy = false; }
    }

    private void ApplyPrepared(ExperimentWorkstationPreparation value)
    {
        Transfers.Clear(); SourceKeys.Clear(); BottleBindings.Clear();
        foreach (var row in value.Payload.Transfers)
        {
            var item = new WorkstationPreparationTransferRowViewModel(row.Transfer, MarkDirty)
            {
                SourceIdentity = $"{row.SourceBusinessSampleId} / {row.SourceSampleBarcode}"
            };
            Transfers.Add(item);
        }
        foreach (var source in value.Payload.Transfers.Select(row => new WorkstationTemplateSourceChoice(row.Transfer.SourceModule, row.Transfer.SourceX, row.Transfer.SourceY)).Distinct()) SourceKeys.Add(source);
        foreach (var binding in value.Payload.BottleBindings.OrderBy(item => item.BottleNumber)) AddBottle(binding.BottleNumber, binding.SampleId, binding.TemplateSource is null ? null : new WorkstationTemplateSourceChoice(binding.TemplateSource.Module, binding.TemplateSource.X, binding.TemplateSource.Y));
        IsDirty = false;
    }
    private void AddBottle(int number, Guid? sampleId = null, WorkstationTemplateSourceChoice? source = null) => BottleBindings.Add(new WorkstationPreparationBottleBindingViewModel(number, VerifiedSamples, SourceKeys, sampleId, source, MarkDirty));
    private void MarkDirty()
    {
        foreach (var transfer in Transfers)
        {
            var binding = BottleBindings.FirstOrDefault(item => item.SelectedSource is { } source &&
                source.Module == transfer.Original.SourceModule && source.X == transfer.Original.SourceX && source.Y == transfer.Original.SourceY);
            transfer.SourceIdentity = binding?.SelectedSample is { } sample ? $"{sample.SampleNumber} / {sample.Barcode}" : "Unbound source";
        }
        IsDirty = true;
        RaiseStates();
    }
    private void Reset() { IsBusy = false; IsApplicable = false; CanMutate = false; IsTemplateMode = false; RequiresPreparation = false; IsResolved = false; IsDirty = false; Preparation = null; Transfers.Clear(); SourceKeys.Clear(); BottleBindings.Clear(); VerifiedSamples.Clear(); }
    private void RaiseStates() { OnPropertyChanged(nameof(CanSave)); OnPropertyChanged(nameof(CanImport)); OnPropertyChanged(nameof(IsVerificationCurrent)); OnPropertyChanged(nameof(BlocksNewTemplateMode)); _saveCommand.RaiseCanExecuteChanged(); _importCommand.RaiseCanExecuteChanged(); _beginTemplateModeCommand.RaiseCanExecuteChanged(); }
    private static string Read(WorkflowNode node, string key) => node.Configuration.TryGetValue(key, out var value) ? value?.Trim() ?? string.Empty : string.Empty;
}

public sealed record WorkstationTemplateSourceChoice(string Module, int X, int Y)
{
    public string Display => $"{Module} ({X}, {Y})";
    public WorkstationTemplateSourceKey ToContract() => new() { Module = Module, X = X, Y = Y };
}
public sealed class WorkstationPreparationBottleBindingViewModel : ExperimentBindableObject
{
    private ExperimentSampleVerificationRowViewModel? _sample; private WorkstationTemplateSourceChoice? _source; private readonly Action _changed;
    public WorkstationPreparationBottleBindingViewModel(int number, IReadOnlyList<ExperimentSampleVerificationRowViewModel> samples, IReadOnlyList<WorkstationTemplateSourceChoice> sources, Guid? sampleId, WorkstationTemplateSourceChoice? source, Action changed) { BottleNumber = number; Samples = samples; Sources = sources; _sample = samples.FirstOrDefault(item => item.SampleId == sampleId); _source = source; _changed = changed; }
    public int BottleNumber { get; } public IReadOnlyList<ExperimentSampleVerificationRowViewModel> Samples { get; } public IReadOnlyList<WorkstationTemplateSourceChoice> Sources { get; }
    public ExperimentSampleVerificationRowViewModel? SelectedSample { get => _sample; set { if (SetField(ref _sample, value)) _changed(); } }
    public WorkstationTemplateSourceChoice? SelectedSource { get => _source; set { if (SetField(ref _source, value)) { OnPropertyChanged(nameof(SourceUseStatus)); _changed(); } } }
    public ICommand ClearSourceCommand => new RelayCommand(() => SelectedSource = null, () => SelectedSource is not null);
    public string SourceUseStatus => SelectedSource is null ? "Unused barcode slot — not dispensed material" : "Template source binding";
}
public sealed class WorkstationPreparationTransferRowViewModel : ExperimentBindableObject
{
    private readonly Action _changed; private string _targetModule; private int _targetX; private int _targetY; private int _volume; private string _sourceIdentity = "Unbound source";
    public WorkstationPreparationTransferRowViewModel(SampleWorkstationTransferRow row, Action changed) { Original = row; _changed = changed; _targetModule = row.TargetModule; _targetX = row.TargetX; _targetY = row.TargetY; _volume = row.VolumeMicroliters; }
    public SampleWorkstationTransferRow Original { get; } public string Source => $"{Original.SourceModule} ({Original.SourceX}, {Original.SourceY})"; public string LiquidCode => Original.LiquidCode;
    public string SourceIdentity { get => _sourceIdentity; set => SetField(ref _sourceIdentity, value); }
    public string TargetModule { get => _targetModule; set { if (SetField(ref _targetModule, value)) _changed(); } } public int TargetX { get => _targetX; set { if (SetField(ref _targetX, value)) _changed(); } } public int TargetY { get => _targetY; set { if (SetField(ref _targetY, value)) _changed(); } } public int VolumeMicroliters { get => _volume; set { if (SetField(ref _volume, value)) _changed(); } }
    public SampleWorkstationTransferRow ToContract() => Original with { TargetModule = TargetModule.Trim(), TargetX = TargetX, TargetY = TargetY, VolumeMicroliters = VolumeMicroliters };
}
