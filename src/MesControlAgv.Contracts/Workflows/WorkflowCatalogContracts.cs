namespace MesControlAgv.Contracts.Workflows;

/// <summary>Stable device capability identifiers used by workflow definitions.</summary>
public static class WorkflowCapabilityIds
{
    public const string AgvNavigateToStation = "agv.navigate-to-station";
    public const string InstrumentIdentify = "instrument.identify";
    public const string InstrumentReadStatus = "instrument.read-status";
    public const string InstrumentWaitUntilStable = "instrument.wait-until-stable";
    public const string InstrumentSetFlow = "instrument.set-flow";
    public const string InstrumentEnablePump = "instrument.enable-pump";
    public const string InstrumentSetTemperature = "instrument.set-temperature";
    public const string InstrumentLoadMethod = "instrument.load-method";
    public const string InstrumentInject = "instrument.inject";
    public const string InstrumentStartAnalysis = "instrument.start-analysis";
    public const string InstrumentStopAnalysis = "instrument.stop-analysis";
}

public static class WorkflowDeviceFamilyIds
{
    public const string Agv = "agv";
    public const string IonChromatography = "ion-chromatography";
}

/// <summary>Configuration keys stored directly in WorkflowNodeDefinition.Configuration.</summary>
public static class WorkflowNodeConfigurationKeys
{
    public const string TargetStation = "$targetStation";
    public const string DeviceId = "deviceId";
    public const string InstrumentId = "instrumentId";
    public const string TimeoutSeconds = "timeoutSeconds";
    public const string RetryCount = "retryCount";
    public const string Prompt = "prompt";
    public const string RequireComment = "requireComment";
    public const string Measurement = "measurement";
    public const string ComparisonOperator = "operator";
    public const string Threshold = "threshold";
    public const string SampleIntervalSeconds = "sampleIntervalSeconds";
    public const string StableDurationSeconds = "stableDurationSeconds";
    public const string StaleAfterSeconds = "staleAfterSeconds";
}

public enum WorkflowSchemaValueType
{
    String,
    Integer,
    Decimal,
    Boolean,
    DateTimeOffset
}

public enum WorkflowSchemaReferenceKind
{
    None,
    Station,
    Device,
    Capability
}

public enum WorkflowExecutionMode
{
    Immediate,
    DurableTimer,
    ManualSignal,
    DeviceCommand,
    ReadOnlyQuery,
    ReadOnlyPolling
}

public enum WorkflowSafetyClassification
{
    None,
    OperatorInteraction,
    ControlledDeviceAction,
    ReadOnlyDeviceObservation,
    RestrictedDeviceWrite
}

public enum WorkflowLegacyFieldSource
{
    NodeProperty,
    Parameter,
    Configuration
}

/// <summary>
/// Describes where an explicitly invoked migration may obtain a legacy value.
/// Merely loading a graph never applies this binding.
/// </summary>
public sealed record WorkflowLegacyFieldBinding
{
    public WorkflowLegacyFieldSource Source { get; init; }
    public string Key { get; init; } = string.Empty;
}

/// <summary>
/// A scalar field stored as an invariant string in node Configuration. DefaultValue
/// is used only for new nodes or an explicit migration; deserialization never injects it.
/// </summary>
public sealed record WorkflowFieldSchema
{
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public WorkflowSchemaValueType ValueType { get; init; } = WorkflowSchemaValueType.String;
    public WorkflowSchemaReferenceKind ReferenceKind { get; init; }
    public string? DeviceFamily { get; init; }
    public bool IsRequired { get; init; }
    public string? DefaultValue { get; init; }
    public string? Unit { get; init; }
    public decimal? Minimum { get; init; }
    public decimal? Maximum { get; init; }
    public IReadOnlyList<string> AllowedValues { get; init; } = Array.Empty<string>();
    public IReadOnlyList<WorkflowLegacyFieldBinding> LegacyBindings { get; init; } =
        Array.Empty<WorkflowLegacyFieldBinding>();
}

/// <summary>
/// Schema for a flat string dictionary. Unknown fields remain in the graph for
/// lossless round trips but are not thereby valid for publishing.
/// </summary>
public sealed record WorkflowObjectSchema
{
    public IReadOnlyList<WorkflowFieldSchema> Fields { get; init; } = Array.Empty<WorkflowFieldSchema>();
    public bool PreserveUnknownFields { get; init; } = true;
}

public sealed record WorkflowProfileSupport
{
    public bool IsProfileIndependent { get; init; }
    public IReadOnlyList<string> SupportedProductIds { get; init; } = Array.Empty<string>();
}

public sealed record WorkflowNodeTypeDefinition
{
    public string NodeTypeId { get; init; } = string.Empty;
    public string SchemaVersion { get; init; } = "1.0";
    public string DisplayName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public WorkflowObjectSchema ConfigurationSchema { get; init; } = new();
    public WorkflowObjectSchema ResultSchema { get; init; } = new();
    public IReadOnlyList<WorkflowPortDefinition> Ports { get; init; } = Array.Empty<WorkflowPortDefinition>();
    public WorkflowExecutionMode ExecutionMode { get; init; }
    public WorkflowSafetyClassification SafetyClassification { get; init; }
    public IReadOnlyList<string> RequiredCapabilityIds { get; init; } = Array.Empty<string>();
    public WorkflowProfileSupport ProfileSupport { get; init; } = new();
    public bool Enabled { get; init; } = true;
}

public sealed record DeviceCapabilityDefinition
{
    public string CapabilityId { get; init; } = string.Empty;
    public string SchemaVersion { get; init; } = "1.0";
    public string DisplayName { get; init; } = string.Empty;
    public string DeviceFamily { get; init; } = string.Empty;
    public WorkflowObjectSchema ConfigurationSchema { get; init; } = new();
    public WorkflowObjectSchema ResultSchema { get; init; } = new();
    public WorkflowExecutionMode ExecutionMode { get; init; }
    public WorkflowSafetyClassification SafetyClassification { get; init; }
    public WorkflowProfileSupport ProfileSupport { get; init; } = new();
    public bool Enabled { get; init; }
    public bool ControlEnabled { get; init; }
    public string? UnavailableReason { get; init; }
}

/// <summary>
/// Metadata for a user-confirmed migration. The catalog never mutates a graph
/// merely because a matching rule exists.
/// </summary>
public sealed record WorkflowNodeMigrationDefinition
{
    public string SourceNodeTypeId { get; init; } = string.Empty;
    public string SourceSchemaVersion { get; init; } = "1.0";
    public string TargetNodeTypeId { get; init; } = string.Empty;
    public string TargetSchemaVersion { get; init; } = "1.0";
    public IReadOnlyList<string> RequiredParameterNames { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string?> RequiredParameterValues { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    public bool RequiresExplicitConfirmation { get; init; } = true;
}
