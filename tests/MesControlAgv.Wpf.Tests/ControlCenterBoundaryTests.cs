using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Modules;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class ControlCenterBoundaryTests
{
    [Fact]
    public void Standard_module_registry_exposes_ordered_extension_points()
    {
        var registry = ControlCenterModuleRegistry.CreateStandard();

        Assert.Equal(5, registry.Modules.Count);
        Assert.Equal(
            new[]
            {
                ControlCenterModuleIds.TaskMonitor,
                ControlCenterModuleIds.AgvCommunication,
                ControlCenterModuleIds.BatchImport,
                ControlCenterModuleIds.KpiDashboard,
                ControlCenterModuleIds.WorkflowDesigner
            },
            registry.Modules.Select(module => module.Id));
        Assert.True(registry.IsEnabled(ControlCenterModuleIds.WorkflowDesigner));
    }

    [Fact]
    public void Registry_rejects_duplicate_module_ids()
    {
        var registry = new ControlCenterModuleRegistry();
        registry.Register(new TestModule(new("task-monitor", "Task monitor", 10)));

        Assert.Throws<InvalidOperationException>(() =>
            registry.Register(new TestModule(new("task-monitor", "Duplicate", 20))));
    }

    [Fact]
    public void Agv_row_exposes_capabilities_for_command_gating()
    {
        var snapshot = new AgvDashboardSnapshot(
            Online: true,
            ControlOwner: "adapter",
            CurrentStationId: "SAMPLE_01",
            CurrentTaskId: Guid.NewGuid(),
            AgvId: "AGV-02",
            Capabilities: new AgvCapabilitiesResponse(
                SupportsPause: false,
                SupportsResume: true,
                SupportsCancel: false,
                SupportsEmergencyStop: false,
                SupportsLift: true,
                SupportsBarcode: false,
                SupportsStationConfirmation: true));

        var row = new AgvRowViewModel(snapshot);

        Assert.False(row.SupportsPause);
        Assert.True(row.SupportsResume);
        Assert.False(row.SupportsCancel);
        Assert.Contains("\u5347\u964D", row.CapabilitySummary);
        Assert.False(row.Supports("pause"));
        Assert.True(row.Supports("resume"));
    }

    private sealed class TestModule(ControlCenterModuleDescriptor descriptor) : IControlCenterModule
    {
        public ControlCenterModuleDescriptor Descriptor { get; } = descriptor;
    }
}

