using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Workflows;

namespace MesControlAgv.WorkflowContract.Tests;

public sealed class WorkflowCatalogTests
{
    [Fact]
    public void Built_in_catalog_declares_G3_nodes_and_G6_contract_only_nodes()
    {
        var catalog = BuiltInWorkflowCatalog.Create();

        Assert.Equal(BuiltInWorkflowCatalog.CurrentCatalogVersion, catalog.NodeTypes.CatalogVersion);
        Assert.Equal(BuiltInWorkflowCatalog.CurrentCatalogVersion, catalog.Capabilities.CatalogVersion);
        Assert.Equal(
        [
            WorkflowGraphNodeTypeIds.Move,
            WorkflowGraphNodeTypeIds.CompensationStart,
            WorkflowGraphNodeTypeIds.Condition,
            WorkflowGraphNodeTypeIds.End,
            WorkflowGraphNodeTypeIds.ManualConfirmation,
            WorkflowGraphNodeTypeIds.ParallelFork,
            WorkflowGraphNodeTypeIds.ParallelJoin,
            WorkflowGraphNodeTypeIds.SignalWait,
            WorkflowGraphNodeTypeIds.Start,
            WorkflowGraphNodeTypeIds.Subflow,
            WorkflowGraphNodeTypeIds.TimedWait,
            WorkflowGraphNodeTypeIds.InstrumentReadStatus,
            WorkflowGraphNodeTypeIds.InstrumentWaitUntilStable
        ],
            catalog.NodeTypes.Definitions.Select(definition => definition.NodeTypeId).ToArray());
        Assert.All(
            catalog.NodeTypes.Definitions,
            definition => Assert.Equal(BuiltInWorkflowCatalog.CurrentSchemaVersion, definition.SchemaVersion));
        Assert.Equal(2, WorkflowGraphDocument.ExplicitEdgesSchemaVersion);
        Assert.Equal(3, WorkflowGraphDocument.CurrentSchemaVersion);
        Assert.True(WorkflowGraphDocument.IsPublishableSchemaVersion(2));
        Assert.True(WorkflowGraphDocument.IsPublishableSchemaVersion(3));
        Assert.False(WorkflowGraphDocument.IsPublishableSchemaVersion(1));
    }

    [Fact]
    public void G6B_enables_condition_and_signal_while_later_advanced_nodes_remain_blocked()
    {
        var catalog = BuiltInWorkflowCatalog.Create().NodeTypes;
        var enabledIds = new[]
        {
            WorkflowGraphNodeTypeIds.Condition,
            WorkflowGraphNodeTypeIds.SignalWait
        };
        var blockedIds = new[]
        {
            WorkflowGraphNodeTypeIds.ParallelFork,
            WorkflowGraphNodeTypeIds.ParallelJoin,
            WorkflowGraphNodeTypeIds.Subflow,
            WorkflowGraphNodeTypeIds.CompensationStart
        };

        foreach (var nodeTypeId in enabledIds)
        {
            var definition = GetNode(catalog, nodeTypeId);
            Assert.True(definition.Enabled);
            Assert.Null(definition.UnavailableReason);
            Assert.Empty(definition.RequiredCapabilityIds);
            Assert.Equal(
                WorkflowCatalogPublishDisposition.EligibleForValidation,
                catalog.Resolve(nodeTypeId, definition.SchemaVersion).PublishDisposition);
        }

        foreach (var nodeTypeId in blockedIds)
        {
            var definition = GetNode(catalog, nodeTypeId);
            Assert.False(definition.Enabled);
            Assert.Equal(BuiltInWorkflowCatalog.AdvancedFlowContractOnlyReason, definition.UnavailableReason);
            Assert.Empty(definition.RequiredCapabilityIds);
            Assert.Equal(
                WorkflowCatalogPublishDisposition.BlockedByCatalogCompatibility,
                catalog.Resolve(nodeTypeId, definition.SchemaVersion).PublishDisposition);
        }

        var condition = GetNode(catalog, WorkflowGraphNodeTypeIds.Condition);
        AssertPort(condition, "condition", WorkflowPortDirection.Output, WorkflowEdgeKind.ConditionTrue);
        AssertPort(condition, "default", WorkflowPortDirection.Output, WorkflowEdgeKind.ConditionFalse);

        var fork = GetNode(catalog, WorkflowGraphNodeTypeIds.ParallelFork);
        AssertField(fork, WorkflowNodeConfigurationKeys.ParallelGatewayKey, required: true, defaultValue: null);
        AssertPort(fork, "branch", WorkflowPortDirection.Output, WorkflowEdgeKind.Parallel);
        Assert.Equal(
            WorkflowPortCardinality.Many,
            GetNode(catalog, WorkflowGraphNodeTypeIds.ParallelJoin).Ports.Single(port => port.Key == "in").Cardinality);

        var subflow = GetNode(catalog, WorkflowGraphNodeTypeIds.Subflow);
        AssertField(subflow, WorkflowNodeConfigurationKeys.SubflowWorkflowId, required: true, defaultValue: null);
        AssertField(subflow, WorkflowNodeConfigurationKeys.SubflowVersion, required: true, defaultValue: null);

        var signal = GetNode(catalog, WorkflowGraphNodeTypeIds.SignalWait);
        AssertField(signal, WorkflowNodeConfigurationKeys.SignalName, required: true, defaultValue: null);
        AssertField(signal, WorkflowNodeConfigurationKeys.CorrelationKey, required: true, defaultValue: null);

        var compensation = GetNode(catalog, WorkflowGraphNodeTypeIds.CompensationStart);
        AssertPort(compensation, "compensation", WorkflowPortDirection.Output, WorkflowEdgeKind.Compensation);
    }

    [Fact]
    public void Node_schemas_define_stable_fields_defaults_and_outcome_ports()
    {
        var catalog = BuiltInWorkflowCatalog.Create().NodeTypes;

        var move = GetNode(catalog, WorkflowGraphNodeTypeIds.Move);
        AssertField(move, WorkflowNodeConfigurationKeys.TargetStation, required: true, defaultValue: null);
        AssertField(move, WorkflowNodeConfigurationKeys.TimeoutSeconds, required: true, defaultValue: "300");
        AssertField(move, WorkflowNodeConfigurationKeys.RetryCount, required: true, defaultValue: "0");
        AssertPort(move, "success", WorkflowPortDirection.Output, WorkflowEdgeKind.Success);
        AssertPort(move, "failure", WorkflowPortDirection.Output, WorkflowEdgeKind.Failure);
        AssertPort(move, "timeout", WorkflowPortDirection.Output, WorkflowEdgeKind.Timeout);
        Assert.Equal([WorkflowCapabilityIds.AgvNavigateToStation], move.RequiredCapabilityIds);

        var wait = GetNode(catalog, WorkflowGraphNodeTypeIds.TimedWait);
        var duration = AssertField(
            wait,
            WorkflowRuntimeParameterNames.WaitDurationSeconds,
            required: true,
            defaultValue: "1");
        Assert.Equal(0, duration.Minimum);
        Assert.Equal(86400, duration.Maximum);
        Assert.Single(duration.LegacyBindings);

        var manual = GetNode(catalog, WorkflowGraphNodeTypeIds.ManualConfirmation);
        AssertField(manual, WorkflowNodeConfigurationKeys.Prompt, required: true,
            defaultValue: "Confirm that the step is complete.");
        AssertField(manual, WorkflowNodeConfigurationKeys.TimeoutSeconds, required: true, defaultValue: "3600");
        AssertField(manual, WorkflowNodeConfigurationKeys.RequireComment, required: true, defaultValue: "false");
        AssertPort(manual, "cancelled", WorkflowPortDirection.Output, WorkflowEdgeKind.Cancelled);

        var read = GetNode(catalog, WorkflowGraphNodeTypeIds.InstrumentReadStatus);
        AssertField(read, WorkflowNodeConfigurationKeys.InstrumentId, required: true, defaultValue: null);
        AssertField(read, WorkflowNodeConfigurationKeys.TimeoutSeconds, required: true, defaultValue: "30");
        AssertField(read, WorkflowNodeConfigurationKeys.RetryCount, required: true, defaultValue: "2");
        Assert.Equal([WorkflowCapabilityIds.InstrumentReadStatus], read.RequiredCapabilityIds);

        var stable = GetNode(catalog, WorkflowGraphNodeTypeIds.InstrumentWaitUntilStable);
        AssertField(stable, WorkflowNodeConfigurationKeys.Measurement, required: true, defaultValue: null);
        AssertField(stable, WorkflowNodeConfigurationKeys.SampleIntervalSeconds, required: true, defaultValue: "2");
        AssertField(stable, WorkflowNodeConfigurationKeys.StableDurationSeconds, required: true, defaultValue: "30");
        AssertField(stable, WorkflowNodeConfigurationKeys.TimeoutSeconds, required: true, defaultValue: "600");
        AssertField(stable, WorkflowNodeConfigurationKeys.StaleAfterSeconds, required: true, defaultValue: "5");
        Assert.Equal([WorkflowCapabilityIds.InstrumentWaitUntilStable], stable.RequiredCapabilityIds);

        Assert.Empty(GetNode(catalog, WorkflowGraphNodeTypeIds.Start).ConfigurationSchema.Fields);
        Assert.Empty(GetNode(catalog, WorkflowGraphNodeTypeIds.End).ConfigurationSchema.Fields);
    }

    [Fact]
    public void Instrument_catalog_exposes_only_normalized_read_only_capabilities()
    {
        var capabilities = BuiltInWorkflowCatalog.Create().Capabilities.Definitions
            .Where(definition => definition.DeviceFamily == WorkflowDeviceFamilyIds.IonChromatography)
            .ToArray();

        Assert.Equal(
        [
            WorkflowCapabilityIds.InstrumentIdentify,
            WorkflowCapabilityIds.InstrumentReadStatus,
            WorkflowCapabilityIds.InstrumentWaitUntilStable
        ],
            capabilities.Where(definition => definition.Enabled)
                .Select(definition => definition.CapabilityId)
                .ToArray());
        Assert.All(capabilities, definition => Assert.False(definition.ControlEnabled));
        Assert.All(
            capabilities.Where(definition => definition.Enabled),
            definition => Assert.Equal(
                WorkflowSafetyClassification.ReadOnlyDeviceObservation,
                definition.SafetyClassification));

        var restrictedIds = new[]
        {
            WorkflowCapabilityIds.InstrumentSetFlow,
            WorkflowCapabilityIds.InstrumentEnablePump,
            WorkflowCapabilityIds.InstrumentSetTemperature,
            WorkflowCapabilityIds.InstrumentLoadMethod,
            WorkflowCapabilityIds.InstrumentInject,
            WorkflowCapabilityIds.InstrumentStartAnalysis,
            WorkflowCapabilityIds.InstrumentStopAnalysis
        };
        Assert.Equal(
            restrictedIds.OrderBy(id => id, StringComparer.Ordinal),
            capabilities.Where(definition => !definition.Enabled)
                .Select(definition => definition.CapabilityId));
        Assert.All(capabilities.Where(definition => !definition.Enabled), definition =>
        {
            Assert.Equal(WorkflowSafetyClassification.RestrictedDeviceWrite, definition.SafetyClassification);
            Assert.Equal(BuiltInWorkflowCatalog.RestrictedInstrumentWriteReason, definition.UnavailableReason);
        });

        var schemaKeys = capabilities
            .SelectMany(definition => definition.ConfigurationSchema.Fields.Concat(definition.ResultSchema.Fields))
            .Select(field => field.Key)
            .ToArray();
        Assert.DoesNotContain(schemaKeys, ContainsProtocolDetail);
    }

    [Fact]
    public void Catalog_resolution_is_exact_and_blocks_unknown_or_future_schemas()
    {
        var catalog = BuiltInWorkflowCatalog.Create();

        var supported = catalog.NodeTypes.Resolve(
            WorkflowGraphNodeTypeIds.Start,
            BuiltInWorkflowCatalog.CurrentSchemaVersion);
        Assert.Equal(WorkflowCatalogResolutionStatus.Supported, supported.Status);
        Assert.Equal(WorkflowCatalogEditMode.Typed, supported.EditMode);
        Assert.True(supported.PreserveOnSave);
        Assert.Equal(WorkflowCatalogPublishDisposition.EligibleForValidation, supported.PublishDisposition);

        var unknown = catalog.NodeTypes.Resolve("vendor.future-node", "1.0");
        Assert.Equal(WorkflowCatalogResolutionStatus.UnknownType, unknown.Status);
        Assert.Equal(WorkflowCatalogEditMode.PreserveReadOnly, unknown.EditMode);
        Assert.True(unknown.PreserveOnSave);
        Assert.Equal(WorkflowCatalogPublishDisposition.BlockedByCatalogCompatibility, unknown.PublishDisposition);

        var future = catalog.NodeTypes.Resolve(WorkflowGraphNodeTypeIds.Start, "2.0");
        Assert.Equal(WorkflowCatalogResolutionStatus.FutureSchemaVersion, future.Status);
        Assert.Equal(WorkflowCatalogEditMode.PreserveReadOnly, future.EditMode);
        Assert.Equal(WorkflowCatalogPublishDisposition.BlockedByCatalogCompatibility, future.PublishDisposition);

        var malformed = catalog.NodeTypes.Resolve(WorkflowGraphNodeTypeIds.Start, "future");
        Assert.Equal(WorkflowCatalogResolutionStatus.UnsupportedSchemaVersion, malformed.Status);

        var disabled = catalog.Capabilities.Resolve(
            WorkflowCapabilityIds.InstrumentStartAnalysis,
            BuiltInWorkflowCatalog.CurrentSchemaVersion);
        Assert.Equal(WorkflowCatalogResolutionStatus.Supported, disabled.Status);
        Assert.Equal(WorkflowCatalogPublishDisposition.BlockedByCatalogCompatibility, disabled.PublishDisposition);

        var controlDisabledCatalog = new DeviceCapabilityCatalog(
            "test-1",
            [
                new DeviceCapabilityDefinition
                {
                    CapabilityId = "test.control",
                    DisplayName = "Controlled Test",
                    DeviceFamily = "test-device",
                    ExecutionMode = WorkflowExecutionMode.DeviceCommand,
                    SafetyClassification = WorkflowSafetyClassification.ControlledDeviceAction,
                    ProfileSupport = new WorkflowProfileSupport { IsProfileIndependent = true },
                    Enabled = true,
                    ControlEnabled = false
                }
            ]);
        Assert.Equal(
            WorkflowCatalogPublishDisposition.BlockedByCatalogCompatibility,
            controlDisabledCatalog.Resolve("test.control", "1.0").PublishDisposition);
    }

    [Fact]
    public void New_catalog_node_ids_do_not_enter_the_legacy_runtime_projection()
    {
        Assert.Equal(
            WorkflowNodeType.Custom,
            WorkflowGraphNodeTypeIds.ToContractType(WorkflowGraphNodeTypeIds.TimedWait));
        Assert.Equal(
            WorkflowNodeType.Custom,
            WorkflowGraphNodeTypeIds.ToContractType(WorkflowGraphNodeTypeIds.ManualConfirmation));
        Assert.Equal(
            WorkflowNodeType.Custom,
            WorkflowGraphNodeTypeIds.ToContractType(WorkflowGraphNodeTypeIds.InstrumentReadStatus));
        Assert.Equal(
            WorkflowNodeType.Custom,
            WorkflowGraphNodeTypeIds.ToContractType(WorkflowGraphNodeTypeIds.InstrumentWaitUntilStable));
        Assert.Equal(
            WorkflowNodeType.Custom,
            WorkflowGraphNodeTypeIds.ToContractType(WorkflowGraphNodeTypeIds.Condition));
        Assert.Equal(
            WorkflowNodeType.Custom,
            WorkflowGraphNodeTypeIds.ToContractType(WorkflowGraphNodeTypeIds.ParallelFork));
        Assert.Equal(
            WorkflowNodeType.Custom,
            WorkflowGraphNodeTypeIds.ToContractType(WorkflowGraphNodeTypeIds.Subflow));
    }

    [Fact]
    public void Legacy_wait_and_instrument_migrations_are_explicit_and_narrow()
    {
        var catalog = BuiltInWorkflowCatalog.Create().NodeTypes;

        Assert.Equal(
            WorkflowCatalogResolutionStatus.UnknownType,
            catalog.Resolve(WorkflowGraphNodeTypeIds.Wait, "1.0").Status);
        Assert.Equal(
            WorkflowCatalogResolutionStatus.UnknownType,
            catalog.Resolve(WorkflowGraphNodeTypeIds.InstrumentOperation, "1.0").Status);

        var wait = Assert.Single(catalog.GetMigrations(WorkflowGraphNodeTypeIds.Wait, "1.0"));
        Assert.Equal(WorkflowGraphNodeTypeIds.TimedWait, wait.TargetNodeTypeId);
        Assert.Equal([WorkflowRuntimeParameterNames.WaitDurationSeconds], wait.RequiredParameterNames);
        Assert.True(wait.RequiresExplicitConfirmation);

        var instrument = Assert.Single(catalog.GetMigrations(
            WorkflowGraphNodeTypeIds.InstrumentOperation,
            "1.0"));
        Assert.Equal(WorkflowGraphNodeTypeIds.InstrumentReadStatus, instrument.TargetNodeTypeId);
        Assert.Equal([WorkflowRuntimeParameterNames.InstrumentId], instrument.RequiredParameterNames);
        Assert.Equal("read-status", instrument.RequiredParameterValues[WorkflowRuntimeParameterNames.InstrumentOperation]);
        Assert.True(instrument.RequiresExplicitConfirmation);

        Assert.Empty(catalog.GetMigrations(WorkflowGraphNodeTypeIds.Wait, "2.0"));
    }

    [Fact]
    public void Version_two_graph_round_trip_preserves_unknown_node_schema_and_configuration()
    {
        var nodeId = Guid.NewGuid();
        var document = WorkflowDocumentEditor.Deserialize(
            $$"""
            {
              "id": "{{Guid.NewGuid()}}",
              "schemaVersion": 2,
              "name": "Future node draft",
              "nodes": [
                {
                  "id": "{{nodeId}}",
                  "nodeTypeId": "vendor.future-node",
                  "schemaVersion": "9.1",
                  "name": "Preserve me",
                  "ports": [
                    {
                      "key": "future-output",
                      "displayName": "Future Output",
                      "direction": 1,
                      "dataType": "future-data",
                      "cardinality": 0,
                      "edgeKind": 0
                    }
                  ],
                  "configuration": {
                    "futureField": "future-value",
                    "nestedJson": "{\"preserve\":true}"
                  }
                }
              ],
              "edges": [],
              "layouts": [],
              "viewport": { "x": 0, "y": 0, "zoom": 1 }
            }
            """);

        var contract = WorkflowGraphContractAdapter.ToContract(document);
        var roundTrip = WorkflowGraphContractAdapter.FromContract(contract);
        var node = Assert.Single(roundTrip.Nodes);

        Assert.Equal(2, roundTrip.SchemaVersion);
        Assert.Equal("vendor.future-node", node.NodeTypeId);
        Assert.Equal("9.1", node.SchemaVersion);
        Assert.Equal("future-value", node.Configuration["futureField"]);
        Assert.Equal("{\"preserve\":true}", node.Configuration["nestedJson"]);
        var port = Assert.Single(node.Ports);
        Assert.Equal("future-output", port.Key);
        Assert.Equal("future-data", port.DataType);

        var resolution = BuiltInWorkflowCatalog.Create().NodeTypes.Resolve(node.NodeTypeId, node.SchemaVersion);
        Assert.Equal(WorkflowCatalogResolutionStatus.UnknownType, resolution.Status);
        Assert.True(resolution.PreserveOnSave);
        Assert.Equal(WorkflowCatalogEditMode.PreserveReadOnly, resolution.EditMode);
    }

    [Fact]
    public void Catalog_snapshots_are_frozen_and_reject_duplicate_versions()
    {
        var fields = new List<WorkflowFieldSchema>
        {
            new() { Key = "value", DisplayName = "Value" }
        };
        var definition = new WorkflowNodeTypeDefinition
        {
            NodeTypeId = "test.node",
            SchemaVersion = "1.0",
            DisplayName = "Test Node",
            Category = "Test",
            ConfigurationSchema = new WorkflowObjectSchema { Fields = fields },
            ProfileSupport = new WorkflowProfileSupport { IsProfileIndependent = true }
        };
        var catalog = new WorkflowNodeTypeCatalog("test-1", [definition]);

        fields.Add(new WorkflowFieldSchema { Key = "late", DisplayName = "Late" });
        Assert.Single(Assert.Single(catalog.Definitions).ConfigurationSchema.Fields);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<WorkflowFieldSchema>)catalog.Definitions[0].ConfigurationSchema.Fields).Add(
                new WorkflowFieldSchema { Key = "blocked", DisplayName = "Blocked" }));

        Assert.Throws<ArgumentException>(() => new WorkflowNodeTypeCatalog(
            "test-1",
            [definition, definition with { DisplayName = "Duplicate" }]));
    }

    private static WorkflowNodeTypeDefinition GetNode(IWorkflowNodeTypeCatalog catalog, string nodeTypeId)
    {
        Assert.True(catalog.TryGet(
            nodeTypeId,
            BuiltInWorkflowCatalog.CurrentSchemaVersion,
            out var definition));
        return Assert.IsType<WorkflowNodeTypeDefinition>(definition);
    }

    private static WorkflowFieldSchema AssertField(
        WorkflowNodeTypeDefinition definition,
        string key,
        bool required,
        string? defaultValue)
    {
        var field = Assert.Single(definition.ConfigurationSchema.Fields, candidate => candidate.Key == key);
        Assert.Equal(required, field.IsRequired);
        Assert.Equal(defaultValue, field.DefaultValue);
        return field;
    }

    private static void AssertPort(
        WorkflowNodeTypeDefinition definition,
        string key,
        WorkflowPortDirection direction,
        WorkflowEdgeKind edgeKind)
    {
        var port = Assert.Single(definition.Ports, candidate => candidate.Key == key);
        Assert.Equal(direction, port.Direction);
        Assert.Equal(edgeKind, port.EdgeKind);
    }

    private static bool ContainsProtocolDetail(string key) =>
        new[] { "register", "address", "raw", "frame", "hex", "command", "script" }
            .Any(token => key.Contains(token, StringComparison.OrdinalIgnoreCase));
}
