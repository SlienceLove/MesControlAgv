using System.Diagnostics;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Workflows;

namespace MesControlAgv.WorkflowContract.Tests;

public sealed class WorkflowGraphEditorTests
{
    [Fact]
    public void Spike_samples_have_the_declared_sizes()
    {
        Assert.Equal((20, 19), (WorkflowSpikeSamples.CreateLinear().Nodes.Count, WorkflowSpikeSamples.CreateLinear().Edges.Count));
        Assert.Equal((60, 90), (WorkflowSpikeSamples.CreateBranching().Nodes.Count, WorkflowSpikeSamples.CreateBranching().Edges.Count));
        Assert.Equal((200, 350), (WorkflowSpikeSamples.CreateLarge().Nodes.Count, WorkflowSpikeSamples.CreateLarge().Edges.Count));
    }

    [Fact]
    public void Spike_sample_edges_match_their_source_port_semantics()
    {
        foreach (var document in new[]
        {
            WorkflowSpikeSamples.CreateLinear(),
            WorkflowSpikeSamples.CreateBranching(),
            WorkflowSpikeSamples.CreateLarge(),
            WorkflowSpikeSamples.CreateCopyProbe()
        })
        {
            foreach (var edge in document.Edges)
            {
                var source = document.Nodes.Single(node => node.Id == edge.SourceNodeId);
                var port = source.Ports.Single(item => string.Equals(item.Key, edge.SourcePort, StringComparison.OrdinalIgnoreCase));
                Assert.Equal(edge.Kind, port.EdgeKind);
            }
        }
    }

    [Fact]
    public void Connection_policy_rejects_duplicate_and_wrong_direction_connections()
    {
        var source = new WorkflowNodeDefinition
        {
            NodeTypeId = "test.source",
            Name = "Source",
            Ports = [new WorkflowPortDefinition { Key = "out", Direction = WorkflowPortDirection.Output }]
        };
        var target = new WorkflowNodeDefinition
        {
            NodeTypeId = "test.target",
            Name = "Target",
            Ports = [new WorkflowPortDefinition
            {
                Key = "in",
                Direction = WorkflowPortDirection.Input,
                Cardinality = WorkflowPortCardinality.Single
            }]
        };
        var document = new WorkflowGraphDocument { Nodes = [source, target] };
        var editor = new WorkflowDocumentEditor(document);

        var first = editor.TryConnect(source.Id, "out", target.Id, "in");
        var duplicate = editor.TryConnect(source.Id, "out", target.Id, "in");
        var reverse = editor.TryConnect(target.Id, "in", source.Id, "out");

        Assert.True(first.IsAllowed);
        Assert.Equal("WF-CONNECTION-DUPLICATE", duplicate.Code);
        Assert.Equal("WF-CONNECTION-SOURCE-DIRECTION", reverse.Code);
    }

    [Fact]
    public void Connection_policy_rejects_mismatched_port_types_and_outcome_semantics()
    {
        var source = new WorkflowNodeDefinition
        {
            NodeTypeId = "test.source",
            Name = "Source",
            Ports = [new WorkflowPortDefinition
            {
                Key = "failure",
                Direction = WorkflowPortDirection.Output,
                DataType = "control",
                EdgeKind = WorkflowEdgeKind.Failure
            }]
        };
        var controlTarget = new WorkflowNodeDefinition
        {
            NodeTypeId = "test.target",
            Name = "Control target",
            Ports = [new WorkflowPortDefinition { Key = "in", Direction = WorkflowPortDirection.Input, DataType = "control" }]
        };
        var dataTarget = new WorkflowNodeDefinition
        {
            NodeTypeId = "test.data-target",
            Name = "Data target",
            Ports = [new WorkflowPortDefinition { Key = "in", Direction = WorkflowPortDirection.Input, DataType = "instrument-status" }]
        };
        var editor = new WorkflowDocumentEditor(new WorkflowGraphDocument { Nodes = [source, controlTarget, dataTarget] });

        var wrongSemantic = editor.TryConnect(source.Id, "failure", controlTarget.Id, "in", WorkflowEdgeKind.Success);
        var wrongType = editor.TryConnect(source.Id, "failure", dataTarget.Id, "in", WorkflowEdgeKind.Failure);
        var correct = editor.TryConnect(source.Id, "failure", controlTarget.Id, "in", WorkflowEdgeKind.Failure);

        Assert.Equal("WF-CONNECTION-SEMANTIC", wrongSemantic.Code);
        Assert.Equal("WF-CONNECTION-TYPE", wrongType.Code);
        Assert.True(correct.IsAllowed);
    }

    [Fact]
    public void Copy_and_paste_rewrites_node_and_edge_ids()
    {
        var document = WorkflowSpikeSamples.CreateCopyProbe();
        var editor = new WorkflowDocumentEditor(document);
        var fragment = editor.Copy(document.Nodes.Select(node => node.Id));

        Assert.Equal(5, fragment.Nodes.Count);
        Assert.Equal(6, fragment.Edges.Count);

        var pastedIds = editor.Paste(fragment);

        Assert.Equal(5, pastedIds.Count);
        Assert.Equal(10, editor.Current.Nodes.Count);
        Assert.Equal(12, editor.Current.Edges.Count);
        Assert.Equal(10, editor.Current.Nodes.Select(node => node.Id).Distinct().Count());
        Assert.Equal(12, editor.Current.Edges.Select(edge => edge.Id).Distinct().Count());
        Assert.All(editor.Current.Edges.Skip(6), edge =>
        {
            Assert.Contains(edge.SourceNodeId, pastedIds);
            Assert.Contains(edge.TargetNodeId, pastedIds);
        });
    }

    [Fact]
    public void One_hundred_undo_redo_operations_restore_the_same_document()
    {
        var document = WorkflowSpikeSamples.CreateLinear();
        var editor = new WorkflowDocumentEditor(document);
        var nodeId = document.Nodes[1].Id;

        for (var index = 0; index < 100; index++)
            Assert.True(editor.TryUpdateLayout(nodeId, index * 10, index * 5));

        var expected = editor.Serialize();
        for (var index = 0; index < 100; index++) Assert.True(editor.Undo());
        Assert.False(editor.CanUndo);
        for (var index = 0; index < 100; index++) Assert.True(editor.Redo());

        Assert.Equal(expected, editor.Serialize());
    }

    [Fact]
    public void Layout_is_deterministic_and_does_not_change_edges()
    {
        var original = WorkflowSpikeSamples.CreateBranching();
        var service = new WorkflowLayeredLayout();

        var first = service.Arrange(original);
        var second = service.Arrange(original);

        Assert.Equal(first.Edges, second.Edges);
        Assert.Equal(first.Layouts, second.Layouts);
        Assert.Equal(original.Edges, first.Edges);
        Assert.Equal(original.Nodes.Count, first.Layouts.Count);
        Assert.All(first.Layouts, layout => Assert.True(double.IsFinite(layout.X) && double.IsFinite(layout.Y)));
    }

    [Fact]
    public void Large_spike_graph_can_be_serialized_and_arranged_under_two_seconds()
    {
        var graph = WorkflowSpikeSamples.CreateLarge();
        var editor = new WorkflowDocumentEditor(graph);
        var stopwatch = Stopwatch.StartNew();

        editor.TryApplyLayout(document => new WorkflowLayeredLayout().Arrange(document));
        var json = editor.Serialize();
        var roundTrip = WorkflowDocumentEditor.Deserialize(json);
        stopwatch.Stop();

        Assert.Equal(graph.Nodes.Count, roundTrip.Nodes.Count);
        Assert.Equal(graph.Edges.Count, roundTrip.Edges.Count);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Graph preparation took {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");
    }

    [Fact]
    public void Batch_layout_update_is_recorded_as_one_undo_entry()
    {
        var document = WorkflowSpikeSamples.CreateLinear();
        var editor = new WorkflowDocumentEditor(document);
        var original = editor.Serialize();
        var layouts = document.Nodes.Select((node, index) => new WorkflowNodeLayout
        {
            NodeId = node.Id,
            X = index * 10,
            Y = index * 20
        }).ToArray();

        Assert.True(editor.TryUpdateLayouts(layouts));
        Assert.True(editor.Undo());
        Assert.False(editor.CanUndo);
        Assert.Equal(original, editor.Serialize());
    }

    [Fact]
    public void Contracts_and_domain_do_not_reference_nodify()
    {
        var contracts = typeof(WorkflowGraphDocument).Assembly.GetReferencedAssemblies();
        var domain = typeof(WorkflowDocumentEditor).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(contracts, assembly => assembly.Name?.Contains("Nodify", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(domain, assembly => assembly.Name?.Contains("Nodify", StringComparison.OrdinalIgnoreCase) == true);
    }
}
