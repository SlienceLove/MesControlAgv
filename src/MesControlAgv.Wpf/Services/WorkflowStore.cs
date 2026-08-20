using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using ContractWorkflowGraphDocument = MesControlAgv.Contracts.Workflows.WorkflowGraphDocument;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Wpf.Workflows;

namespace MesControlAgv.Wpf.Services;

public sealed class WorkflowStore
{
    private readonly WorkflowDocumentImporter _importer = new();

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

    public WorkflowImportReport? LastLoadReport { get; private set; }

    public IReadOnlyList<ContractWorkflowGraphDocument> LoadDocuments()
    {
        if (!File.Exists(FilePath))
        {
            LastLoadUsedDefaults = true;
            LastLoadReport = null;
            return CreateDefaultWorkflows().Select(WorkflowDocumentMapper.ToGraph).ToArray();
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var result = _importer.Import(json, FilePath);
            LastLoadReport = result.Report.RequiresUserAttention ? result.Report : null;
            if (!result.CanImport)
            {
                LastLoadUsedDefaults = true;
                return CreateDefaultWorkflows().Select(WorkflowDocumentMapper.ToGraph).ToArray();
            }

            LastLoadUsedDefaults = false;
            return result.Documents;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LastLoadUsedDefaults = true;
            LastLoadReport = new WorkflowImportReport
            {
                SourceName = FilePath,
                Issues =
                [
                    new WorkflowImportIssue(
                        WorkflowImportIssueSeverity.Error,
                        "IMPORT_READ_FAILED",
                        exception.Message,
                        FilePath)
                ]
            };
            return CreateDefaultWorkflows().Select(WorkflowDocumentMapper.ToGraph).ToArray();
        }
    }

    public IReadOnlyList<WorkflowDefinition> Load() =>
        LoadDocuments().Select(WorkflowDocumentMapper.FromGraph).ToArray();

    public WorkflowImportResult ImportFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return _importer.Import(File.ReadAllText(filePath), filePath);
    }

    public WorkflowImportResult ImportJson(string json, string? sourceName = null) =>
        _importer.Import(json, sourceName);

    public void Save(IEnumerable<WorkflowDefinition> workflows)
    {
        ArgumentNullException.ThrowIfNull(workflows);

        SaveDocuments(workflows.Select(WorkflowDocumentMapper.ToGraph));
    }

    public void SaveDocuments(IEnumerable<ContractWorkflowGraphDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var snapshot = documents.Select(document => document with
        {
            SchemaVersion = ContractWorkflowGraphDocument.CurrentSchemaVersion
        }).ToList();
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
        LastLoadReport = null;
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
            CreateLinearWorkflow(
                "标准搬运实验",
                "从配置取货位取货，运输到配置放货位并放货。",
            [
                Node(WorkflowNodeType.Start, "开始", "启动实验流程", null, 0, 100, 1),
                Node(WorkflowNodeType.Move, "前往取货位", "AGV 前往配置取货位", sourceStationId, 180, 100, 2),
                Node(WorkflowNodeType.Pickup, "确认取货", "操作员确认已完成取货", sourceStationId, 360, 100, 3),
                Node(WorkflowNodeType.Move, "前往放货位", "AGV 前往配置放货位", targetStationId, 540, 100, 4),
                Node(WorkflowNodeType.Dropoff, "确认放货", "操作员确认已完成放货", targetStationId, 720, 100, 5),
                Node(WorkflowNodeType.End, "结束", "实验流程完成", null, 900, 100, 6)
            ]),
            CreateLinearWorkflow(
                "故障恢复实验",
                "模拟运输超时、恢复后重试并完成放货。",
            [
                Node(WorkflowNodeType.Start, "开始", "启动故障恢复实验", null, 0, 260, 1),
                Node(WorkflowNodeType.Move, "前往取货位", "发送取货运输任务", sourceStationId, 180, 260, 2),
                Node(WorkflowNodeType.Wait, "模拟超时", "等待并观察超时状态", null, 360, 260, 3),
                Node(WorkflowNodeType.Custom, "恢复并重试", "恢复 AGV 后重新执行任务", null, 540, 260, 4),
                Node(WorkflowNodeType.Dropoff, "确认放货", "确认恢复后的任务完成放货", targetStationId, 720, 260, 5),
                Node(WorkflowNodeType.End, "结束", "实验流程完成", null, 900, 260, 6)
            ])
        ];
    }

    private static WorkflowDefinition CreateLinearWorkflow(
        string name,
        string description,
        IReadOnlyList<WorkflowNode> nodes)
    {
        var edges = new List<MesControlAgv.Contracts.Workflows.WorkflowEdgeDefinition>();
        for (var index = 0; index < nodes.Count - 1; index++)
        {
            var source = nodes[index];
            var target = nodes[index + 1];
            source.NextNodeIds.Add(target.Id);
            edges.Add(CreateSuccessEdge(source.Id, target.Id));
        }

        return new WorkflowDefinition
        {
            Name = name,
            Description = description,
            IsPreset = true,
            Nodes = new System.Collections.ObjectModel.ObservableCollection<WorkflowNode>(nodes),
            Edges = new System.Collections.ObjectModel.ObservableCollection<MesControlAgv.Contracts.Workflows.WorkflowEdgeDefinition>(edges)
        };
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

    private static MesControlAgv.Contracts.Workflows.WorkflowEdgeDefinition CreateSuccessEdge(
        Guid sourceNodeId,
        Guid targetNodeId) => new()
    {
        SourceNodeId = sourceNodeId,
        SourcePort = "success",
        TargetNodeId = targetNodeId,
        TargetPort = "in",
        Kind = MesControlAgv.Contracts.Workflows.WorkflowEdgeKind.Success
    };

}

internal sealed class WorkflowGraphStorageEnvelope
{
    public const string CurrentFormat = "mes.workflow.graph";

    public string Format { get; set; } = CurrentFormat;
    public int SchemaVersion { get; set; } = ContractWorkflowGraphDocument.CurrentSchemaVersion;
    public List<ContractWorkflowGraphDocument> Workflows { get; set; } = [];
}
