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

