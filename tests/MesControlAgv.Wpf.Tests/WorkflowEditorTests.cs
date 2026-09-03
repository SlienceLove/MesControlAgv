using ContractWorkflowEdgeDefinition = MesControlAgv.Contracts.Workflows.WorkflowEdgeDefinition;
using ContractWorkflowEdgeKind = MesControlAgv.Contracts.Workflows.WorkflowEdgeKind;
using ContractWorkflowGraphDocument = MesControlAgv.Contracts.Workflows.WorkflowGraphDocument;
using ContractWorkflowConditionOperator = MesControlAgv.Contracts.Workflows.WorkflowConditionOperator;
using ContractWorkflowConditionValueSource = MesControlAgv.Contracts.Workflows.WorkflowConditionValueSource;
using ContractWorkflowNodeLayout = MesControlAgv.Contracts.Workflows.WorkflowNodeLayout;
using ContractWorkflowViewport = MesControlAgv.Contracts.Workflows.WorkflowCanvasViewport;
using MesControlAgv.Domain.Profiles;
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
    public void Default_workflows_persist_explicit_linear_edges_and_runtime_links()
    {
        var workflows = WorkflowStore.CreateDefaultWorkflows();

        Assert.All(workflows, workflow =>
        {
            var nodes = workflow.Nodes.OrderBy(node => node.Order).ToArray();
            Assert.Equal(6, nodes.Length);
            Assert.Equal(5, workflow.Edges.Count);

            for (var index = 0; index < nodes.Length - 1; index++)
            {
                Assert.Equal(nodes[index + 1].Id, Assert.Single(nodes[index].NextNodeIds));
                var edge = Assert.Single(workflow.Edges, candidate => candidate.SourceNodeId == nodes[index].Id);
                Assert.Equal(nodes[index + 1].Id, edge.TargetNodeId);
                Assert.Equal(ContractWorkflowEdgeKind.Success, edge.Kind);
            }

            Assert.Empty(nodes[^1].NextNodeIds);
        });
    }

    [Fact]
    public void Aubo_template_uses_runtime_station_and_arm_identifiers()
    {
        var workflow = WorkflowStore.CreateAuboStationProgramWorkflow(
            "LM7",
            "LM6",
            firstProgramName: "现场程序A",
            secondProgramName: "现场程序B",
            armDeviceId: "ARM-02");

        var nodes = workflow.Nodes.OrderBy(node => node.Order).ToArray();
        Assert.Equal("LM7", nodes[1].TargetStation);
        Assert.Equal("LM6", nodes[3].TargetStation);
        Assert.Equal("LM7 到站机械臂程序", nodes[2].Name);
        Assert.Equal("LM6 到站机械臂程序", nodes[4].Name);
        Assert.Equal("现场程序A", nodes[2].Configuration[MesControlAgv.Contracts.Workflows.WorkflowNodeConfigurationKeys.ProgramName]);
        Assert.Equal("现场程序B", nodes[4].Configuration[MesControlAgv.Contracts.Workflows.WorkflowNodeConfigurationKeys.ProgramName]);
        Assert.All(new[] { nodes[2], nodes[4] }, node =>
            Assert.Equal("ARM-02", node.Configuration[MesControlAgv.Contracts.Workflows.WorkflowNodeConfigurationKeys.DeviceId]));
    }

    [Fact]
    public void Standard_material_handling_template_has_the_four_leg_sequence_and_programs()
    {
        var workflow = WorkflowStore.CreateStandardMaterialHandlingWorkflow(
            "LM1",
            "LM7",
            "LM2",
            armDeviceId: "ARM-02");

        var nodes = workflow.Nodes.OrderBy(node => node.Order).ToArray();
        Assert.Equal(9, nodes.Length);
        Assert.Equal(
            new[] { null, "LM7", null, "LM2", null, "LM7", null, "LM1", null },
            nodes.Select(node => node.TargetStation));
        Assert.Equal(
            new[] { "取料盘.pro", "放料盘.pro", "回收料盘.pro" },
            nodes.Where(node => node.Type == WorkflowNodeType.RobotProgram)
                .Select(node => node.Configuration[MesControlAgv.Contracts.Workflows.WorkflowNodeConfigurationKeys.ProgramName]));
        Assert.Equal(8, workflow.Edges.Count);
        Assert.Equal("从原点开始", nodes[0].Name);
        Assert.Equal("结束", nodes[^1].Name);
        Assert.All(nodes.Zip(nodes.Skip(1)), pair =>
            Assert.Equal(pair.Second.Id, Assert.Single(pair.First.NextNodeIds)));
    }

    [Fact]
    public void Editor_standard_template_resolves_numbered_field_stations_and_reports_mapping()
    {
        using var fixture = new TempWorkflowFile();
        var profile = ProfileConfiguration.Default with
        {
            Stations =
            [
                new StationProfile { Code = 1, StationId = "LM1", AgvStationId = "LM1", Name = "充电原点", Type = "Charge", Enabled = true },
                new StationProfile { Code = 7, StationId = "LM7", AgvStationId = "LM7", Name = "站点1", Type = "Station", Enabled = true },
                new StationProfile { Code = 2, StationId = "LM2", AgvStationId = "LM2", Name = "LM2", Type = "Station", Enabled = true }
            ],
            Map = new MapProfile
            {
                StationIds = ["LM1", "LM7", "LM2"],
                Edges =
                [
                    new MapEdgeProfile { From = "LM1", To = "LM7" },
                    new MapEdgeProfile { From = "LM7", To = "LM2" },
                    new MapEdgeProfile { From = "LM2", To = "LM7" },
                    new MapEdgeProfile { From = "LM7", To = "LM1" }
                ]
            }
        };
        var editor = new WorkflowEditorViewModel(
            new WorkflowStore(fixture.Path),
            profileConfiguration: profile);

        editor.CreateAuboTemplateCommand.Execute(null);

        var workflow = editor.SelectedWorkflow!;
        var nodes = workflow.Nodes.OrderBy(node => node.Order).ToArray();
        Assert.Equal(new[] { "LM7", "LM2", "LM7", "LM1" }, nodes
            .Where(node => node.Type == WorkflowNodeType.Move)
            .Select(node => node.TargetStation));
        Assert.Contains("原点 LM1", editor.Message, StringComparison.Ordinal);
        Assert.Contains("站点1 LM7", editor.Message, StringComparison.Ordinal);
        Assert.Contains("站点2 LM2", editor.Message, StringComparison.Ordinal);
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
    public void Graph_mapper_fills_layout_for_nodes_added_after_a_saved_layout()
    {
        var first = new WorkflowNode { Type = WorkflowNodeType.Start, Name = "Start", X = 10, Y = 20, Order = 1 };
        var added = new WorkflowNode { Type = WorkflowNodeType.Wait, Name = "Added", X = 615, Y = 225, Order = 2 };
        var workflow = new WorkflowDefinition
        {
            Nodes = [first, added],
            Layouts = [new ContractWorkflowNodeLayout { NodeId = first.Id, X = 10, Y = 20 }]
        };

        var graph = WorkflowDocumentMapper.ToGraph(workflow);

        Assert.Equal(2, graph.Layouts.Count);
        var addedLayout = graph.Layouts.Single(layout => layout.NodeId == added.Id);
        Assert.Equal(615, addedLayout.X);
        Assert.Equal(225, addedLayout.Y);
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
    public void Store_migrates_edge_less_v1_graph_document_to_sequential_edges()
    {
        using var fixture = new TempWorkflowFile();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fixture.Path)!);
        var start = Guid.NewGuid();
        var end = Guid.NewGuid();
        File.WriteAllText(
            fixture.Path,
            $$"""
            {
              "format": "mes.workflow.graph",
              "schemaVersion": 1,
              "workflows": [
                {
                  "id": "{{Guid.NewGuid()}}",
                  "schemaVersion": 1,
                  "name": "V1 graph",
                  "nodes": [
                    { "id": "{{start}}", "nodeTypeId": "core.start", "name": "Start" },
                    { "id": "{{end}}", "nodeTypeId": "core.end", "name": "End" }
                  ],
                  "edges": []
                }
              ]
            }
            """);

        var store = new WorkflowStore(fixture.Path);
        var workflow = Assert.Single(store.Load());

        var edge = Assert.Single(workflow.Edges);
        Assert.Equal(start, edge.SourceNodeId);
        Assert.Equal(end, edge.TargetNodeId);
        Assert.Equal(end, Assert.Single(workflow.Nodes.Single(node => node.Id == start).NextNodeIds));
        Assert.NotNull(store.LastLoadReport);
        Assert.Equal(WorkflowImportSourceFormat.LegacyGraphEnvelope, store.LastLoadReport!.SourceFormat);
        Assert.Equal(1, store.LastLoadReport.MigratedEdgeCount);
        Assert.Contains("迁移生成 1 条顺序边", store.LastLoadReport.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Store_migrates_edge_less_legacy_wpf_array_to_sequential_edges()
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
                "name": "Legacy edge-less",
                "nodes": [
                  { "id": "{{start}}", "type": 0, "name": "Start", "order": 1 },
                  { "id": "{{end}}", "type": 5, "name": "End", "order": 2 }
                ]
              }
            ]
            """);

        var workflow = Assert.Single(new WorkflowStore(fixture.Path).Load());

        var edge = Assert.Single(workflow.Edges);
        Assert.Equal(start, edge.SourceNodeId);
        Assert.Equal(end, edge.TargetNodeId);
        Assert.Equal(end, Assert.Single(workflow.Nodes.Single(node => node.Id == start).NextNodeIds));
    }

    [Fact]
    public void Store_preserves_intentionally_disconnected_v2_graph_document()
    {
        using var fixture = new TempWorkflowFile();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fixture.Path)!);
        var start = Guid.NewGuid();
        var end = Guid.NewGuid();
        File.WriteAllText(
            fixture.Path,
            $$"""
            {
              "format": "mes.workflow.graph",
              "schemaVersion": 2,
              "workflows": [
                {
                  "id": "{{Guid.NewGuid()}}",
                  "schemaVersion": 2,
                  "name": "Disconnected draft",
                  "nodes": [
                    { "id": "{{start}}", "nodeTypeId": "core.start", "name": "Start" },
                    { "id": "{{end}}", "nodeTypeId": "core.end", "name": "End" }
                  ],
                  "edges": []
                }
              ]
            }
            """);

        var store = new WorkflowStore(fixture.Path);
        var document = Assert.Single(store.LoadDocuments());
        var workflow = WorkflowDocumentMapper.FromGraph(document);

        Assert.Equal(ContractWorkflowGraphDocument.CurrentSchemaVersion, document.SchemaVersion);
        Assert.Empty(workflow.Edges);
        Assert.All(workflow.Nodes, node => Assert.Empty(node.NextNodeIds));
    }

    [Fact]
    public void V3_import_preserves_structured_condition_and_reports_unknown_nested_fields()
    {
        var start = Guid.NewGuid();
        var end = Guid.NewGuid();
        var edgeId = Guid.NewGuid();
        var result = new WorkflowDocumentImporter().Import(
            $$"""
            {
              "id": "{{Guid.NewGuid()}}",
              "schemaVersion": 3,
              "name": "Condition import",
              "nodes": [
                { "id": "{{start}}", "nodeTypeId": "core.start", "name": "Start" },
                { "id": "{{end}}", "nodeTypeId": "core.end", "name": "End" }
              ],
              "edges": [
                {
                  "id": "{{edgeId}}",
                  "sourceNodeId": "{{start}}",
                  "sourcePort": "success",
                  "targetNodeId": "{{end}}",
                  "targetPort": "in",
                  "kind": "Success",
                  "conditionExpression": {
                    "schemaVersion": "1.0",
                    "source": "RunInput",
                    "sourceKey": "sample.pressure",
                    "valueType": "Decimal",
                    "operator": "LessThanOrEqual",
                    "compareValue": "12",
                    "missingValueBehavior": "Fail",
                    "futureField": "must be reported"
                  }
                }
              ]
            }
            """,
            "condition-v3.json");

        Assert.True(result.CanImport);
        var expression = Assert.Single(Assert.Single(result.Documents).Edges).ConditionExpression;
        Assert.NotNull(expression);
        Assert.Equal(ContractWorkflowConditionValueSource.RunInput, expression!.Source);
        Assert.Equal(ContractWorkflowConditionOperator.LessThanOrEqual, expression.Operator);
        Assert.Contains(result.Report.Issues, issue =>
            issue.Code == "UNSUPPORTED_FIELD_REPORTED" &&
            issue.Location!.EndsWith("conditionExpression.futureField", StringComparison.Ordinal));
    }

    [Fact]
    public void Compatibility_import_reports_legacy_experiment_conversion_and_unsupported_fields()
    {
        using var fixture = new TempWorkflowFile();
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var edgeId = Guid.NewGuid();
        var viewModel = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path));
        var initialCount = viewModel.GraphDocuments.Count;

        var imported = viewModel.ImportCompatibilityJson(
            $$"""
            {
              "nodes": [
                {
                  "id": "{{sourceId}}",
                  "title": "Import",
                  "description": "Load sample data",
                  "type": "DataImport",
                  "x": 10,
                  "y": 20,
                  "script": "must be reported"
                },
                {
                  "id": "{{targetId}}",
                  "title": "Read D160",
                  "type": "InstrumentOperation",
                  "x": 300,
                  "y": 20
                }
              ],
              "connections": [
                {
                  "id": "{{edgeId}}",
                  "sourceNodeId": "{{sourceId}}",
                  "targetNodeId": "{{targetId}}",
                  "condition": "sample.ready",
                  "color": "#34A853"
                }
              ]
            }
            """,
            "legacy-experiment.json");

        Assert.True(imported);
        Assert.Equal(initialCount + 1, viewModel.GraphDocuments.Count);
        var graph = viewModel.SelectedGraphDocument!;
        Assert.Equal("legacy.experiment.DataImport", graph.Nodes.Single(node => node.Id == sourceId).NodeTypeId);
        Assert.Equal(10, graph.Layouts.Single(layout => layout.NodeId == sourceId).X);
        var connection = Assert.Single(graph.Edges);
        Assert.Equal("sample.ready", connection.Condition);
        Assert.Equal("#34A853", connection.Metadata["legacyColor"]);
        Assert.Equal(WorkflowImportSourceFormat.LegacyExperimentFlow, viewModel.LastImportReport!.SourceFormat);
        Assert.Contains(viewModel.LastImportReport.Issues, issue => issue.Code == "UNSUPPORTED_FIELD_REPORTED");
        Assert.Contains(viewModel.LastImportReport.Issues, issue => issue.Code == "LEGACY_NODE_TYPE_REQUIRES_REVIEW");
        Assert.True(viewModel.HasImportReport);
        Assert.Contains("旧实验流程设计器", viewModel.ImportReportSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Compatibility_import_rejects_future_schema_without_changing_editor_state()
    {
        using var fixture = new TempWorkflowFile();
        var viewModel = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path));
        var beforeIds = viewModel.GraphDocuments.Select(document => document.Id).ToArray();

        var imported = viewModel.ImportCompatibilityJson(
            $$"""
            {
              "format": "mes.workflow.graph",
              "schemaVersion": {{ContractWorkflowGraphDocument.CurrentSchemaVersion + 1}},
              "workflows": [
                {
                  "id": "{{Guid.NewGuid()}}",
                  "name": "Future",
                  "nodes": []
                }
              ]
            }
            """,
            "future.json");

        Assert.False(imported);
        Assert.Equal(beforeIds, viewModel.GraphDocuments.Select(document => document.Id));
        Assert.True(viewModel.LastImportReport!.HasErrors);
        Assert.Contains(viewModel.LastImportReport.Issues, issue => issue.Code == "GRAPH_SCHEMA_UNSUPPORTED");
        Assert.Contains("未修改", viewModel.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compatibility_import_rejects_future_envelope_when_child_declares_current_schema()
    {
        using var fixture = new TempWorkflowFile();
        var viewModel = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path));
        var beforeIds = viewModel.GraphDocuments.Select(document => document.Id).ToArray();

        var imported = viewModel.ImportCompatibilityJson(
            $$"""
            {
              "format": "mes.workflow.graph",
              "schemaVersion": {{ContractWorkflowGraphDocument.CurrentSchemaVersion + 1}},
              "workflows": [
                {
                  "id": "{{Guid.NewGuid()}}",
                  "schemaVersion": {{ContractWorkflowGraphDocument.CurrentSchemaVersion}},
                  "name": "Future envelope",
                  "nodes": []
                }
              ]
            }
            """,
            "future-envelope.json");

        Assert.False(imported);
        Assert.Equal(beforeIds, viewModel.GraphDocuments.Select(document => document.Id));
        Assert.Contains(viewModel.LastImportReport!.Issues, issue => issue.Code == "GRAPH_SCHEMA_UNSUPPORTED");
    }

    [Fact]
    public void Compatibility_import_rejects_legacy_dangling_next_node_reference()
    {
        var start = Guid.NewGuid();
        var result = new WorkflowDocumentImporter().Import(
            $$"""
            [
              {
                "id": "{{Guid.NewGuid()}}",
                "name": "Dangling legacy next node",
                "nodes": [
                  {
                    "id": "{{start}}",
                    "type": 0,
                    "name": "Start",
                    "order": 1,
                    "nextNodeIds": ["{{Guid.NewGuid()}}"]
                  }
                ]
              }
            ]
            """,
            "dangling-legacy-next.json");

        Assert.False(result.CanImport);
        Assert.Contains(result.Report.Issues, issue => issue.Code == "LEGACY_NEXT_NODE_DANGLING");
    }

    [Fact]
    public void Compatibility_import_rejects_dangling_and_duplicate_layouts()
    {
        var workflowId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var result = new WorkflowDocumentImporter().Import(
            $$"""
            {
              "id": "{{workflowId}}",
              "schemaVersion": 2,
              "name": "Invalid layouts",
              "nodes": [
                { "id": "{{nodeId}}", "nodeTypeId": "core.start", "name": "Start" }
              ],
              "layouts": [
                { "nodeId": "{{nodeId}}", "x": 10, "y": 20 },
                { "nodeId": "{{nodeId}}", "x": 30, "y": 40 },
                { "nodeId": "{{Guid.NewGuid()}}", "x": 50, "y": 60 }
              ]
            }
            """,
            "invalid-layouts.json");

        Assert.False(result.CanImport);
        Assert.Contains(result.Report.Issues, issue => issue.Code == "GRAPH_LAYOUT_DUPLICATE");
        Assert.Contains(result.Report.Issues, issue => issue.Code == "GRAPH_LAYOUT_DANGLING");
    }

    [Fact]
    public void Compatibility_import_rejects_duplicate_workflow_ids_without_partial_application()
    {
        using var fixture = new TempWorkflowFile();
        var workflowId = Guid.NewGuid();
        var viewModel = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path));
        var beforeIds = viewModel.GraphDocuments.Select(document => document.Id).ToArray();

        var imported = viewModel.ImportCompatibilityJson(
            $$"""
            {
              "format": "mes.workflow.graph",
              "schemaVersion": 2,
              "workflows": [
                { "id": "{{workflowId}}", "schemaVersion": 2, "name": "First", "nodes": [] },
                { "id": "{{workflowId}}", "schemaVersion": 2, "name": "Second", "nodes": [] }
              ]
            }
            """,
            "duplicates.json");

        Assert.False(imported);
        Assert.Equal(beforeIds, viewModel.GraphDocuments.Select(document => document.Id));
        Assert.Contains(viewModel.LastImportReport!.Issues, issue => issue.Code == "GRAPH_WORKFLOW_ID_DUPLICATE");
    }

    [Fact]
    public void Legacy_wpf_array_with_graph_named_fields_is_not_misclassified_as_graph_document()
    {
        using var fixture = new TempWorkflowFile();
        var start = Guid.NewGuid();
        var end = Guid.NewGuid();
        var edge = Guid.NewGuid();
        var result = new WorkflowDocumentImporter().Import(
            $$"""
            [
              {
                "id": "{{Guid.NewGuid()}}",
                "name": "Legacy with edges",
                "nodes": [
                  { "id": "{{start}}", "type": 0, "name": "Start", "order": 1 },
                  { "id": "{{end}}", "type": 5, "name": "End", "order": 2 }
                ],
                "edges": [
                  {
                    "id": "{{edge}}",
                    "sourceNodeId": "{{start}}",
                    "sourcePort": "success",
                    "targetNodeId": "{{end}}",
                    "targetPort": "in",
                    "kind": 0
                  }
                ],
                "layouts": [
                  { "nodeId": "{{start}}", "x": 10, "y": 20 },
                  { "nodeId": "{{end}}", "x": 220, "y": 20 }
                ]
              }
            ]
            """,
            "legacy-wpf.json");

        Assert.True(result.CanImport);
        Assert.Equal(WorkflowImportSourceFormat.LegacyWpfArray, result.Report.SourceFormat);
        var graph = Assert.Single(result.Documents);
        Assert.Equal("core.start", graph.Nodes.Single(node => node.Id == start).NodeTypeId);
        Assert.Equal("core.end", graph.Nodes.Single(node => node.Id == end).NodeTypeId);
        Assert.Equal(edge, Assert.Single(graph.Edges).Id);
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
    public void Property_panel_node_delete_removes_associated_graph_edges_and_layout()
    {
        using var fixture = new TempWorkflowFile();
        var viewModel = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path));
        var workflow = viewModel.SelectedWorkflow!;
        var deleted = workflow.Nodes[1];
        viewModel.SelectedNode = deleted;

        Assert.Contains(workflow.Edges, edge =>
            edge.SourceNodeId == deleted.Id || edge.TargetNodeId == deleted.Id);
        Assert.Contains(workflow.Layouts, layout => layout.NodeId == deleted.Id);

        viewModel.DeleteNodeCommand.Execute(null);

        Assert.DoesNotContain(workflow.Edges, edge =>
            edge.SourceNodeId == deleted.Id || edge.TargetNodeId == deleted.Id);
        Assert.DoesNotContain(workflow.Layouts, layout => layout.NodeId == deleted.Id);
        Assert.DoesNotContain(viewModel.SelectedGraphDocument!.Edges, edge =>
            edge.SourceNodeId == deleted.Id || edge.TargetNodeId == deleted.Id);
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
    public void Main_editor_canvas_keeps_selection_properties_layout_and_parameters_in_sync()
    {
        using var fixture = new TempWorkflowFile();
        var viewModel = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path));
        var workflow = viewModel.SelectedWorkflow!;
        var canvas = Assert.IsType<WorkflowCanvasSpikeViewModel>(viewModel.CanvasViewModel);
        var selectedId = workflow.Nodes.Skip(1).First().Id;

        Assert.Contains("统一流程文档", canvas.Status, StringComparison.Ordinal);
        Assert.Equal("已加载 5 条连接。", canvas.LastConnectionMessage);

        var nodeBeforeViewportChange = workflow.Nodes[0];
        canvas.UpdateViewport(50, 75, 1.25);
        Assert.Equal(new ContractWorkflowViewport { X = 50, Y = 75, Zoom = 1.25 }, workflow.Viewport);
        Assert.Equal(new ContractWorkflowViewport { X = 50, Y = 75, Zoom = 1.25 }, viewModel.SelectedGraphDocument!.Viewport);
        Assert.Same(nodeBeforeViewportChange, workflow.Nodes[0]);

        canvas.SelectedNodes.Clear();
        canvas.SelectedNodes.Add(canvas.Nodes.Single(node => node.Id == selectedId));
        Assert.Equal(selectedId, viewModel.SelectedNode?.Id);

        var originalName = viewModel.SelectedNode!.Name;
        viewModel.SelectedNode!.Name = "Updated from property panel";
        Assert.Equal(
            "Updated from property panel",
            canvas.Nodes.Single(node => node.Id == selectedId).Name);
        Assert.Equal(
            "Updated from property panel",
            viewModel.SelectedGraphDocument!.Nodes.Single(node => node.Id == selectedId).Name);
        canvas.UndoCommand.Execute(null);
        Assert.Equal(originalName, workflow.Nodes.Single(node => node.Id == selectedId).Name);
        Assert.Equal(originalName, viewModel.SelectedGraphDocument!.Nodes.Single(node => node.Id == selectedId).Name);
        canvas.RedoCommand.Execute(null);
        Assert.Equal("Updated from property panel", workflow.Nodes.Single(node => node.Id == selectedId).Name);
        Assert.Equal("Updated from property panel", viewModel.SelectedGraphDocument!.Nodes.Single(node => node.Id == selectedId).Name);

        viewModel.AddNodeAt(WorkflowNodeType.Wait, 615, 225);
        var addedId = viewModel.SelectedNode!.Id;
        Assert.Equal(
            viewModel.SelectedNode.Ports.Count,
            viewModel.SelectedNode.Ports.Select(port => port.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var parameter = Assert.Single(viewModel.SelectedNode.Parameters);
        parameter.Value = "12";
        var canvasNode = canvas.Nodes.Single(node => node.Id == addedId);
        Assert.Equal(new System.Windows.Point(615, 225), canvasNode.Location);
        Assert.Contains("12", canvas.Document.Nodes.Single(node => node.Id == addedId)
            .Configuration[MesControlAgv.Domain.Workflows.WorkflowGraphContractAdapter.ParametersConfigurationKey]);

        canvasNode.Location = new System.Windows.Point(720, 340);
        canvas.CommitNodeLocationsCommand.Execute(null);

        var committed = workflow.Nodes.Single(node => node.Id == addedId);
        Assert.Equal(720, committed.X);
        Assert.Equal(340, committed.Y);
        Assert.Equal("12", Assert.Single(committed.Parameters).Value);
    }

    [Fact]
    public void Main_editor_canvas_deletion_updates_the_single_workflow_projection()
    {
        using var fixture = new TempWorkflowFile();
        var viewModel = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path));
        var canvas = Assert.IsType<WorkflowCanvasSpikeViewModel>(viewModel.CanvasViewModel);
        var deletedId = canvas.Nodes.Last().Id;

        canvas.SelectNode(deletedId);
        canvas.DeleteCommand.Execute(null);

        Assert.DoesNotContain(viewModel.Nodes, node => node.Id == deletedId);
        Assert.DoesNotContain(canvas.Document.Nodes, node => node.Id == deletedId);
        Assert.DoesNotContain(viewModel.SelectedGraphDocument!.Nodes, node => node.Id == deletedId);
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

