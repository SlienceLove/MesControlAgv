using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using MesControlAgv.Contracts.Materials;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

public sealed class CreateSampleViewModel : INotifyPropertyChanged
{
    private readonly IMesClient _mes;
    private readonly IReadOnlyList<SampleMaterial> _existingSamples;
    private readonly string _actor;
    private readonly Func<Task> _refresh;
    private readonly Guid _requestId = Guid.NewGuid();
    private string _barcode = string.Empty;
    private string _batchId = string.Empty;
    private string _sampleType = string.Empty;
    private string _materialCode = string.Empty;
    private string _locationCode = "DEFAULT";
    private string _status = "填写样品信息并选择检测方法。";
    private DetectionStandardRevision? _selectedStandard;
    private string? _boundMethodCode;
    private bool _isMethodLocked;

    public CreateSampleViewModel(
        IMesClient mes, IReadOnlyList<SampleMaterial> existingSamples, string actor, Func<Task> refresh)
    {
        _mes = mes;
        _existingSamples = existingSamples;
        _actor = actor;
        _refresh = refresh;
        BatchSuggestions = existingSamples.Select(item => item.SampleBatchId).Distinct().OrderBy(value => value).ToList();
        SaveCommand = new AsyncCommand(SaveAsync, () => !IsSaved);
    }

    public IReadOnlyList<string> BatchSuggestions { get; }
    public ObservableCollection<DetectionStandardRevision> Standards { get; } = [];
    public AsyncCommand SaveCommand { get; }
    public bool IsSaved { get; private set; }
    public string Barcode { get => _barcode; set => SetField(ref _barcode, value); }
    public string SampleType { get => _sampleType; set => SetField(ref _sampleType, value); }
    public string MaterialCode { get => _materialCode; set => SetField(ref _materialCode, value); }
    public string LocationCode { get => _locationCode; set => SetField(ref _locationCode, value); }
    public string Status { get => _status; private set => SetField(ref _status, value); }
    public bool IsMethodLocked { get => _isMethodLocked; private set => SetField(ref _isMethodLocked, value); }
    public bool IsMethodSelectionEnabled => !IsMethodLocked;
    public string? BoundMethodCode { get => _boundMethodCode; private set => SetField(ref _boundMethodCode, value); }
    public DetectionStandardRevision? SelectedStandard
    {
        get => _selectedStandard;
        set => SetField(ref _selectedStandard, value);
    }
    public string BatchId
    {
        get => _batchId;
        set
        {
            if (!SetField(ref _batchId, value)) return;
            ApplyBatchMethod();
        }
    }

    public async Task LoadAsync()
    {
        try
        {
            var standards = await _mes.GetDetectionStandardsAsync(null, false, CancellationToken.None);
            Standards.Clear();
            foreach (var item in standards) Standards.Add(item);
            ApplyBatchMethod();
            if (Standards.Count == 0) Status = "暂无启用的检测标准，请先到“检测标准”创建方法。";
        }
        catch (Exception exception) { Status = $"加载检测方法失败：{exception.Message}"; }
    }

    public async Task SaveAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Barcode) || string.IsNullOrWhiteSpace(BatchId))
                throw new ArgumentException("样品条码和批次不能为空。");
            if (!IsMethodLocked && SelectedStandard is not { IsActive: true })
                throw new ArgumentException("请选择启用中的检测方法。");
            var request = new MaterialImportRequest
            {
                RequestId = _requestId,
                Actor = _actor,
                Source = "manual-single-sample",
                Samples = [new SampleImportRow
                {
                    RowNumber = 1,
                    Barcode = Barcode.Trim(),
                    SampleBatchId = BatchId.Trim(),
                    SampleType = SampleType.Trim(),
                    MaterialCode = string.IsNullOrWhiteSpace(MaterialCode) ? null : MaterialCode.Trim(),
                    LocationCode = LocationCode.Trim(),
                    DetectionMethodCode = IsMethodLocked ? null : SelectedStandard!.MethodCode
                }]
            };
            var preview = await _mes.PreviewMaterialImportAsync(request, CancellationToken.None);
            if (!preview.CanCommit)
                throw new InvalidOperationException(preview.Issues.FirstOrDefault()?.Message ?? "样品资料校验未通过。");
            if (preview.AcceptedCount != 1)
                throw new InvalidOperationException("该样品条码已存在，请换用新的条码。");
            var result = await _mes.ImportMaterialsAsync(request, CancellationToken.None);
            if (result.Data?.ImportedSamples != 1 && !result.IsIdempotentReplay)
                throw new InvalidOperationException("样品未创建，请刷新列表核对。");
            IsSaved = true;
            SaveCommand.RaiseCanExecuteChanged();
            Status = $"样品 {Barcode.Trim()} 已创建，方法 {BoundMethodCode ?? SelectedStandard?.MethodCode} 已关联批次 {BatchId.Trim()}。";
            await _refresh();
        }
        catch (Exception exception) { Status = $"创建失败：{exception.Message}"; }
    }

    private void ApplyBatchMethod()
    {
        var method = _existingSamples.FirstOrDefault(item =>
            string.Equals(item.SampleBatchId, BatchId.Trim(), StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(item.DetectionMethodCode))?.DetectionMethodCode;
        BoundMethodCode = method;
        IsMethodLocked = method is not null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMethodSelectionEnabled)));
        if (method is null)
        {
            SelectedStandard = null;
            Status = "填写样品信息并选择检测方法。";
            return;
        }
        SelectedStandard = Standards.FirstOrDefault(item =>
            string.Equals(item.MethodCode, method, StringComparison.OrdinalIgnoreCase));
        Status = SelectedStandard is null
            ? $"批次已关联方法 {method}，该方法当前未启用；新样品仍沿用原批次方法。"
            : $"批次已关联方法 {method}，新样品将沿用。";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

public sealed class CreateConsumableViewModel : INotifyPropertyChanged
{
    private readonly IMesClient _mes;
    private readonly IReadOnlyList<MaterialLotInventory> _inventory;
    private readonly string _actor;
    private readonly Func<Task> _refresh;
    private readonly Guid _requestId = Guid.NewGuid();
    private string _materialCode = string.Empty;
    private string _materialName = string.Empty;
    private string _lotCode = string.Empty;
    private string _quantity = "1";
    private string _unit = string.Empty;
    private string _barcode = string.Empty;
    private string _specification = string.Empty;
    private string _locationCode = "DEFAULT";
    private string _status = "填写耗材批次和入库数量。";
    private DateTime? _manufactureDate;
    private DateTime? _expiryDate;

    public CreateConsumableViewModel(
        IMesClient mes, IReadOnlyList<MaterialLotInventory> inventory, string actor, Func<Task> refresh)
    {
        _mes = mes;
        _inventory = inventory;
        _actor = actor;
        _refresh = refresh;
        SaveCommand = new AsyncCommand(SaveAsync, () => !IsSaved);
    }

    public AsyncCommand SaveCommand { get; }
    public bool IsSaved { get; private set; }
    public string MaterialCode { get => _materialCode; set => SetField(ref _materialCode, value); }
    public string MaterialName { get => _materialName; set => SetField(ref _materialName, value); }
    public string LotCode { get => _lotCode; set => SetField(ref _lotCode, value); }
    public string Quantity { get => _quantity; set => SetField(ref _quantity, value); }
    public string Unit { get => _unit; set => SetField(ref _unit, value); }
    public string Barcode { get => _barcode; set => SetField(ref _barcode, value); }
    public string Specification { get => _specification; set => SetField(ref _specification, value); }
    public string LocationCode { get => _locationCode; set => SetField(ref _locationCode, value); }
    public string Status { get => _status; private set => SetField(ref _status, value); }
    public DateTime? ManufactureDate { get => _manufactureDate; set => SetField(ref _manufactureDate, value); }
    public DateTime? ExpiryDate { get => _expiryDate; set => SetField(ref _expiryDate, value); }

    public async Task SaveAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(MaterialCode) || string.IsNullOrWhiteSpace(MaterialName) ||
                string.IsNullOrWhiteSpace(LotCode))
                throw new ArgumentException("物料编码、名称和批号不能为空。");
            if (!decimal.TryParse(Quantity, NumberStyles.Float, CultureInfo.CurrentCulture, out var count) &&
                !decimal.TryParse(Quantity, NumberStyles.Float, CultureInfo.InvariantCulture, out count) || count <= 0)
                throw new ArgumentException("数量必须是大于 0 的数字。");
            var existing = _inventory.Any(item =>
                string.Equals(item.MaterialCode, MaterialCode.Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.LotCode, LotCode.Trim(), StringComparison.OrdinalIgnoreCase));
            var result = await _mes.ReceiveMaterialAsync(new ReceiveMaterialRequest
            {
                RequestId = _requestId,
                Actor = _actor,
                MaterialCode = MaterialCode.Trim(),
                MaterialName = MaterialName.Trim(),
                LotCode = LotCode.Trim(),
                Quantity = count,
                Unit = string.IsNullOrWhiteSpace(Unit) ? null : Unit.Trim(),
                Barcode = string.IsNullOrWhiteSpace(Barcode) ? null : Barcode.Trim(),
                Specification = string.IsNullOrWhiteSpace(Specification) ? null : Specification.Trim(),
                LocationCode = LocationCode.Trim(),
                ManufactureDate = ManufactureDate is null ? null : new DateTimeOffset(ManufactureDate.Value),
                ExpiryDate = ExpiryDate is null ? null : new DateTimeOffset(ExpiryDate.Value)
            }, CancellationToken.None);
            IsSaved = true;
            SaveCommand.RaiseCanExecuteChanged();
            Status = result.IsIdempotentReplay ? "入库请求已确认。" :
                existing ? "现有批次已增加库存。" : "耗材批次已创建并入库。";
            await _refresh();
        }
        catch (Exception exception) { Status = $"创建失败：{exception.Message}"; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
