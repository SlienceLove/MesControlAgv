using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Domain.Workflows;

namespace MesControlAgv.WorkflowContract.Tests;

public sealed class WorkflowPublicationValidatorTests
{
    private static readonly WorkflowCatalogSet Catalog = BuiltInWorkflowCatalog.Create();

    [Fact]
    public void Valid_typed_graph_is_publishable_with_locatable_exception_path_warnings()
    {
        var workflow = CreateLinearWorkflow(WorkflowGraphNodeTypeIds.Move, MoveConfiguration("SAMPLE_01"));

        var result = new WorkflowValidator().ValidateForPublication(workflow);

        Assert.True(result.IsValid);
        Assert.True(result.HasWarnings);
        Assert.Equal(WorkflowValidator.PublicationValidatorVersion, result.ValidatorVersion);
        Assert.Equal(BuiltInWorkflowCatalog.CurrentCatalogVersion, result.CatalogVersion);
        Assert.Equal("MES-AGV", result.ProfileProductId);
        Assert.Equal(ProfileConfiguration.Default.Product.Version, result.ProfileVersion);
        Assert.Equal(2, result.Issues.Count(issue =>
            issue.Code == WorkflowPublicationIssueCodes.ExceptionPathMissing &&
            issue.Severity == WorkflowValidationSeverity.Warning));
        Assert.All(result.Issues, issue => Assert.NotNull(issue.NodeId));
    }

    [Fact]
    public void Required_schema_defaults_are_not_silently_injected_during_validation()
    {
        var configuration = MoveConfiguration("SAMPLE_01");
        configuration.Remove(WorkflowNodeConfigurationKeys.TimeoutSeconds);
        var workflow = CreateLinearWorkflow(WorkflowGraphNodeTypeIds.Move, configuration);
        var move = GetMiddleNode(workflow);

        var result = new WorkflowValidator().ValidateForPublication(workflow);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowPublicationIssueCodes.FieldRequired &&
            issue.NodeId == move.Id &&
            issue.ConfigurationKey == WorkflowNodeConfigurationKeys.TimeoutSeconds);
    }

    [Fact]
    public void Missing_success_default_path_blocks_publication()
    {
        var workflow = CreateLinearWorkflow(WorkflowGraphNodeTypeIds.Move, MoveConfiguration("SAMPLE_01"));
        var move = GetMiddleNode(workflow);
        var disconnected = workflow with
        {
            Nodes = workflow.Nodes.Select(node => node.Id == move.Id
                ? node with { NextNodeIds = Array.Empty<Guid>() }
                : node).ToArray(),
            Edges = workflow.Edges.Where(edge => edge.SourceNodeId != move.Id).ToArray()
        };

        var result = new WorkflowValidator().ValidateForPublication(disconnected);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowPublicationIssueCodes.DefaultPathMissing &&
            issue.NodeId == move.Id);
    }

    [Theory]
    [InlineData("UNKNOWN_STATION", WorkflowPublicationIssueCodes.StationUnknown)]
    [InlineData("SAMPLE_01", WorkflowPublicationIssueCodes.StationDisabled)]
    public void Unknown_or_disabled_station_blocks_publication(string stationId, string expectedCode)
    {
        var profile = ProfileConfiguration.Default;
        if (expectedCode == WorkflowPublicationIssueCodes.StationDisabled)
        {
            profile = profile with
            {
                Stations = profile.Stations.Select(station => station.StationId == stationId
                    ? station with { Enabled = false }
                    : station).ToArray()
            };
        }
        var workflow = CreateLinearWorkflow(WorkflowGraphNodeTypeIds.Move, MoveConfiguration(stationId));
        var move = GetMiddleNode(workflow);
        var validator = CreateValidator(profile: profile);

        var result = validator.ValidateForPublication(workflow);

        Assert.Contains(result.Issues, issue =>
            issue.Code == expectedCode &&
            issue.NodeId == move.Id &&
            issue.ConfigurationKey == WorkflowNodeConfigurationKeys.TargetStation);
    }

    [Theory]
    [InlineData("UNKNOWN", WorkflowPublicationIssueCodes.DeviceUnknown)]
    [InlineData("CIC-D160-01", WorkflowPublicationIssueCodes.DeviceDisabled)]
    public void Unknown_or_disabled_device_blocks_publication(string deviceId, string expectedCode)
    {
        var profile = ProfileConfiguration.Default;
        if (expectedCode == WorkflowPublicationIssueCodes.DeviceDisabled)
        {
            profile = profile with
            {
                WorkflowDevices = profile.WorkflowDevices.Select(device => device.DeviceId == deviceId
                    ? device with { Enabled = false }
                    : device).ToArray()
            };
        }
        var workflow = CreateLinearWorkflow(
            WorkflowGraphNodeTypeIds.InstrumentReadStatus,
            InstrumentReadConfiguration(deviceId));
        var instrument = GetMiddleNode(workflow);

        var result = CreateValidator(profile: profile).ValidateForPublication(workflow);

        Assert.Contains(result.Issues, issue =>
            issue.Code == expectedCode &&
            issue.NodeId == instrument.Id &&
            issue.ConfigurationKey == WorkflowNodeConfigurationKeys.InstrumentId);
    }

    [Fact]
    public void Device_must_declare_every_capability_required_by_the_node_type()
    {
        var profile = ProfileConfiguration.Default with
        {
            WorkflowDevices = ProfileConfiguration.Default.WorkflowDevices.Select(device =>
                device.DeviceId == "CIC-D160-01"
                    ? device with { CapabilityIds = [WorkflowCapabilityIds.InstrumentIdentify] }
                    : device).ToArray()
        };
        var workflow = CreateLinearWorkflow(
            WorkflowGraphNodeTypeIds.InstrumentReadStatus,
            InstrumentReadConfiguration("CIC-D160-01"));

        var result = CreateValidator(profile: profile).ValidateForPublication(workflow);

        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowPublicationIssueCodes.DeviceCapabilityMissing &&
            issue.ConfigurationKey == WorkflowNodeConfigurationKeys.InstrumentId);
    }

    [Fact]
    public void Catalog_disabled_write_capability_cannot_be_enabled_by_graph_json()
    {
        var capabilities = new DeviceCapabilityCatalog(
            "restricted-test",
            Catalog.Capabilities.Definitions);
        var nodeDefinitions = Catalog.NodeTypes.Definitions.Select(definition =>
            definition.NodeTypeId == WorkflowGraphNodeTypeIds.InstrumentReadStatus
                ? definition with
                {
                    RequiredCapabilityIds = [WorkflowCapabilityIds.InstrumentStartAnalysis]
                }
                : definition).ToArray();
        var nodeTypes = new WorkflowNodeTypeCatalog(
            "restricted-test",
            nodeDefinitions,
            Catalog.NodeTypes.Migrations,
            capabilities);
        var validator = CreateValidator(new WorkflowCatalogSet(nodeTypes, capabilities));
        var workflow = CreateLinearWorkflow(
            WorkflowGraphNodeTypeIds.InstrumentReadStatus,
            InstrumentReadConfiguration("CIC-D160-01"));

        var result = validator.ValidateForPublication(workflow);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowPublicationIssueCodes.CapabilityDisabled);
        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowPublicationIssueCodes.CapabilityControlDisabled);
        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowPublicationIssueCodes.DeviceCapabilityMissing);
    }

    [Fact]
    public void Profile_dependent_node_and_capability_reject_an_unsupported_product()
    {
        var profile = ProfileConfiguration.Default with
        {
            Product = ProfileConfiguration.Default.Product with { ProductId = "OTHER-PRODUCT" }
        };
        var workflow = CreateLinearWorkflow(WorkflowGraphNodeTypeIds.Move, MoveConfiguration("SAMPLE_01"));

        var result = CreateValidator(profile: profile).ValidateForPublication(workflow);

        Assert.Contains(result.Issues, issue => issue.Code == WorkflowPublicationIssueCodes.ProfileUnsupported);
        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowPublicationIssueCodes.CapabilityProfileUnsupported);
    }

    [Theory]
    [InlineData("vendor.future-node", "1.0", WorkflowPublicationIssueCodes.NodeTypeUnknown)]
    [InlineData(WorkflowGraphNodeTypeIds.Move, "9.0", WorkflowPublicationIssueCodes.NodeSchemaFuture)]
    [InlineData(WorkflowGraphNodeTypeIds.Move, "invalid", WorkflowPublicationIssueCodes.NodeSchemaUnsupported)]
    public void Unknown_future_or_malformed_node_schema_is_preserved_but_not_publishable(
        string nodeTypeId,
        string schemaVersion,
        string expectedCode)
    {
        var workflow = CreateLinearWorkflow(WorkflowGraphNodeTypeIds.Move, MoveConfiguration("SAMPLE_01"));
        var middle = GetMiddleNode(workflow);
        var changed = workflow with
        {
            Nodes = workflow.Nodes.Select(node => node.Id == middle.Id
                ? node with
                {
                    NodeTypeId = nodeTypeId,
                    SchemaVersion = schemaVersion,
                    Type = nodeTypeId == WorkflowGraphNodeTypeIds.Move
                        ? WorkflowNodeType.Move
                        : WorkflowNodeType.Custom
                }
                : node).ToArray()
        };

        var result = new WorkflowValidator().ValidateForPublication(changed);

        Assert.Contains(result.Issues, issue => issue.Code == expectedCode && issue.NodeId == middle.Id);
    }

    [Fact]
    public void Node_ports_are_validated_against_catalog_metadata_not_graph_claims()
    {
        var workflow = CreateLinearWorkflow(WorkflowGraphNodeTypeIds.Move, MoveConfiguration("SAMPLE_01"));
        var move = GetMiddleNode(workflow);
        var changed = workflow with
        {
            Nodes = workflow.Nodes.Select(node => node.Id == move.Id
                ? node with
                {
                    Ports = node.Ports.Select(port => port.Key == "success"
                        ? port with { DataType = "forged-result", EdgeKind = WorkflowEdgeKind.Failure }
                        : port).ToArray()
                }
                : node).ToArray()
        };

        var result = new WorkflowValidator().ValidateForPublication(changed);

        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowPublicationIssueCodes.NodePortSchemaMismatch && issue.NodeId == move.Id);
    }

    [Fact]
    public void Edge_semantic_errors_carry_the_explicit_edge_id()
    {
        var workflow = CreateLinearWorkflow(WorkflowGraphNodeTypeIds.Move, MoveConfiguration("SAMPLE_01"));
        var move = GetMiddleNode(workflow);
        var edge = workflow.Edges.Single(candidate => candidate.SourceNodeId == move.Id);
        var changed = workflow with
        {
            Edges = workflow.Edges.Select(candidate => candidate.Id == edge.Id
                ? candidate with { Kind = WorkflowEdgeKind.Failure }
                : candidate).ToArray()
        };

        var result = new WorkflowValidator().ValidateForPublication(changed);

        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowPublicationIssueCodes.EdgeKindMismatch &&
            issue.EdgeId == edge.Id &&
            issue.NodeId == move.Id);
    }

    [Theory]
    [InlineData("0", WorkflowPublicationIssueCodes.FieldOutOfRange)]
    [InlineData("1.5", WorkflowPublicationIssueCodes.FieldTypeInvalid)]
    public void Typed_field_range_and_value_type_are_enforced(string timeout, string expectedCode)
    {
        var configuration = MoveConfiguration("SAMPLE_01");
        configuration[WorkflowNodeConfigurationKeys.TimeoutSeconds] = timeout;
        var workflow = CreateLinearWorkflow(WorkflowGraphNodeTypeIds.Move, configuration);

        var result = new WorkflowValidator().ValidateForPublication(workflow);

        Assert.Contains(result.Issues, issue =>
            issue.Code == expectedCode &&
            issue.ConfigurationKey == WorkflowNodeConfigurationKeys.TimeoutSeconds);
    }

    [Fact]
    public void Legacy_v1_validation_contract_remains_available_for_published_runtime_snapshots()
    {
        var start = Guid.NewGuid();
        var move = Guid.NewGuid();
        var end = Guid.NewGuid();
        var legacy = new WorkflowDefinition
        {
            Name = "Legacy published definition",
            Nodes =
            [
                new WorkflowNode { Id = start, Type = WorkflowNodeType.Start, Name = "Start", Order = 1, NextNodeIds = [move] },
                new WorkflowNode { Id = move, Type = WorkflowNodeType.Move, Name = "Move", TargetStation = "SAMPLE_01", Order = 2, NextNodeIds = [end] },
                new WorkflowNode { Id = end, Type = WorkflowNodeType.End, Name = "End", Order = 3 }
            ]
        };
        var validator = new WorkflowValidator();

        var runtimeResult = validator.Validate(legacy);
        var publicationResult = validator.ValidateForPublication(legacy);

        Assert.True(runtimeResult.IsValid);
        Assert.Equal(WorkflowValidator.ValidatorVersion, runtimeResult.ValidatorVersion);
        Assert.False(publicationResult.IsValid);
        Assert.Equal(WorkflowValidator.PublicationValidatorVersion, publicationResult.ValidatorVersion);
        Assert.Contains(publicationResult.Issues, issue =>
            issue.Code == WorkflowPublicationIssueCodes.NodeTypeRequired);
    }

    private static WorkflowValidator CreateValidator(
        WorkflowCatalogSet? catalog = null,
        ProfileConfiguration? profile = null) => new(
        catalog ?? Catalog,
        WorkflowPublicationContext.FromProfile(profile ?? ProfileConfiguration.Default));

    private static WorkflowDefinition CreateLinearWorkflow(
        string middleTypeId,
        IReadOnlyDictionary<string, string?> configuration)
    {
        var startId = Guid.NewGuid();
        var middleId = Guid.NewGuid();
        var endId = Guid.NewGuid();
        var start = CreateNode(startId, WorkflowGraphNodeTypeIds.Start, "Start", 1, [middleId]);
        var middle = CreateNode(middleId, middleTypeId, "Action", 2, [endId], configuration);
        var end = CreateNode(endId, WorkflowGraphNodeTypeIds.End, "End", 3, []);
        return new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            SchemaVersion = WorkflowGraphDocument.CurrentSchemaVersion,
            Name = "Typed publication graph",
            Nodes = [start, middle, end],
            Edges =
            [
                CreateEdge(startId, "success", middleId),
                CreateEdge(middleId, "success", endId)
            ]
        };
    }

    private static WorkflowNode CreateNode(
        Guid id,
        string nodeTypeId,
        string name,
        int order,
        IReadOnlyList<Guid> nextNodeIds,
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        Assert.True(Catalog.NodeTypes.TryGet(
            nodeTypeId,
            BuiltInWorkflowCatalog.CurrentSchemaVersion,
            out var definition));
        Assert.NotNull(definition);
        configuration ??= new Dictionary<string, string?>();
        configuration.TryGetValue(WorkflowNodeConfigurationKeys.TargetStation, out var targetStation);
        return new WorkflowNode
        {
            Id = id,
            Type = WorkflowGraphNodeTypeIds.ToContractType(nodeTypeId),
            NodeTypeId = nodeTypeId,
            SchemaVersion = definition!.SchemaVersion,
            Name = name,
            Order = order,
            TargetStation = targetStation,
            NextNodeIds = nextNodeIds,
            Ports = definition.Ports,
            Configuration = configuration
        };
    }

    private static WorkflowEdgeDefinition CreateEdge(Guid sourceNodeId, string sourcePort, Guid targetNodeId) => new()
    {
        Id = Guid.NewGuid(),
        SourceNodeId = sourceNodeId,
        SourcePort = sourcePort,
        TargetNodeId = targetNodeId,
        TargetPort = "in",
        Kind = WorkflowEdgeKind.Success
    };

    private static Dictionary<string, string?> MoveConfiguration(string stationId) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [WorkflowNodeConfigurationKeys.TargetStation] = stationId,
            [WorkflowNodeConfigurationKeys.TimeoutSeconds] = "300",
            [WorkflowNodeConfigurationKeys.RetryCount] = "0"
        };

    private static Dictionary<string, string?> InstrumentReadConfiguration(string deviceId) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [WorkflowNodeConfigurationKeys.InstrumentId] = deviceId,
            [WorkflowNodeConfigurationKeys.TimeoutSeconds] = "30",
            [WorkflowNodeConfigurationKeys.RetryCount] = "2"
        };

    private static WorkflowNode GetMiddleNode(WorkflowDefinition workflow) =>
        workflow.Nodes.Single(node => node.Order == 2);
}
