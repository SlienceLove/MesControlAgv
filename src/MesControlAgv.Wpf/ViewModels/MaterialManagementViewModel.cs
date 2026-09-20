using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using MesControlAgv.Contracts.Materials;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// Compact client-side state for samples, consumable inventory and traceability.
/// It intentionally keeps warehouse fields out of the experiment editor; all
/// writes go through the MES material API.
/// </summary>
public sealed class MaterialManagementViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IMesClient _mes;
    private readonly string _actor =
        Environment.GetEnvironmentVariable("WORKFLOW_OPERATOR") ?? Environment.UserName;
    private string _importMode = "样品";
    private string _selectedFilePath = string.Empty;
    private MaterialImportRequest? _pendingImport;
    private MaterialImportPreview? _preview;
    private string _importStatus = "请选择 CSV 或 XLSX 文件。";
    private string _scanCode = string.Empty;
    private string _scanStatus = "等待扫码。";
    private MaterialScanResult? _scanResult;
    private string _inventoryMaterialCode = string.Empty;
    private string _inventoryLotCode = string.Empty;
    private string _inventoryLocationCode = string.Empty;
    private string _traceQuery = string.Empty;
    private string _traceStatus = "输入条码、物料编码或批号后查询。";
    private string _receiveMaterialCode = string.Empty;
    private string _receiveMaterialName = string.Empty;
    private string _receiveLotCode = string.Empty;
    private string _receiveQuantity = "1";
    private string _receiveUnit = string.Empty;
    private string _receiveLocationCode = "DEFAULT";
    private string _actionStatus = string.Empty;
    private bool _includeQuarantined;

    public MaterialManagementViewModel(IMesClient mes)
    {
        _mes = mes ?? throw new ArgumentNullException(nameof(mes));
        RefreshCommand = new AsyncCommand(() => RefreshAsync());
        PreviewImportCommand = new AsyncCommand(PreviewImportAsync, () => _pendingImport is not null);
        CommitImportCommand = new AsyncCommand(CommitImportAsync, () => _pendingImport is not null && Preview?.CanCommit == true);
        ScanCommand = new AsyncCommand(ScanAsync, () => !string.IsNullOrWhiteSpace(ScanCode));
        QueryTraceCommand = new AsyncCommand(QueryTraceAsync, () => !string.IsNullOrWhiteSpace(TraceQuery));
        ReceiveCommand = new AsyncCommand(ReceiveAsync, () =>
            !string.IsNullOrWhiteSpace(ReceiveMaterialCode) &&
            !string.IsNullOrWhiteSpace(ReceiveLotCode));
    }

    public ObservableCollection<MaterialCatalogItem> Catalog { get; } = [];
    public ObservableCollection<WarehouseLocation> Locations { get; } = [];
    public ObservableCollection<SampleMaterial> Samples { get; } = [];
    public ObservableCollection<MaterialLotInventory> Inventory { get; } = [];
    public ObservableCollection<MaterialTraceEvent> TraceEvents { get; } = [];
    public ObservableCollection<MaterialImportIssue> ImportIssues { get; } = [];
    public OfflineDataStateViewModel OfflineState { get; } = new();

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand PreviewImportCommand { get; }
    public AsyncCommand CommitImportCommand { get; }
    public AsyncCommand ScanCommand { get; }
    public AsyncCommand QueryTraceCommand { get; }
    public AsyncCommand ReceiveCommand { get; }

    public string ImportMode
    {
        get => _importMode;
        set
        {
            if (!SetField(ref _importMode, value)) return;
            _pendingImport = null;
            Preview = null;
            ImportIssues.Clear();
            ImportStatus = "请选择与导入类型对应的文件。";
            PreviewImportCommand.RaiseCanExecuteChanged();
            CommitImportCommand.RaiseCanExecuteChanged();
        }
    }

    public IReadOnlyList<string> ImportModes { get; } = ["样品", "耗材"];

    public string SelectedFilePath
    {
        get => _selectedFilePath;
        private set => SetField(ref _selectedFilePath, value);
    }

    public string ImportStatus
    {
        get => _importStatus;
        private set => SetField(ref _importStatus, value);
    }

    public MaterialImportPreview? Preview
    {
        get => _preview;
        private set
        {
            if (!SetField(ref _preview, value)) return;
            OnPropertyChanged(nameof(CanCommitImport));
            CommitImportCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanCommitImport => Preview?.CanCommit == true && _pendingImport is not null;

    public string ScanCode
    {
        get => _scanCode;
        set
        {
            if (!SetField(ref _scanCode, value)) return;
            ScanCommand.RaiseCanExecuteChanged();
        }
    }

    public string ScanStatus
    {
        get => _scanStatus;
        private set => SetField(ref _scanStatus, value);
    }

    public MaterialScanResult? ScanResult
    {
        get => _scanResult;
        private set => SetField(ref _scanResult, value);
    }

    public string InventoryMaterialCode
    {
        get => _inventoryMaterialCode;
        set => SetField(ref _inventoryMaterialCode, value);
    }

    public string InventoryLotCode
    {
        get => _inventoryLotCode;
        set => SetField(ref _inventoryLotCode, value);
    }

    public string InventoryLocationCode
    {
        get => _inventoryLocationCode;
        set => SetField(ref _inventoryLocationCode, value);
    }

    public bool IncludeQuarantined
    {
        get => _includeQuarantined;
        set => SetField(ref _includeQuarantined, value);
    }

    public string TraceQuery
    {
        get => _traceQuery;
        set
        {
            if (!SetField(ref _traceQuery, value)) return;
            QueryTraceCommand.RaiseCanExecuteChanged();
        }
    }

    public string TraceStatus
    {
        get => _traceStatus;
        private set => SetField(ref _traceStatus, value);
    }

    public string ReceiveMaterialCode
    {
        get => _receiveMaterialCode;
        set
        {
            if (!SetField(ref _receiveMaterialCode, value)) return;
            ReceiveCommand.RaiseCanExecuteChanged();
        }
    }

    public string ReceiveMaterialName
    {
        get => _receiveMaterialName;
        set => SetField(ref _receiveMaterialName, value);
    }

    public string ReceiveLotCode
    {
        get => _receiveLotCode;
        set
        {
            if (!SetField(ref _receiveLotCode, value)) return;
            ReceiveCommand.RaiseCanExecuteChanged();
        }
    }

    public string ReceiveQuantity
    {
        get => _receiveQuantity;
        set => SetField(ref _receiveQuantity, value);
    }

    public string ReceiveUnit
    {
        get => _receiveUnit;
        set => SetField(ref _receiveUnit, value);
    }

    public string ReceiveLocationCode
    {
        get => _receiveLocationCode;
        set => SetField(ref _receiveLocationCode, value);
    }

    public string ActionStatus
    {
        get => _actionStatus;
        private set => SetField(ref _actionStatus, value);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        OfflineState.BeginLoading("正在刷新物料数据...");
        try
        {
            var catalogTask = _mes.GetMaterialCatalogAsync(null, null, cancellationToken);
            var locationsTask = _mes.GetMaterialLocationsAsync(cancellationToken);
            var samplesTask = _mes.GetSamplesAsync(null, null, null, cancellationToken);
            var inventoryTask = _mes.GetMaterialInventoryAsync(
                InventoryMaterialCode,
                InventoryLotCode,
                InventoryLocationCode,
                IncludeQuarantined,
                cancellationToken);
            await Task.WhenAll(catalogTask, locationsTask, samplesTask, inventoryTask);
            Replace(Catalog, await catalogTask);
            Replace(Locations, await locationsTask);
            Replace(Samples, await samplesTask);
            Replace(Inventory, await inventoryTask);
            OfflineState.MarkReady(
                Catalog.Count + Samples.Count + Inventory.Count > 0,
                $"已更新：样品 {Samples.Count} 条，库存 {Inventory.Count} 条。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            OfflineState.MarkCancelled();
        }
        catch (Exception exception)
        {
            OfflineState.MarkError("物料数据刷新失败。", exception.Message);
            ActionStatus = exception.Message;
        }
    }

    public async Task LoadImportFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        SelectedFilePath = filePath;
        ImportIssues.Clear();
        Preview = null;
        try
        {
            await using var stream = File.OpenRead(filePath);
            var rows = TabularFileReader.ReadRows(stream, Path.GetFileName(filePath));
            _pendingImport = BuildImportRequest(rows);
            ImportStatus = $"已读取 {rows.Count} 行，点击“预览校验”。";
            PreviewImportCommand.RaiseCanExecuteChanged();
            CommitImportCommand.RaiseCanExecuteChanged();
        }
        catch (Exception exception)
        {
            _pendingImport = null;
            ImportStatus = $"文件读取失败：{exception.Message}";
            ImportIssues.Add(new MaterialImportIssue { RowNumber = 0, Code = "FILE", Message = exception.Message });
            PreviewImportCommand.RaiseCanExecuteChanged();
            CommitImportCommand.RaiseCanExecuteChanged();
        }
    }

    public async Task PreviewImportAsync()
    {
        if (_pendingImport is null) return;
        try
        {
            Preview = await _mes.PreviewMaterialImportAsync(_pendingImport, CancellationToken.None);
            Replace(ImportIssues, Preview.Issues);
            ImportStatus = $"可提交 {Preview.AcceptedCount} 行，已有 {Preview.ExistingCount} 行，问题 {Preview.Issues.Count} 行。";
        }
        catch (Exception exception)
        {
            ImportStatus = $"预览失败：{exception.Message}";
        }
    }

    public async Task CommitImportAsync()
    {
        if (_pendingImport is null || !CanCommitImport) return;
        try
        {
            var result = await _mes.ImportMaterialsAsync(_pendingImport, CancellationToken.None);
            ActionStatus = result.IsIdempotentReplay ? "导入请求已幂等重放。" : "物料导入完成。";
            ImportStatus = $"样品 {result.Data?.ImportedSamples ?? 0} 条，耗材批次 {result.Data?.ImportedConsumableLots ?? 0} 条。";
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ActionStatus = $"导入失败：{exception.Message}";
        }
    }

    public async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(ScanCode)) return;
        try
        {
            ScanResult = await _mes.ScanMaterialAsync(new MaterialScanRequest
            {
                RequestId = Guid.NewGuid(),
                Actor = _actor,
                RawCode = ScanCode.Trim(),
                Kind = MaterialScanKind.Unknown,
                Source = "manual"
            }, CancellationToken.None);
            ScanStatus = ScanResult.IsResolved
                ? $"已识别：{ScanResult.Kind}"
                : $"未识别：{ScanResult.IssueCode} {ScanResult.Message}";
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ScanStatus = $"扫码失败：{exception.Message}";
        }
    }

    public async Task ReceiveAsync()
    {
        if (!decimal.TryParse(ReceiveQuantity, NumberStyles.Number, CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
        {
            ActionStatus = "入库数量必须是大于 0 的数字。";
            return;
        }

        try
        {
            var result = await _mes.ReceiveMaterialAsync(new ReceiveMaterialRequest
            {
                RequestId = Guid.NewGuid(),
                Actor = _actor,
                MaterialCode = ReceiveMaterialCode,
                MaterialName = ReceiveMaterialName,
                LotCode = ReceiveLotCode,
                Quantity = quantity,
                Unit = ReceiveUnit,
                LocationCode = ReceiveLocationCode
            }, CancellationToken.None);
            ActionStatus = result.IsIdempotentReplay ? "入库请求已幂等重放。" : "入库完成。";
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ActionStatus = $"入库失败：{exception.Message}";
        }
    }

    public async Task QueryTraceAsync()
    {
        if (string.IsNullOrWhiteSpace(TraceQuery)) return;
        try
        {
            var value = TraceQuery.Trim();
            var byBarcode = _mes.TraceMaterialAsync(value, null, null, null, 200, CancellationToken.None);
            var byMaterial = _mes.TraceMaterialAsync(null, value, null, null, 200, CancellationToken.None);
            var byLot = _mes.TraceMaterialAsync(null, null, value, null, 200, CancellationToken.None);
            await Task.WhenAll(byBarcode, byMaterial, byLot);
            var events = (await byBarcode)
                .Concat(await byMaterial)
                .Concat(await byLot)
                .GroupBy(item => item.Id)
                .Select(group => group.First())
                .OrderByDescending(item => item.OccurredAt)
                .ToList();
            Replace(TraceEvents, events);
            TraceStatus = $"找到 {TraceEvents.Count} 条溯源记录。";
        }
        catch (Exception exception)
        {
            TraceStatus = $"溯源查询失败：{exception.Message}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose()
    {
        // Kept disposable so MainViewModel can own the page lifecycle uniformly.
    }

    private MaterialImportRequest BuildImportRequest(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0) throw new InvalidDataException("文件没有可导入的行。");
        var headers = rows[0]
            .Select((value, index) => (Key: TabularFileReader.NormalizeHeader(value), Index: index))
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .ToDictionary(item => item.Key, item => item.Index, StringComparer.OrdinalIgnoreCase);
        string Get(IReadOnlyList<string> row, params string[] names)
        {
            foreach (var name in names)
            {
                if (headers.TryGetValue(TabularFileReader.NormalizeHeader(name), out var index) && index < row.Count)
                    return row[index].Trim();
            }
            return string.Empty;
        }

        var samples = new List<SampleImportRow>();
        var consumables = new List<ConsumableImportRow>();
        for (var index = 1; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            if (string.Equals(ImportMode, "样品", StringComparison.Ordinal))
            {
                samples.Add(new SampleImportRow
                {
                    RowNumber = index + 1,
                    Barcode = Get(row, "barcode", "条码", "samplebarcode", "样品条码"),
                    SampleBatchId = Get(row, "samplebatchid", "batchid", "样品批次", "批次"),
                    MaterialCode = Get(row, "materialcode", "物料编码"),
                    SampleType = Get(row, "sampletype", "样品类型"),
                    LocationCode = Get(row, "locationcode", "库位")
                });
            }
            else
            {
                var quantityText = Get(row, "quantity", "数量");
                _ = decimal.TryParse(quantityText, NumberStyles.Number, CultureInfo.InvariantCulture, out var quantity);
                consumables.Add(new ConsumableImportRow
                {
                    RowNumber = index + 1,
                    MaterialCode = Get(row, "materialcode", "物料编码"),
                    Name = Get(row, "name", "名称", "物料名称"),
                    Specification = Get(row, "specification", "规格"),
                    Unit = Get(row, "unit", "单位"),
                    LotCode = Get(row, "lotcode", "lot", "批号"),
                    Quantity = quantity,
                    ManufactureDate = ParseDate(Get(row, "manufacturedate", "生产日期")),
                    ExpiryDate = ParseDate(Get(row, "expirydate", "有效期", "失效日期")),
                    LocationCode = Get(row, "locationcode", "库位"),
                    Barcode = Get(row, "barcode", "条码", "批次条码")
                });
            }
        }

        return new MaterialImportRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = _actor,
            Source = Path.GetFileName(SelectedFilePath),
            Samples = samples,
            Consumables = consumables
        };
    }

    private static DateTimeOffset? ParseDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : null;

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
