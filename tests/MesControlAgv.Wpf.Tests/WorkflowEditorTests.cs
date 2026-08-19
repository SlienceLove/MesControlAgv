using ContractWorkflowEdgeDefinition = MesControlAgv.Contracts.Workflows.WorkflowEdgeDefinition;
using ContractWorkflowEdgeKind = MesControlAgv.Contracts.Workflows.WorkflowEdgeKind;
using ContractWorkflowNodeLayout = MesControlAgv.Contracts.Workflows.WorkflowNodeLayout;
using ContractWorkflowViewport = MesControlAgv.Contracts.Workflows.WorkflowCanvasViewport;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Workflows;

namespace MesControlAgv.Wpf.Tests;

public sealed class WorkflowEditorTests
{
    [Fact]
    public void Missing_store_loads_at_least_two_preset_workflows()
    {
        using var fixture = new TempWorkflowFile();
        var store = new WorkflowStore(fixture.Path);

        var workflows = store.Load();

        Assert.True(workflows.Count >= 2);
        Assert.All(workflows.Take(2), workflow =>
        {
            Assert.True(workflow.IsPreset);
            Assert.NotEmpty(workflow.Nodes);
        });
    }

    [Fact]
    public void Missing_store_rebuilds_presets_from_enabled_profile_station_types()
    {
        using var fixture = new TempWorkflowFile();
        var editor = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path));

        var changed = editor.ApplyProfileStations(
        [
            new DashboardStation(1, "Disabled sample", "SAMPLE_DISABLED", false, "Sample"),
            new DashboardStation(2, "Profile sample", "SAMPLE_PROFILE", true, "Sample"),
            new DashboardStation(3, "Profile preparation", "PREP_PROFILE", true, "Preparation"),
            new DashboardStation(4, "Fallback", "FALLBACK", true, "Dropoff")
        ]);

        Assert.True(changed);
        var targets = editor.Workflows
            .SelectMany(workflow => workflow.Nodes)
            .Where(node => node.TargetStation is not null)
            .Select(node => node.TargetStation)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new[] { "PREP_PROFILE", "SAMPLE_PROFILE" }, targets.OrderBy(value => value));
        Assert.DoesNotContain("SAMPLE_01", targets);
        Assert.DoesNotContain("ST_PREP_01", targets);
        Assert.False(editor.ApplyProfileStations(
        [
            new DashboardStation(10, "Other source", "OTHER_SOURCE", true),
            new DashboardStation(11, "Other target", "OTHER_TARGET", true)
        ]));
    }

    [Fact]
    public void Persisted_workflows_are_not_rewritten_from_profile_stations()
    {
        using var fixture = new TempWorkflowFile();
        var store = new WorkflowStore(fixture.Path);
        var workflow = new WorkflowDefinition { Name = "Persisted" };
        workflow.Nodes.Add(new WorkflowNode
        {
            Type = WorkflowNodeType.Move,
            Name = "Existing move",
            TargetStation = "SAVED_STATION",
            Order = 1
        });
        store.Save([workflow]);
        var editor = new WorkflowEditorViewModel(store);

        var changed = editor.ApplyProfileStations(
        [
            new DashboardStation(1, "Source", "PROFILE_SOURCE", true, "Pickup"),
            new DashboardStation(2, "Target", "PROFILE_TARGET", true, "Dropoff")
        ]);

        Assert.False(changed);
        Assert.Equal("SAVED_STATION", Assert.Single(Assert.Single(editor.Workflows).Nodes).TargetStation);
    }

    [Fact]
    public void Store_round_trips_workflow_and_node_properties_as_json()
    {
        using var fixture = new TempWorkflowFile();
        var store = new WorkflowStore(fixture.Path);
        var workflow = new WorkflowDefinition { Name = "温控实验", Description = "测试流程" };
        workflow.Nodes.Add(new WorkflowNode
        {
            Type = WorkflowNodeType.Wait,
            Name = "等待稳定",
            Description = "等待温度稳定",
            TargetStation = "ST_PREP_01",
            X = 123.5,
            Y = 45.25,
            Order = 1
        });

        store.Save([workflow]);
        var loaded = store.Load();
        var node = Assert.Single(Assert.Single(loaded).Nodes);

        Assert.Equal("温控实验", loaded[0].Name);
        Assert.Equal(WorkflowNodeType.Wait, node.Type);
        Assert.Equal("ST_PREP_01", node.TargetStation);
        Assert.Equal(123.5, node.X);
        Assert.Equal(45.25, node.Y);
        Assert.Contains("温控实验", File.ReadAllText(fixture.Path));
    }

    [Fact]
    public void Store_writes_graph_envelope_and_round_trips_explicit_edges_and_viewport()
    {
        using var fixture = new TempWorkflowFile();
        var store = new WorkflowStore(fixture.Path);
        var source = new WorkflowNode
        {
            Type = WorkflowNodeType.Start,
            Name = "Start",
            Order = 1,
            X = 12,
            Y = 34
        };
        var target = new WorkflowNode
        {
            Type = WorkflowNodeType.End,
            Name = "End",
            Order = 2,
            X = 320,
            Y = 34
        };
        source.NextNodeIds.Add(target.Id);
        var edge = new ContractWorkflowEdgeDefinition
        {
            SourceNodeId = source.Id,
            SourcePort = "success",
            TargetNodeId = target.Id,
            TargetPort = "in",
            Kind = ContractWorkflowEdgeKind.Success,
            Metadata = new Dictionary<string, string?> { ["legacyColor"] = "#34A853" }
        };
        var workflow = new WorkflowDefinition
        {
            Name = "Graph persisted",
            Nodes = [source, target],
            Edges = [edge],
            Layouts =
            [
                new ContractWorkflowNodeLayout { NodeId = source.Id, X = source.X, Y = source.Y },
                new ContractWorkflowNodeLayout { NodeId = target.Id, X = target.X, Y = target.Y }
            ],
            Viewport = new ContractWorkflowViewport { X = 50, Y = 75, Zoom = 1.5 }
        };

        store.Save([workflow]);
        var loaded = Assert.Single(store.Load());

        var loadedEdge = Assert.Single(loaded.Edges);
        Assert.Equal(edge.Id, loadedEdge.Id);
        Assert.Equal(edge.SourceNodeId, loadedEdge.SourceNodeId);
        Assert.Equal(edge.TargetNodeId, loadedEdge.TargetNodeId);
        Assert.Equal(edge.Kind, loadedEdge.Kind);
        Assert.Equal("#34A853", loadedEdge.Metadata["legacyColor"]);
        Assert.Equal(workflow.Layouts, loaded.Layouts);
        Assert.Equal(workflow.Viewport, loaded.Viewport);
        var json = File.ReadAllText(fixture.Path);
        Assert.Contains("mes.workflow.graph", json, StringComparison.Ordinal);
        Assert.StartsWith("{", json.TrimStart(), StringComparison.Ordinal);
    }

    [Fact]
    public void Store_imports_legacy_wpf_array_and_rewrites_it_as_graph_envelope()
    {
        using var fixture = new TempWorkflowFile();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fixture.Path)!);
        var start = Guid.NewGuid();
        var end = Guid.NewGuid();
        File.WriteAllText(
            fixture.Path,
            $$"""
            [
              {
                "id": "{{Guid.NewGuid()}}",
                "name": "Legacy local",
                "description": "old array",
                "nodes": [
                  { "id": "{{start}}", "type": 0, "name": "Start", "x": 10, "y": 20, "order": 1, "nextNodeIds": ["{{end}}"] },
                  { "id": "{{end}}", "type": 5, "name": "End", "x": 220, "y": 20, "order": 2, "nextNodeIds": [] }
                ]
              }
            ]
            """);
        var store = new WorkflowStore(fixture.Path);

        var imported = Assert.Single(store.Load());

        Assert.Equal("Legacy local", imported.Name);
        Assert.Single(imported.Edges);
        Assert.Equal(2, imported.Layouts.Count);
        store.Save([imported]);
        Assert.Contains("mes.workflow.graph", File.ReadAllText(fixture.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_experiment_editor_bridge_preserves_connections_and_metadata()
    {
        var editor = new ExperimentFlowEditorViewModel();
        var source = new ExperimentFlowNode
        {
            Id = Guid.NewGuid(),
            Type = "DataImport",
            Title = "Import",
            Description = "Load sample data",
            Location = new System.Windows.Point(10, 20)
        };
        var target = new ExperimentFlowNode
        {
            Id = Guid.NewGuid(),
            Type = "InstrumentOperation",
            Title = "Read D160",
            Location = new System.Windows.Point(300, 20)
        };
        var sourcePort = new ExperimentFlowConnector { Id = Guid.NewGuid(), IsInput = false, Node = source };
        var targetPort = new ExperimentFlowConnector { Id = Guid.NewGuid(), IsInput = true, Node = target };
        source.Output.Add(sourcePort);
        target.Input.Add(targetPort);
        editor.Nodes.Add(source);
        editor.Nodes.Add(target);
        editor.Connections.Add(new ExperimentFlowConnection
        {
            Id = Guid.NewGuid(),
            Source = sourcePort,
            Target = targetPort,
            Condition = "sample.ready",
            Color = "#34A853"
        });

        var graph = ExperimentFlowGraphAdapter.ToGraph(editor);
        var legacy = ExperimentFlowGraphAdapter.ToLegacyConfig(graph);

        Assert.Equal("DataImport", legacy.Nodes.Single(node => node.Id == source.Id).Type);
        var connection = Assert.Single(legacy.Connections);
        Assert.Equal("sample.ready", connection.Condition);
        Assert.Equal("#34A853", connection.Color);
        Assert.Equal(10, legacy.Nodes.Single(node => node.Id == source.Id).X);
    }

    [Fact]
    public void Editor_supports_create_copy_delete_and_save()
    {
        using var fixture = new TempWorkflowFile();
        var viewModel = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path));
        var initialCount = viewModel.Workflows.Count;

        viewModel.NewWorkflowCommand.Execute(null);
        Assert.Equal(initialCount + 1, viewModel.Workflows.Count);
        Assert.Equal("新实验流程", viewModel.SelectedWorkflow!.Name);

        viewModel.CopyWorkflowCommand.Execute(null);
        Assert.Equal(initialCount + 2, viewModel.Workflows.Count);
        Assert.EndsWith("副本", viewModel.SelectedWorkflow!.Name);

        viewModel.DeleteWorkflowCommand.Execute(null);
        Assert.Equal(initialCount + 1, viewModel.Workflows.Count);

        viewModel.SaveCommand.Execute(null);
        Assert.True(File.Exists(fixture.Path));
        Assert.NotEmpty(new WorkflowStore(fixture.Path).Load());
    }

    [Fact]
    public void Editor_supports_add_delete_and_reorder_nodes()
    {
        using var fixture = new TempWorkflowFile();
        var viewModel = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path));
        var workflow = viewModel.SelectedWorkflow!;
        var initialCount = workflow.Nodes.Count;

        viewModel.AddNodeCommand.Execute(null);
        Assert.NotNull(viewModel.SelectedNode);
        var added = viewModel.SelectedNode!;
        Assert.Equal(initialCount + 1, workflow.Nodes.Count);
        Assert.Equal(initialCount + 1, added.Order);
        Assert.True(viewModel.MoveNodeLeftCommand.CanExecute(null));

        var previousOrder = added.Order;
        viewModel.MoveNodeLeftCommand.Execute(null);
        Assert.Equal(previousOrder - 1, added.Order);
        Assert.True(viewModel.MoveNodeRightCommand.CanExecute(null));

        viewModel.DeleteNodeCommand.Execute(null);
        Assert.Equal(initialCount, workflow.Nodes.Count);
    }

    [Fact]
    public void Editor_creates_runtime_parameters_for_wait_and_instrument_nodes()
    {
        using var fixture = new TempWorkflowFile();
        var viewModel = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path));

        viewModel.AddNodeAt(WorkflowNodeType.Wait, 100, 100);
        var wait = viewModel.SelectedNode!;
        var duration = Assert.Single(wait.Parameters);
        Assert.Equal("durationSeconds", duration.Name);
        Assert.Equal("1", duration.Value);

        viewModel.AddNodeAt(WorkflowNodeType.InstrumentOperation, 300, 100);
        var instrument = viewModel.SelectedNode!;
        Assert.Equal(new[] { "instrumentId", "operation" }, instrument.Parameters.Select(parameter => parameter.Name));
        Assert.All(instrument.Parameters, parameter => Assert.True(parameter.IsRequired));

        viewModel.AddParameterCommand.Execute(null);
        Assert.Equal(3, instrument.Parameters.Count);
        Assert.NotNull(viewModel.SelectedParameter);
        viewModel.DeleteParameterCommand.Execute(null);
        Assert.Equal(2, instrument.Parameters.Count);
    }

    [Fact]
    public void Selected_workflow_and_node_raise_property_notifications()
    {
        using var fixture = new TempWorkflowFile();
        var viewModel = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path));
        var properties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => properties.Add(args.PropertyName);

        viewModel.SelectedWorkflow = viewModel.Workflows[1];
        viewModel.SelectedNode = viewModel.SelectedWorkflow.Nodes.Last();

        Assert.Contains(nameof(WorkflowEditorViewModel.SelectedWorkflow), properties);
        Assert.Contains(nameof(WorkflowEditorViewModel.Nodes), properties);
        Assert.Contains(nameof(WorkflowEditorViewModel.SelectedNode), properties);
    }

    private sealed class TempWorkflowFile : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MesControlAgv.WorkflowTests", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "workflows.json");

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}

