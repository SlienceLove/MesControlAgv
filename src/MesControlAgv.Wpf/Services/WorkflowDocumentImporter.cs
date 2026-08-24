using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Workflows;
using WpfWorkflowDefinition = MesControlAgv.Wpf.Workflows.WorkflowDefinition;

namespace MesControlAgv.Wpf.Services;

public enum WorkflowImportSourceFormat
{
    Unknown,
    CurrentGraphEnvelope,
    LegacyGraphEnvelope,
    GraphDocument,
    GraphDocumentArray,
    LegacyWpfArray,
    LegacyExperimentFlow
}

public enum WorkflowImportIssueSeverity
{
    Information,
    Warning,
    Error
}

public sealed record WorkflowImportIssue(
    WorkflowImportIssueSeverity Severity,
    string Code,
    string Message,
    string? Location = null);

public sealed record WorkflowImportReport
{
    public string SourceName { get; init; } = "JSON";
    public WorkflowImportSourceFormat SourceFormat { get; init; }
    public int? SourceSchemaVersion { get; init; }
    public int WorkflowCount { get; init; }
    public int NodeCount { get; init; }
    public int EdgeCount { get; init; }
    public int MigratedEdgeCount { get; init; }
    public IReadOnlyList<WorkflowImportIssue> Issues { get; init; } = [];

    public bool HasErrors => Issues.Any(issue => issue.Severity == WorkflowImportIssueSeverity.Error);
    public bool HasWarnings => Issues.Any(issue => issue.Severity == WorkflowImportIssueSeverity.Warning);
    public bool IsLegacy => SourceFormat is not WorkflowImportSourceFormat.CurrentGraphEnvelope;
    public bool RequiresUserAttention => IsLegacy || Issues.Count > 0 || MigratedEdgeCount > 0;

    public string Summary =>
        $"{FormatDisplayName(SourceFormat)}：{WorkflowCount} 个流程，{NodeCount} 个节点，{EdgeCount} 条边" +
        (MigratedEdgeCount > 0 ? $"；迁移生成 {MigratedEdgeCount} 条顺序边" : string.Empty) +
        (HasErrors ? "；存在阻断错误" : HasWarnings ? "；存在需确认项" : "；转换完成");

    public string Details => Issues.Count == 0
        ? "未发现不支持或被丢弃的字段。"
        : string.Join(Environment.NewLine, Issues.Select(issue =>
            $"[{SeverityDisplayName(issue.Severity)}] {issue.Code}：{issue.Message}" +
            (string.IsNullOrWhiteSpace(issue.Location) ? string.Empty : $"（{issue.Location}）")));

    private static string FormatDisplayName(WorkflowImportSourceFormat format) => format switch
    {
        WorkflowImportSourceFormat.Unknown => "无法识别的流程文件",
        WorkflowImportSourceFormat.CurrentGraphEnvelope => "当前图文档",
        WorkflowImportSourceFormat.LegacyGraphEnvelope => "旧版图文档",
        WorkflowImportSourceFormat.GraphDocument => "单个图文档",
        WorkflowImportSourceFormat.GraphDocumentArray => "图文档数组",
        WorkflowImportSourceFormat.LegacyWpfArray => "旧 WPF 流程",
        WorkflowImportSourceFormat.LegacyExperimentFlow => "旧实验流程设计器",
        _ => "流程文件"
    };

    private static string SeverityDisplayName(WorkflowImportIssueSeverity severity) => severity switch
    {
        WorkflowImportIssueSeverity.Information => "信息",
        WorkflowImportIssueSeverity.Warning => "警告",
        WorkflowImportIssueSeverity.Error => "错误",
        _ => "信息"
    };
}

public sealed record WorkflowImportResult
{
    public IReadOnlyList<WorkflowGraphDocument> Documents { get; init; } = [];
    public WorkflowImportReport Report { get; init; } = new();
    public bool CanImport => Documents.Count > 0 && !Report.HasErrors;
}

/// <summary>
/// Structured compatibility boundary for workflow JSON. It recognizes every
/// historical WPF shape, upgrades supported graph schemas, and records any
/// synthesized or unsupported content instead of silently discarding it.
/// </summary>
public sealed class WorkflowDocumentImporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public WorkflowImportResult Import(string json, string? sourceName = null)
    {
        var source = string.IsNullOrWhiteSpace(sourceName) ? "JSON" : sourceName.Trim();
        if (string.IsNullOrWhiteSpace(json))
        {
            return Failure(source, "IMPORT_EMPTY", "文件为空，未找到可导入的流程。", null);
        }

        try
        {
            using var parsed = JsonDocument.Parse(json);
            return parsed.RootElement.ValueKind switch
            {
                JsonValueKind.Object => ImportObject(parsed.RootElement, source),
                JsonValueKind.Array => ImportArray(parsed.RootElement, source),
                _ => Failure(source, "IMPORT_ROOT_INVALID", "JSON 根元素必须是对象或数组。", "$" )
            };
        }
        catch (JsonException exception)
        {
            return Failure(source, "IMPORT_JSON_INVALID", exception.Message, "$" );
        }
        catch (NotSupportedException exception)
        {
            return Failure(source, "IMPORT_VALUE_UNSUPPORTED", exception.Message, "$" );
        }
    }

    private static WorkflowImportResult ImportObject(JsonElement root, string source)
    {
        if (TryGetProperty(root, "workflows", out var workflows))
        {
            if (workflows.ValueKind != JsonValueKind.Array)
            {
                return Failure(source, "IMPORT_WORKFLOWS_INVALID", "workflows 必须是数组。", "$.workflows");
            }

            var schemaVersion = ReadSchemaVersion(root, 1);
            var format = schemaVersion < WorkflowGraphDocument.CurrentSchemaVersion
                ? WorkflowImportSourceFormat.LegacyGraphEnvelope
                : WorkflowImportSourceFormat.CurrentGraphEnvelope;
            var context = new ImportContext(source, format, schemaVersion);
            ReportUnknownProperties(root, GraphEnvelopeProperties, "$", context);
            if (schemaVersion > WorkflowGraphDocument.CurrentSchemaVersion)
            {
                context.Error(
                    "GRAPH_SCHEMA_UNSUPPORTED",
                    $"图文档信封 schema v{schemaVersion} 高于当前支持的 v{WorkflowGraphDocument.CurrentSchemaVersion}，为避免字段丢失已拒绝导入。",
                    "$.schemaVersion");
                return context.Complete();
            }
            if (TryGetProperty(root, "format", out var formatValue) &&
                (formatValue.ValueKind != JsonValueKind.String ||
                 !string.Equals(formatValue.GetString(), WorkflowGraphStorageEnvelope.CurrentFormat, StringComparison.Ordinal)))
            {
                context.Warning(
                    "GRAPH_FORMAT_UNEXPECTED",
                    $"格式标识不是“{WorkflowGraphStorageEnvelope.CurrentFormat}”，已按可识别字段尝试转换。",
                    "$.format");
            }
            ImportGraphElements(workflows, schemaVersion, "$.workflows", context);
            return context.Complete();
        }

        if (TryGetProperty(root, "connections", out _))
        {
            return ImportLegacyExperiment(root, source);
        }

        if (LooksLikeGraphDocument(root))
        {
            var schemaVersion = ReadSchemaVersion(root, 1);
            var context = new ImportContext(
                source,
                WorkflowImportSourceFormat.GraphDocument,
                schemaVersion);
            ImportGraphElement(root, schemaVersion, "$", context);
            return context.Complete();
        }

        if (TryGetProperty(root, "nodes", out _))
        {
            var context = new ImportContext(source, WorkflowImportSourceFormat.LegacyWpfArray, 1);
            ImportLegacyWpfElements([root], "$", context);
            return context.Complete();
        }

        return Failure(source, "IMPORT_FORMAT_UNKNOWN", "无法识别流程 JSON 格式。", "$" );
    }

    private static WorkflowImportResult ImportArray(JsonElement root, string source)
    {
        if (root.GetArrayLength() == 0)
        {
            return Failure(source, "IMPORT_NO_WORKFLOWS", "数组中没有流程。", "$" );
        }

        if (LooksLikeGraphArray(root))
        {
            var first = root.EnumerateArray().First();
            var schemaVersion = ReadSchemaVersion(first, 1);
            var context = new ImportContext(source, WorkflowImportSourceFormat.GraphDocumentArray, schemaVersion);
            ImportGraphElements(root, schemaVersion, "$", context);
            return context.Complete();
        }

        var legacyContext = new ImportContext(source, WorkflowImportSourceFormat.LegacyWpfArray, 1);
        ImportLegacyWpfElements(root.EnumerateArray(), "$", legacyContext);
        return legacyContext.Complete();
    }

    private static WorkflowImportResult ImportLegacyExperiment(JsonElement root, string source)
    {
        var context = new ImportContext(source, WorkflowImportSourceFormat.LegacyExperimentFlow, null);
        ReportUnknownProperties(root, LegacyExperimentProperties, "$", context);
        if (TryGetProperty(root, "nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind == JsonValueKind.Object)
                    ReportUnknownProperties(node, LegacyExperimentNodeProperties, $"$.nodes[{index}]", context);
                index++;
            }
        }
        if (TryGetProperty(root, "connections", out var connections) && connections.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var connection in connections.EnumerateArray())
            {
                if (connection.ValueKind == JsonValueKind.Object)
                    ReportUnknownProperties(connection, LegacyExperimentConnectionProperties, $"$.connections[{index}]", context);
                index++;
            }
        }

        var config = JsonSerializer.Deserialize<ExperimentFlowConfigDto>(root.GetRawText(), JsonOptions);
        if (config is null)
        {
            context.Error("IMPORT_LEGACY_EMPTY", "旧实验流程配置为空。", "$" );
            return context.Complete();
        }

        config.Nodes ??= [];
        config.Connections ??= [];

        ValidateLegacyExperiment(config, context);
        var graph = ExperimentFlowGraphAdapter.FromLegacyConfig(
            config,
            Path.GetFileNameWithoutExtension(source));
        context.AddDocument(graph, config.Connections.Count);
        context.Information(
            "LEGACY_EXPERIMENT_CONVERTED",
            $"已将 {config.Nodes.Count} 个旧节点和 {config.Connections.Count} 条旧连接转换为图文档；连接颜色保存在元数据中。",
            "$" );
        return context.Complete();
    }

    private static void ValidateLegacyExperiment(
        ExperimentFlowConfigDto config,
        ImportContext context)
    {
        var duplicateNodeIds = config.Nodes
            .GroupBy(node => node.Id)
            .Where(group => group.Key == Guid.Empty || group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        foreach (var id in duplicateNodeIds)
        {
            context.Error(
                "LEGACY_NODE_ID_INVALID",
                id == Guid.Empty ? "旧格式包含空节点 ID。" : $"旧格式包含重复节点 ID {id}。",
                "$.nodes");
        }

        var nodeIds = config.Nodes.Select(node => node.Id).ToHashSet();
        var duplicateEdgeIds = config.Connections
            .GroupBy(connection => connection.Id)
            .Where(group => group.Key == Guid.Empty || group.Count() > 1)
            .ToArray();
        foreach (var group in duplicateEdgeIds)
        {
            context.Error(
                "LEGACY_EDGE_ID_INVALID",
                group.Key == Guid.Empty ? "旧格式包含空连接 ID。" : $"旧格式包含重复连接 ID {group.Key}。",
                "$.connections");
        }
        foreach (var connection in config.Connections)
        {
            if (!nodeIds.Contains(connection.SourceNodeId) || !nodeIds.Contains(connection.TargetNodeId))
            {
                context.Error(
                    "LEGACY_EDGE_DANGLING",
                    $"连接 {connection.Id} 引用了不存在的源节点或目标节点。",
                    "$.connections");
            }
        }

        var knownTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Start", "Move", "Wait", "Pickup", "Dropoff", "End", "InstrumentOperation", "Custom"
        };
        foreach (var node in config.Nodes.Where(node => !knownTypes.Contains(node.Type ?? string.Empty)))
        {
            context.Warning(
                "LEGACY_NODE_TYPE_REQUIRES_REVIEW",
                $"节点“{node.Title}”的旧类型“{node.Type}”已保留为兼容自定义类型，发布前需要人工确认语义。",
                $"$.nodes[{config.Nodes.IndexOf(node)}].type");
        }
    }

    private static void ImportLegacyWpfElements(
        IEnumerable<JsonElement> elements,
        string path,
        ImportContext context)
    {
        var index = 0;
        foreach (var element in elements)
        {
            var location = path == "$" ? $"$[{index}]" : path;
            if (element.ValueKind != JsonValueKind.Object)
            {
                context.Error("IMPORT_WORKFLOW_INVALID", "流程项必须是对象。", location);
                index++;
                continue;
            }

            ReportUnknownProperties(element, LegacyWpfWorkflowProperties, location, context);
            ReportLegacyWpfNestedProperties(element, location, context);
            var workflow = JsonSerializer.Deserialize<WpfWorkflowDefinition>(element.GetRawText(), JsonOptions);
            if (workflow is null)
            {
                context.Error("IMPORT_WORKFLOW_EMPTY", "流程项无法反序列化。", location);
                index++;
                continue;
            }

            workflow.Nodes ??= [];
            workflow.Edges ??= [];
            workflow.Layouts ??= [];
            workflow.Viewport ??= new WorkflowCanvasViewport();
            foreach (var node in workflow.Nodes)
                node.NextNodeIds ??= [];

            var legacyNodeIds = workflow.Nodes.Select(node => node.Id).ToHashSet();
            foreach (var node in workflow.Nodes)
            {
                foreach (var targetId in node.NextNodeIds.Where(targetId => !legacyNodeIds.Contains(targetId)))
                {
                    context.Error(
                        "LEGACY_NEXT_NODE_DANGLING",
                        $"节点 {node.Id} 的 NextNodeIds 引用了不存在的目标节点 {targetId}。",
                        $"{location}.nodes");
                }
            }

            var migratedEdges = 0;
            var hasAnyConnection = workflow.Edges.Count > 0 ||
                workflow.Nodes.Any(node => node.NextNodeIds.Count > 0);
            if (!hasAnyConnection && workflow.Nodes.Count > 1)
            {
                var orderedNodes = workflow.Nodes.OrderBy(node => node.Order).ToArray();
                for (var nodeIndex = 0; nodeIndex < orderedNodes.Length - 1; nodeIndex++)
                    orderedNodes[nodeIndex].NextNodeIds.Add(orderedNodes[nodeIndex + 1].Id);
                migratedEdges = orderedNodes.Length - 1;
            }

            var graph = WorkflowDocumentMapper.ToGraph(workflow) with
            {
                SchemaVersion = WorkflowGraphDocument.CurrentSchemaVersion
            };
            context.AddDocument(NormalizeGraph(graph, location, context), migratedEdges);
            if (migratedEdges > 0)
            {
                context.Information(
                    "LEGACY_SEQUENTIAL_EDGES_CREATED",
                    $"流程“{workflow.Name}”原本没有连接，已按节点顺序生成 {migratedEdges} 条成功边。",
                    location);
            }
            index++;
        }
    }

    private static void ImportGraphElements(
        JsonElement elements,
        int fallbackSchemaVersion,
        string path,
        ImportContext context)
    {
        var index = 0;
        foreach (var element in elements.EnumerateArray())
        {
            ImportGraphElement(element, fallbackSchemaVersion, $"{path}[{index}]", context);
            index++;
        }
    }

    private static void ImportGraphElement(
        JsonElement element,
        int fallbackSchemaVersion,
        string path,
        ImportContext context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            context.Error("IMPORT_GRAPH_INVALID", "图文档项必须是对象。", path);
            return;
        }

        ReportUnknownProperties(element, GraphDocumentProperties, path, context);
        ReportGraphNestedProperties(element, path, context);
        var sourceSchemaVersion = ReadSchemaVersion(element, fallbackSchemaVersion);
        if (sourceSchemaVersion > WorkflowGraphDocument.CurrentSchemaVersion)
        {
            context.Error(
                "GRAPH_SCHEMA_UNSUPPORTED",
                $"图文档 schema v{sourceSchemaVersion} 高于当前支持的 v{WorkflowGraphDocument.CurrentSchemaVersion}，为避免字段丢失已拒绝导入。",
                $"{path}.schemaVersion");
            return;
        }

        var graph = JsonSerializer.Deserialize<WorkflowGraphDocument>(element.GetRawText(), JsonOptions);
        if (graph is null)
        {
            context.Error("IMPORT_GRAPH_EMPTY", "图文档项无法反序列化。", path);
            return;
        }

        var nodes = graph.Nodes ?? [];
        var edges = graph.Edges ?? [];
        var migratedEdges = 0;
        if (sourceSchemaVersion < WorkflowGraphDocument.ExplicitEdgesSchemaVersion &&
            edges.Count == 0 &&
            nodes.Count > 1)
        {
            edges = BuildSequentialEdges(nodes.Select(node => node.Id));
            migratedEdges = edges.Count;
            context.Information(
                "GRAPH_SEQUENTIAL_EDGES_CREATED",
                $"v{sourceSchemaVersion} 流程“{graph.Name}”原本没有连接，已按节点顺序生成 {migratedEdges} 条成功边。",
                path);
        }

        var migrated = graph with
        {
            SchemaVersion = WorkflowGraphDocument.CurrentSchemaVersion,
            Nodes = nodes,
            Edges = edges
        };
        context.AddDocument(NormalizeGraph(migrated, path, context), migratedEdges);
    }

    private static WorkflowGraphDocument NormalizeGraph(
        WorkflowGraphDocument graph,
        string path,
        ImportContext context)
    {
        var nodes = graph.Nodes ?? [];
        var duplicateNodeIds = nodes
            .GroupBy(node => node.Id)
            .Where(group => group.Key == Guid.Empty || group.Count() > 1)
            .ToArray();
        foreach (var group in duplicateNodeIds)
        {
            context.Error(
                "GRAPH_NODE_ID_INVALID",
                group.Key == Guid.Empty ? "图文档包含空节点 ID。" : $"图文档包含重复节点 ID {group.Key}。",
                $"{path}.nodes");
        }

        var normalizedNodes = nodes.Select(node =>
        {
            var type = WorkflowGraphNodeTypeIds.ToContractType(node.NodeTypeId);
            var nodeTypeId = string.IsNullOrWhiteSpace(node.NodeTypeId)
                ? WorkflowGraphNodeTypeIds.For(type)
                : node.NodeTypeId;
            var ports = node.Ports is { Count: > 0 }
                ? node.Ports
                : WorkflowGraphContractAdapter.CreatePorts(type);
            if (node.Ports is not { Count: > 0 })
            {
                context.Information(
                    "GRAPH_PORTS_SYNTHESIZED",
                    $"节点“{node.Name}”缺少端口定义，已根据节点类型生成兼容端口。",
                    $"{path}.nodes");
            }
            return node with
            {
                NodeTypeId = nodeTypeId,
                SchemaVersion = string.IsNullOrWhiteSpace(node.SchemaVersion) ? "1.0" : node.SchemaVersion,
                Ports = ports,
                Configuration = node.Configuration ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            };
        }).ToArray();

        var nodeIds = normalizedNodes.Select(node => node.Id).ToHashSet();
        var edges = graph.Edges ?? [];
        foreach (var group in edges.GroupBy(edge => edge.Id).Where(group => group.Key == Guid.Empty || group.Count() > 1))
        {
            context.Error(
                "GRAPH_EDGE_ID_INVALID",
                group.Key == Guid.Empty ? "图文档包含空边 ID。" : $"图文档包含重复边 ID {group.Key}。",
                $"{path}.edges");
        }
        foreach (var edge in edges)
        {
            if (!nodeIds.Contains(edge.SourceNodeId) || !nodeIds.Contains(edge.TargetNodeId))
            {
                context.Error(
                    "GRAPH_EDGE_DANGLING",
                    $"边 {edge.Id} 引用了不存在的源节点或目标节点。",
                    $"{path}.edges");
            }
        }

        var sourceLayouts = graph.Layouts ?? [];
        foreach (var layout in sourceLayouts.Where(layout => !nodeIds.Contains(layout.NodeId)))
        {
            context.Error(
                "GRAPH_LAYOUT_DANGLING",
                $"布局引用了不存在的节点 {layout.NodeId}。",
                $"{path}.layouts");
        }
        foreach (var group in sourceLayouts.GroupBy(layout => layout.NodeId).Where(group => group.Count() > 1))
        {
            context.Error(
                "GRAPH_LAYOUT_DUPLICATE",
                $"节点 {group.Key} 包含 {group.Count()} 条重复布局记录。",
                $"{path}.layouts");
        }

        var layouts = sourceLayouts
            .Where(layout => nodeIds.Contains(layout.NodeId))
            .GroupBy(layout => layout.NodeId)
            .ToDictionary(group => group.Key, group => group.Last());
        foreach (var node in normalizedNodes)
        {
            if (!layouts.ContainsKey(node.Id))
                layouts[node.Id] = new WorkflowNodeLayout { NodeId = node.Id };
        }

        return graph with
        {
            Nodes = normalizedNodes,
            Edges = edges,
            Layouts = layouts.Values.ToArray(),
            Viewport = graph.Viewport ?? new WorkflowCanvasViewport()
        };
    }

    private static IReadOnlyList<WorkflowEdgeDefinition> BuildSequentialEdges(IEnumerable<Guid> nodeIds)
    {
        var ids = nodeIds.ToArray();
        return ids.Zip(ids.Skip(1), (source, target) => new WorkflowEdgeDefinition
        {
            SourceNodeId = source,
            SourcePort = "success",
            TargetNodeId = target,
            TargetPort = "in",
            Kind = WorkflowEdgeKind.Success
        }).ToArray();
    }

    private static void ReportUnknownProperties(
        JsonElement element,
        IReadOnlySet<string> knownProperties,
        string path,
        ImportContext context)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (knownProperties.Contains(property.Name)) continue;
            context.Warning(
                "UNSUPPORTED_FIELD_REPORTED",
                $"字段“{property.Name}”不属于该格式的已知字段，转换器未使用其内容。",
                $"{path}.{property.Name}");
        }
    }

    private static void ReportGraphNestedProperties(
        JsonElement workflow,
        string path,
        ImportContext context)
    {
        ReportObjectArrayProperties(workflow, "nodes", GraphNodeProperties, path, context, (node, nodePath) =>
            ReportObjectArrayProperties(node, "ports", GraphPortProperties, nodePath, context));
        ReportObjectArrayProperties(workflow, "edges", GraphEdgeProperties, path, context, (edge, edgePath) =>
            ReportObjectProperties(
                edge,
                "conditionExpression",
                GraphConditionExpressionProperties,
                edgePath,
                context));
        ReportObjectArrayProperties(workflow, "layouts", GraphLayoutProperties, path, context);
        ReportObjectProperties(workflow, "viewport", GraphViewportProperties, path, context);
    }

    private static void ReportLegacyWpfNestedProperties(
        JsonElement workflow,
        string path,
        ImportContext context)
    {
        ReportObjectArrayProperties(workflow, "nodes", LegacyWpfNodeProperties, path, context, (node, nodePath) =>
        {
            ReportObjectArrayProperties(node, "parameters", WorkflowParameterProperties, nodePath, context);
            ReportObjectArrayProperties(node, "ports", GraphPortProperties, nodePath, context);
        });
        ReportObjectArrayProperties(workflow, "edges", GraphEdgeProperties, path, context, (edge, edgePath) =>
            ReportObjectProperties(
                edge,
                "conditionExpression",
                GraphConditionExpressionProperties,
                edgePath,
                context));
        ReportObjectArrayProperties(workflow, "layouts", GraphLayoutProperties, path, context);
        ReportObjectProperties(workflow, "viewport", GraphViewportProperties, path, context);
    }

    private static void ReportObjectArrayProperties(
        JsonElement owner,
        string propertyName,
        IReadOnlySet<string> knownProperties,
        string ownerPath,
        ImportContext context,
        Action<JsonElement, string>? inspectItem = null)
    {
        if (!TryGetProperty(owner, propertyName, out var values) || values.ValueKind != JsonValueKind.Array)
            return;
        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            var valuePath = $"{ownerPath}.{propertyName}[{index}]";
            if (value.ValueKind == JsonValueKind.Object)
            {
                ReportUnknownProperties(value, knownProperties, valuePath, context);
                inspectItem?.Invoke(value, valuePath);
            }
            index++;
        }
    }

    private static void ReportObjectProperties(
        JsonElement owner,
        string propertyName,
        IReadOnlySet<string> knownProperties,
        string ownerPath,
        ImportContext context)
    {
        if (TryGetProperty(owner, propertyName, out var value) && value.ValueKind == JsonValueKind.Object)
            ReportUnknownProperties(value, knownProperties, $"{ownerPath}.{propertyName}", context);
    }

    private static int ReadSchemaVersion(JsonElement element, int fallback)
    {
        if (!TryGetProperty(element, "schemaVersion", out var schemaVersion) ||
            schemaVersion.ValueKind != JsonValueKind.Number ||
            !schemaVersion.TryGetInt32(out var value) ||
            value <= 0)
        {
            return fallback;
        }
        return value;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static bool LooksLikeGraphDocument(JsonElement element)
    {
        if (!TryGetProperty(element, "nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            return TryGetProperty(element, "schemaVersion", out _);
        var first = nodes.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProperty(first, "nodeTypeId", out _)) return true;
            if (TryGetProperty(first, "type", out _) || TryGetProperty(first, "graphNodeTypeId", out _)) return false;
        }
        return TryGetProperty(element, "schemaVersion", out _);
    }

    private static bool LooksLikeGraphArray(JsonElement root)
    {
        var first = root.EnumerateArray().FirstOrDefault();
        return first.ValueKind == JsonValueKind.Object && LooksLikeGraphDocument(first);
    }

    private static WorkflowImportResult Failure(
        string source,
        string code,
        string message,
        string? location) => new()
    {
        Report = new WorkflowImportReport
        {
            SourceName = source,
            Issues = [new WorkflowImportIssue(WorkflowImportIssueSeverity.Error, code, message, location)]
        }
    };

    private sealed class ImportContext(
        string sourceName,
        WorkflowImportSourceFormat sourceFormat,
        int? sourceSchemaVersion)
    {
        private readonly List<WorkflowGraphDocument> _documents = [];
        private readonly List<WorkflowImportIssue> _issues = [];
        private readonly HashSet<Guid> _workflowIds = [];
        private int _migratedEdgeCount;

        public void AddDocument(WorkflowGraphDocument document, int migratedEdgeCount)
        {
            if (document.Id == Guid.Empty)
            {
                Error("GRAPH_WORKFLOW_ID_INVALID", "流程 ID 不能为空。", "$" );
            }
            else if (!_workflowIds.Add(document.Id))
            {
                Error(
                    "GRAPH_WORKFLOW_ID_DUPLICATE",
                    $"导入文件包含重复流程 ID {document.Id}，为避免静默覆盖已拒绝整批导入。",
                    "$" );
            }
            _documents.Add(document);
            _migratedEdgeCount += migratedEdgeCount;
        }

        public void Information(string code, string message, string? location = null) =>
            AddIssue(WorkflowImportIssueSeverity.Information, code, message, location);

        public void Warning(string code, string message, string? location = null) =>
            AddIssue(WorkflowImportIssueSeverity.Warning, code, message, location);

        public void Error(string code, string message, string? location = null) =>
            AddIssue(WorkflowImportIssueSeverity.Error, code, message, location);

        private void AddIssue(
            WorkflowImportIssueSeverity severity,
            string code,
            string message,
            string? location) =>
            _issues.Add(new WorkflowImportIssue(severity, code, message, location));

        public WorkflowImportResult Complete()
        {
            if (_documents.Count == 0 && _issues.All(issue => issue.Severity != WorkflowImportIssueSeverity.Error))
                Error("IMPORT_NO_WORKFLOWS", "文件中没有可导入的流程。", "$" );

            return new WorkflowImportResult
            {
                Documents = _documents.ToArray(),
                Report = new WorkflowImportReport
                {
                    SourceName = sourceName,
                    SourceFormat = sourceFormat,
                    SourceSchemaVersion = sourceSchemaVersion,
                    WorkflowCount = _documents.Count,
                    NodeCount = _documents.Sum(document => document.Nodes.Count),
                    EdgeCount = _documents.Sum(document => document.Edges.Count),
                    MigratedEdgeCount = _migratedEdgeCount,
                    Issues = _issues.ToArray()
                }
            };
        }
    }

    private static readonly HashSet<string> GraphEnvelopeProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "format", "schemaVersion", "workflows"
    };

    private static readonly HashSet<string> GraphDocumentProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "schemaVersion", "name", "description", "isPreset", "publishedVersion",
        "nodes", "edges", "layouts", "viewport"
    };

    private static readonly HashSet<string> LegacyWpfWorkflowProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "schemaVersion", "name", "description", "isPreset", "publishedVersion",
        "nodes", "edges", "layouts", "viewport"
    };

    private static readonly HashSet<string> LegacyExperimentProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "nodes", "connections"
    };

    private static readonly HashSet<string> LegacyExperimentNodeProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "title", "description", "type", "x", "y"
    };

    private static readonly HashSet<string> LegacyExperimentConnectionProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "sourceNodeId", "targetNodeId", "condition", "color"
    };

    private static readonly HashSet<string> GraphNodeProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "nodeTypeId", "schemaVersion", "name", "description", "ports", "configuration"
    };

    private static readonly HashSet<string> LegacyWpfNodeProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "graphNodeTypeId", "schemaVersion", "type", "typeDescription", "name", "description",
        "targetStation", "x", "y", "order", "parameters", "nextNodeIds", "ports", "configuration"
    };

    private static readonly HashSet<string> WorkflowParameterProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "value", "dataType", "isRequired"
    };

    private static readonly HashSet<string> GraphPortProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "key", "displayName", "direction", "dataType", "cardinality", "edgeKind"
    };

    private static readonly HashSet<string> GraphEdgeProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "sourceNodeId", "sourcePort", "targetNodeId", "targetPort", "kind", "condition",
        "conditionExpression", "priority", "metadata"
    };

    private static readonly HashSet<string> GraphConditionExpressionProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "schemaVersion", "source", "sourceNodeId", "sourceKey", "valueType", "operator", "compareValue",
        "missingValueBehavior"
    };

    private static readonly HashSet<string> GraphLayoutProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "nodeId", "x", "y", "width", "height"
    };

    private static readonly HashSet<string> GraphViewportProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "x", "y", "zoom"
    };
}
