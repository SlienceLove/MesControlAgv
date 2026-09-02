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
            .AddJsonFile(FileName, optional: false, reloadOnChange: false)
            .AddEnvironmentVariables()
            .AddCommandLine(args);
    }
}
