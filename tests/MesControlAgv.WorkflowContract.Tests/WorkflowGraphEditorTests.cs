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
    public void Structured_condition_round_trip_and_paste_remap_its_node_output_reference()
    {
        var source = new WorkflowNodeDefinition
        {
            NodeTypeId = "test.source",
            Name = "Source",
            Ports =
            [
                new WorkflowPortDefinition
                {
                    Key = "success",
                    DisplayName = "Success",
                    Direction = WorkflowPortDirection.Output,
                    EdgeKind = WorkflowEdgeKind.Success
                }
            ]
        };
        var gateway = new WorkflowNodeDefinition
        {
            NodeTypeId = WorkflowGraphNodeTypeIds.Condition,
            Name = "Gateway",
            Ports =
            [
                new WorkflowPortDefinition
                {
                    Key = "in",
                    DisplayName = "Input",
                    Direction = WorkflowPortDirection.Input,
                    Cardinality = WorkflowPortCardinality.Single
                },
                new WorkflowPortDefinition
                {
                    Key = "condition",
                    DisplayName = "Condition",
                    Direction = WorkflowPortDirection.Output,
                    EdgeKind = WorkflowEdgeKind.ConditionTrue
                }
            ]
        };
        var target = new WorkflowNodeDefinition
        {
            NodeTypeId = "test.target",
            Name = "Target",
            Ports =
            [
                new WorkflowPortDefinition
                {
                    Key = "in",
                    DisplayName = "Input",
                    Direction = WorkflowPortDirection.Input,
                    Cardinality = WorkflowPortCardinality.Many
                }
            ]
        };
        var expression = new WorkflowConditionExpression
        {
            Source = WorkflowConditionValueSource.NodeOutput,
            SourceNodeId = source.Id,
            SourceKey = "pressureMpa",
            ValueType = WorkflowSchemaValueType.Decimal,
            Operator = WorkflowConditionOperator.LessThanOrEqual,
            CompareValue = "12",
            MissingValueBehavior = WorkflowConditionMissingValueBehavior.Wait
        };
        var document = new WorkflowGraphDocument
        {
            Nodes = [source, gateway, target],
            Edges =
            [
                new WorkflowEdgeDefinition
                {
                    SourceNodeId = source.Id,
                    SourcePort = "success",
                    TargetNodeId = gateway.Id,
                    TargetPort = "in"
                },
                new WorkflowEdgeDefinition
                {
                    SourceNodeId = gateway.Id,
                    SourcePort = "condition",
                    TargetNodeId = target.Id,
                    TargetPort = "in",
                    Kind = WorkflowEdgeKind.ConditionTrue,
                    ConditionExpression = expression
                }
            ]
        };
        var serialized = new WorkflowDocumentEditor(document).Serialize();
        var roundTrip = WorkflowDocumentEditor.Deserialize(serialized);
        var contractRoundTrip = WorkflowGraphContractAdapter.FromContract(
            WorkflowGraphContractAdapter.ToContract(roundTrip));

        Assert.Equal(expression, contractRoundTrip.Edges.Single(edge => edge.SourceNodeId == gateway.Id).ConditionExpression);

        var editor = new WorkflowDocumentEditor(roundTrip);
        var fragment = editor.Copy(roundTrip.Nodes.Select(node => node.Id));
        var pastedIds = editor.Paste(fragment);
        var pastedSource = editor.Current.Nodes.Single(node => pastedIds.Contains(node.Id) && node.Name == "Source");
        var pastedGateway = editor.Current.Nodes.Single(node => pastedIds.Contains(node.Id) && node.Name == "Gateway");
        var pastedCondition = editor.Current.Edges.Single(edge =>
            edge.SourceNodeId == pastedGateway.Id && edge.SourcePort == "condition");

        Assert.Equal(pastedSource.Id, pastedCondition.ConditionExpression!.SourceNodeId);
        Assert.NotEqual(source.Id, pastedCondition.ConditionExpression.SourceNodeId);
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
    public void Viewport_updates_are_persisted_without_polluting_edit_history()
    {
        var document = WorkflowSpikeSamples.CreateLinear();
        var editor = new WorkflowDocumentEditor(document);
        var nodeId = document.Nodes[1].Id;

        Assert.True(editor.TryUpdateLayout(nodeId, 640, 320));
        Assert.True(editor.TryUpdateViewport(new WorkflowCanvasViewport { X = 80, Y = 40, Zoom = 1.5 }));
        Assert.False(editor.TryUpdateViewport(new WorkflowCanvasViewport { X = 80, Y = 40, Zoom = 1.5 }));

        Assert.True(editor.Undo());
        Assert.False(editor.CanUndo);
        Assert.Equal(new WorkflowCanvasViewport { X = 80, Y = 40, Zoom = 1.5 }, editor.Current.Viewport);

        Assert.True(editor.Redo());
        Assert.Equal(new WorkflowCanvasViewport { X = 80, Y = 40, Zoom = 1.5 }, editor.Current.Viewport);
    }

    [Fact]
    public void Property_projection_changes_share_history_and_preserve_lifecycle_metadata()
    {
        var document = WorkflowSpikeSamples.CreateLinear();
        var editor = new WorkflowDocumentEditor(document);
        var renamed = document with
        {
            Nodes = document.Nodes
                .Select((node, index) => index == 0 ? node with { Name = "Renamed" } : node)
                .ToArray()
        };

        Assert.True(editor.TryReplaceDocument(renamed));
        Assert.True(editor.TryReplaceDocument(renamed with { PublishedVersion = 7 }, recordHistory: false));

        Assert.True(editor.Undo());
        Assert.Equal(document.Nodes[0].Name, editor.Current.Nodes[0].Name);
        Assert.Equal(7, editor.Current.PublishedVersion);

        Assert.True(editor.Redo());
        Assert.Equal("Renamed", editor.Current.Nodes[0].Name);
        Assert.Equal(7, editor.Current.PublishedVersion);
    }

    [Fact]
    public void Contracts_and_domain_do_not_reference_nodify()
    {
        var contracts = typeof(WorkflowGraphDocument).Assembly.GetReferencedAssemblies();
        var domain = typeof(WorkflowDocumentEditor).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(contracts, assembly => assembly.Name?.Contains("Nodify", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(domain, assembly => assembly.Name?.Contains("Nodify", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void Graph_contract_adapter_preserves_edges_ports_layout_and_parameters()
    {
        var start = WorkflowGraphContractAdapter.CreateNode(WorkflowNodeType.Start, "Start");
        var instrument = WorkflowGraphContractAdapter.CreateNode(
            WorkflowNodeType.InstrumentOperation,
            "Read D160",
            configuration: new Dictionary<string, string?>
            {
                [WorkflowGraphContractAdapter.TargetStationConfigurationKey] = "D160",
                [WorkflowGraphContractAdapter.ParametersConfigurationKey] =
                    "[{\"name\":\"instrumentId\",\"value\":\"D160\",\"dataType\":\"string\",\"isRequired\":true}]"
            });
        var end = WorkflowGraphContractAdapter.CreateNode(WorkflowNodeType.End, "End");
        var document = new WorkflowGraphDocument
        {
            Id = Guid.NewGuid(),
            Name = "Instrument graph",
            Description = "Graph source of truth",
            Nodes = [start, instrument, end],
            Edges =
            [
                new WorkflowEdgeDefinition
                {
                    SourceNodeId = start.Id,
                    SourcePort = "success",
                    TargetNodeId = instrument.Id,
                    TargetPort = "in",
                    Kind = WorkflowEdgeKind.Success,
                    Metadata = new Dictionary<string, string?> { ["legacyColor"] = "#4285F4" }
                },
                new WorkflowEdgeDefinition
                {
                    SourceNodeId = instrument.Id,
                    SourcePort = "success",
                    TargetNodeId = end.Id,
                    TargetPort = "in",
                    Kind = WorkflowEdgeKind.Success
                }
            ],
            Layouts =
            [
                new WorkflowNodeLayout { NodeId = start.Id, X = 10, Y = 20 },
                new WorkflowNodeLayout { NodeId = instrument.Id, X = 240, Y = 20 },
                new WorkflowNodeLayout { NodeId = end.Id, X = 470, Y = 20 }
            ],
            Viewport = new WorkflowCanvasViewport { X = 4, Y = 8, Zoom = 1.25 }
        };

        var contract = WorkflowGraphContractAdapter.ToContract(document);
        var roundTrip = WorkflowGraphContractAdapter.FromContract(contract);

        Assert.Equal(document.Id, contract.Id);
        Assert.Equal(document.Edges, contract.Edges);
        Assert.Equal(document.Edges, roundTrip.Edges);
        Assert.Equal(document.Layouts, roundTrip.Layouts);
        Assert.Equal(document.Viewport, roundTrip.Viewport);
        var roundTripInstrument = Assert.Single(roundTrip.Nodes.Where(node => node.Id == instrument.Id));
        Assert.Equal(WorkflowGraphNodeTypeIds.InstrumentOperation, roundTripInstrument.NodeTypeId);
        Assert.Equal("D160", roundTripInstrument.Configuration[WorkflowGraphContractAdapter.TargetStationConfigurationKey]);
        Assert.Contains("instrumentId", roundTripInstrument.Configuration[WorkflowGraphContractAdapter.ParametersConfigurationKey]);
        Assert.Equal(end.Id, Assert.Single(contract.Nodes.Single(node => node.Id == instrument.Id).NextNodeIds));
        Assert.Equal(240, contract.Nodes.Single(node => node.Id == instrument.Id).X);
    }

    [Fact]
    public void Graph_adapter_imports_legacy_next_node_ids_as_explicit_edges()
    {
        var start = Guid.NewGuid();
        var end = Guid.NewGuid();
        var legacy = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Legacy",
            Nodes =
            [
                new WorkflowNode { Id = start, Type = WorkflowNodeType.Start, Name = "Start", Order = 1, NextNodeIds = [end] },
                new WorkflowNode { Id = end, Type = WorkflowNodeType.End, Name = "End", Order = 2 }
            ]
        };

        var graph = WorkflowGraphContractAdapter.FromContract(legacy);

        var edge = Assert.Single(graph.Edges);
        Assert.Equal(start, edge.SourceNodeId);
        Assert.Equal(end, edge.TargetNodeId);
        Assert.Equal("success", edge.SourcePort);
        Assert.Equal("in", edge.TargetPort);
    }
}
