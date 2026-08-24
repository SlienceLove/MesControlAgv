using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Domain.Workflows;

/// <summary>
/// G3-A's deterministic design-time catalog. It contains metadata only and has
/// no dependency on a device gateway, transport, active Profile, or runtime worker.
/// </summary>
public static class BuiltInWorkflowCatalog
{
    public const string CurrentCatalogVersion = "1.1";
    public const string CurrentSchemaVersion = "1.0";
    public const string CurrentProductId = "MES-AGV";
    public const string AdvancedFlowContractOnlyReason =
        "Advanced flow semantics are contract-only in G6-A; runtime orchestration is not enabled.";
    public const string RestrictedInstrumentWriteReason =
        "Instrument write capability is not authorized by the current read-only safety gate.";

    public static WorkflowCatalogSet Create()
    {
        var capabilities = new DeviceCapabilityCatalog(
            CurrentCatalogVersion,
            CreateCapabilityDefinitions());
        var nodeTypes = new WorkflowNodeTypeCatalog(
            CurrentCatalogVersion,
            CreateNodeTypeDefinitions(),
            CreateMigrations(),
            capabilities);
        return new WorkflowCatalogSet(nodeTypes, capabilities);
    }

    private static IReadOnlyList<WorkflowNodeTypeDefinition> CreateNodeTypeDefinitions() =>
    [
        new()
        {
            NodeTypeId = WorkflowGraphNodeTypeIds.Start,
            SchemaVersion = CurrentSchemaVersion,
            DisplayName = "Start",
            Category = "Control",
            Ports = [SuccessOutput()],
            ExecutionMode = WorkflowExecutionMode.Immediate,
            SafetyClassification = WorkflowSafetyClassification.None,
            ProfileSupport = ProfileIndependent()
        },
        new()
        {
            NodeTypeId = WorkflowGraphNodeTypeIds.End,
            SchemaVersion = CurrentSchemaVersion,
            DisplayName = "End",
            Category = "Control",
            Ports = [ControlInput(WorkflowPortCardinality.Many)],
            ExecutionMode = WorkflowExecutionMode.Immediate,
            SafetyClassification = WorkflowSafetyClassification.None,
            ProfileSupport = ProfileIndependent()
        },
        new()
        {
            NodeTypeId = WorkflowGraphNodeTypeIds.Move,
            SchemaVersion = CurrentSchemaVersion,
            DisplayName = "AGV Navigate To Station",
            Category = "Transport",
            ConfigurationSchema = AgvMoveConfiguration(),
            ResultSchema = AgvMoveResult(),
            Ports = [
                ControlInput(),
                SuccessOutput(),
                FailureOutput(),
                TimeoutOutput()
            ],
            ExecutionMode = WorkflowExecutionMode.DeviceCommand,
            SafetyClassification = WorkflowSafetyClassification.ControlledDeviceAction,
            RequiredCapabilityIds = [WorkflowCapabilityIds.AgvNavigateToStation],
            ProfileSupport = CurrentProfile()
        },
        new()
        {
            NodeTypeId = WorkflowGraphNodeTypeIds.TimedWait,
            SchemaVersion = CurrentSchemaVersion,
            DisplayName = "Timed Wait",
            Category = "Wait",
            ConfigurationSchema = new WorkflowObjectSchema
            {
                Fields = [DurationSeconds()]
            },
            ResultSchema = new WorkflowObjectSchema
            {
                Fields =
                [
                    DateTimeField("completedAtUtc", "Completed At", required: true)
                ]
            },
            Ports = [ControlInput(), SuccessOutput()],
            ExecutionMode = WorkflowExecutionMode.DurableTimer,
            SafetyClassification = WorkflowSafetyClassification.None,
            ProfileSupport = ProfileIndependent()
        },
        new()
        {
            NodeTypeId = WorkflowGraphNodeTypeIds.ManualConfirmation,
            SchemaVersion = CurrentSchemaVersion,
            DisplayName = "Manual Confirmation",
            Category = "Manual",
            ConfigurationSchema = new WorkflowObjectSchema
            {
                Fields =
                [
                    StringField(
                        WorkflowNodeConfigurationKeys.Prompt,
                        "Prompt",
                        required: true,
                        defaultValue: "Confirm that the step is complete."),
                    IntegerField(
                        WorkflowNodeConfigurationKeys.TimeoutSeconds,
                        "Confirmation Timeout",
                        required: true,
                        defaultValue: "3600",
                        unit: "s",
                        minimum: 1,
                        maximum: 86400),
                    BooleanField(
                        WorkflowNodeConfigurationKeys.RequireComment,
                        "Require Comment",
                        required: true,
                        defaultValue: "false")
                ]
            },
            ResultSchema = new WorkflowObjectSchema
            {
                Fields =
                [
                    BooleanField("confirmed", "Confirmed", required: true),
                    StringField("confirmedBy", "Confirmed By", required: true),
                    DateTimeField("confirmedAtUtc", "Confirmed At", required: true),
                    StringField("comment", "Comment")
                ]
            },
            Ports =
            [
                ControlInput(),
                SuccessOutput(),
                TimeoutOutput(),
                CancelledOutput()
            ],
            ExecutionMode = WorkflowExecutionMode.ManualSignal,
            SafetyClassification = WorkflowSafetyClassification.OperatorInteraction,
            ProfileSupport = ProfileIndependent()
        },
        new()
        {
            NodeTypeId = WorkflowGraphNodeTypeIds.Condition,
            SchemaVersion = CurrentSchemaVersion,
            DisplayName = "Condition Gateway",
            Category = "Control",
            ResultSchema = new WorkflowObjectSchema
            {
                Fields =
                [
                    StringField("matchedEdgeId", "Matched Edge", required: true),
                    DateTimeField("evaluatedAtUtc", "Evaluated At", required: true)
                ]
            },
            Ports =
            [
                ControlInput(),
                Output("condition", "Condition", WorkflowEdgeKind.ConditionTrue),
                Output("default", "Default", WorkflowEdgeKind.ConditionFalse, WorkflowPortCardinality.Single)
            ],
            ExecutionMode = WorkflowExecutionMode.Immediate,
            SafetyClassification = WorkflowSafetyClassification.None,
            ProfileSupport = ProfileIndependent(),
            Enabled = false,
            UnavailableReason = AdvancedFlowContractOnlyReason
        },
        new()
        {
            NodeTypeId = WorkflowGraphNodeTypeIds.SignalWait,
            SchemaVersion = CurrentSchemaVersion,
            DisplayName = "Wait For External Signal",
            Category = "Wait",
            ConfigurationSchema = new WorkflowObjectSchema
            {
                Fields =
                [
                    StringField(WorkflowNodeConfigurationKeys.SignalName, "Signal Name", required: true),
                    StringField(WorkflowNodeConfigurationKeys.CorrelationKey, "Correlation Input Key", required: true),
                    IntegerField(
                        WorkflowNodeConfigurationKeys.TimeoutSeconds,
                        "Signal Timeout",
                        required: true,
                        defaultValue: "3600",
                        unit: "s",
                        minimum: 1,
                        maximum: 86400)
                ]
            },
            ResultSchema = new WorkflowObjectSchema
            {
                Fields =
                [
                    StringField("signalId", "Signal", required: true),
                    StringField("signalName", "Signal Name", required: true),
                    DateTimeField("receivedAtUtc", "Received At", required: true)
                ]
            },
            Ports = [ControlInput(), SuccessOutput(), TimeoutOutput(), CancelledOutput()],
            ExecutionMode = WorkflowExecutionMode.ManualSignal,
            SafetyClassification = WorkflowSafetyClassification.None,
            ProfileSupport = ProfileIndependent(),
            Enabled = false,
            UnavailableReason = AdvancedFlowContractOnlyReason
        },
        new()
        {
            NodeTypeId = WorkflowGraphNodeTypeIds.ParallelFork,
            SchemaVersion = CurrentSchemaVersion,
            DisplayName = "Parallel Fork",
            Category = "Control",
            ConfigurationSchema = ParallelGatewayConfiguration(),
            Ports = [ControlInput(), Output("branch", "Branch", WorkflowEdgeKind.Parallel)],
            ExecutionMode = WorkflowExecutionMode.Immediate,
            SafetyClassification = WorkflowSafetyClassification.None,
            ProfileSupport = ProfileIndependent(),
            Enabled = false,
            UnavailableReason = AdvancedFlowContractOnlyReason
        },
        new()
        {
            NodeTypeId = WorkflowGraphNodeTypeIds.ParallelJoin,
            SchemaVersion = CurrentSchemaVersion,
            DisplayName = "Parallel Join",
            Category = "Control",
            ConfigurationSchema = ParallelGatewayConfiguration(),
            Ports = [ControlInput(WorkflowPortCardinality.Many), SuccessOutput()],
            ExecutionMode = WorkflowExecutionMode.Immediate,
            SafetyClassification = WorkflowSafetyClassification.None,
            ProfileSupport = ProfileIndependent(),
            Enabled = false,
            UnavailableReason = AdvancedFlowContractOnlyReason
        },
        new()
        {
            NodeTypeId = WorkflowGraphNodeTypeIds.Subflow,
            SchemaVersion = CurrentSchemaVersion,
            DisplayName = "Pinned Subflow",
            Category = "Control",
            ConfigurationSchema = new WorkflowObjectSchema
            {
                Fields =
                [
                    StringField(WorkflowNodeConfigurationKeys.SubflowWorkflowId, "Workflow Id", required: true),
                    IntegerField(
                        WorkflowNodeConfigurationKeys.SubflowVersion,
                        "Workflow Version",
                        required: true,
                        minimum: 1)
                ]
            },
            ResultSchema = new WorkflowObjectSchema
            {
                Fields = [StringField("workflowRunId", "Subflow Run", required: true)]
            },
            Ports = [ControlInput(), SuccessOutput(), FailureOutput(), TimeoutOutput(), CancelledOutput()],
            ExecutionMode = WorkflowExecutionMode.Immediate,
            SafetyClassification = WorkflowSafetyClassification.None,
            ProfileSupport = ProfileIndependent(),
            Enabled = false,
            UnavailableReason = AdvancedFlowContractOnlyReason
        },
        new()
        {
            NodeTypeId = WorkflowGraphNodeTypeIds.CompensationStart,
            SchemaVersion = CurrentSchemaVersion,
            DisplayName = "Compensation Start",
            Category = "Control",
            Ports =
            [
                ControlInput(WorkflowPortCardinality.Many),
                Output("compensation", "Compensation", WorkflowEdgeKind.Compensation, WorkflowPortCardinality.Single)
            ],
            ExecutionMode = WorkflowExecutionMode.Immediate,
            SafetyClassification = WorkflowSafetyClassification.None,
            ProfileSupport = ProfileIndependent(),
            Enabled = false,
            UnavailableReason = AdvancedFlowContractOnlyReason
        },
        new()
        {
            NodeTypeId = WorkflowGraphNodeTypeIds.InstrumentReadStatus,
            SchemaVersion = CurrentSchemaVersion,
            DisplayName = "Read Instrument Status",
            Category = "Instrument",
            ConfigurationSchema = InstrumentReadConfiguration(),
            ResultSchema = InstrumentStatusResult(),
            Ports =
            [
                ControlInput(),
                SuccessOutput(),
                FailureOutput(),
                TimeoutOutput()
            ],
            ExecutionMode = WorkflowExecutionMode.ReadOnlyQuery,
            SafetyClassification = WorkflowSafetyClassification.ReadOnlyDeviceObservation,
            RequiredCapabilityIds = [WorkflowCapabilityIds.InstrumentReadStatus],
            ProfileSupport = CurrentProfile()
        },
        new()
        {
            NodeTypeId = WorkflowGraphNodeTypeIds.InstrumentWaitUntilStable,
            SchemaVersion = CurrentSchemaVersion,
            DisplayName = "Wait Until Instrument Is Stable",
            Category = "Instrument",
            ConfigurationSchema = InstrumentWaitConfiguration(),
            ResultSchema = InstrumentWaitResult(),
            Ports =
            [
                ControlInput(),
                SuccessOutput(),
                FailureOutput(),
                TimeoutOutput()
            ],
            ExecutionMode = WorkflowExecutionMode.ReadOnlyPolling,
            SafetyClassification = WorkflowSafetyClassification.ReadOnlyDeviceObservation,
            RequiredCapabilityIds = [WorkflowCapabilityIds.InstrumentWaitUntilStable],
            ProfileSupport = CurrentProfile()
        }
    ];

    private static IReadOnlyList<DeviceCapabilityDefinition> CreateCapabilityDefinitions()
    {
        var instrumentProfile = CurrentProfile();
        var restricted = new[]
        {
            (WorkflowCapabilityIds.InstrumentSetFlow, "Set Instrument Flow"),
            (WorkflowCapabilityIds.InstrumentEnablePump, "Enable Instrument Pump"),
            (WorkflowCapabilityIds.InstrumentSetTemperature, "Set Instrument Temperature"),
            (WorkflowCapabilityIds.InstrumentLoadMethod, "Load Instrument Method"),
            (WorkflowCapabilityIds.InstrumentInject, "Inject Instrument Sample"),
            (WorkflowCapabilityIds.InstrumentStartAnalysis, "Start Instrument Analysis"),
            (WorkflowCapabilityIds.InstrumentStopAnalysis, "Stop Instrument Analysis")
        };

        return
        [
            new DeviceCapabilityDefinition
            {
                CapabilityId = WorkflowCapabilityIds.AgvNavigateToStation,
                SchemaVersion = CurrentSchemaVersion,
                DisplayName = "AGV Navigate To Station",
                DeviceFamily = WorkflowDeviceFamilyIds.Agv,
                ConfigurationSchema = AgvMoveConfiguration(),
                ResultSchema = AgvMoveResult(),
                ExecutionMode = WorkflowExecutionMode.DeviceCommand,
                SafetyClassification = WorkflowSafetyClassification.ControlledDeviceAction,
                ProfileSupport = CurrentProfile(),
                Enabled = true,
                ControlEnabled = true
            },
            new DeviceCapabilityDefinition
            {
                CapabilityId = WorkflowCapabilityIds.InstrumentIdentify,
                SchemaVersion = CurrentSchemaVersion,
                DisplayName = "Identify Instrument",
                DeviceFamily = WorkflowDeviceFamilyIds.IonChromatography,
                ConfigurationSchema = InstrumentIdentityConfiguration(),
                ResultSchema = InstrumentIdentityResult(),
                ExecutionMode = WorkflowExecutionMode.ReadOnlyQuery,
                SafetyClassification = WorkflowSafetyClassification.ReadOnlyDeviceObservation,
                ProfileSupport = instrumentProfile,
                Enabled = true,
                ControlEnabled = false
            },
            new DeviceCapabilityDefinition
            {
                CapabilityId = WorkflowCapabilityIds.InstrumentReadStatus,
                SchemaVersion = CurrentSchemaVersion,
                DisplayName = "Read Instrument Status",
                DeviceFamily = WorkflowDeviceFamilyIds.IonChromatography,
                ConfigurationSchema = InstrumentReadConfiguration(),
                ResultSchema = InstrumentStatusResult(),
                ExecutionMode = WorkflowExecutionMode.ReadOnlyQuery,
                SafetyClassification = WorkflowSafetyClassification.ReadOnlyDeviceObservation,
                ProfileSupport = instrumentProfile,
                Enabled = true,
                ControlEnabled = false
            },
            new DeviceCapabilityDefinition
            {
                CapabilityId = WorkflowCapabilityIds.InstrumentWaitUntilStable,
                SchemaVersion = CurrentSchemaVersion,
                DisplayName = "Wait Until Instrument Is Stable",
                DeviceFamily = WorkflowDeviceFamilyIds.IonChromatography,
                ConfigurationSchema = InstrumentWaitConfiguration(),
                ResultSchema = InstrumentWaitResult(),
                ExecutionMode = WorkflowExecutionMode.ReadOnlyPolling,
                SafetyClassification = WorkflowSafetyClassification.ReadOnlyDeviceObservation,
                ProfileSupport = instrumentProfile,
                Enabled = true,
                ControlEnabled = false
            },
            .. restricted.Select(capability => new DeviceCapabilityDefinition
            {
                CapabilityId = capability.Item1,
                SchemaVersion = CurrentSchemaVersion,
                DisplayName = capability.Item2,
                DeviceFamily = WorkflowDeviceFamilyIds.IonChromatography,
                ExecutionMode = WorkflowExecutionMode.DeviceCommand,
                SafetyClassification = WorkflowSafetyClassification.RestrictedDeviceWrite,
                ProfileSupport = instrumentProfile,
                Enabled = false,
                ControlEnabled = false,
                UnavailableReason = RestrictedInstrumentWriteReason
            })
        ];
    }

    private static IReadOnlyList<WorkflowNodeMigrationDefinition> CreateMigrations() =>
    [
        new()
        {
            SourceNodeTypeId = WorkflowGraphNodeTypeIds.Wait,
            SourceSchemaVersion = CurrentSchemaVersion,
            TargetNodeTypeId = WorkflowGraphNodeTypeIds.TimedWait,
            TargetSchemaVersion = CurrentSchemaVersion,
            RequiredParameterNames = [WorkflowRuntimeParameterNames.WaitDurationSeconds],
            RequiresExplicitConfirmation = true
        },
        new()
        {
            SourceNodeTypeId = WorkflowGraphNodeTypeIds.InstrumentOperation,
            SourceSchemaVersion = CurrentSchemaVersion,
            TargetNodeTypeId = WorkflowGraphNodeTypeIds.InstrumentReadStatus,
            TargetSchemaVersion = CurrentSchemaVersion,
            RequiredParameterNames = [WorkflowRuntimeParameterNames.InstrumentId],
            RequiredParameterValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [WorkflowRuntimeParameterNames.InstrumentOperation] = "read-status"
            },
            RequiresExplicitConfirmation = true
        }
    ];

    private static WorkflowObjectSchema AgvMoveConfiguration() => new()
    {
        Fields =
        [
            StringField(
                WorkflowNodeConfigurationKeys.TargetStation,
                "Target Station",
                required: true,
                referenceKind: WorkflowSchemaReferenceKind.Station,
                legacyBindings:
                [
                    new WorkflowLegacyFieldBinding
                    {
                        Source = WorkflowLegacyFieldSource.NodeProperty,
                        Key = "targetStation"
                    }
                ]),
            StringField(
                WorkflowNodeConfigurationKeys.DeviceId,
                "AGV",
                referenceKind: WorkflowSchemaReferenceKind.Device,
                deviceFamily: WorkflowDeviceFamilyIds.Agv),
            IntegerField(
                WorkflowNodeConfigurationKeys.TimeoutSeconds,
                "Navigation Timeout",
                required: true,
                defaultValue: "300",
                unit: "s",
                minimum: 1,
                maximum: 86400),
            IntegerField(
                WorkflowNodeConfigurationKeys.RetryCount,
                "Retry Count",
                required: true,
                defaultValue: "0",
                minimum: 0,
                maximum: 3)
        ]
    };

    private static WorkflowObjectSchema AgvMoveResult() => new()
    {
        Fields =
        [
            StringField("deviceId", "AGV", required: true),
            StringField("stationId", "Station", required: true),
            StringField("deviceTaskId", "Device Task", required: true),
            DateTimeField("arrivedAtUtc", "Arrived At", required: true)
        ]
    };

    private static WorkflowObjectSchema InstrumentIdentityConfiguration() => new()
    {
        Fields = [InstrumentIdField()]
    };

    private static WorkflowObjectSchema ParallelGatewayConfiguration() => new()
    {
        Fields =
        [
            StringField(
                WorkflowNodeConfigurationKeys.ParallelGatewayKey,
                "Gateway Pair Key",
                required: true)
        ]
    };

    private static WorkflowObjectSchema InstrumentReadConfiguration() => new()
    {
        Fields =
        [
            InstrumentIdField(),
            IntegerField(
                WorkflowNodeConfigurationKeys.TimeoutSeconds,
                "Read Timeout",
                required: true,
                defaultValue: "30",
                unit: "s",
                minimum: 1,
                maximum: 3600),
            IntegerField(
                WorkflowNodeConfigurationKeys.RetryCount,
                "Read Retry Count",
                required: true,
                defaultValue: "2",
                minimum: 0,
                maximum: 10)
        ]
    };

    private static WorkflowObjectSchema InstrumentWaitConfiguration() => new()
    {
        Fields =
        [
            InstrumentIdField(),
            StringField(
                WorkflowNodeConfigurationKeys.Measurement,
                "Measurement",
                required: true,
                allowedValues:
                [
                    "pressureMpa",
                    "columnTemperatureCelsius",
                    "conductivityUsPerCm",
                    "totalConductivityUsPerCm",
                    "flowMlPerMinute"
                ]),
            StringField(
                WorkflowNodeConfigurationKeys.ComparisonOperator,
                "Comparison",
                required: true,
                defaultValue: "less-than-or-equal",
                allowedValues:
                [
                    "less-than",
                    "less-than-or-equal",
                    "equal",
                    "greater-than-or-equal",
                    "greater-than"
                ]),
            DecimalField(
                WorkflowNodeConfigurationKeys.Threshold,
                "Threshold",
                required: true),
            DecimalField(
                WorkflowNodeConfigurationKeys.SampleIntervalSeconds,
                "Sample Interval",
                required: true,
                defaultValue: "2",
                unit: "s",
                minimum: 0.1m,
                maximum: 3600),
            DecimalField(
                WorkflowNodeConfigurationKeys.StableDurationSeconds,
                "Stable Duration",
                required: true,
                defaultValue: "30",
                unit: "s",
                minimum: 0,
                maximum: 86400),
            DecimalField(
                WorkflowNodeConfigurationKeys.TimeoutSeconds,
                "Maximum Wait",
                required: true,
                defaultValue: "600",
                unit: "s",
                minimum: 1,
                maximum: 86400),
            DecimalField(
                WorkflowNodeConfigurationKeys.StaleAfterSeconds,
                "Data Stale After",
                required: true,
                defaultValue: "5",
                unit: "s",
                minimum: 0.1m,
                maximum: 3600)
        ]
    };

    private static WorkflowObjectSchema InstrumentIdentityResult() => new()
    {
        Fields =
        [
            StringField("instrumentId", "Instrument", required: true),
            StringField("model", "Model", required: true),
            StringField("serialNumber", "Serial Number", required: true),
            DateTimeField("observedAtUtc", "Observed At", required: true)
        ]
    };

    private static WorkflowObjectSchema InstrumentStatusResult() => new()
    {
        Fields =
        [
            StringField("instrumentId", "Instrument", required: true),
            StringField("model", "Model", required: true),
            StringField("serialNumber", "Serial Number", required: true),
            BooleanField("online", "Online", required: true),
            StringField("deviceState", "Device State", required: true),
            DateTimeField("observedAtUtc", "Observed At", required: true),
            DecimalField("pressureMpa", "Pressure", unit: "MPa"),
            DecimalField("columnTemperatureCelsius", "Column Temperature", unit: "C"),
            DecimalField("conductivityUsPerCm", "Conductivity", unit: "uS/cm"),
            DecimalField("totalConductivityUsPerCm", "Total Conductivity", unit: "uS/cm"),
            DecimalField("flowMlPerMinute", "Flow", unit: "mL/min"),
            StringField("mappingConfidence", "Mapping Confidence")
        ]
    };

    private static WorkflowObjectSchema InstrumentWaitResult() => new()
    {
        Fields =
        [
            StringField("instrumentId", "Instrument", required: true),
            StringField("measurement", "Measurement", required: true),
            DecimalField("observedValue", "Observed Value", required: true),
            DateTimeField("observedAtUtc", "Observed At", required: true),
            DateTimeField("satisfiedAtUtc", "Satisfied At", required: true)
        ]
    };

    private static WorkflowFieldSchema InstrumentIdField() => StringField(
        WorkflowNodeConfigurationKeys.InstrumentId,
        "Instrument",
        required: true,
        referenceKind: WorkflowSchemaReferenceKind.Device,
        deviceFamily: WorkflowDeviceFamilyIds.IonChromatography,
        legacyBindings:
        [
            new WorkflowLegacyFieldBinding
            {
                Source = WorkflowLegacyFieldSource.Parameter,
                Key = WorkflowRuntimeParameterNames.InstrumentId
            }
        ]);

    private static WorkflowFieldSchema DurationSeconds() => DecimalField(
        WorkflowRuntimeParameterNames.WaitDurationSeconds,
        "Duration",
        required: true,
        defaultValue: "1",
        unit: "s",
        minimum: 0,
        maximum: 86400,
        legacyBindings:
        [
            new WorkflowLegacyFieldBinding
            {
                Source = WorkflowLegacyFieldSource.Parameter,
                Key = WorkflowRuntimeParameterNames.WaitDurationSeconds
            }
        ]);

    private static WorkflowFieldSchema StringField(
        string key,
        string displayName,
        bool required = false,
        string? defaultValue = null,
        WorkflowSchemaReferenceKind referenceKind = WorkflowSchemaReferenceKind.None,
        string? deviceFamily = null,
        IReadOnlyList<string>? allowedValues = null,
        IReadOnlyList<WorkflowLegacyFieldBinding>? legacyBindings = null) => new()
    {
        Key = key,
        DisplayName = displayName,
        ValueType = WorkflowSchemaValueType.String,
        ReferenceKind = referenceKind,
        DeviceFamily = deviceFamily,
        IsRequired = required,
        DefaultValue = defaultValue,
        AllowedValues = allowedValues ?? [],
        LegacyBindings = legacyBindings ?? []
    };

    private static WorkflowFieldSchema IntegerField(
        string key,
        string displayName,
        bool required = false,
        string? defaultValue = null,
        string? unit = null,
        decimal? minimum = null,
        decimal? maximum = null) => new()
    {
        Key = key,
        DisplayName = displayName,
        ValueType = WorkflowSchemaValueType.Integer,
        IsRequired = required,
        DefaultValue = defaultValue,
        Unit = unit,
        Minimum = minimum,
        Maximum = maximum
    };

    private static WorkflowFieldSchema DecimalField(
        string key,
        string displayName,
        bool required = false,
        string? defaultValue = null,
        string? unit = null,
        decimal? minimum = null,
        decimal? maximum = null,
        IReadOnlyList<WorkflowLegacyFieldBinding>? legacyBindings = null) => new()
    {
        Key = key,
        DisplayName = displayName,
        ValueType = WorkflowSchemaValueType.Decimal,
        IsRequired = required,
        DefaultValue = defaultValue,
        Unit = unit,
        Minimum = minimum,
        Maximum = maximum,
        LegacyBindings = legacyBindings ?? []
    };

    private static WorkflowFieldSchema BooleanField(
        string key,
        string displayName,
        bool required = false,
        string? defaultValue = null) => new()
    {
        Key = key,
        DisplayName = displayName,
        ValueType = WorkflowSchemaValueType.Boolean,
        IsRequired = required,
        DefaultValue = defaultValue
    };

    private static WorkflowFieldSchema DateTimeField(
        string key,
        string displayName,
        bool required = false) => new()
    {
        Key = key,
        DisplayName = displayName,
        ValueType = WorkflowSchemaValueType.DateTimeOffset,
        IsRequired = required
    };

    private static WorkflowPortDefinition ControlInput(
        WorkflowPortCardinality cardinality = WorkflowPortCardinality.Single) => new()
    {
        Key = "in",
        DisplayName = "Input",
        Direction = WorkflowPortDirection.Input,
        DataType = "control",
        Cardinality = cardinality
    };

    private static WorkflowPortDefinition SuccessOutput() => Output(
        "success",
        "Success",
        WorkflowEdgeKind.Success);

    private static WorkflowPortDefinition FailureOutput() => Output(
        "failure",
        "Failure",
        WorkflowEdgeKind.Failure);

    private static WorkflowPortDefinition TimeoutOutput() => Output(
        "timeout",
        "Timeout",
        WorkflowEdgeKind.Timeout);

    private static WorkflowPortDefinition CancelledOutput() => Output(
        "cancelled",
        "Cancelled",
        WorkflowEdgeKind.Cancelled);

    private static WorkflowPortDefinition Output(
        string key,
        string displayName,
        WorkflowEdgeKind kind,
        WorkflowPortCardinality cardinality = WorkflowPortCardinality.Many) => new()
    {
        Key = key,
        DisplayName = displayName,
        Direction = WorkflowPortDirection.Output,
        DataType = "control",
        Cardinality = cardinality,
        EdgeKind = kind
    };

    private static WorkflowProfileSupport ProfileIndependent() => new()
    {
        IsProfileIndependent = true
    };

    private static WorkflowProfileSupport CurrentProfile() => new()
    {
        SupportedProductIds = [CurrentProductId]
    };
}
