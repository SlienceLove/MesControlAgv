using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using ContractWorkflowGraphDocument = MesControlAgv.Contracts.Workflows.WorkflowGraphDocument;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Wpf.Workflows;

namespace MesControlAgv.Wpf.Services;

public sealed class WorkflowStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public WorkflowStore(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MesControlAgv", "workflows.json")
            : filePath;
    }

    public string FilePath { get; }

    public bool LastLoadUsedDefaults { get; private set; }

    public IReadOnlyList<WorkflowDefinition> Load()
    {
        if (!File.Exists(FilePath))
        {
            LastLoadUsedDefaults = true;
            return CreateDefaultWorkflows();
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var workflows = DeserializeStoredWorkflows(json);
            if (workflows.Count == 0)
            {
                LastLoadUsedDefaults = true;
                return CreateDefaultWorkflows();
            }
            Normalize(workflows);
            LastLoadUsedDefaults = false;
            return workflows;
        }
        catch (JsonException)
        {
            LastLoadUsedDefaults = true;
            return CreateDefaultWorkflows();
        }
        catch (IOException)
        {
            LastLoadUsedDefaults = true;
            return CreateDefaultWorkflows();
        }
    }

    public void Save(IEnumerable<WorkflowDefinition> workflows)
    {
        ArgumentNullException.ThrowIfNull(workflows);

        var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var snapshot = workflows
            .Select(WorkflowDocumentMapper.ToGraph)
            .ToList();
        var envelope = new WorkflowGraphStorageEnvelope
        {
            Format = WorkflowGraphStorageEnvelope.CurrentFormat,
            SchemaVersion = ContractWorkflowGraphDocument.CurrentSchemaVersion,
            Workflows = snapshot
        };
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        var temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, FilePath, overwrite: true);
        LastLoadUsedDefaults = false;
    }

    public static IReadOnlyList<WorkflowDefinition> CreateDefaultWorkflows() =>
        CreateDefaultWorkflows("SAMPLE_01", "ST_PREP_01");

    public static IReadOnlyList<WorkflowDefinition> CreateDefaultWorkflows(
        string sourceStationId,
        string targetStationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStationId);

        return
        [
        new WorkflowDefinition
        {
            Name = "标准搬运实验",
            Description = "从配置取货位取货，运输到配置放货位并放货。",
            IsPreset = true,
            Nodes =
            [
                Node(WorkflowNodeType.Start, "开始", "启动实验流程", null, 0, 100, 1),
                Node(WorkflowNodeType.Move, "前往取货位", "AGV 前往配置取货位", sourceStationId, 180, 100, 2),
                Node(WorkflowNodeType.Pickup, "确认取货", "操作员确认已完成取货", sourceStationId, 360, 100, 3),
                Node(WorkflowNodeType.Move, "前往放货位", "AGV 前往配置放货位", targetStationId, 540, 100, 4),
                Node(WorkflowNodeType.Dropoff, "确认放货", "操作员确认已完成放货", targetStationId, 720, 100, 5),
                Node(WorkflowNodeType.End, "结束", "实验流程完成", null, 900, 100, 6)
            ]
        },
        new WorkflowDefinition
        {
            Name = "故障恢复实验",
            Description = "模拟运输超时、恢复后重试并完成放货。",
            IsPreset = true,
            Nodes =
            [
                Node(WorkflowNodeType.Start, "开始", "启动故障恢复实验", null, 0, 260, 1),
                Node(WorkflowNodeType.Move, "前往取货位", "发送取货运输任务", sourceStationId, 180, 260, 2),
                Node(WorkflowNodeType.Wait, "模拟超时", "等待并观察超时状态", null, 360, 260, 3),
                Node(WorkflowNodeType.Custom, "恢复并重试", "恢复 AGV 后重新执行任务", null, 540, 260, 4),
                Node(WorkflowNodeType.Dropoff, "确认放货", "确认恢复后的任务完成放货", targetStationId, 720, 260, 5),
                Node(WorkflowNodeType.End, "结束", "实验流程完成", null, 900, 260, 6)
            ]
        }
        ];
    }

    private static WorkflowNode Node(WorkflowNodeType type, string name, string description, string? targetStation, double x, double y, int order) => new()
    {
        Type = type,
        Name = name,
        Description = description,
        TargetStation = targetStation,
        X = x,
        Y = y,
        Order = order
    };

    private static IReadOnlyList<WorkflowDefinition> DeserializeStoredWorkflows(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            var envelope = JsonSerializer.Deserialize<WorkflowGraphStorageEnvelope>(json, JsonOptions);
            if (envelope?.Workflows is null)
            {
                return [];
            }

            return envelope.Workflows
                .Select(WorkflowDocumentMapper.FromGraph)
                .ToArray();
        }

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        if (LooksLikeGraphArray(document.RootElement))
        {
            var graphDocuments = JsonSerializer.Deserialize<List<ContractWorkflowGraphDocument>>(json, JsonOptions) ?? [];
            return graphDocuments.Select(WorkflowDocumentMapper.FromGraph).ToArray();
        }

        // Legacy WPF JSON is an import-only shape. It is immediately converted
        // through the graph mapper so subsequent saves use the new envelope.
        var legacy = JsonSerializer.Deserialize<List<WorkflowDefinition>>(json, JsonOptions) ?? [];
        return legacy
            .Select(workflow => WorkflowDocumentMapper.FromGraph(WorkflowDocumentMapper.ToGraph(workflow)))
            .ToArray();
    }

    private static bool LooksLikeGraphArray(JsonElement root)
    {
        var first = root.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object) return false;
        if (first.TryGetProperty("layouts", out _) ||
            first.TryGetProperty("viewport", out _) ||
            first.TryGetProperty("schemaVersion", out _))
        {
            return true;
        }

        if (!first.TryGetProperty("nodes", out var nodes) ||
            nodes.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var node = nodes.EnumerateArray().FirstOrDefault();
        return node.ValueKind == JsonValueKind.Object && node.TryGetProperty("nodeTypeId", out _);
    }

    private static void Normalize(IEnumerable<WorkflowDefinition> workflows)
    {
        foreach (var workflow in workflows)
        {
            workflow.Nodes = new System.Collections.ObjectModel.ObservableCollection<WorkflowNode>(workflow.Nodes.OrderBy(node => node.Order));
            var order = 1;
            foreach (var node in workflow.Nodes) node.Order = order++;
        }
    }
}

internal sealed class WorkflowGraphStorageEnvelope
{
    public const string CurrentFormat = "mes.workflow.graph";

    public string Format { get; set; } = CurrentFormat;
    public int SchemaVersion { get; set; } = ContractWorkflowGraphDocument.CurrentSchemaVersion;
    public List<ContractWorkflowGraphDocument> Workflows { get; set; } = [];
}
