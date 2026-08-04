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

