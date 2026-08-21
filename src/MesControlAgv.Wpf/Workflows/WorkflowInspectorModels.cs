using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Workflows;

namespace MesControlAgv.Wpf.Workflows;

public enum WorkflowInspectorEditorKind
{
    Text,
    Number,
    Boolean,
    Selection,
    ReadOnly
}

public sealed record WorkflowInspectorOption(
    string Value,
    string DisplayName,
    bool IsAvailable = true,
    string? UnavailableReason = null);

public sealed record WorkflowInspectorCapabilityViewModel(
    string CapabilityId,
    string DisplayName,
    bool IsAvailable,
    string Status);

/// <summary>
/// Stable WPF projection of one catalog schema field. Values remain invariant
/// strings because WorkflowGraphDocument.Configuration is the canonical owner.
/// </summary>
public sealed class WorkflowInspectorFieldViewModel : INotifyPropertyChanged
{
    private readonly Action<WorkflowInspectorFieldViewModel, string?>? _commit;
    private string? _value;
    private string? _validationMessage;

    internal WorkflowInspectorFieldViewModel(
        Guid nodeId,
        string key,
        string displayName,
        WorkflowSchemaValueType valueType,
        WorkflowSchemaReferenceKind referenceKind,
        bool isRequired,
        string? unit,
        decimal? minimum,
        decimal? maximum,
        WorkflowInspectorEditorKind editorKind,
        IReadOnlyList<WorkflowInspectorOption> options,
        string? value,
        bool isReadOnly,
        bool isUnknown,
        bool requiresMigration,
        Action<WorkflowInspectorFieldViewModel, string?>? commit)
    {
        NodeId = nodeId;
        Key = key;
        DisplayName = displayName;
        ValueType = valueType;
        ReferenceKind = referenceKind;
        IsRequired = isRequired;
        Unit = unit;
        Minimum = minimum;
        Maximum = maximum;
        EditorKind = editorKind;
        Options = options;
        _value = value;
        IsReadOnly = isReadOnly;
        IsUnknown = isUnknown;
        RequiresMigration = requiresMigration;
        _commit = commit;
        Validate();
    }

    public Guid NodeId { get; }
    public string Key { get; }
    public string DisplayName { get; }
    public WorkflowSchemaValueType ValueType { get; }
    public WorkflowSchemaReferenceKind ReferenceKind { get; }
    public bool IsRequired { get; }
    public string? Unit { get; }
    public decimal? Minimum { get; }
    public decimal? Maximum { get; }
    public WorkflowInspectorEditorKind EditorKind { get; }
    public IReadOnlyList<WorkflowInspectorOption> Options { get; }
    public bool IsReadOnly { get; }
    public bool IsUnknown { get; }
    public bool RequiresMigration { get; }

    public bool IsTextEditor => EditorKind == WorkflowInspectorEditorKind.Text;
    public bool IsNumberEditor => EditorKind == WorkflowInspectorEditorKind.Number;
    public bool IsBooleanEditor => EditorKind == WorkflowInspectorEditorKind.Boolean;
    public bool IsSelectionEditor => EditorKind == WorkflowInspectorEditorKind.Selection;
    public bool IsReadOnlyEditor => EditorKind == WorkflowInspectorEditorKind.ReadOnly;
    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public string? Value
    {
        get => _value;
        set
        {
            if (IsReadOnly) return;
            var normalized = Normalize(value);
            if (EditorKind == WorkflowInspectorEditorKind.Selection &&
                !string.IsNullOrWhiteSpace(normalized) &&
                !Options.Any(option =>
                    option.IsAvailable &&
                    string.Equals(option.Value, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                ValidationMessage = $"值 '{normalized}' 不在当前 Profile/目录的可用选项中。";
                return;
            }

            if (string.Equals(_value, normalized, StringComparison.Ordinal)) return;
            _value = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BooleanValue));
            Validate();
            _commit?.Invoke(this, normalized);
        }
    }

    public bool BooleanValue
    {
        get => bool.TryParse(Value, out var parsed) && parsed;
        set => Value = value ? "true" : "false";
    }

    public string? ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (_validationMessage == value) return;
            _validationMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasValidationMessage));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private string? Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return EditorKind is WorkflowInspectorEditorKind.Number or WorkflowInspectorEditorKind.Selection
            ? value.Trim()
            : value;
    }

    private void Validate()
    {
        if (IsUnknown)
        {
            ValidationMessage = "未知配置字段已只读保留；迁移前不能发布。";
            return;
        }

        if (IsReadOnly)
        {
            ValidationMessage = RequiresMigration ? "当前节点 schema 不受支持，字段只读保留。" : null;
            return;
        }

        if (string.IsNullOrWhiteSpace(Value))
        {
            ValidationMessage = IsRequired ? "此字段为必填项。" : null;
            return;
        }

        if (EditorKind == WorkflowInspectorEditorKind.Selection)
        {
            var option = Options.FirstOrDefault(candidate =>
                string.Equals(candidate.Value, Value, StringComparison.OrdinalIgnoreCase));
            ValidationMessage = option switch
            {
                null => "当前值不在 Profile/目录选项中。",
                { IsAvailable: false } => option.UnavailableReason ?? "当前选项不可用。",
                _ => null
            };
            return;
        }

        if (!TryParse(Value!, out var numericValue))
        {
            ValidationMessage = $"请输入有效的{DescribeValueType(ValueType)}。";
            return;
        }

        if (numericValue is { } number &&
            (Minimum is { } minimum && number < minimum ||
             Maximum is { } maximum && number > maximum))
        {
            ValidationMessage = Minimum is { } min && Maximum is { } max
                ? $"允许范围为 {min} 至 {max}{FormatUnit()}."
                : Minimum is { } lower
                    ? $"最小值为 {lower}{FormatUnit()}."
                    : $"最大值为 {Maximum}{FormatUnit()}.";
            return;
        }

        ValidationMessage = null;
    }

    private bool TryParse(string value, out decimal? numericValue)
    {
        numericValue = null;
        switch (ValueType)
        {
            case WorkflowSchemaValueType.String:
                return true;
            case WorkflowSchemaValueType.Integer:
                if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) ||
                    integer != decimal.Truncate(integer))
                    return false;
                numericValue = integer;
                return true;
            case WorkflowSchemaValueType.Decimal:
                if (!decimal.TryParse(
                        value,
                        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture,
                        out var number))
                    return false;
                numericValue = number;
                return true;
            case WorkflowSchemaValueType.Boolean:
                return bool.TryParse(value, out _);
            case WorkflowSchemaValueType.DateTimeOffset:
                return DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out _);
            default:
                return false;
        }
    }

    private string FormatUnit() => string.IsNullOrWhiteSpace(Unit) ? string.Empty : $" {Unit}";

    private static string DescribeValueType(WorkflowSchemaValueType valueType) => valueType switch
    {
        WorkflowSchemaValueType.Integer => "整数",
        WorkflowSchemaValueType.Decimal => "数值",
        WorkflowSchemaValueType.Boolean => "布尔值",
        WorkflowSchemaValueType.DateTimeOffset => "日期时间",
        _ => "文本"
    };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class WorkflowInspectorViewModel : INotifyPropertyChanged
{
    private readonly ObservableCollection<WorkflowInspectorFieldViewModel> _fields = [];
    private readonly ObservableCollection<WorkflowInspectorCapabilityViewModel> _capabilities = [];
    private bool _hasNode;
    private bool _isTyped;
    private bool _requiresMigration;
    private string _nodeTypeId = string.Empty;
    private string _nodeTypeDisplayName = string.Empty;
    private string _schemaVersion = string.Empty;
    private string _category = string.Empty;
    private string _executionMode = string.Empty;
    private string _safetyClassification = string.Empty;
    private string _compatibilityMessage = string.Empty;

    public WorkflowInspectorViewModel()
    {
        Fields = new ReadOnlyObservableCollection<WorkflowInspectorFieldViewModel>(_fields);
        Capabilities = new ReadOnlyObservableCollection<WorkflowInspectorCapabilityViewModel>(_capabilities);
    }

    public ReadOnlyObservableCollection<WorkflowInspectorFieldViewModel> Fields { get; }
    public ReadOnlyObservableCollection<WorkflowInspectorCapabilityViewModel> Capabilities { get; }

    public bool HasNode { get => _hasNode; private set => SetField(ref _hasNode, value); }
    public bool IsTyped { get => _isTyped; private set => SetField(ref _isTyped, value); }
    public bool RequiresMigration { get => _requiresMigration; private set => SetField(ref _requiresMigration, value); }
    public string NodeTypeId { get => _nodeTypeId; private set => SetField(ref _nodeTypeId, value); }
    public string NodeTypeDisplayName { get => _nodeTypeDisplayName; private set => SetField(ref _nodeTypeDisplayName, value); }
    public string SchemaVersion { get => _schemaVersion; private set => SetField(ref _schemaVersion, value); }
    public string Category { get => _category; private set => SetField(ref _category, value); }
    public string ExecutionMode { get => _executionMode; private set => SetField(ref _executionMode, value); }
    public string SafetyClassification { get => _safetyClassification; private set => SetField(ref _safetyClassification, value); }
    public string CompatibilityMessage { get => _compatibilityMessage; private set => SetField(ref _compatibilityMessage, value); }
    public bool HasFields => Fields.Count > 0;
    public bool HasCapabilities => Capabilities.Count > 0;

    internal void Load(
        WorkflowNode? node,
        WorkflowCatalogSet catalog,
        WorkflowPublicationContext profile,
        IReadOnlyDictionary<string, string> stationNames,
        Action<WorkflowInspectorFieldViewModel, string?> commit)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(stationNames);
        ArgumentNullException.ThrowIfNull(commit);

        _fields.Clear();
        _capabilities.Clear();
        if (node is null)
        {
            HasNode = false;
            IsTyped = false;
            RequiresMigration = false;
            NodeTypeId = string.Empty;
            NodeTypeDisplayName = string.Empty;
            SchemaVersion = string.Empty;
            Category = string.Empty;
            ExecutionMode = string.Empty;
            SafetyClassification = string.Empty;
            CompatibilityMessage = "请选择一个节点。";
            RaiseCollectionState();
            return;
        }

        HasNode = true;
        NodeTypeId = node.GraphNodeTypeId;
        SchemaVersion = node.SchemaVersion;
        var resolution = catalog.NodeTypes.Resolve(node.GraphNodeTypeId, node.SchemaVersion);
        var definition = resolution.Definition;
        IsTyped = resolution.Status == WorkflowCatalogResolutionStatus.Supported && definition is not null;
        NodeTypeDisplayName = definition?.DisplayName ?? node.GraphNodeTypeId;
        Category = definition?.Category ?? "Compatibility";
        ExecutionMode = definition is null ? "-" : DescribeExecutionMode(definition.ExecutionMode);
        SafetyClassification = definition is null ? "-" : DescribeSafety(definition.SafetyClassification);

        if (definition is null)
        {
            foreach (var pair in node.Configuration.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                _fields.Add(CreateUnknownField(node.Id, pair.Key, pair.Value));

            RequiresMigration = true;
            CompatibilityMessage = resolution.Status switch
            {
                WorkflowCatalogResolutionStatus.UnknownType => "未知节点类型：配置已只读保留，需显式迁移。",
                WorkflowCatalogResolutionStatus.FutureSchemaVersion => "节点使用未来 schema：配置已只读保留，需升级编辑器或迁移。",
                _ => "节点 schema 不受支持：配置已只读保留，需显式迁移。"
            };
            RaiseCollectionState();
            return;
        }

        var schemaKeys = definition.ConfigurationSchema.Fields
            .Select(field => field.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var field in definition.ConfigurationSchema.Fields)
        {
            TryGetValue(node.Configuration, field.Key, out var value);
            var options = BuildOptions(field, definition, catalog, profile, stationNames, value);
            _fields.Add(new WorkflowInspectorFieldViewModel(
                node.Id,
                field.Key,
                field.DisplayName,
                field.ValueType,
                field.ReferenceKind,
                field.IsRequired,
                field.Unit,
                field.Minimum,
                field.Maximum,
                ResolveEditorKind(field),
                options,
                value,
                isReadOnly: false,
                isUnknown: false,
                requiresMigration: false,
                commit));
        }

        var unknownFields = node.Configuration
            .Where(pair => !schemaKeys.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var pair in unknownFields)
            _fields.Add(CreateUnknownField(node.Id, pair.Key, pair.Value));

        foreach (var capabilityId in definition.RequiredCapabilityIds)
            _capabilities.Add(BuildCapability(capabilityId, catalog, profile));

        RequiresMigration = unknownFields.Length > 0;
        CompatibilityMessage = unknownFields.Length == 0
            ? "节点类型和 schema 已匹配当前目录。"
            : $"保留了 {unknownFields.Length} 个未知字段；迁移前不能发布。";
        RaiseCollectionState();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static WorkflowInspectorFieldViewModel CreateUnknownField(Guid nodeId, string key, string? value) => new(
        nodeId,
        key,
        key,
        WorkflowSchemaValueType.String,
        WorkflowSchemaReferenceKind.None,
        isRequired: false,
        unit: null,
        minimum: null,
        maximum: null,
        WorkflowInspectorEditorKind.ReadOnly,
        Array.Empty<WorkflowInspectorOption>(),
        value,
        isReadOnly: true,
        isUnknown: true,
        requiresMigration: true,
        commit: null);

    private static IReadOnlyList<WorkflowInspectorOption> BuildOptions(
        WorkflowFieldSchema field,
        WorkflowNodeTypeDefinition definition,
        WorkflowCatalogSet catalog,
        WorkflowPublicationContext profile,
        IReadOnlyDictionary<string, string> stationNames,
        string? currentValue)
    {
        var options = field.ReferenceKind switch
        {
            WorkflowSchemaReferenceKind.Station => profile.Stations
                .OrderBy(station => station.StationId, StringComparer.Ordinal)
                .Select(station => new WorkflowInspectorOption(
                    station.StationId,
                    DescribeStation(station, stationNames),
                    station.Enabled,
                    station.Enabled ? null : "站点在当前 Profile 中已禁用。"))
                .ToList(),
            WorkflowSchemaReferenceKind.Device => BuildDeviceOptions(field, definition, profile),
            WorkflowSchemaReferenceKind.Capability => BuildCapabilityOptions(catalog, profile),
            _ when field.AllowedValues.Count > 0 => field.AllowedValues
                .Select(value => new WorkflowInspectorOption(value, DescribeAllowedValue(value)))
                .ToList(),
            _ => []
        };

        if (!field.IsRequired && field.ReferenceKind == WorkflowSchemaReferenceKind.Device)
            options.Insert(0, new WorkflowInspectorOption(string.Empty, "自动选择"));

        if (!string.IsNullOrWhiteSpace(currentValue) &&
            options.All(option => !string.Equals(option.Value, currentValue, StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(new WorkflowInspectorOption(
                currentValue,
                $"{currentValue}（当前值不可用）",
                false,
                "当前值不在 Profile/目录中；选择有效值前将原样保留。"));
        }

        return options.AsReadOnly();
    }

    private static List<WorkflowInspectorOption> BuildDeviceOptions(
        WorkflowFieldSchema field,
        WorkflowNodeTypeDefinition definition,
        WorkflowPublicationContext profile)
    {
        var requiresControl = definition.SafetyClassification is
            WorkflowSafetyClassification.ControlledDeviceAction or
            WorkflowSafetyClassification.RestrictedDeviceWrite;
        return profile.GetDevices(field.DeviceFamily ?? string.Empty)
            .OrderBy(device => device.DeviceId, StringComparer.Ordinal)
            .Select(device =>
            {
                var missingCapability = definition.RequiredCapabilityIds.FirstOrDefault(capabilityId =>
                    !device.Provides(capabilityId));
                var available = device.Enabled &&
                                (!requiresControl || device.ControlEnabled) &&
                                missingCapability is null;
                var reason = !device.Enabled
                    ? "设备在当前 Profile 中已禁用。"
                    : requiresControl && !device.ControlEnabled
                        ? "设备控制在当前 Profile 中未启用。"
                        : missingCapability is not null
                            ? $"设备未声明能力 {missingCapability}."
                            : null;
                return new WorkflowInspectorOption(
                    device.DeviceId,
                    available ? device.DeviceId : $"{device.DeviceId}（不可用）",
                    available,
                    reason);
            })
            .ToList();
    }

    private static List<WorkflowInspectorOption> BuildCapabilityOptions(
        WorkflowCatalogSet catalog,
        WorkflowPublicationContext profile) => catalog.Capabilities.Definitions
        .GroupBy(capability => capability.CapabilityId, StringComparer.OrdinalIgnoreCase)
        .Select(group => catalog.Capabilities.GetLatest(group.Key)!)
        .OrderBy(capability => capability.CapabilityId, StringComparer.Ordinal)
        .Select(capability =>
        {
            var available = IsCapabilityAvailable(capability, profile, out var reason);
            return new WorkflowInspectorOption(
                capability.CapabilityId,
                $"{capability.DisplayName} ({capability.CapabilityId})" + (available ? string.Empty : "（不可用）"),
                available,
                reason);
        })
        .ToList();

    private static WorkflowInspectorCapabilityViewModel BuildCapability(
        string capabilityId,
        WorkflowCatalogSet catalog,
        WorkflowPublicationContext profile)
    {
        var capability = catalog.Capabilities.GetLatest(capabilityId);
        if (capability is null)
            return new WorkflowInspectorCapabilityViewModel(capabilityId, capabilityId, false, "目录中不存在");

        var available = IsCapabilityAvailable(capability, profile, out var reason);
        return new WorkflowInspectorCapabilityViewModel(
            capability.CapabilityId,
            capability.DisplayName,
            available,
            available ? "当前 Profile 可用" : reason ?? "不可用");
    }

    private static bool IsCapabilityAvailable(
        DeviceCapabilityDefinition capability,
        WorkflowPublicationContext profile,
        out string? reason)
    {
        if (!capability.Enabled)
        {
            reason = capability.UnavailableReason ?? "能力已禁用。";
            return false;
        }
        if (capability.SafetyClassification is
                WorkflowSafetyClassification.ControlledDeviceAction or
                WorkflowSafetyClassification.RestrictedDeviceWrite &&
            !capability.ControlEnabled)
        {
            reason = capability.UnavailableReason ?? "能力控制未启用。";
            return false;
        }
        if (!SupportsProfile(capability.ProfileSupport, profile.ProductId))
        {
            reason = "能力不支持当前 Profile。";
            return false;
        }

        var providers = profile.GetDevices(capability.DeviceFamily)
            .Where(device => device.Enabled && device.Provides(capability.CapabilityId));
        if (capability.SafetyClassification is
            WorkflowSafetyClassification.ControlledDeviceAction or
            WorkflowSafetyClassification.RestrictedDeviceWrite)
            providers = providers.Where(device => device.ControlEnabled);
        if (!providers.Any())
        {
            reason = "当前 Profile 没有可用设备提供此能力。";
            return false;
        }

        reason = null;
        return true;
    }

    private static WorkflowInspectorEditorKind ResolveEditorKind(WorkflowFieldSchema field)
    {
        if (field.ReferenceKind != WorkflowSchemaReferenceKind.None || field.AllowedValues.Count > 0)
            return WorkflowInspectorEditorKind.Selection;
        return field.ValueType switch
        {
            WorkflowSchemaValueType.Boolean => WorkflowInspectorEditorKind.Boolean,
            WorkflowSchemaValueType.Integer or WorkflowSchemaValueType.Decimal => WorkflowInspectorEditorKind.Number,
            _ => WorkflowInspectorEditorKind.Text
        };
    }

    private static string DescribeStation(
        WorkflowStationAvailability station,
        IReadOnlyDictionary<string, string> stationNames)
    {
        var label = stationNames.TryGetValue(station.StationId, out var name) && !string.IsNullOrWhiteSpace(name)
            ? $"{name} ({station.StationId})"
            : station.StationId;
        return station.Enabled ? label : $"{label}（已禁用）";
    }

    private static string DescribeAllowedValue(string value) => value switch
    {
        "pressureMpa" => "压力 (pressureMpa)",
        "columnTemperatureCelsius" => "柱温 (columnTemperatureCelsius)",
        "conductivityUsPerCm" => "电导率 (conductivityUsPerCm)",
        "totalConductivityUsPerCm" => "总电导率 (totalConductivityUsPerCm)",
        "flowMlPerMinute" => "流量 (flowMlPerMinute)",
        "less-than" => "小于",
        "less-than-or-equal" => "小于或等于",
        "equal" => "等于",
        "greater-than-or-equal" => "大于或等于",
        "greater-than" => "大于",
        _ => value
    };

    private static string DescribeExecutionMode(WorkflowExecutionMode mode) => mode switch
    {
        WorkflowExecutionMode.Immediate => "即时",
        WorkflowExecutionMode.DurableTimer => "持久计时",
        WorkflowExecutionMode.ManualSignal => "人工信号",
        WorkflowExecutionMode.DeviceCommand => "设备命令",
        WorkflowExecutionMode.ReadOnlyQuery => "只读查询",
        WorkflowExecutionMode.ReadOnlyPolling => "只读轮询",
        _ => mode.ToString()
    };

    private static string DescribeSafety(WorkflowSafetyClassification classification) => classification switch
    {
        WorkflowSafetyClassification.None => "无设备操作",
        WorkflowSafetyClassification.OperatorInteraction => "人工交互",
        WorkflowSafetyClassification.ControlledDeviceAction => "受控设备操作",
        WorkflowSafetyClassification.ReadOnlyDeviceObservation => "只读设备观测",
        WorkflowSafetyClassification.RestrictedDeviceWrite => "受限设备写入",
        _ => classification.ToString()
    };

    private static bool SupportsProfile(WorkflowProfileSupport support, string productId) =>
        support.IsProfileIndependent ||
        support.SupportedProductIds.Contains(productId, StringComparer.OrdinalIgnoreCase);

    private static bool TryGetValue(
        IReadOnlyDictionary<string, string?> values,
        string key,
        out string? value)
    {
        foreach (var pair in values)
        {
            if (!string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
            value = pair.Value;
            return true;
        }

        value = null;
        return false;
    }

    private void RaiseCollectionState()
    {
        OnPropertyChanged(nameof(Fields));
        OnPropertyChanged(nameof(Capabilities));
        OnPropertyChanged(nameof(HasFields));
        OnPropertyChanged(nameof(HasCapabilities));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
