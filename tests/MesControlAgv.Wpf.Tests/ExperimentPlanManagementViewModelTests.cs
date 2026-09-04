using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class ExperimentPlanManagementViewModelTests
{
    [Fact]
    public async Task Refresh_projects_versions_completeness_and_issue_navigation()
    {
        var fixture = PlanFixture.Create(ExperimentPlanStatus.Draft, valid: false);
        using var viewModel = new ExperimentPlanManagementViewModel(fixture.Client)
        {
            Reason = "Review completeness"
        };

        await viewModel.RefreshAsync();

        Assert.Equal(fixture.Plan.PlanId, Assert.Single(viewModel.Plans).PlanId);
        Assert.Equal(fixture.Plan.Version, Assert.Single(viewModel.Versions).Version);
        Assert.Equal("1 项错误", viewModel.Plans[0].Completeness);
        Assert.Equal("校验未通过 / 1 项问题", viewModel.ValidationSummary);
        var issue = Assert.Single(viewModel.ValidationIssues);
        viewModel.SelectedValidationIssue = issue;
        Assert.Equal(3, viewModel.DetailTabIndex);
        Assert.True(viewModel.CanValidate);
        Assert.False(viewModel.CanPublish);
    }

    [Fact]
    public async Task New_plan_saves_structured_material_parameter_and_resource_draft()
    {
        var fixture = PlanFixture.Empty();
        using var viewModel = new ExperimentPlanManagementViewModel(fixture.Client)
        {
            Reason = "Create controlled plan"
        };
        await viewModel.RefreshAsync();

        viewModel.NewPlanCommand.Execute(null);
        viewModel.Name = "Anion acceptance";
        viewModel.Description = "Pinned plan draft";
        viewModel.ProfileProductId = "MES-PROFILE";
        viewModel.ProfileVersion = "1.0";
        viewModel.LayoutId = "lab-a";
        viewModel.AddMaterialCommand.Execute(null);
        var material = Assert.Single(viewModel.Materials);
        material.MaterialId = "sample";
        material.Name = "Sample vial";
        material.Quantity = 1;
        material.Unit = "vial";
        viewModel.AddParameterCommand.Execute(null);
        var parameter = Assert.Single(viewModel.Parameters);
        parameter.Name = "batch";
        parameter.Value = "B-42";
        viewModel.AddResourceCommand.Execute(null);
        var resource = Assert.Single(viewModel.ResourceRequirements);
        resource.ResourceType = ExperimentResourceTypeIds.Instrument;
        resource.ResourceId = "D160_01";

        Assert.True(viewModel.CanSaveDraft);
        await viewModel.SaveDraftAsync();

        Assert.NotNull(fixture.Client.LastSavedDraft);
        var request = fixture.Client.LastSavedDraft!;
        Assert.Equal("Anion acceptance", request.Draft.Name);
        Assert.Equal(fixture.WorkflowId, request.Draft.WorkflowId);
        Assert.Equal(3, request.Draft.WorkflowVersion);
        Assert.Equal("Sample vial", Assert.Single(request.Draft.MaterialRequirements).Name);
        Assert.Equal("B-42", request.Draft.DefaultParameters["batch"]);
        Assert.Equal("D160_01", Assert.Single(request.Draft.ResourceRequirements).ResourceId);
        Assert.False(viewModel.IsNewPlan);
        Assert.False(viewModel.IsDirty);
        Assert.Contains("草稿已保存", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task New_plan_composes_ordered_published_workflow_steps()
    {
        var fixture = PlanFixture.Empty();
        using var viewModel = new ExperimentPlanManagementViewModel(fixture.Client)
        {
            Reason = "Compose verified workflow templates"
        };
        await viewModel.RefreshAsync();

        viewModel.NewPlanCommand.Execute(null);
        viewModel.Name = "Composed experiment";
        viewModel.ProfileProductId = "MES-PROFILE";
        viewModel.ProfileVersion = "1.0";

        Assert.Single(viewModel.WorkflowSteps);
        viewModel.AddWorkflowStepCommand.Execute(null);
        Assert.Equal(2, viewModel.WorkflowSteps.Count);
        var second = viewModel.SelectedWorkflowStep!;
        second.Name = "第二段验证";
        viewModel.MoveWorkflowStepUpCommand.Execute(null);

        Assert.Same(second, viewModel.WorkflowSteps[0]);
        Assert.Equal(new[] { 1, 2 }, viewModel.WorkflowSteps.Select(step => step.Order).ToArray());

        await viewModel.SaveDraftAsync();

        var steps = fixture.Client.LastSavedDraft!.Draft.WorkflowSteps;
        Assert.Equal(2, steps.Count);
        Assert.Equal(new[] { 1, 2 }, steps.Select(step => step.Order).ToArray());
        Assert.All(steps, step =>
        {
            Assert.Equal(fixture.WorkflowId, step.WorkflowId);
            Assert.Equal(3, step.WorkflowVersion);
        });
        Assert.Equal(steps[0].WorkflowId, fixture.Client.LastSavedDraft.Draft.WorkflowId);
        Assert.Equal(steps[0].WorkflowVersion, fixture.Client.LastSavedDraft.Draft.WorkflowVersion);
    }

    [Fact]
    public async Task Verified_template_filter_hides_unselected_templates_and_keeps_current_selection()
    {
        var fixture = PlanFixture.Empty();
        var unverifiedId = fixture.Client.AddWorkflowTemplate("User workflow", isPreset: false, version: 4);
        var verifiedId = fixture.Client.AddWorkflowTemplate("Verified workflow", isPreset: true, version: 5);
        using var viewModel = new ExperimentPlanManagementViewModel(fixture.Client)
        {
            Reason = "Filter workflow catalog"
        };

        await viewModel.RefreshAsync();
        viewModel.NewPlanCommand.Execute(null);
        var selected = Assert.Single(viewModel.WorkflowSteps).SelectedWorkflowVersion;

        Assert.Contains(viewModel.PublishedWorkflowVersions, option => option.WorkflowId == unverifiedId);
        Assert.Contains(viewModel.PublishedWorkflowVersions, option => option.WorkflowId == verifiedId);

        viewModel.ShowVerifiedWorkflowTemplatesOnly = true;

        Assert.Same(selected, viewModel.WorkflowSteps[0].SelectedWorkflowVersion);
        Assert.Contains(selected, viewModel.PublishedWorkflowVersions);
        Assert.DoesNotContain(viewModel.PublishedWorkflowVersions, option => option.WorkflowId == unverifiedId);
        Assert.Contains(viewModel.PublishedWorkflowVersions, option => option.WorkflowId == verifiedId);
        Assert.Contains("仅系统/已验证模板", viewModel.WorkflowTemplateFilterSummary, StringComparison.Ordinal);

        viewModel.ShowVerifiedWorkflowTemplatesOnly = false;

        Assert.Same(selected, viewModel.WorkflowSteps[0].SelectedWorkflowVersion);
        Assert.Contains(viewModel.PublishedWorkflowVersions, option => option.WorkflowId == unverifiedId);
    }

    [Fact]
    public async Task Published_template_projects_capabilities_resources_duration_and_validation_metadata()
    {
        var fixture = PlanFixture.Empty();
        var workflowId = fixture.Client.AddWorkflowTemplate(
            "搬运检测模板",
            isPreset: true,
            version: 6,
            definition: new WorkflowDefinition
            {
                Name = "搬运检测模板",
                IsPreset = true,
                Nodes =
                [
                    new WorkflowNode
                    {
                        Type = WorkflowNodeType.Start,
                        NodeTypeId = WorkflowGraphNodeTypeIds.Start
                    },
                    new WorkflowNode
                    {
                        Type = WorkflowNodeType.Move,
                        NodeTypeId = WorkflowGraphNodeTypeIds.Move,
                        TargetStation = "LM7",
                        Configuration = new Dictionary<string, string?>
                        {
                            ["estimatedDurationMinutes"] = "4"
                        }
                    },
                    new WorkflowNode
                    {
                        Type = WorkflowNodeType.RobotProgram,
                        NodeTypeId = WorkflowGraphNodeTypeIds.RobotExecuteProgram,
                        Configuration = new Dictionary<string, string?>
                        {
                            ["estimatedDurationMinutes"] = "6"
                        }
                    },
                    new WorkflowNode
                    {
                        Type = WorkflowNodeType.End,
                        NodeTypeId = WorkflowGraphNodeTypeIds.End
                    }
                ]
            },
            validation: new WorkflowValidationResult
            {
                ValidatorVersion = "workflow-publication-v3",
                ValidatedAt = DateTimeOffset.Parse("2026-09-04T08:00:00Z"),
                ProfileProductId = "MES-AGV"
            },
            publishedAt: DateTimeOffset.Parse("2026-09-04T08:01:00Z"));
        using var viewModel = new ExperimentPlanManagementViewModel(fixture.Client)
        {
            Reason = "Project workflow metadata"
        };

        await viewModel.RefreshAsync();

        var option = Assert.Single(viewModel.PublishedWorkflowVersions.Where(item => item.WorkflowId == workflowId));
        Assert.Equal([WorkflowCapabilityIds.AgvNavigateToStation, WorkflowCapabilityIds.RobotExecuteProgram], option.CapabilityIds);
        Assert.Equal([ExperimentResourceTypeIds.Agv, ExperimentResourceTypeIds.RobotArm, ExperimentResourceTypeIds.Station], option.ResourceTypes);
        Assert.Equal(10, option.EstimatedDurationMinutes);
        Assert.Contains("预计 10 分钟", option.EstimatedDurationDisplay, StringComparison.Ordinal);
        Assert.Contains("已验证 2026-09-04", option.ValidationDisplay, StringComparison.Ordinal);
        Assert.Contains("AGV 到站", option.CapabilitiesDisplay, StringComparison.Ordinal);
        Assert.Contains("机械臂程序", option.CapabilitiesDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_composed_step_duration_is_explained_and_step_issue_selects_the_step()
    {
        var fixture = PlanFixture.Create(ExperimentPlanStatus.Draft, valid: null);
        var first = new ExperimentPlanWorkflowStep
        {
            StepId = Guid.NewGuid(),
            Order = 1,
            WorkflowId = fixture.WorkflowId,
            WorkflowVersion = 3,
            Name = "第一段"
        };
        var second = first with { StepId = Guid.NewGuid(), Order = 2, Name = "第二段" };
        fixture.Client.Upsert(fixture.Plan with { WorkflowSteps = [first, second] });
        using var viewModel = new ExperimentPlanManagementViewModel(fixture.Client)
        {
            Reason = "Review step duration"
        };

        await viewModel.RefreshAsync();

        Assert.Equal(2, viewModel.MissingWorkflowDurationCount);
        Assert.Contains("2 个步骤缺少预计时长", viewModel.WorkflowStepsValidationSummary, StringComparison.Ordinal);
        var issue = new ExperimentPlanValidationIssueItemViewModel(new ExperimentPlanValidationIssue
        {
            Code = ExperimentSchedulingIssueCodes.WorkflowStepInvalid,
            Message = "Composed workflow steps require a positive estimated duration.",
            Field = "workflowSteps[1].estimatedDurationMinutes"
        });
        viewModel.SelectedValidationIssue = issue;

        Assert.Equal(1, issue.StepIndex);
        Assert.Equal("步骤 2", issue.StepDisplay);
        Assert.Equal("步骤 2 · 预计时长", issue.FieldDisplay);
        Assert.Equal(4, viewModel.DetailTabIndex);
        Assert.Same(viewModel.WorkflowSteps[1], viewModel.SelectedWorkflowStep);
    }

    [Fact]
    public async Task Plan_keeps_unavailable_referenced_workflow_version_visible_and_explicitly_marked()
    {
        var fixture = PlanFixture.Create(ExperimentPlanStatus.Draft, valid: null);
        var step = new ExperimentPlanWorkflowStep
        {
            StepId = Guid.NewGuid(),
            Order = 1,
            WorkflowId = fixture.WorkflowId,
            WorkflowVersion = 99,
            Name = "Legacy instrument read"
        };
        fixture.Client.Upsert(fixture.Plan with
        {
            WorkflowVersion = 99,
            WorkflowSteps = [step]
        });
        using var viewModel = new ExperimentPlanManagementViewModel(fixture.Client)
        {
            Reason = "Review legacy reference"
        };

        await viewModel.RefreshAsync();

        var selected = Assert.Single(viewModel.WorkflowSteps).SelectedWorkflowVersion;
        Assert.NotNull(selected);
        Assert.False(selected!.IsAvailable);
        Assert.Equal("Legacy instrument read", selected.Name);
        Assert.Contains("已引用但不可用", selected.Display, StringComparison.Ordinal);
        Assert.Contains(selected, viewModel.PublishedWorkflowVersions);

        viewModel.ShowVerifiedWorkflowTemplatesOnly = true;

        Assert.Same(selected, viewModel.WorkflowSteps[0].SelectedWorkflowVersion);
        Assert.Contains(selected, viewModel.PublishedWorkflowVersions);
    }

    [Fact]
    public void Workflow_step_editor_roundtrips_parameter_overrides_without_data_loss()
    {
        var stepId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var step = new ExperimentPlanWorkflowStep
        {
            StepId = stepId,
            Order = 1,
            WorkflowId = workflowId,
            WorkflowVersion = 7,
            Name = "Instrument read",
            EstimatedDurationMinutes = 20,
            Parameters = new Dictionary<string, string?>
            {
                ["method"] = "anion",
                ["sampleVolume"] = "10"
            }
        };
        var option = new PublishedWorkflowVersionOption(workflowId, 7, "Instrument read");

        var editor = ExperimentPlanWorkflowStepEditorViewModel.From(step, option);
        var roundTripped = editor.ToContract(1);

        Assert.Equal(stepId, roundTripped.StepId);
        Assert.Equal(step.Parameters, roundTripped.Parameters);
        Assert.Equal("2 个步骤参数覆盖", editor.ParametersSummary);
    }

    [Fact]
    public void Workflow_step_editor_accepts_batch_parameter_overrides_and_rejects_invalid_text()
    {
        var editor = new ExperimentPlanWorkflowStepEditorViewModel
        {
            ParameterOverridesText = "method=anion; sampleVolume=10"
        };

        var contract = editor.ToContract(1);

        Assert.Equal("anion", contract.Parameters["method"]);
        Assert.Equal("10", contract.Parameters["sampleVolume"]);
        Assert.False(editor.HasParameterOverridesError);

        editor.ParameterOverridesText = "method=anion; =missing-name";

        Assert.True(editor.HasParameterOverridesError);
        Assert.Contains("参数名不能为空", editor.ParameterOverridesError, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => editor.ToContract(1));
    }

    [Fact]
    public async Task Lifecycle_commands_follow_draft_validated_published_and_next_draft_states()
    {
        var fixture = PlanFixture.Create(ExperimentPlanStatus.Draft, valid: null);
        using var viewModel = new ExperimentPlanManagementViewModel(fixture.Client)
        {
            Reason = "Lifecycle acceptance"
        };
        await viewModel.RefreshAsync();

        Assert.True(viewModel.ValidateCommand.CanExecute(null));
        Assert.False(viewModel.PublishCommand.CanExecute(null));
        await viewModel.ValidateAsync();

        Assert.Equal(1, fixture.Client.ValidateCalls);
        Assert.True(viewModel.PublishCommand.CanExecute(null));
        await viewModel.PublishAsync();

        Assert.Equal(1, fixture.Client.PublishCalls);
        Assert.True(viewModel.CreateNextDraftCommand.CanExecute(null));
        await viewModel.CreateNextDraftAsync();

        Assert.Equal(1, fixture.Client.NextDraftCalls);
        Assert.Equal(2, viewModel.SelectedVersion!.Version);
        Assert.Equal("草稿", viewModel.SelectedVersion.Status);
        Assert.False(viewModel.PublishCommand.CanExecute(null));
    }

    private sealed record PlanFixture(PlanClientStub Client, ExperimentPlan Plan, Guid WorkflowId)
    {
        public static PlanFixture Empty()
        {
            var workflowId = Guid.NewGuid();
            return new PlanFixture(new PlanClientStub(workflowId), new ExperimentPlan(), workflowId);
        }

        public static PlanFixture Create(ExperimentPlanStatus status, bool? valid)
        {
            var workflowId = Guid.NewGuid();
            var validation = valid is null
                ? null
                : new ExperimentPlanValidationResult
                {
                    ValidatorVersion = "1.0",
                    ValidatedAt = DateTimeOffset.Parse("2026-08-24T02:00:00Z"),
                    ValidatedBy = "planner",
                    Issues = valid.Value
                        ? []
                        :
                        [
                            new ExperimentPlanValidationIssue
                            {
                                Code = ExperimentSchedulingIssueCodes.ResourceNotConfigured,
                                Message = "Resource is not configured.",
                                Field = "resourceRequirements",
                                Resource = new ExperimentResourceReference
                                {
                                    ResourceType = ExperimentResourceTypeIds.Instrument,
                                    ResourceId = "D160_MISSING"
                                }
                            }
                        ]
                };
            var plan = new ExperimentPlan
            {
                PlanId = Guid.NewGuid(),
                Version = 1,
                Name = "Acceptance plan",
                WorkflowId = workflowId,
                WorkflowVersion = 3,
                Status = status,
                Validation = validation,
                ResourceRequirements =
                [
                    new ExperimentResourceRequirement
                    {
                        ResourceType = ExperimentResourceTypeIds.Instrument,
                        ResourceId = "D160_MISSING"
                    }
                ],
                UpdatedAt = DateTimeOffset.Parse("2026-08-24T02:00:00Z")
            };
            var client = new PlanClientStub(workflowId);
            client.Upsert(plan);
            return new PlanFixture(client, plan, workflowId);
        }
    }

    private sealed class PlanClientStub : IMesClient
    {
        private readonly Dictionary<Guid, List<ExperimentPlan>> _plans = [];
        private readonly Dictionary<Guid, WorkflowDefinition> _workflowDefinitions = [];
        private readonly Dictionary<Guid, List<WorkflowVersion>> _workflowVersions = [];
        public Guid WorkflowId { get; }
        public SaveExperimentPlanDraftRequest? LastSavedDraft { get; private set; }
        public int ValidateCalls { get; private set; }
        public int PublishCalls { get; private set; }
        public int NextDraftCalls { get; private set; }

        public PlanClientStub(Guid workflowId)
        {
            WorkflowId = workflowId;
            AddWorkflowTemplate("Published workflow", isPreset: false, version: 3, workflowId: workflowId);
        }

        public Guid AddWorkflowTemplate(
            string name,
            bool isPreset,
            int version,
            WorkflowDefinition? definition = null,
            WorkflowValidationResult? validation = null,
            DateTimeOffset? publishedAt = null)
        {
            var workflowId = Guid.NewGuid();
            AddWorkflowTemplate(name, isPreset, version, workflowId, definition, validation, publishedAt);
            return workflowId;
        }

        private void AddWorkflowTemplate(
            string name,
            bool isPreset,
            int version,
            Guid workflowId,
            WorkflowDefinition? definition = null,
            WorkflowValidationResult? validation = null,
            DateTimeOffset? publishedAt = null)
        {
            definition ??= new WorkflowDefinition();
            definition = definition with
            {
                Id = workflowId,
                Name = string.IsNullOrWhiteSpace(definition.Name) ? name : definition.Name,
                IsPreset = isPreset || definition.IsPreset
            };
            _workflowDefinitions[workflowId] = definition;
            _workflowVersions[workflowId] =
            [
                new WorkflowVersion
                {
                    WorkflowId = workflowId,
                    Version = version,
                    Definition = definition,
                    Status = WorkflowVersionStatus.Published,
                    PublishStatus = WorkflowPublishStatus.Published,
                    Validation = validation,
                    PublishedAt = publishedAt
                }
            ];
        }

        public Task<IReadOnlyList<WorkflowDefinition>> GetWorkflowsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkflowDefinition>>(_workflowDefinitions.Values.ToArray());

        public Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(
            Guid requestedWorkflowId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkflowVersion>>(
                _workflowVersions.GetValueOrDefault(requestedWorkflowId) ?? []);

        public Task<IReadOnlyList<ExperimentPlan>> GetExperimentPlansAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExperimentPlan>>(_plans.Values
                .Select(versions => versions.OrderByDescending(plan => plan.Version).First())
                .OrderBy(plan => plan.Name)
                .ToArray());

        public Task<IReadOnlyList<ExperimentPlan>> GetExperimentPlanVersionsAsync(
            Guid planId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExperimentPlan>>(
                _plans.GetValueOrDefault(planId)?.OrderByDescending(plan => plan.Version).ToArray() ?? []);

        public Task<ExperimentPlan> CreateExperimentPlanDraftAsync(
            SaveExperimentPlanDraftRequest request,
            CancellationToken cancellationToken)
        {
            LastSavedDraft = request;
            var plan = FromDraft(Guid.NewGuid(), 1, request.Draft);
            Upsert(plan);
            return Task.FromResult(plan);
        }

        public Task<ExperimentPlan> UpdateExperimentPlanDraftAsync(
            Guid planId,
            int version,
            SaveExperimentPlanDraftRequest request,
            CancellationToken cancellationToken)
        {
            LastSavedDraft = request;
            var plan = FromDraft(planId, version, request.Draft);
            Upsert(plan);
            return Task.FromResult(plan);
        }

        public Task<ExperimentPlan> ValidateExperimentPlanAsync(
            Guid planId,
            int version,
            ExperimentSchedulingActionRequest request,
            CancellationToken cancellationToken)
        {
            ValidateCalls++;
            var plan = Find(planId, version) with
            {
                Status = ExperimentPlanStatus.Validated,
                Validation = new ExperimentPlanValidationResult
                {
                    ValidatorVersion = "1.0",
                    ValidatedAt = DateTimeOffset.UtcNow,
                    ValidatedBy = request.Actor
                }
            };
            Upsert(plan);
            return Task.FromResult(plan);
        }

        public Task<ExperimentPlan> PublishExperimentPlanAsync(
            Guid planId,
            int version,
            ExperimentSchedulingActionRequest request,
            CancellationToken cancellationToken)
        {
            PublishCalls++;
            var plan = Find(planId, version) with { Status = ExperimentPlanStatus.Published };
            Upsert(plan);
            return Task.FromResult(plan);
        }

        public Task<ExperimentPlan> CreateNextExperimentPlanDraftAsync(
            Guid planId,
            int sourceVersion,
            ExperimentSchedulingActionRequest request,
            CancellationToken cancellationToken)
        {
            NextDraftCalls++;
            var source = Find(planId, sourceVersion);
            var plan = source with
            {
                Version = sourceVersion + 1,
                Status = ExperimentPlanStatus.Draft,
                Validation = null
            };
            Upsert(plan);
            return Task.FromResult(plan);
        }

        public void Upsert(ExperimentPlan plan)
        {
            if (!_plans.TryGetValue(plan.PlanId, out var versions))
            {
                versions = [];
                _plans.Add(plan.PlanId, versions);
            }
            versions.RemoveAll(item => item.Version == plan.Version);
            versions.Add(plan);
        }

        private ExperimentPlan Find(Guid planId, int version) =>
            _plans[planId].Single(plan => plan.Version == version);

        private static ExperimentPlan FromDraft(Guid planId, int version, ExperimentPlanDraft draft) => new()
        {
            PlanId = planId,
            Version = version,
            Name = draft.Name,
            Description = draft.Description,
            WorkflowId = draft.WorkflowId,
            WorkflowVersion = draft.WorkflowVersion,
            WorkflowSteps = draft.WorkflowSteps,
            Status = ExperimentPlanStatus.Draft,
            MaterialRequirements = draft.MaterialRequirements,
            DefaultParameters = draft.DefaultParameters,
            ResourceRequirements = draft.ResourceRequirements,
            ProfileProductId = draft.ProfileProductId,
            ProfileVersion = draft.ProfileVersion,
            LayoutId = draft.LayoutId,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        public Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DashboardTask>>([]);
        public Task<KpiDashboard> GetKpiDashboardAsync(DateOnly date, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<DashboardTaskDetail?> GetTaskDetailAsync(Guid taskId, CancellationToken cancellationToken) =>
            Task.FromResult<DashboardTaskDetail?>(null);
        public Task<AgvDashboardSnapshot> GetAgvSnapshotAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<DashboardTask> CreateTaskAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<DashboardTask> CreateTaskAsync(int sourceStationCode, int targetStationCode, int priority, string? description, string? externalId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<DashboardTask> MarkArrivedAsync(Guid taskId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<DashboardTask> ConfirmPickupAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<DashboardTask> ConfirmDropoffAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<DashboardTask> RetryAsync(Guid taskId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<DashboardTask> RecoverAsync(Guid taskId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<DashboardTask> CancelAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
