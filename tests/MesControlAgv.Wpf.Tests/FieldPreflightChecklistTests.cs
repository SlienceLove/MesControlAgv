using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class FieldPreflightChecklistTests
{
    [Fact]
    public void Read_only_profile_maps_static_values_but_keeps_live_gates_for_field_confirmation()
    {
        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = RepositoryRoot(),
            RuntimeMode = "physical",
            ManageLocalServices = "false",
            AdapterConfigurationPath = Fixture("physical-readonly-adapter.json")
        });

        var checklist = FieldPreflightChecklistBuilder.Build(report);

        Assert.True(checklist.OfflineConfigurationAcceptable);
        Assert.False(checklist.CanDetermineGo);
        Assert.True(checklist.RequiresFieldConfirmation);
        Assert.Equal(FieldPreflightInputStatus.KnownOffline, Item(checklist, "ADAPTER_RUN_MODE").Status);
        Assert.Equal(FieldPreflightInputStatus.KnownOffline, Item(checklist, "AGV_ACQUIRE_CONTROL").Status);
        Assert.Equal(FieldPreflightInputStatus.KnownOffline, Item(checklist, "AGV_PUSH").Status);
        Assert.Equal(FieldPreflightInputStatus.NeedsFieldConfirmation, Item(checklist, "AGV_MINIMUM_CONFIDENCE").Status);
        Assert.Equal(FieldPreflightInputStatus.NeedsFieldConfirmation, Item(checklist, "CONTROL_OWNER").Status);
        Assert.Equal(FieldPreflightInputStatus.NeedsFieldConfirmation, Item(checklist, "MAP_IDENTITY").Status);
        Assert.Contains("不判定 GO", checklist.DecisionText, StringComparison.Ordinal);
    }

    [Fact]
    public void Standard_mode_is_an_offline_block_for_a_read_only_field_preflight()
    {
        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = RepositoryRoot(),
            RuntimeMode = "physical",
            ManageLocalServices = "false",
            AdapterConfigurationPath = Fixture("physical-standard-conflict-adapter.json")
        });

        var checklist = FieldPreflightChecklistBuilder.Build(report);

        Assert.True(checklist.HasOfflineBlocks);
        Assert.False(checklist.OfflineConfigurationAcceptable);
        Assert.Equal(FieldPreflightInputStatus.BlockedByOfflineConfiguration, Item(checklist, "STARTUP_CONFIGURATION").Status);
        Assert.Equal(FieldPreflightInputStatus.BlockedByOfflineConfiguration, Item(checklist, "ADAPTER_RUN_MODE").Status);
        Assert.Equal(FieldPreflightInputStatus.BlockedByOfflineConfiguration, Item(checklist, "AGV_ACQUIRE_CONTROL").Status);
        Assert.Equal(FieldPreflightInputStatus.BlockedByOfflineConfiguration, Item(checklist, "AGV_PUSH").Status);
        Assert.False(checklist.CanDetermineGo);
    }

    [Fact]
    public void Simulator_mode_marks_physical_inputs_not_applicable_without_claiming_field_readiness()
    {
        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput
        {
            BaseDirectory = RepositoryRoot(),
            RuntimeMode = "simulator",
            ManageLocalServices = "false",
            AdapterConfigurationPath = Fixture("simulator-adapter.json")
        });

        var checklist = FieldPreflightChecklistBuilder.Build(report);

        Assert.False(checklist.CanDetermineGo);
        Assert.Equal(FieldPreflightInputStatus.NotApplicable, Item(checklist, "RUNTIME_MODE").Status);
        Assert.Contains("现场", checklist.SummaryText, StringComparison.Ordinal);
    }

    private static FieldPreflightInputItem Item(FieldPreflightChecklist checklist, string code) =>
        Assert.Single(checklist.Items.Where(item => item.Code == code));

    private static string Fixture(string fileName) =>
        Path.Combine(RepositoryRoot(), "tests", "MesControlAgv.Wpf.Tests", "fixtures", "startup-diagnostics", fileName);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MesControlAgv.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
