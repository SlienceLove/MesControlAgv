using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Workflows;

namespace MesControlAgv.WorkflowContract.Tests;

public sealed class WorkflowAdvancedPublicationValidatorTests
{
    private static readonly WorkflowCatalogSet Catalog = BuiltInWorkflowCatalog.Create();

    [Fact]
    public void Version_two_typed_graph_remains_publishable_after_v3_contract_introduction()
    {
        var start = Node(WorkflowGraphNodeTypeIds.Start, "Start", 1);
        var wait = Node(
            WorkflowGraphNodeTypeIds.TimedWait,
            "Wait",
            2,
            new Dictionary<string, string?> { [WorkflowRuntimeParameterNames.WaitDurationSeconds] = "1" });
        var end = Node(WorkflowGraphNodeTypeIds.End, "End", 3);
        var graph = Workflow(
            [start, wait, end],
            [Edge(start, "success", wait), Edge(wait, "success", end)]) with
        {
            SchemaVersion = WorkflowGraphDocument.ExplicitEdgesSchemaVersion
        };

        var result = new WorkflowValidator().ValidateForPublication(graph);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Issues, issue =>
            issue.Code is WorkflowPublicationIssueCodes.DocumentSchemaUnsupported or
                WorkflowPublicationIssueCodes.AdvancedSchemaRequired);
    }

    [Fact]
    public void Structured_condition_is_validated_but_remains_blocked_by_the_contract_only_catalog_gate()
    {
        var graph = ConditionGraph();

        var result = new WorkflowValidator().ValidateForPublication(graph);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowPublicationIssueCodes.NodeTypeDisabled &&
            issue.NodeId == graph.Nodes.Single(node => node.NodeTypeId == WorkflowGraphNodeTypeIds.Condition).Id &&
            issue.Message == BuiltInWorkflowCatalog.AdvancedFlowContractOnlyReason);
        Assert.DoesNotContain(result.Issues, issue => issue.Code.StartsWith("WF-CONDITION-", StringComparison.Ordinal));
    }

    [Fact]
    public void Structured_condition_requires_v3_and_cannot_be_attached_to_an_arbitrary_edge()
    {
        var start = Node(WorkflowGraphNodeTypeIds.Start, "Start", 1);
        var end = Node(WorkflowGraphNodeTypeIds.End, "End", 2);
        var edge = Edge(start, "success", end) with
        {
            ConditionExpression = RunInputCondition("sample.pressure", "12")
        };
        var graph = Workflow([start, end], [edge]) with
        {
            SchemaVersion = WorkflowGraphDocument.ExplicitEdgesSchemaVersion
        };

        var result = new WorkflowValidator().ValidateForPublication(graph);

        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowPublicationIssueCodes.AdvancedSchemaRequired && issue.EdgeId == edge.Id);
        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowPublicationIssueCodes.ConditionExpressionLocation && issue.EdgeId == edge.Id);
    }

    [Fact]
    public void Condition_gateway_requires_one_default_branch_and_explicit_missing_value_behavior()
    {
        var graph = ConditionGraph();
        var gateway = graph.Nodes.Single(node => node.NodeTypeId == WorkflowGraphNodeTypeIds.Condition);
        var conditionEdge = graph.Edges.Single(edge => edge.SourceNodeId == gateway.Id && edge.SourcePort == "condition");
        var changedEdges = graph.Edges
            .Where(edge => !(edge.SourceNodeId == gateway.Id && edge.SourcePort == "default"))
            .Select(edge => edge.Id == conditionEdge.Id
                ? edge with
                {
                    ConditionExpression = edge.ConditionExpression! with
                    {
                        MissingValueBehavior = WorkflowConditionMissingValueBehavior.Unspecified
                    }
                }
                : edge)
            .ToArray();
        var changed = Project(graph with { Edges = changedEdges });

        var result = new WorkflowValidator().ValidateForPublication(changed);

        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowPublicationIssueCodes.ConditionDefaultInvalid && issue.NodeId == gateway.Id);
        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowPublicationIssueCodes.ConditionMissingBehavior && issue.EdgeId == conditionEdge.Id);
    }

    [Fact]
    public void Node_output_condition_checks_declared_result_type_and_upstream_ownership()
    {
        var graph = ConditionGraph();
        var gateway = graph.Nodes.Single(node => node.NodeTypeId == WorkflowGraphNodeTypeIds.Condition);
        var read = graph.Nodes.Single(node => node.NodeTypeId == WorkflowGraphNodeTypeIds.InstrumentReadStatus);
        var branch = graph.Edges.Single(edge => edge.SourceNodeId == gateway.Id && edge.SourcePort == "condition");
        var wrongType = Project(graph with
        {
            Edges = graph.Edges.Select(edge => edge.Id == branch.Id
                ? edge with
                {
                    ConditionExpression = edge.ConditionExpression! with
                    {
                        SourceNodeId = read.Id,
                        ValueType = WorkflowSchemaValueType.String,
                        CompareValue = "12"
                    }
                }
                : edge).ToArray()
        });

        var wrongTypeResult = new WorkflowValidator().ValidateForPublication(wrongType);

        Assert.Contains(wrongTypeResult.Issues, issue =>
            issue.Code == WorkflowPublicationIssueCodes.ConditionTypeMismatch && issue.EdgeId == branch.Id);

        var end = graph.Nodes.Single(node => node.NodeTypeId == WorkflowGraphNodeTypeIds.End);
        var downstream = Project(graph with
        {
            Edges = graph.Edges.Select(edge => edge.Id == branch.Id
                ? edge with
                {
                    ConditionExpression = edge.ConditionExpression! with
                    {
                        SourceNodeId = end.Id,
                        SourceKey = "pressureMpa"
                    }
                }
                : edge).ToArray()
        });

        var downstreamResult = new WorkflowValidator().ValidateForPublication(downstream);

        Assert.Contains(downstreamResult.Issues, issue =>
            issue.Code == WorkflowPublicationIssueCodes.ConditionSourceUnavailable && issue.EdgeId == branch.Id);
    }

    [Fact]
    public void Parallel_pair_is_structurally_validated_but_both_gateways_remain_disabled()
    {
        var graph = ParallelGraph();

        var result = new WorkflowValidator().ValidateForPublication(graph);

        Assert.Equal(2, result.Issues.Count(issue => issue.Code == WorkflowPublicationIssueCodes.NodeTypeDisabled));
        Assert.DoesNotContain(result.Issues, issue => issue.Code.StartsWith("WF-PARALLEL-", StringComparison.Ordinal));
    }

    [Fact]
    public void Parallel_pair_rejects_a_branch_that_bypasses_its_join()
    {
        var graph = ParallelGraph();
        var join = graph.Nodes.Single(node => node.NodeTypeId == WorkflowGraphNodeTypeIds.ParallelJoin);
        var end = graph.Nodes.Single(node => node.NodeTypeId == WorkflowGraphNodeTypeIds.End);
        var incoming = graph.Edges.Where(edge => edge.TargetNodeId == join.Id).Last();
        var changed = Project(graph with
        {
            Edges = graph.Edges.Select(edge => edge.Id == incoming.Id
                ? edge with { TargetNodeId = end.Id }
                : edge).ToArray()
        });

        var result = new WorkflowValidator().ValidateForPublication(changed);

        Assert.Contains(result.Issues, issue => issue.Code == WorkflowPublicationIssueCodes.ParallelBranchInvalid);
        Assert.Contains(result.Issues, issue =>
            issue.Code == WorkflowPublicationIssueCodes.ParallelJoinInvalid && issue.NodeId == join.Id);
    }

    [Fact]
    public void Signal_and_subflow_references_are_structured_and_self_reference_is_rejected()
    {
        var start = Node(WorkflowGraphNodeTypeIds.Start, "Start", 1);
        var signal = Node(
            WorkflowGraphNodeTypeIds.SignalWait,
            "Signal",
            2,
            new Dictionary<string, string?>
            {
                [WorkflowNodeConfigurationKeys.SignalName] = "lims.result-ready",
                [WorkflowNodeConfigurationKeys.CorrelationKey] = "sample.batch-id",
                [WorkflowNodeConfigurationKeys.TimeoutSeconds] = "3600"
            });
        var subflow = Node(
            WorkflowGraphNodeTypeIds.Subflow,
            "Subflow",
            3,
            new Dictionary<string, string?>
            {
                [WorkflowNodeConfigurationKeys.SubflowWorkflowId] = Guid.NewGuid().ToString(),
                [WorkflowNodeConfigurationKeys.SubflowVersion] = "2"
            });
        var end = Node(WorkflowGraphNodeTypeIds.End, "End", 4);
        var graph = Workflow(
            [start, signal, subflow, end],
            [Edge(start, "success", signal), Edge(signal, "success", subflow), Edge(subflow, "success", end)]);

        var result = new WorkflowValidator().ValidateForPublication(graph);

        Assert.DoesNotContain(result.Issues, issue =>
            issue.Code is WorkflowPublicationIssueCodes.SignalReferenceInvalid or
                WorkflowPublicationIssueCodes.SubflowReferenceInvalid);

        var invalid = Project(graph with
        {
            Nodes = graph.Nodes.Select(node => node.Id == signal.Id
                    ? node with
                    {
                        Configuration = new Dictionary<string, string?>(node.Configuration)
                        {
                            [WorkflowNodeConfigurationKeys.SignalName] = "lims.ready || true"
                        }
                    }
                    : node.Id == subflow.Id
                        ? node with
                        {
                            Configuration = new Dictionary<string, string?>(node.Configuration)
                            {
                                [WorkflowNodeConfigurationKeys.SubflowWorkflowId] = graph.Id.ToString()
                            }
                        }
                        : node)
                .ToArray()
        });

        var invalidResult = new WorkflowValidator().ValidateForPublication(invalid);

        Assert.Contains(invalidResult.Issues, issue => issue.Code == WorkflowPublicationIssueCodes.SignalReferenceInvalid);
        Assert.Contains(invalidResult.Issues, issue => issue.Code == WorkflowPublicationIssueCodes.SubflowReferenceInvalid);
    }

    [Fact]
    public void Compensation_start_requires_an_exception_entry_and_one_tagged_recovery_edge()
    {
        var graph = CompensationGraph();

        var result = new WorkflowValidator().ValidateForPublication(graph);

        Assert.Single(result.Issues, issue =>
            issue.Code == WorkflowPublicationIssueCodes.NodeTypeDisabled &&
            issue.NodeId == graph.Nodes.Single(node => node.NodeTypeId == WorkflowGraphNodeTypeIds.CompensationStart).Id);
        Assert.DoesNotContain(result.Issues, issue => issue.Code == WorkflowPublicationIssueCodes.CompensationPathInvalid);

        var compensation = graph.Nodes.Single(node => node.NodeTypeId == WorkflowGraphNodeTypeIds.CompensationStart);
        var incoming = graph.Edges.Single(edge => edge.TargetNodeId == compensation.Id);
        var invalid = Project(graph with
        {
            Edges = graph.Edges.Select(edge => edge.Id == incoming.Id
                ? edge with { Kind = WorkflowEdgeKind.Success }
                : edge).ToArray()
        });

        var invalidResult = new WorkflowValidator().ValidateForPublication(invalid);

        Assert.Contains(invalidResult.Issues, issue =>
            issue.Code == WorkflowPublicationIssueCodes.CompensationPathInvalid && issue.EdgeId == incoming.Id);
    }

    private static WorkflowDefinition ConditionGraph()
    {
        var start = Node(WorkflowGraphNodeTypeIds.Start, "Start", 1);
        var read = Node(
            WorkflowGraphNodeTypeIds.InstrumentReadStatus,
            "Read",
            2,
            new Dictionary<string, string?>
            {
                [WorkflowNodeConfigurationKeys.InstrumentId] = "CIC-D160-01",
                [WorkflowNodeConfigurationKeys.TimeoutSeconds] = "30",
                [WorkflowNodeConfigurationKeys.RetryCount] = "2"
            });
        var condition = Node(WorkflowGraphNodeTypeIds.Condition, "Condition", 3);
        var end = Node(WorkflowGraphNodeTypeIds.End, "End", 4);
        return Workflow(
            [start, read, condition, end],
            [
                Edge(start, "success", read),
                Edge(read, "success", condition),
                Edge(condition, "condition", end, WorkflowEdgeKind.ConditionTrue, priority: 10) with
                {
                    ConditionExpression = new WorkflowConditionExpression
                    {
                        Source = WorkflowConditionValueSource.NodeOutput,
                        SourceNodeId = read.Id,
                        SourceKey = "pressureMpa",
                        ValueType = WorkflowSchemaValueType.Decimal,
                        Operator = WorkflowConditionOperator.LessThanOrEqual,
                        CompareValue = "12",
                        MissingValueBehavior = WorkflowConditionMissingValueBehavior.Wait
                    }
                },
                Edge(condition, "default", end, WorkflowEdgeKind.ConditionFalse)
            ]);
    }

    private static WorkflowDefinition ParallelGraph()
    {
        var start = Node(WorkflowGraphNodeTypeIds.Start, "Start", 1);
        var pair = new Dictionary<string, string?>
        {
            [WorkflowNodeConfigurationKeys.ParallelGatewayKey] = "sample-preparation"
        };
        var fork = Node(WorkflowGraphNodeTypeIds.ParallelFork, "Fork", 2, pair);
        var left = Node(
            WorkflowGraphNodeTypeIds.TimedWait,
            "Left",
            3,
            new Dictionary<string, string?> { [WorkflowRuntimeParameterNames.WaitDurationSeconds] = "1" });
        var right = Node(
            WorkflowGraphNodeTypeIds.TimedWait,
            "Right",
            4,
            new Dictionary<string, string?> { [WorkflowRuntimeParameterNames.WaitDurationSeconds] = "2" });
        var join = Node(WorkflowGraphNodeTypeIds.ParallelJoin, "Join", 5, pair);
        var end = Node(WorkflowGraphNodeTypeIds.End, "End", 6);
        return Workflow(
            [start, fork, left, right, join, end],
            [
                Edge(start, "success", fork),
                Edge(fork, "branch", left, WorkflowEdgeKind.Parallel, priority: 0),
                Edge(fork, "branch", right, WorkflowEdgeKind.Parallel, priority: 1),
                Edge(left, "success", join),
                Edge(right, "success", join),
                Edge(join, "success", end)
            ]);
    }

    private static WorkflowDefinition CompensationGraph()
    {
        var start = Node(WorkflowGraphNodeTypeIds.Start, "Start", 1);
        var move = Node(
            WorkflowGraphNodeTypeIds.Move,
            "Move",
            2,
            new Dictionary<string, string?>
            {
                [WorkflowNodeConfigurationKeys.TargetStation] = "SAMPLE_01",
                [WorkflowNodeConfigurationKeys.TimeoutSeconds] = "300",
                [WorkflowNodeConfigurationKeys.RetryCount] = "0"
            });
        var compensation = Node(WorkflowGraphNodeTypeIds.CompensationStart, "Compensation", 3);
        var recovery = Node(
            WorkflowGraphNodeTypeIds.TimedWait,
            "Recovery",
            4,
            new Dictionary<string, string?> { [WorkflowRuntimeParameterNames.WaitDurationSeconds] = "1" });
        var end = Node(WorkflowGraphNodeTypeIds.End, "End", 5);
        return Workflow(
            [start, move, compensation, recovery, end],
            [
                Edge(start, "success", move),
                Edge(move, "success", end),
                Edge(move, "failure", compensation, WorkflowEdgeKind.Failure),
                Edge(compensation, "compensation", recovery, WorkflowEdgeKind.Compensation),
                Edge(recovery, "success", end)
            ]);
    }

    private static WorkflowConditionExpression RunInputCondition(string key, string value) => new()
    {
        Source = WorkflowConditionValueSource.RunInput,
        SourceKey = key,
        ValueType = WorkflowSchemaValueType.Decimal,
        Operator = WorkflowConditionOperator.LessThanOrEqual,
        CompareValue = value,
        MissingValueBehavior = WorkflowConditionMissingValueBehavior.Fail
    };

    private static WorkflowNode Node(
        string nodeTypeId,
        string name,
        int order,
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        var definition = Catalog.NodeTypes.GetLatest(nodeTypeId) ??
                         throw new InvalidOperationException($"Missing test catalog node '{nodeTypeId}'.");
        configuration ??= new Dictionary<string, string?>();
        configuration.TryGetValue(WorkflowNodeConfigurationKeys.TargetStation, out var targetStation);
        return new WorkflowNode
        {
            Id = Guid.NewGuid(),
            Type = WorkflowGraphNodeTypeIds.ToContractType(nodeTypeId),
            NodeTypeId = nodeTypeId,
            SchemaVersion = definition.SchemaVersion,
            Name = name,
            Order = order,
            TargetStation = targetStation,
            Ports = definition.Ports,
            Configuration = new Dictionary<string, string?>(configuration, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static WorkflowEdgeDefinition Edge(
        WorkflowNode source,
        string sourcePort,
        WorkflowNode target,
        WorkflowEdgeKind kind = WorkflowEdgeKind.Success,
        int priority = 0) => new()
    {
        SourceNodeId = source.Id,
        SourcePort = sourcePort,
        TargetNodeId = target.Id,
        TargetPort = "in",
        Kind = kind,
        Priority = priority
    };

    private static WorkflowDefinition Workflow(
        IReadOnlyList<WorkflowNode> nodes,
        IReadOnlyList<WorkflowEdgeDefinition> edges) => Project(new WorkflowDefinition
    {
        Id = Guid.NewGuid(),
        SchemaVersion = WorkflowGraphDocument.CurrentSchemaVersion,
        Name = "G6-A contract graph",
        Nodes = nodes,
        Edges = edges
    });

    private static WorkflowDefinition Project(WorkflowDefinition workflow)
    {
        var edges = workflow.Edges ?? [];
        return workflow with
        {
            Nodes = (workflow.Nodes ?? []).Select(node => node with
            {
                NextNodeIds = edges
                    .Where(edge => edge.SourceNodeId == node.Id)
                    .OrderBy(edge => edge.Priority)
                    .ThenBy(edge => edge.Id)
                    .Select(edge => edge.TargetNodeId)
                    .Distinct()
                    .ToArray()
            }).ToArray()
        };
    }
}
