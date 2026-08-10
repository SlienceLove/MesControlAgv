using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
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

    public IReadOnlyList<WorkflowDefinition> Load()
    {
        if (!File.Exists(FilePath)) return CreateDefaultWorkflows();

        try
        {
            var json = File.ReadAllText(FilePath);
            var workflows = JsonSerializer.Deserialize<List<WorkflowDefinition>>(json, JsonOptions);
            if (workflows is null || workflows.Count == 0) return CreateDefaultWorkflows();
            Normalize(workflows);
            return workflows;
        }
        catch (JsonException)
        {
            return CreateDefaultWorkflows();
        }
        catch (IOException)
        {
            return CreateDefaultWorkflows();
        }
    }

    public void Save(IEnumerable<WorkflowDefinition> workflows)
    {
        ArgumentNullException.ThrowIfNull(workflows);

        var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var snapshot = workflows.Select(CloneForStorage).ToList();
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        var temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, FilePath, overwrite: true);
    }

    public static IReadOnlyList<WorkflowDefinition> CreateDefaultWorkflows() =>
    [
        new WorkflowDefinition
        {
            Name = "标准搬运实验",
            Description = "从样品位取货，运输到液体前处理工作站并放货。",
            IsPreset = true,
            Nodes =
            [
                Node(WorkflowNodeType.Start, "开始", "启动实验流程", null, 0, 100, 1),
                Node(WorkflowNodeType.Move, "前往取货位", "AGV 前往起点站点", null, 180, 100, 2),
                Node(WorkflowNodeType.Pickup, "确认取货", "操作员确认已完成取货", null, 360, 100, 3),
                Node(WorkflowNodeType.Move, "前往放货位", "AGV 前往终点站点", null, 540, 100, 4),
                Node(WorkflowNodeType.Dropoff, "确认放货", "操作员确认已完成放货", null, 720, 100, 5),
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
                Node(WorkflowNodeType.Move, "前往取货位", "发送取货运输任务", null, 180, 260, 2),
                Node(WorkflowNodeType.Wait, "模拟超时", "等待并观察超时状态", null, 360, 260, 3),
                Node(WorkflowNodeType.Custom, "恢复并重试", "恢复 AGV 后重新执行任务", null, 540, 260, 4),
                Node(WorkflowNodeType.Dropoff, "确认放货", "确认恢复后的任务完成放货", null, 720, 260, 5),
                Node(WorkflowNodeType.End, "结束", "实验流程完成", null, 900, 260, 6)
            ]
        }
    ];

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

    private static WorkflowDefinition CloneForStorage(WorkflowDefinition workflow) => new()
    {
        Id = workflow.Id,
        Name = workflow.Name,
        Description = workflow.Description,
        IsPreset = workflow.IsPreset,
        PublishedVersion = workflow.PublishedVersion,
        Nodes = new System.Collections.ObjectModel.ObservableCollection<WorkflowNode>(workflow.Nodes.OrderBy(node => node.Order).Select(node => new WorkflowNode
        {
            Id = node.Id,
            Type = node.Type,
            Name = node.Name,
            Description = node.Description,
            TargetStation = node.TargetStation,
            X = node.X,
            Y = node.Y,
            Order = node.Order,
            Parameters = new System.Collections.ObjectModel.ObservableCollection<WorkflowNodeParameter>(node.Parameters.Select(parameter => new WorkflowNodeParameter
            {
                Name = parameter.Name,
                Value = parameter.Value,
                DataType = parameter.DataType,
                IsRequired = parameter.IsRequired
            })),
            NextNodeIds = new System.Collections.ObjectModel.ObservableCollection<Guid>(node.NextNodeIds)
        }))
    };

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

