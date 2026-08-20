using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class WorkflowCanvasSpikeViewModelTests
{
    [Fact]
    public void Starts_with_the_linear_in_memory_dataset()
    {
        var viewModel = new WorkflowCanvasSpikeViewModel();

        Assert.Equal(20, viewModel.NodeCount);
        Assert.Equal(19, viewModel.EdgeCount);
        Assert.Equal("已加载 19 条连接。", viewModel.LastConnectionMessage);
        Assert.Contains("不连接 MES", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_dataset_rebuilds_the_canvas_projection()
    {
        var viewModel = new WorkflowCanvasSpikeViewModel();

        viewModel.LoadDatasetCommand.Execute("branching");
        Assert.Equal(60, viewModel.NodeCount);
        Assert.Equal(90, viewModel.EdgeCount);

        viewModel.LoadDatasetCommand.Execute("large");
        Assert.Equal(200, viewModel.NodeCount);
        Assert.Equal(350, viewModel.EdgeCount);
    }

    [Fact]
    public void Runtime_mode_locks_editing_without_removing_runtime_overlay()
    {
        var viewModel = new WorkflowCanvasSpikeViewModel();

        viewModel.CanvasMode = MesControlAgv.Contracts.Workflows.WorkflowCanvasMode.Runtime;

        Assert.False(viewModel.IsEditing);
        Assert.True(viewModel.IsReadOnly);
        Assert.All(viewModel.Nodes, node => Assert.False(node.IsEditing));
        Assert.Contains(viewModel.Nodes, node => !string.IsNullOrWhiteSpace(node.RuntimeState));
    }

    [Fact]
    public void Copy_and_paste_from_the_spike_rewrites_and_adds_nodes()
    {
        var viewModel = new WorkflowCanvasSpikeViewModel();
        foreach (var node in viewModel.Nodes.Take(5)) viewModel.SelectedNodes.Add(node);

        viewModel.CopyCommand.Execute(null);
        viewModel.PasteCommand.Execute(null);

        Assert.Equal(25, viewModel.NodeCount);
        Assert.Equal(23, viewModel.EdgeCount);
        Assert.Contains("ID 已重写", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_only_mode_rejects_connection_completion()
    {
        var viewModel = new WorkflowCanvasSpikeViewModel();
        var source = viewModel.Nodes[0].OutputPorts.First();
        var target = viewModel.Nodes[1].InputPorts.First();
        viewModel.CanvasMode = MesControlAgv.Contracts.Workflows.WorkflowCanvasMode.ReadOnly;

        viewModel.CompleteConnectionCommand.Execute(Tuple.Create<object, object>(source, target));

        Assert.Equal("当前画布为只读，不能创建连接。", viewModel.LastConnectionMessage);
        Assert.Equal(19, viewModel.EdgeCount);
    }

    [Fact]
    public void Connection_completion_preserves_the_source_port_edge_semantic()
    {
        var viewModel = new WorkflowCanvasSpikeViewModel();
        var source = viewModel.Nodes[0].OutputPorts.Single(port => port.Key == "failure");
        var target = viewModel.Nodes[1].InputPorts.Single();

        viewModel.CompleteConnectionCommand.Execute(Tuple.Create<object, object>(source, target));

        Assert.Equal(20, viewModel.EdgeCount);
        Assert.Equal(MesControlAgv.Contracts.Workflows.WorkflowEdgeKind.Failure, viewModel.Connections.Last().Kind);
        Assert.Equal("失败", viewModel.Connections.Last().Label);
    }

    [Fact]
    public void In_memory_json_round_trip_preserves_graph_and_layout_without_a_device_call()
    {
        var viewModel = new WorkflowCanvasSpikeViewModel();
        viewModel.LoadDatasetCommand.Execute("branching");
        var expectedNodes = viewModel.Nodes.Select(node => (node.Id, node.Name, node.Location)).ToArray();
        var expectedEdges = viewModel.Connections.Select(edge => (edge.Id, edge.SourceNode.Id, edge.TargetNode.Id, edge.Kind, edge.Label)).ToArray();

        viewModel.RoundTripCommand.Execute(null);

        Assert.Equal(expectedNodes, viewModel.Nodes.Select(node => (node.Id, node.Name, node.Location)).ToArray());
        Assert.Equal(expectedEdges, viewModel.Connections.Select(edge => (edge.Id, edge.SourceNode.Id, edge.TargetNode.Id, edge.Kind, edge.Label)).ToArray());
        Assert.Contains("未写入本地或 MES", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Main_editor_callback_only_runs_for_document_changes()
    {
        var callbackCount = 0;
        var viewModel = new WorkflowCanvasSpikeViewModel(null, _ => callbackCount++);

        viewModel.SelectNode(viewModel.Nodes[0].Id);
        viewModel.CopyCommand.Execute(null);
        viewModel.RoundTripCommand.Execute(null);

        Assert.Equal(0, callbackCount);

        var moved = viewModel.Nodes[0];
        moved.Location = new System.Windows.Point(moved.Location.X + 25, moved.Location.Y + 15);
        viewModel.CommitNodeLocationsCommand.Execute(null);

        Assert.Equal(1, callbackCount);
        var layout = viewModel.Document.Layouts.Single(item => item.NodeId == moved.Id);
        Assert.Equal(moved.Location.X, layout.X);
        Assert.Equal(moved.Location.Y, layout.Y);
    }

    [Fact]
    public void Selection_changes_are_exposed_without_changing_the_document()
    {
        var callbackCount = 0;
        var viewModel = new WorkflowCanvasSpikeViewModel(null, _ => callbackCount++);
        Guid? selectedNodeId = null;
        viewModel.SelectionChanged += (_, nodeId) => selectedNodeId = nodeId;
        var expected = viewModel.Nodes[3].Id;

        viewModel.SelectNode(expected);

        Assert.Equal(expected, selectedNodeId);
        Assert.Equal(expected, viewModel.SelectedNode?.Id);
        Assert.Equal(0, callbackCount);
    }

    [Fact]
    public void Viewport_change_is_committed_without_creating_an_undo_entry()
    {
        var documentCallbackCount = 0;
        MesControlAgv.Contracts.Workflows.WorkflowCanvasViewport? committed = null;
        var viewModel = new WorkflowCanvasSpikeViewModel(null, _ => documentCallbackCount++);
        viewModel.ViewportChanged += (_, viewport) => committed = viewport;

        viewModel.UpdateViewport(120, 80, 1.4);

        Assert.Equal(new MesControlAgv.Contracts.Workflows.WorkflowCanvasViewport
        {
            X = 120,
            Y = 80,
            Zoom = 1.4
        }, committed);
        Assert.Equal(0, documentCallbackCount);
        Assert.False(viewModel.UndoCommand.CanExecute(null));
    }
}
