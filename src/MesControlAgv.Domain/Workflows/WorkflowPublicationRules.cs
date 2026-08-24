using System.Globalization;
using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Domain.Workflows;

public static class WorkflowPublicationIssueCodes
{
    public const string DocumentSchemaUnsupported = "WF-DOCUMENT-SCHEMA-UNSUPPORTED";
    public const string NodeTypeRequired = "WF-NODE-TYPE-REQUIRED";
    public const string NodeTypeUnknown = "WF-NODE-TYPE-UNKNOWN";
    public const string NodeSchemaUnsupported = "WF-NODE-SCHEMA-UNSUPPORTED";
    public const string NodeSchemaFuture = "WF-NODE-SCHEMA-FUTURE";
    public const string NodeTypeDisabled = "WF-NODE-TYPE-DISABLED";
    public const string NodeLegacyProjectionMismatch = "WF-NODE-LEGACY-PROJECTION-MISMATCH";
    public const string NodePortMissing = "WF-NODE-PORT-MISSING";
    public const string NodePortUnknown = "WF-NODE-PORT-UNKNOWN";
    public const string NodePortDuplicate = "WF-NODE-PORT-DUPLICATE";
    public const string NodePortSchemaMismatch = "WF-NODE-PORT-SCHEMA-MISMATCH";
    public const string FieldRequired = "WF-FIELD-REQUIRED";
    public const string FieldUnknown = "WF-FIELD-UNKNOWN";
    public const string FieldDuplicate = "WF-FIELD-DUPLICATE";
    public const string FieldTypeInvalid = "WF-FIELD-TYPE-INVALID";
    public const string FieldOutOfRange = "WF-FIELD-OUT-OF-RANGE";
    public const string FieldValueNotAllowed = "WF-FIELD-VALUE-NOT-ALLOWED";
    public const string ProfileUnsupported = "WF-PROFILE-UNSUPPORTED";
    public const string StationUnknown = "WF-STATION-UNKNOWN";
    public const string StationDisabled = "WF-STATION-DISABLED";
    public const string DeviceUnknown = "WF-DEVICE-UNKNOWN";
    public const string DeviceDisabled = "WF-DEVICE-DISABLED";
    public const string DeviceFamilyMismatch = "WF-DEVICE-FAMILY-MISMATCH";
    public const string DeviceCapabilityMissing = "WF-DEVICE-CAPABILITY-MISSING";
    public const string DeviceControlDisabled = "WF-DEVICE-CONTROL-DISABLED";
    public const string DeviceAutoSelectionUnavailable = "WF-DEVICE-AUTO-SELECTION-UNAVAILABLE";
    public const string CapabilityUnknown = "WF-CAPABILITY-UNKNOWN";
    public const string CapabilityDisabled = "WF-CAPABILITY-DISABLED";
    public const string CapabilityControlDisabled = "WF-CAPABILITY-CONTROL-DISABLED";
    public const string CapabilityProfileUnsupported = "WF-CAPABILITY-PROFILE-UNSUPPORTED";
    public const string EdgeIdRequired = "WF-EDGE-ID-REQUIRED";
    public const string EdgeIdDuplicate = "WF-EDGE-ID-DUPLICATE";
    public const string EdgeNodeUnknown = "WF-EDGE-NODE-UNKNOWN";
    public const string EdgeSelfReference = "WF-EDGE-SELF-REFERENCE";
    public const string EdgePortUnknown = "WF-EDGE-PORT-UNKNOWN";
    public const string EdgeDirectionInvalid = "WF-EDGE-DIRECTION-INVALID";
    public const string EdgeTypeMismatch = "WF-EDGE-TYPE-MISMATCH";
    public const string EdgeKindMismatch = "WF-EDGE-KIND-MISMATCH";
    public const string EdgeDuplicate = "WF-EDGE-DUPLICATE";
    public const string EdgeCardinality = "WF-EDGE-CARDINALITY";
    public const string EdgeConditionUnsupported = "WF-EDGE-CONDITION-UNSUPPORTED";
    public const string AdvancedSchemaRequired = "WF-G6-SCHEMA-REQUIRED";
    public const string ConditionExpressionRequired = "WF-CONDITION-EXPRESSION-REQUIRED";
    public const string ConditionExpressionLocation = "WF-CONDITION-EXPRESSION-LOCATION";
    public const string ConditionExpressionSchema = "WF-CONDITION-EXPRESSION-SCHEMA";
    public const string ConditionSourceInvalid = "WF-CONDITION-SOURCE-INVALID";
    public const string ConditionSourceUnavailable = "WF-CONDITION-SOURCE-UNAVAILABLE";
    public const string ConditionTypeMismatch = "WF-CONDITION-TYPE-MISMATCH";
    public const string ConditionOperatorInvalid = "WF-CONDITION-OPERATOR-INVALID";
    public const string ConditionValueInvalid = "WF-CONDITION-VALUE-INVALID";
    public const string ConditionMissingBehavior = "WF-CONDITION-MISSING-BEHAVIOR";
    public const string ConditionDefaultInvalid = "WF-CONDITION-DEFAULT-INVALID";
    public const string ConditionPriorityInvalid = "WF-CONDITION-PRIORITY-INVALID";
    public const string SignalReferenceInvalid = "WF-SIGNAL-REFERENCE-INVALID";
    public const string InteractionOutcomePathInvalid = "WF-INTERACTION-OUTCOME-PATH-INVALID";
    public const string ParallelPairInvalid = "WF-PARALLEL-PAIR-INVALID";
    public const string ParallelBranchInvalid = "WF-PARALLEL-BRANCH-INVALID";
    public const string ParallelJoinInvalid = "WF-PARALLEL-JOIN-INVALID";
    public const string SubflowReferenceInvalid = "WF-SUBFLOW-REFERENCE-INVALID";
    public const string CompensationPathInvalid = "WF-COMPENSATION-PATH-INVALID";
    public const string BoundaryStart = "WF-GRAPH-START";
    public const string BoundaryEnd = "WF-GRAPH-END";
    public const string StartIncoming = "WF-PATH-START-INCOMING";
    public const string EndOutgoing = "WF-PATH-END-OUTGOING";
    public const string DefaultPathMissing = "WF-PATH-DEFAULT-MISSING";
    public const string DefaultPathAmbiguous = "WF-PATH-DEFAULT-AMBIGUOUS";
    public const string ExceptionPathMissing = "WF-PATH-EXCEPTION-MISSING";
    public const string ExceptionPathAmbiguous = "WF-PATH-EXCEPTION-AMBIGUOUS";
    public const string NodeUnreachable = "WF-PATH-NODE-UNREACHABLE";
    public const string EndUnreachable = "WF-PATH-END-UNREACHABLE";
    public const string CycleUnsupported = "WF-PATH-CYCLE-UNSUPPORTED";
}

internal static class WorkflowPublicationRules
{
    public static void Validate(
        WorkflowDefinition workflow,
        WorkflowCatalogSet catalogs,
        WorkflowPublicationContext context,
        ICollection<WorkflowValidationIssue> issues)
    {
        if (!WorkflowGraphDocument.IsPublishableSchemaVersion(workflow.SchemaVersion))
        {
            issues.Add(Error(
                WorkflowPublicationIssueCodes.DocumentSchemaUnsupported,
                $"Workflow document schema '{workflow.SchemaVersion}' is not supported for publication."));
        }

        var nodes = workflow.Nodes ?? [];
        var edges = workflow.Edges ?? [];
        var resolvedNodes = ResolveNodes(nodes, catalogs, context, issues);
        var resolvedEdges = ValidateEdges(edges, nodes, resolvedNodes, issues);
        ValidateLegacyProjection(edges, resolvedNodes, issues);
        ValidateTopology(resolvedNodes, resolvedEdges, issues);
        WorkflowAdvancedPublicationRules.Validate(workflow, catalogs, issues);
    }

    private static IReadOnlyDictionary<Guid, ResolvedNode> ResolveNodes(
        IReadOnlyList<WorkflowNode> nodes,
        WorkflowCatalogSet catalogs,
        WorkflowPublicationContext context,
        ICollection<WorkflowValidationIssue> issues)
    {
        var result = new Dictionary<Guid, ResolvedNode>();
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.NodeTypeId))
            {
                issues.Add(Error(
                    WorkflowPublicationIssueCodes.NodeTypeRequired,
                    "A stable node type id is required for publication.",
                    node.Id));
                continue;
            }

            var resolution = catalogs.NodeTypes.Resolve(node.NodeTypeId, node.SchemaVersion);
            if (resolution.Status != WorkflowCatalogResolutionStatus.Supported || resolution.Definition is null)
            {
                var (code, message) = resolution.Status switch
                {
                    WorkflowCatalogResolutionStatus.UnknownType => (
                        WorkflowPublicationIssueCodes.NodeTypeUnknown,
                        $"Node type '{node.NodeTypeId}' is not present in the active catalog."),
                    WorkflowCatalogResolutionStatus.FutureSchemaVersion => (
                        WorkflowPublicationIssueCodes.NodeSchemaFuture,
                        $"Node type '{node.NodeTypeId}' uses future schema '{node.SchemaVersion}'."),
                    _ => (
                        WorkflowPublicationIssueCodes.NodeSchemaUnsupported,
                        $"Node type '{node.NodeTypeId}' schema '{node.SchemaVersion}' is not supported.")
                };
                issues.Add(Error(code, message, node.Id));
                continue;
            }

            var definition = resolution.Definition;
            if (!definition.Enabled)
            {
                issues.Add(Error(
                    WorkflowPublicationIssueCodes.NodeTypeDisabled,
                    definition.UnavailableReason ?? $"Node type '{node.NodeTypeId}' is disabled.",
                    node.Id));
            }
            if (!SupportsProfile(definition.ProfileSupport, context.ProductId))
            {
                issues.Add(Error(
                    WorkflowPublicationIssueCodes.ProfileUnsupported,
                    $"Node type '{node.NodeTypeId}' does not support Profile product '{context.ProductId}'.",
                    node.Id));
            }

            ValidatePorts(node, definition, issues);
            ValidateConfiguration(node, definition, catalogs.Capabilities, context, issues);
            if (node.Id != Guid.Empty && !result.ContainsKey(node.Id))
                result.Add(node.Id, new ResolvedNode(node, definition));
        }

        return result;
    }

    private static void ValidatePorts(
        WorkflowNode node,
        WorkflowNodeTypeDefinition definition,
        ICollection<WorkflowValidationIssue> issues)
    {
        var actualPorts = node.Ports ?? [];
        foreach (var duplicate in actualPorts
                     .Where(port => !string.IsNullOrWhiteSpace(port.Key))
                     .GroupBy(port => port.Key, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            issues.Add(Error(
                WorkflowPublicationIssueCodes.NodePortDuplicate,
                $"Port '{duplicate.Key}' is duplicated on node '{node.Name}'.",
                node.Id));
        }

        foreach (var expected in definition.Ports)
        {
            var actual = actualPorts.FirstOrDefault(port =>
                string.Equals(port.Key, expected.Key, StringComparison.Ordinal));
            if (actual is null)
            {
                issues.Add(Error(
                    WorkflowPublicationIssueCodes.NodePortMissing,
                    $"Catalog port '{expected.Key}' is missing from node '{node.Name}'.",
                    node.Id));
                continue;
            }

            if (actual.Direction != expected.Direction ||
                !string.Equals(actual.DataType, expected.DataType, StringComparison.Ordinal) ||
                actual.Cardinality != expected.Cardinality ||
                actual.EdgeKind != expected.EdgeKind)
            {
                issues.Add(Error(
                    WorkflowPublicationIssueCodes.NodePortSchemaMismatch,
                    $"Port '{expected.Key}' does not match the active node schema.",
                    node.Id));
            }
        }

        foreach (var actual in actualPorts.Where(actual => !definition.Ports.Any(expected =>
                     string.Equals(expected.Key, actual.Key, StringComparison.Ordinal))))
        {
            issues.Add(Error(
                WorkflowPublicationIssueCodes.NodePortUnknown,
                $"Port '{actual.Key}' is not declared by node type '{definition.NodeTypeId}'.",
                node.Id));
        }
    }

    private static void ValidateConfiguration(
        WorkflowNode node,
        WorkflowNodeTypeDefinition definition,
        IDeviceCapabilityCatalog capabilities,
        WorkflowPublicationContext context,
        ICollection<WorkflowValidationIssue> issues)
    {
        var configuration = node.Configuration ?? new Dictionary<string, string?>();
        var fieldByKey = definition.ConfigurationSchema.Fields.ToDictionary(
            field => field.Key,
            StringComparer.OrdinalIgnoreCase);
        foreach (var duplicate in configuration.Keys
                     .Where(key => !string.IsNullOrWhiteSpace(key))
                     .GroupBy(key => key, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            issues.Add(FieldError(
                WorkflowPublicationIssueCodes.FieldDuplicate,
                $"Configuration field '{duplicate.Key}' is duplicated using different casing.",
                node.Id,
                duplicate.Key));
        }

        foreach (var key in configuration.Keys.Where(key => !fieldByKey.ContainsKey(key)))
        {
            issues.Add(FieldError(
                WorkflowPublicationIssueCodes.FieldUnknown,
                $"Configuration field '{key}' is not declared by node type '{definition.NodeTypeId}'.",
                node.Id,
                key));
        }

        foreach (var field in definition.ConfigurationSchema.Fields)
        {
            var hasValue = TryGetValue(configuration, field.Key, out var value) &&
                           !string.IsNullOrWhiteSpace(value);
            if (field.IsRequired && !hasValue)
            {
                issues.Add(FieldError(
                    WorkflowPublicationIssueCodes.FieldRequired,
                    $"Configuration field '{field.Key}' is required.",
                    node.Id,
                    field.Key));
                continue;
            }
            if (!hasValue) continue;

            if (!TryParse(field.ValueType, value!, out var numericValue))
            {
                issues.Add(FieldError(
                    WorkflowPublicationIssueCodes.FieldTypeInvalid,
                    $"Configuration field '{field.Key}' is not a valid {field.ValueType} value.",
                    node.Id,
                    field.Key));
                continue;
            }
            if (numericValue is { } number &&
                (field.Minimum is { } minimum && number < minimum ||
                 field.Maximum is { } maximum && number > maximum))
            {
                issues.Add(FieldError(
                    WorkflowPublicationIssueCodes.FieldOutOfRange,
                    $"Configuration field '{field.Key}' is outside its allowed range.",
                    node.Id,
                    field.Key));
            }
            if (field.AllowedValues.Count > 0 && !field.AllowedValues.Contains(value!, StringComparer.Ordinal))
            {
                issues.Add(FieldError(
                    WorkflowPublicationIssueCodes.FieldValueNotAllowed,
                    $"Configuration field '{field.Key}' has a value that is not allowed by the schema.",
                    node.Id,
                    field.Key));
            }
            if (field.ReferenceKind == WorkflowSchemaReferenceKind.Station)
                ValidateStation(node.Id, field.Key, value!, context, issues);
        }

        var requiredCapabilities = ResolveCapabilities(node, definition, capabilities, context, issues);
        ValidateDeviceReferences(node, definition, requiredCapabilities, context, issues);
    }

    private static IReadOnlyList<DeviceCapabilityDefinition> ResolveCapabilities(
        WorkflowNode node,
        WorkflowNodeTypeDefinition definition,
        IDeviceCapabilityCatalog capabilities,
        WorkflowPublicationContext context,
        ICollection<WorkflowValidationIssue> issues)
    {
        var result = new List<DeviceCapabilityDefinition>();
        foreach (var capabilityId in definition.RequiredCapabilityIds)
        {
            var capability = capabilities.GetLatest(capabilityId);
            if (capability is null)
            {
                issues.Add(Error(
                    WorkflowPublicationIssueCodes.CapabilityUnknown,
                    $"Required capability '{capabilityId}' is not present in the active catalog.",
                    node.Id));
                continue;
            }

            result.Add(capability);
            if (!capability.Enabled)
            {
                issues.Add(Error(
                    WorkflowPublicationIssueCodes.CapabilityDisabled,
                    capability.UnavailableReason ?? $"Required capability '{capabilityId}' is disabled.",
                    node.Id));
            }
            if (RequiresControl(capability) && !capability.ControlEnabled)
            {
                issues.Add(Error(
                    WorkflowPublicationIssueCodes.CapabilityControlDisabled,
                    capability.UnavailableReason ?? $"Control for capability '{capabilityId}' is disabled.",
                    node.Id));
            }
            if (!SupportsProfile(capability.ProfileSupport, context.ProductId))
            {
                issues.Add(Error(
                    WorkflowPublicationIssueCodes.CapabilityProfileUnsupported,
                    $"Capability '{capabilityId}' does not support Profile product '{context.ProductId}'.",
                    node.Id));
            }
        }

        return result;
    }

    private static void ValidateDeviceReferences(
        WorkflowNode node,
        WorkflowNodeTypeDefinition definition,
        IReadOnlyList<DeviceCapabilityDefinition> capabilities,
        WorkflowPublicationContext context,
        ICollection<WorkflowValidationIssue> issues)
    {
        var fields = definition.ConfigurationSchema.Fields
            .Where(field => field.ReferenceKind == WorkflowSchemaReferenceKind.Device)
            .ToArray();
        foreach (var field in fields)
        {
            if (!TryGetValue(node.Configuration ?? new Dictionary<string, string?>(), field.Key, out var deviceId) ||
                string.IsNullOrWhiteSpace(deviceId))
            {
                if (!field.IsRequired)
                    ValidateAutoSelection(node.Id, field, capabilities, context, issues);
                continue;
            }

            if (!context.TryGetDevice(deviceId, out var device) || device is null)
            {
                issues.Add(FieldError(
                    WorkflowPublicationIssueCodes.DeviceUnknown,
                    $"Device '{deviceId}' is not declared by the active Profile.",
                    node.Id,
                    field.Key));
                continue;
            }
            if (!string.IsNullOrWhiteSpace(field.DeviceFamily) &&
                !string.Equals(device.DeviceFamily, field.DeviceFamily, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(FieldError(
                    WorkflowPublicationIssueCodes.DeviceFamilyMismatch,
                    $"Device '{deviceId}' is not a '{field.DeviceFamily}' device.",
                    node.Id,
                    field.Key));
                continue;
            }
            if (!device.Enabled)
            {
                issues.Add(FieldError(
                    WorkflowPublicationIssueCodes.DeviceDisabled,
                    $"Device '{deviceId}' is disabled by the active Profile.",
                    node.Id,
                    field.Key));
            }

            foreach (var capability in capabilities.Where(capability =>
                         string.IsNullOrWhiteSpace(field.DeviceFamily) ||
                         string.Equals(capability.DeviceFamily, field.DeviceFamily, StringComparison.OrdinalIgnoreCase)))
            {
                if (!device.Provides(capability.CapabilityId))
                {
                    issues.Add(FieldError(
                        WorkflowPublicationIssueCodes.DeviceCapabilityMissing,
                        $"Device '{deviceId}' does not declare capability '{capability.CapabilityId}'.",
                        node.Id,
                        field.Key));
                }
                if (RequiresControl(capability) && !device.ControlEnabled)
                {
                    issues.Add(FieldError(
                        WorkflowPublicationIssueCodes.DeviceControlDisabled,
                        $"Device '{deviceId}' does not allow workflow control.",
                        node.Id,
                        field.Key));
                }
            }
        }
    }

    private static void ValidateAutoSelection(
        Guid nodeId,
        WorkflowFieldSchema field,
        IReadOnlyList<DeviceCapabilityDefinition> capabilities,
        WorkflowPublicationContext context,
        ICollection<WorkflowValidationIssue> issues)
    {
        var required = capabilities
            .Where(capability => string.IsNullOrWhiteSpace(field.DeviceFamily) ||
                                 string.Equals(capability.DeviceFamily, field.DeviceFamily, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var available = context.GetDevices(field.DeviceFamily ?? string.Empty).Any(device =>
            device.Enabled &&
            required.All(capability => device.Provides(capability.CapabilityId) &&
                                       (!RequiresControl(capability) || device.ControlEnabled)));
        if (!available)
        {
            issues.Add(FieldError(
                WorkflowPublicationIssueCodes.DeviceAutoSelectionUnavailable,
                $"No enabled '{field.DeviceFamily}' device can satisfy automatic selection.",
                nodeId,
                field.Key));
        }
    }

    private static void ValidateStation(
        Guid nodeId,
        string fieldKey,
        string stationId,
        WorkflowPublicationContext context,
        ICollection<WorkflowValidationIssue> issues)
    {
        if (!context.TryGetStation(stationId, out var station) || station is null)
        {
            issues.Add(FieldError(
                WorkflowPublicationIssueCodes.StationUnknown,
                $"Station '{stationId}' is not declared by the active Profile.",
                nodeId,
                fieldKey));
        }
        else if (!station.Enabled)
        {
            issues.Add(FieldError(
                WorkflowPublicationIssueCodes.StationDisabled,
                $"Station '{stationId}' is disabled by the active Profile.",
                nodeId,
                fieldKey));
        }
    }

    private static IReadOnlyList<ResolvedEdge> ValidateEdges(
        IReadOnlyList<WorkflowEdgeDefinition> edges,
        IReadOnlyList<WorkflowNode> nodes,
        IReadOnlyDictionary<Guid, ResolvedNode> resolvedNodes,
        ICollection<WorkflowValidationIssue> issues)
    {
        var nodeIds = nodes.Where(node => node.Id != Guid.Empty).Select(node => node.Id).ToHashSet();
        var edgeIds = new HashSet<Guid>();
        var connections = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ResolvedEdge>();
        foreach (var edge in edges)
        {
            var valid = true;
            if (edge.Id == Guid.Empty)
            {
                issues.Add(EdgeError(
                    WorkflowPublicationIssueCodes.EdgeIdRequired,
                    "Every workflow edge must have a non-empty id.",
                    edge));
                valid = false;
            }
            else if (!edgeIds.Add(edge.Id))
            {
                issues.Add(EdgeError(
                    WorkflowPublicationIssueCodes.EdgeIdDuplicate,
                    $"Edge id '{edge.Id}' is duplicated.",
                    edge));
                valid = false;
            }

            if (!nodeIds.Contains(edge.SourceNodeId) || !nodeIds.Contains(edge.TargetNodeId))
            {
                issues.Add(EdgeError(
                    WorkflowPublicationIssueCodes.EdgeNodeUnknown,
                    "The edge source or target node does not exist.",
                    edge));
                continue;
            }
            if (edge.SourceNodeId == edge.TargetNodeId)
            {
                issues.Add(EdgeError(
                    WorkflowPublicationIssueCodes.EdgeSelfReference,
                    "A workflow edge cannot connect a node to itself.",
                    edge));
                valid = false;
            }
            if (!string.IsNullOrWhiteSpace(edge.Condition))
            {
                issues.Add(EdgeError(
                    WorkflowPublicationIssueCodes.EdgeConditionUnsupported,
                    "Conditional expressions are not supported by the G3 publication gate.",
                    edge));
                valid = false;
            }

            if (!resolvedNodes.TryGetValue(edge.SourceNodeId, out var source) ||
                !resolvedNodes.TryGetValue(edge.TargetNodeId, out var target))
                continue;

            var sourcePort = source.Definition.Ports.FirstOrDefault(port =>
                string.Equals(port.Key, edge.SourcePort, StringComparison.Ordinal));
            var targetPort = target.Definition.Ports.FirstOrDefault(port =>
                string.Equals(port.Key, edge.TargetPort, StringComparison.Ordinal));
            if (sourcePort is null || targetPort is null)
            {
                issues.Add(EdgeError(
                    WorkflowPublicationIssueCodes.EdgePortUnknown,
                    "The edge references a port that is not declared by the active node schema.",
                    edge));
                continue;
            }
            if (sourcePort.Direction != WorkflowPortDirection.Output ||
                targetPort.Direction != WorkflowPortDirection.Input)
            {
                issues.Add(EdgeError(
                    WorkflowPublicationIssueCodes.EdgeDirectionInvalid,
                    "An edge must connect an output port to an input port.",
                    edge));
                valid = false;
            }
            if (!string.Equals(sourcePort.DataType, targetPort.DataType, StringComparison.Ordinal))
            {
                issues.Add(EdgeError(
                    WorkflowPublicationIssueCodes.EdgeTypeMismatch,
                    "The edge source and target data types do not match.",
                    edge));
                valid = false;
            }
            if (sourcePort.EdgeKind is { } expectedKind && expectedKind != edge.Kind)
            {
                issues.Add(EdgeError(
                    WorkflowPublicationIssueCodes.EdgeKindMismatch,
                    $"Edge kind '{edge.Kind}' does not match source port '{sourcePort.Key}'.",
                    edge));
                valid = false;
            }

            var connectionKey = string.Join(
                '\u001f',
                edge.SourceNodeId,
                edge.SourcePort,
                edge.TargetNodeId,
                edge.TargetPort);
            if (!connections.Add(connectionKey))
            {
                issues.Add(EdgeError(
                    WorkflowPublicationIssueCodes.EdgeDuplicate,
                    "The same workflow connection is declared more than once.",
                    edge));
                valid = false;
            }

            if (valid) result.Add(new ResolvedEdge(edge, source, target, sourcePort, targetPort));
        }

        ValidateCardinality(result, issues);
        return result;
    }

    private static void ValidateCardinality(
        IReadOnlyList<ResolvedEdge> edges,
        ICollection<WorkflowValidationIssue> issues)
    {
        foreach (var group in edges.GroupBy(edge =>
                     $"{edge.Edge.TargetNodeId:N}\u001f{edge.TargetPort.Key}",
                     StringComparer.Ordinal))
        {
            if (group.First().TargetPort.Cardinality != WorkflowPortCardinality.Single || group.Count() <= 1)
                continue;
            foreach (var edge in group.Skip(1))
            {
                issues.Add(EdgeError(
                    WorkflowPublicationIssueCodes.EdgeCardinality,
                    $"Input port '{edge.TargetPort.Key}' accepts only one edge.",
                    edge.Edge));
            }
        }

        foreach (var group in edges.GroupBy(edge =>
                     $"{edge.Edge.SourceNodeId:N}\u001f{edge.SourcePort.Key}",
                     StringComparer.Ordinal))
        {
            if (group.First().SourcePort.Cardinality != WorkflowPortCardinality.Single || group.Count() <= 1)
                continue;
            foreach (var edge in group.Skip(1))
            {
                issues.Add(EdgeError(
                    WorkflowPublicationIssueCodes.EdgeCardinality,
                    $"Output port '{edge.SourcePort.Key}' accepts only one edge.",
                    edge.Edge));
            }
        }
    }

    private static void ValidateLegacyProjection(
        IReadOnlyList<WorkflowEdgeDefinition> edges,
        IReadOnlyDictionary<Guid, ResolvedNode> resolvedNodes,
        ICollection<WorkflowValidationIssue> issues)
    {
        foreach (var resolved in resolvedNodes.Values)
        {
            var node = resolved.Node;
            var expectedType = WorkflowGraphNodeTypeIds.ToContractType(node.NodeTypeId);
            if (node.Type != expectedType)
            {
                issues.Add(Error(
                    WorkflowPublicationIssueCodes.NodeLegacyProjectionMismatch,
                    $"Legacy node type '{node.Type}' does not match node type id '{node.NodeTypeId}'.",
                    node.Id));
            }

            TryGetValue(
                node.Configuration ?? new Dictionary<string, string?>(),
                WorkflowNodeConfigurationKeys.TargetStation,
                out var configuredStation);
            if (!string.Equals(node.TargetStation, configuredStation, StringComparison.Ordinal))
            {
                issues.Add(FieldError(
                    WorkflowPublicationIssueCodes.NodeLegacyProjectionMismatch,
                    "TargetStation does not match the typed station configuration.",
                    node.Id,
                    WorkflowNodeConfigurationKeys.TargetStation));
            }
            if ((node.Parameters ?? []).Count > 0)
            {
                issues.Add(Error(
                    WorkflowPublicationIssueCodes.NodeLegacyProjectionMismatch,
                    "Typed workflow nodes cannot publish legacy free-form parameters.",
                    node.Id));
            }

            var expectedTargets = edges
                .Where(edge => edge.SourceNodeId == node.Id)
                .Select(edge => edge.TargetNodeId)
                .Distinct()
                .ToHashSet();
            var actualTargets = (node.NextNodeIds ?? []).ToHashSet();
            if (!expectedTargets.SetEquals(actualTargets))
            {
                issues.Add(Error(
                    WorkflowPublicationIssueCodes.NodeLegacyProjectionMismatch,
                    "NextNodeIds does not match the explicit edge projection.",
                    node.Id));
            }
        }
    }

    private static void ValidateTopology(
        IReadOnlyDictionary<Guid, ResolvedNode> nodes,
        IReadOnlyList<ResolvedEdge> edges,
        ICollection<WorkflowValidationIssue> issues)
    {
        var starts = nodes.Values.Where(node =>
            string.Equals(node.Definition.NodeTypeId, WorkflowGraphNodeTypeIds.Start, StringComparison.Ordinal)).ToArray();
        var ends = nodes.Values.Where(node =>
            string.Equals(node.Definition.NodeTypeId, WorkflowGraphNodeTypeIds.End, StringComparison.Ordinal)).ToArray();
        if (starts.Length != 1)
            issues.Add(Error(WorkflowPublicationIssueCodes.BoundaryStart, "A publishable graph must contain exactly one Start node."));
        if (ends.Length != 1)
            issues.Add(Error(WorkflowPublicationIssueCodes.BoundaryEnd, "A publishable graph must contain exactly one End node."));

        foreach (var start in starts.Where(start => edges.Any(edge => edge.Edge.TargetNodeId == start.Node.Id)))
        {
            issues.Add(Error(
                WorkflowPublicationIssueCodes.StartIncoming,
                "The Start node cannot have an incoming edge.",
                start.Node.Id));
        }
        foreach (var end in ends.Where(end => edges.Any(edge => edge.Edge.SourceNodeId == end.Node.Id)))
        {
            issues.Add(Error(
                WorkflowPublicationIssueCodes.EndOutgoing,
                "The End node cannot have an outgoing edge.",
                end.Node.Id));
        }

        foreach (var node in nodes.Values)
        {
            foreach (var output in node.Definition.Ports.Where(port =>
                         port.Direction == WorkflowPortDirection.Output))
            {
                var connected = edges.Where(edge =>
                        edge.Edge.SourceNodeId == node.Node.Id &&
                        string.Equals(edge.SourcePort.Key, output.Key, StringComparison.Ordinal))
                    .ToArray();
                if (string.Equals(output.Key, "success", StringComparison.Ordinal))
                {
                    if (connected.Length == 0)
                    {
                        issues.Add(Error(
                            WorkflowPublicationIssueCodes.DefaultPathMissing,
                            $"Node '{node.Node.Name}' is missing its success default path.",
                            node.Node.Id));
                    }
                    else if (connected.Length > 1)
                    {
                        issues.Add(Error(
                            WorkflowPublicationIssueCodes.DefaultPathAmbiguous,
                            $"Node '{node.Node.Name}' has more than one success default path.",
                            node.Node.Id));
                    }
                }
                else if (output.EdgeKind is WorkflowEdgeKind.Failure or WorkflowEdgeKind.Timeout or WorkflowEdgeKind.Cancelled)
                {
                    if (connected.Length == 0)
                    {
                        issues.Add(Warning(
                            WorkflowPublicationIssueCodes.ExceptionPathMissing,
                            $"Node '{node.Node.Name}' has no '{output.Key}' exception path.",
                            node.Node.Id));
                    }
                    else if (connected.Length > 1)
                    {
                        issues.Add(Error(
                            WorkflowPublicationIssueCodes.ExceptionPathAmbiguous,
                            $"Node '{node.Node.Name}' has more than one '{output.Key}' exception path.",
                            node.Node.Id));
                    }
                }
            }
        }

        if (starts.Length == 1)
        {
            var reachable = Traverse(starts[0].Node.Id, edges, reverse: false);
            foreach (var node in nodes.Values.Where(node => !reachable.Contains(node.Node.Id)))
            {
                issues.Add(Error(
                    WorkflowPublicationIssueCodes.NodeUnreachable,
                    $"Node '{node.Node.Name}' is not reachable from Start.",
                    node.Node.Id));
            }
        }
        if (ends.Length == 1)
        {
            var reachesEnd = Traverse(ends[0].Node.Id, edges, reverse: true);
            foreach (var node in nodes.Values.Where(node => !reachesEnd.Contains(node.Node.Id)))
            {
                issues.Add(Error(
                    WorkflowPublicationIssueCodes.EndUnreachable,
                    $"Node '{node.Node.Name}' cannot reach End.",
                    node.Node.Id));
            }
        }

        foreach (var nodeId in FindCyclicNodes(nodes.Keys, edges))
        {
            issues.Add(Error(
                WorkflowPublicationIssueCodes.CycleUnsupported,
                "Cycles are not supported by the G3 publication contract.",
                nodeId));
        }
    }

    private static HashSet<Guid> Traverse(Guid initial, IReadOnlyList<ResolvedEdge> edges, bool reverse)
    {
        var visited = new HashSet<Guid>();
        var pending = new Stack<Guid>();
        pending.Push(initial);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            var next = reverse
                ? edges.Where(edge => edge.Edge.TargetNodeId == current).Select(edge => edge.Edge.SourceNodeId)
                : edges.Where(edge => edge.Edge.SourceNodeId == current).Select(edge => edge.Edge.TargetNodeId);
            foreach (var nodeId in next) pending.Push(nodeId);
        }

        return visited;
    }

    private static IReadOnlyList<Guid> FindCyclicNodes(
        IEnumerable<Guid> nodeIds,
        IReadOnlyList<ResolvedEdge> edges)
    {
        var ids = nodeIds.ToArray();
        var indegree = ids.ToDictionary(id => id, _ => 0);
        foreach (var edge in edges)
            indegree[edge.Edge.TargetNodeId]++;
        var queue = new Queue<Guid>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        while (queue.TryDequeue(out var nodeId))
        {
            foreach (var target in edges.Where(edge => edge.Edge.SourceNodeId == nodeId)
                         .Select(edge => edge.Edge.TargetNodeId))
            {
                if (--indegree[target] == 0) queue.Enqueue(target);
            }
        }

        return indegree.Where(pair => pair.Value > 0).Select(pair => pair.Key).ToArray();
    }

    private static bool TryParse(
        WorkflowSchemaValueType type,
        string value,
        out decimal? numericValue)
    {
        numericValue = null;
        switch (type)
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

    private static bool SupportsProfile(WorkflowProfileSupport support, string productId) =>
        support.IsProfileIndependent ||
        support.SupportedProductIds.Contains(productId, StringComparer.OrdinalIgnoreCase);

    private static bool RequiresControl(DeviceCapabilityDefinition capability) =>
        capability.SafetyClassification is WorkflowSafetyClassification.ControlledDeviceAction or
            WorkflowSafetyClassification.RestrictedDeviceWrite;

    private static WorkflowValidationIssue Error(
        string code,
        string message,
        Guid? nodeId = null) => new()
    {
        Code = code,
        Message = message,
        Severity = WorkflowValidationSeverity.Error,
        NodeId = nodeId
    };

    private static WorkflowValidationIssue Warning(
        string code,
        string message,
        Guid? nodeId = null) => new()
    {
        Code = code,
        Message = message,
        Severity = WorkflowValidationSeverity.Warning,
        NodeId = nodeId
    };

    private static WorkflowValidationIssue FieldError(
        string code,
        string message,
        Guid nodeId,
        string configurationKey) => new()
    {
        Code = code,
        Message = message,
        Severity = WorkflowValidationSeverity.Error,
        NodeId = nodeId,
        ConfigurationKey = configurationKey
    };

    private static WorkflowValidationIssue EdgeError(
        string code,
        string message,
        WorkflowEdgeDefinition edge) => new()
    {
        Code = code,
        Message = message,
        Severity = WorkflowValidationSeverity.Error,
        NodeId = edge.SourceNodeId == Guid.Empty ? null : edge.SourceNodeId,
        EdgeId = edge.Id == Guid.Empty ? null : edge.Id
    };

    private sealed record ResolvedNode(WorkflowNode Node, WorkflowNodeTypeDefinition Definition);

    private sealed record ResolvedEdge(
        WorkflowEdgeDefinition Edge,
        ResolvedNode Source,
        ResolvedNode Target,
        WorkflowPortDefinition SourcePort,
        WorkflowPortDefinition TargetPort);
}
