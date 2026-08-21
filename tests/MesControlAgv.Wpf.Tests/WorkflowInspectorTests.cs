using System.Collections.ObjectModel;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Workflows;

using WpfWorkflowDefinition = MesControlAgv.Wpf.Workflows.WorkflowDefinition;
using WpfWorkflowNode = MesControlAgv.Wpf.Workflows.WorkflowNode;
using WpfWorkflowNodeType = MesControlAgv.Wpf.Workflows.WorkflowNodeType;

namespace MesControlAgv.Wpf.Tests;

public sealed class WorkflowInspectorTests
{
    [Fact]
    public void Catalog_palette_creates_and_round_trips_all_seven_typed_nodes()
    {
        using var fixture = new TempWorkflowFile();
        var editor = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path));
        var catalog = BuiltInWorkflowCatalog.Create();
        var expectedTypeIds = new[]
        {
            WorkflowGraphNodeTypeIds.Start,
            WorkflowGraphNodeTypeIds.End,
            WorkflowGraphNodeTypeIds.Move,
            WorkflowGraphNodeTypeIds.TimedWait,
            WorkflowGraphNodeTypeIds.ManualConfirmation,
            WorkflowGraphNodeTypeIds.InstrumentReadStatus,
            WorkflowGraphNodeTypeIds.InstrumentWaitUntilStable
        };

        Assert.Equal(expectedTypeIds, editor.NodeTypeOptions.Select(option => option.NodeTypeId));
        Assert.All(editor.NodeTypeOptions, option => Assert.True(option.IsAvailable, option.UnavailableReason));

        var snapshots = new Dictionary<Guid, (string TypeId, string Schema, double X, double Y, Dictionary<string, string?> Configuration)>();
        for (var index = 0; index < editor.NodeTypeOptions.Count; index++)
        {
            var option = editor.NodeTypeOptions[index];
            editor.AddNodeAt(option.NodeTypeId, 120 + index * 25, 240 + index * 15);
            var node = Assert.IsType<WpfWorkflowNode>(editor.SelectedNode);
            var definition = Assert.IsType<WorkflowNodeTypeDefinition>(
                catalog.NodeTypes.GetLatest(option.NodeTypeId));

            Assert.Equal(definition.NodeTypeId, node.GraphNodeTypeId);
            Assert.Equal(definition.SchemaVersion, node.SchemaVersion);
            Assert.Equal(definition.Ports, node.Ports);
            Assert.True(editor.Inspector.IsTyped);
            Assert.False(editor.Inspector.RequiresMigration);
            Assert.Equal(
                definition.ConfigurationSchema.Fields.Select(field => field.Key),
                editor.Inspector.Fields.Select(field => field.Key));

            ConfigureAllFields(editor.Inspector.Fields);
            foreach (var field in definition.ConfigurationSchema.Fields.Where(field => field.DefaultValue is not null))
                Assert.True(node.Configuration.ContainsKey(field.Key));

            snapshots[node.Id] = (
                node.GraphNodeTypeId,
                node.SchemaVersion,
                node.X,
                node.Y,
                new Dictionary<string, string?>(node.Configuration, StringComparer.OrdinalIgnoreCase));
        }

        var move = editor.SelectedWorkflow!.Nodes.Single(node =>
            node.GraphNodeTypeId == WorkflowGraphNodeTypeIds.Move && snapshots.ContainsKey(node.Id));
        editor.SelectedNode = move;
        Assert.Equal(
            WorkflowInspectorEditorKind.Selection,
            editor.Inspector.Fields.Single(field => field.Key == WorkflowNodeConfigurationKeys.TargetStation).EditorKind);
        Assert.Equal(
            WorkflowInspectorEditorKind.Number,
            editor.Inspector.Fields.Single(field => field.Key == WorkflowNodeConfigurationKeys.TimeoutSeconds).EditorKind);

        var manual = editor.SelectedWorkflow.Nodes.Single(node =>
            node.GraphNodeTypeId == WorkflowGraphNodeTypeIds.ManualConfirmation && snapshots.ContainsKey(node.Id));
        editor.SelectedNode = manual;
        Assert.Equal(
            WorkflowInspectorEditorKind.Boolean,
            editor.Inspector.Fields.Single(field => field.Key == WorkflowNodeConfigurationKeys.RequireComment).EditorKind);

        editor.SaveCommand.Execute(null);
        var reloaded = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path));
        var reloadedNodes = reloaded.Workflows.SelectMany(workflow => workflow.Nodes).ToDictionary(node => node.Id);
        foreach (var (nodeId, snapshot) in snapshots)
        {
            var node = reloadedNodes[nodeId];
            Assert.Equal(snapshot.TypeId, node.GraphNodeTypeId);
            Assert.Equal(snapshot.Schema, node.SchemaVersion);
            Assert.Equal(snapshot.X, node.X);
            Assert.Equal(snapshot.Y, node.Y);
            Assert.Equal(snapshot.Configuration.Count, node.Configuration.Count);
            foreach (var pair in snapshot.Configuration)
                Assert.Equal(pair.Value, node.Configuration[pair.Key]);
        }
    }

    [Fact]
    public void Reference_fields_only_commit_available_profile_and_catalog_options()
    {
        using var fixture = new TempWorkflowFile();
        var editor = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path));
        editor.ApplyProfileStations(
        [
            new DashboardStation(1, "Active A", "ACTIVE_A", true, "Sample"),
            new DashboardStation(2, "Active B", "ACTIVE_B", true, "Preparation"),
            new DashboardStation(3, "Disabled", "DISABLED", false, "Dropoff")
        ]);
        editor.AddNodeAt(WorkflowGraphNodeTypeIds.Move, 100, 200);
        var node = editor.SelectedNode!;
        var station = editor.Inspector.Fields.Single(field =>
            field.Key == WorkflowNodeConfigurationKeys.TargetStation);

        Assert.Contains(station.Options, option => option.Value == "ACTIVE_A" && option.IsAvailable);
        Assert.Contains(station.Options, option => option.Value == "DISABLED" && !option.IsAvailable);
        station.Value = "MISSING";
        Assert.DoesNotContain(WorkflowNodeConfigurationKeys.TargetStation, node.Configuration.Keys);
        station.Value = "DISABLED";
        Assert.DoesNotContain(WorkflowNodeConfigurationKeys.TargetStation, node.Configuration.Keys);

        station.Value = "ACTIVE_A";
        Assert.Equal("ACTIVE_A", node.Configuration[WorkflowNodeConfigurationKeys.TargetStation]);
        Assert.Equal("ACTIVE_A", node.TargetStation);

        var device = editor.Inspector.Fields.Single(field => field.Key == WorkflowNodeConfigurationKeys.DeviceId);
        device.Value = "MISSING-AGV";
        Assert.DoesNotContain(WorkflowNodeConfigurationKeys.DeviceId, node.Configuration.Keys);
        device.Value = "AGV-01";
        Assert.Equal("AGV-01", node.Configuration[WorkflowNodeConfigurationKeys.DeviceId]);
    }

    [Fact]
    public void Unknown_fields_and_future_node_schema_are_read_only_and_lossless()
    {
        using var fixture = new TempWorkflowFile();
        var catalog = BuiltInWorkflowCatalog.Create();
        var moveDefinition = catalog.NodeTypes.GetLatest(WorkflowGraphNodeTypeIds.Move)!;
        var waitDefinition = catalog.NodeTypes.GetLatest(WorkflowGraphNodeTypeIds.TimedWait)!;
        var known = new WpfWorkflowNode
        {
            Type = WpfWorkflowNodeType.Move,
            GraphNodeTypeId = moveDefinition.NodeTypeId,
            SchemaVersion = moveDefinition.SchemaVersion,
            Name = "Known with extension",
            Order = 1,
            Ports = new ObservableCollection<WorkflowPortDefinition>(moveDefinition.Ports),
            Configuration = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [WorkflowNodeConfigurationKeys.TargetStation] = "SAMPLE_01",
                [WorkflowNodeConfigurationKeys.TimeoutSeconds] = "300",
                [WorkflowNodeConfigurationKeys.RetryCount] = "0",
                ["futureOption"] = "keep-known"
            }
        };
        var future = new WpfWorkflowNode
        {
            Type = WpfWorkflowNodeType.Wait,
            GraphNodeTypeId = waitDefinition.NodeTypeId,
            SchemaVersion = "9.0",
            Name = "Future schema",
            Order = 2,
            Ports = new ObservableCollection<WorkflowPortDefinition>(waitDefinition.Ports),
            Configuration = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [WorkflowRuntimeParameterNames.WaitDurationSeconds] = "12",
                ["futureOption"] = "keep-future"
            }
        };
        var workflow = new WpfWorkflowDefinition { Name = "Compatibility", Nodes = [known, future] };
        var store = new WorkflowStore(fixture.Path);
        store.Save([workflow]);

        var editor = new WorkflowEditorViewModel(store);
        editor.SelectedNode = editor.SelectedWorkflow!.Nodes.Single(node => node.Id == known.Id);
        Assert.True(editor.Inspector.IsTyped);
        Assert.True(editor.Inspector.RequiresMigration);
        var knownUnknown = editor.Inspector.Fields.Single(field => field.Key == "futureOption");
        Assert.True(knownUnknown.IsUnknown);
        Assert.True(knownUnknown.IsReadOnly);
        Assert.Equal("keep-known", knownUnknown.Value);

        editor.SelectedNode = editor.SelectedWorkflow.Nodes.Single(node => node.Id == future.Id);
        Assert.False(editor.Inspector.IsTyped);
        Assert.True(editor.Inspector.RequiresMigration);
        Assert.All(editor.Inspector.Fields, field => Assert.True(field.IsReadOnly));
        Assert.Equal("12", editor.Inspector.Fields.Single(field =>
            field.Key == WorkflowRuntimeParameterNames.WaitDurationSeconds).Value);

        editor.SaveCommand.Execute(null);
        var documents = new WorkflowStore(fixture.Path).LoadDocuments();
        var savedKnown = Assert.Single(documents).Nodes.Single(node => node.Id == known.Id);
        var savedFuture = Assert.Single(documents).Nodes.Single(node => node.Id == future.Id);
        Assert.Equal("keep-known", savedKnown.Configuration["futureOption"]);
        Assert.Equal("12", savedFuture.Configuration[WorkflowRuntimeParameterNames.WaitDurationSeconds]);
        Assert.Equal("keep-future", savedFuture.Configuration["futureOption"]);
        Assert.Equal("9.0", savedFuture.SchemaVersion);
    }

    [Fact]
    public void Switching_nodes_rebuilds_fields_without_leaking_values()
    {
        using var fixture = new TempWorkflowFile();
        var editor = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path));
        editor.AddNodeAt(WorkflowGraphNodeTypeIds.ManualConfirmation, 100, 100);
        var manual = editor.SelectedNode!;
        var prompt = editor.Inspector.Fields.Single(field => field.Key == WorkflowNodeConfigurationKeys.Prompt);
        prompt.Value = "Manual node prompt";

        editor.AddNodeAt(WorkflowGraphNodeTypeIds.InstrumentWaitUntilStable, 300, 100);
        var stable = editor.SelectedNode!;
        Assert.DoesNotContain(editor.Inspector.Fields, field => field.Key == WorkflowNodeConfigurationKeys.Prompt);
        Assert.Contains(editor.Inspector.Fields, field => field.Key == WorkflowNodeConfigurationKeys.Measurement);

        editor.SelectedNode = manual;
        Assert.Equal(
            "Manual node prompt",
            editor.Inspector.Fields.Single(field => field.Key == WorkflowNodeConfigurationKeys.Prompt).Value);
        Assert.DoesNotContain(editor.Inspector.Fields, field => field.Key == WorkflowNodeConfigurationKeys.Measurement);

        editor.SelectedNode = stable;
        Assert.DoesNotContain(editor.Inspector.Fields, field => field.Key == WorkflowNodeConfigurationKeys.Prompt);
    }

    [Fact]
    public void Inspector_edits_participate_in_canvas_undo_and_redo_history()
    {
        using var fixture = new TempWorkflowFile();
        var editor = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path));
        editor.AddNodeAt(WorkflowGraphNodeTypeIds.TimedWait, 615, 225);
        var nodeId = editor.SelectedNode!.Id;
        var duration = editor.Inspector.Fields.Single(field =>
            field.Key == WorkflowRuntimeParameterNames.WaitDurationSeconds);
        Assert.Equal("1", duration.Value);

        duration.Value = "12";
        Assert.Equal("12", editor.SelectedGraphDocument!.Nodes.Single(node => node.Id == nodeId)
            .Configuration[WorkflowRuntimeParameterNames.WaitDurationSeconds]);

        var canvas = Assert.IsType<WorkflowCanvasSpikeViewModel>(editor.CanvasViewModel);
        canvas.UndoCommand.Execute(null);
        Assert.Equal("1", editor.SelectedGraphDocument!.Nodes.Single(node => node.Id == nodeId)
            .Configuration[WorkflowRuntimeParameterNames.WaitDurationSeconds]);
        Assert.Equal("1", editor.Inspector.Fields.Single(field =>
            field.Key == WorkflowRuntimeParameterNames.WaitDurationSeconds).Value);

        canvas.RedoCommand.Execute(null);
        Assert.Equal("12", editor.SelectedGraphDocument!.Nodes.Single(node => node.Id == nodeId)
            .Configuration[WorkflowRuntimeParameterNames.WaitDurationSeconds]);
        Assert.Equal("12", editor.Inspector.Fields.Single(field =>
            field.Key == WorkflowRuntimeParameterNames.WaitDurationSeconds).Value);
    }

    private static void ConfigureAllFields(IEnumerable<WorkflowInspectorFieldViewModel> fields)
    {
        foreach (var field in fields.Where(field => !field.IsReadOnly))
        {
            field.Value = field.EditorKind switch
            {
                WorkflowInspectorEditorKind.Selection => field.Options
                    .First(option => option.IsAvailable && !string.IsNullOrWhiteSpace(option.Value)).Value,
                WorkflowInspectorEditorKind.Boolean => "true",
                WorkflowInspectorEditorKind.Number => field.Value ??
                    (field.Minimum ?? 1m).ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => $"configured-{field.Key}"
            };
        }
    }

    private sealed class TempWorkflowFile : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "MesControlAgv.WorkflowInspectorTests",
            Guid.NewGuid().ToString("N"));

        public string Path => System.IO.Path.Combine(_directory, "workflows.json");

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
