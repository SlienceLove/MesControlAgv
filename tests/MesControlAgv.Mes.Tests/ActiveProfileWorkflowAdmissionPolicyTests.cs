using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Services;

namespace MesControlAgv.Mes.Tests;

public sealed class ActiveProfileWorkflowAdmissionPolicyTests
{
    [Fact]
    public void Active_profile_accepts_enabled_workflow_stations()
    {
        var policy = new ActiveProfileWorkflowAdmissionPolicy(ProfileConfiguration.Default);

        var issues = policy.Validate(CreateVersion("SAMPLE_01"));

        Assert.Empty(issues);
    }

    [Fact]
    public void Active_profile_rejects_missing_or_disabled_workflow_stations()
    {
        var profile = ProfileConfiguration.Default;
        var policy = new ActiveProfileWorkflowAdmissionPolicy(profile with
        {
            Stations = profile.Stations
                .Select(station => station.StationId == "SAMPLE_01" ? station with { Enabled = false } : station)
                .ToArray()
        });

        var disabledIssues = policy.Validate(CreateVersion("SAMPLE_01"));
        var missingIssues = policy.Validate(CreateVersion("UNKNOWN_01"));

        Assert.Equal(ActiveProfileWorkflowAdmissionPolicy.StationUnavailableCode, Assert.Single(disabledIssues).Code);
        Assert.Equal(ActiveProfileWorkflowAdmissionPolicy.StationUnavailableCode, Assert.Single(missingIssues).Code);
    }

    private static WorkflowVersion CreateVersion(string targetStation)
    {
        var workflowId = Guid.NewGuid();
        return new WorkflowVersion
        {
            WorkflowId = workflowId,
            Version = 1,
            Definition = new WorkflowDefinition
            {
                Id = workflowId,
                Name = "Profile admission",
                Nodes =
                [
                    new WorkflowNode
                    {
                        Id = Guid.NewGuid(),
                        Type = WorkflowNodeType.Move,
                        Name = "Move",
                        TargetStation = targetStation,
                        Order = 1
                    }
                ]
            },
            Status = WorkflowVersionStatus.Published,
            PublishStatus = WorkflowPublishStatus.Published
        };
    }
}
