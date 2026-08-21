using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Workflows;

namespace MesControlAgv.Wpf.Tests;

public sealed class WorkflowValidationTests
{
    [Fact]
    public void Validation_projection_maps_metadata_locations_and_filters_without_losing_contract_targets()
    {
        var startId = Guid.NewGuid();
        var endId = Guid.NewGuid();
        var edgeId = Guid.NewGuid();
        var document = CreateDocument(startId, endId, edgeId);
        WorkflowValidationIssueItemViewModel? navigated = null;
        var viewModel = new WorkflowValidationViewModel(issue => navigated = issue);
        var validatedAt = DateTimeOffset.Parse("2026-08-21T02:30:00Z");
        var result = new WorkflowValidationResult
        {
            ValidatorVersion = "workflow-publication-v2",
            CatalogVersion = "catalog-v1",
            ProfileProductId = "MES-AGV",
            ProfileVersion = "2026.08",
            ValidatedAt = validatedAt,
            Issues =
            [
                new WorkflowValidationIssue
                {
                    Code = "FIELD_REQUIRED",
                    Message = "timeoutSeconds is required.",
                    Severity = WorkflowValidationSeverity.Error,
                    NodeId = startId,
                    ConfigurationKey = "timeoutSeconds"
                },
                new WorkflowValidationIssue
                {
                    Code = "EDGE_KIND_MISMATCH",
                    Message = "The edge kind does not match the source port.",
                    Severity = WorkflowValidationSeverity.Warning,
                    NodeId = startId,
                    EdgeId = edgeId
                },
                new WorkflowValidationIssue
                {
                    Code = "WORKFLOW_NAME_REQUIRED",
                    Message = "A workflow name is required.",
                    Severity = WorkflowValidationSeverity.Error
                }
            ]
        };

        viewModel.Load(result, document);

        Assert.Equal(3, viewModel.AllIssues.Count);
        Assert.Equal(2, viewModel.ErrorCount);
        Assert.Equal(1, viewModel.WarningCount);
        Assert.Contains("workflow-publication-v2", viewModel.ValidatorMetadata, StringComparison.Ordinal);
        Assert.Contains("catalog-v1", viewModel.ValidatorMetadata, StringComparison.Ordinal);
        Assert.Contains("MES-AGV", viewModel.ProfileMetadata, StringComparison.Ordinal);
        Assert.Equal("节点：Start", viewModel.AllIssues.Single(issue => issue.Code == "FIELD_REQUIRED").Location);
        Assert.Equal("边：Start -> End", viewModel.AllIssues.Single(issue => issue.Code == "EDGE_KIND_MISMATCH").Location);

        viewModel.SelectedSeverityFilter = viewModel.SeverityFilters.Single(option =>
            option.Severity == WorkflowValidationSeverity.Warning);
        Assert.Equal("EDGE_KIND_MISMATCH", Assert.Single(viewModel.VisibleIssues).Code);

        viewModel.SelectedSeverityFilter = viewModel.SeverityFilters[0];
        viewModel.SelectedLocationFilter = viewModel.LocationFilters.Single(option =>
            option.Key == $"node:{startId:N}");
        var nodeIssue = Assert.Single(viewModel.VisibleIssues);
        viewModel.SelectedIssue = nodeIssue;

        Assert.Same(nodeIssue, navigated);
        var selectedNavigation = Assert.IsType<WorkflowValidationIssueItemViewModel>(navigated);
        Assert.Equal(startId, selectedNavigation.NodeId);
        Assert.Equal("timeoutSeconds", selectedNavigation.ConfigurationKey);
    }

    [Fact]
    public void Selecting_validation_items_selects_node_or_edge_and_requests_canvas_navigation()
    {
        using var fixture = new TempWorkflowFile();
        var editor = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path));
        var document = Assert.IsType<WorkflowGraphDocument>(editor.SelectedGraphDocument);
        var node = document.Nodes.First();
        var edge = document.Edges.First();
        var canvas = Assert.IsType<WorkflowCanvasSpikeViewModel>(editor.CanvasViewModel);
        var navigationRequests = new List<WorkflowValidationIssueItemViewModel>();
        editor.ValidationNavigationRequested += (_, issue) => navigationRequests.Add(issue);
        editor.Validation.Load(
            new WorkflowValidationResult
            {
                Issues =
                [
                    new WorkflowValidationIssue
                    {
                        Code = "NODE_ISSUE",
                        Message = "Node issue",
                        NodeId = node.Id
                    },
                    new WorkflowValidationIssue
                    {
                        Code = "EDGE_ISSUE",
                        Message = "Edge issue",
                        NodeId = edge.SourceNodeId,
                        EdgeId = edge.Id
                    }
                ]
            },
            document);

        var nodeIssue = editor.Validation.AllIssues.Single(issue => issue.Code == "NODE_ISSUE");
        editor.Validation.SelectedIssue = nodeIssue;

        Assert.Same(nodeIssue, Assert.Single(navigationRequests));
        Assert.Equal(node.Id, editor.SelectedNode?.Id);
        Assert.Equal(node.Id, canvas.SelectedNode?.Id);

        var edgeIssue = editor.Validation.AllIssues.Single(issue => issue.Code == "EDGE_ISSUE");
        editor.Validation.SelectedIssue = edgeIssue;

        Assert.Same(edgeIssue, navigationRequests[1]);
        Assert.Equal(edge.Id, navigationRequests[1].EdgeId);
        Assert.Equal(edge.Id, canvas.SelectedConnection?.Id);
        Assert.Null(canvas.SelectedNode);
        Assert.Null(editor.SelectedNode);
        Assert.Contains("EDGE_ISSUE", editor.Message, StringComparison.Ordinal);
    }

    private static WorkflowGraphDocument CreateDocument(Guid startId, Guid endId, Guid edgeId) => new()
    {
        Name = "Validation projection",
        Nodes =
        [
            new WorkflowNodeDefinition { Id = startId, NodeTypeId = WorkflowGraphNodeTypeIds.Start, Name = "Start" },
            new WorkflowNodeDefinition { Id = endId, NodeTypeId = WorkflowGraphNodeTypeIds.End, Name = "End" }
        ],
        Edges =
        [
            new WorkflowEdgeDefinition
            {
                Id = edgeId,
                SourceNodeId = startId,
                SourcePort = "success",
                TargetNodeId = endId,
                TargetPort = "in",
                Kind = WorkflowEdgeKind.Success
            }
        ]
    };

    private sealed class TempWorkflowFile : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "MesControlAgv.WorkflowValidationTests",
            Guid.NewGuid().ToString("N"));

        public string Path => System.IO.Path.Combine(_directory, "workflows.json");

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
