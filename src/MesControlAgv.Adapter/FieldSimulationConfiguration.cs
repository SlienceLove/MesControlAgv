using Microsoft.Extensions.Configuration;

namespace MesControlAgv.Adapter;

/// <summary>
/// Loads the field LM map as a complete simulation profile. Clearing the default
/// JSON sources prevents .NET configuration array merging from duplicating station
/// codes when the legacy Development profile is present.
/// </summary>
public static class FieldSimulationConfiguration
{
    public const string EnvironmentName = "FieldSimulation";
    public const string FileName = "appsettings.FieldSimulation.json";
    public const string MultiAgvProfileEnvironmentVariable = "MES_FIELD_SIMULATION_MULTI_AGV";
    public const string MultiAgvFileName = "appsettings.FieldSimulation.MultiAgv.json";

    public static void ReplaceDefaultSources(
        ConfigurationManager configuration,
        string contentRootPath,
        string[] args)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        ArgumentNullException.ThrowIfNull(args);

        configuration.Sources.Clear();
        configuration
            .SetBasePath(contentRootPath)
            .AddJsonFile(FileName, optional: false, reloadOnChange: false);

        // The three-vehicle profile is deliberately opt-in.  The ordinary
        // FieldSimulation profile remains the single-AGV safe default used by
        // the WPF local startup path.
        if (IsMultiAgvProfileRequested())
            configuration.AddJsonFile(MultiAgvFileName, optional: false, reloadOnChange: false);

        configuration.AddEnvironmentVariables().AddCommandLine(args);
    }

    private static bool IsMultiAgvProfileRequested() =>
        bool.TryParse(Environment.GetEnvironmentVariable(MultiAgvProfileEnvironmentVariable), out var enabled) && enabled;
}
