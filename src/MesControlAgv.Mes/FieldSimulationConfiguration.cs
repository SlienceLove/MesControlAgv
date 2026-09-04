using Microsoft.Extensions.Configuration;

namespace MesControlAgv.Mes;

/// <summary>Loads the complete LM field-simulation profile without array merging.</summary>
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

        // Multi-AGV dispatch is a local simulator-only verification mode, not
        // part of the default FieldSimulation or physical-acceptance profile.
        if (IsMultiAgvProfileRequested())
            configuration.AddJsonFile(MultiAgvFileName, optional: false, reloadOnChange: false);

        configuration.AddEnvironmentVariables().AddCommandLine(args);
    }

    private static bool IsMultiAgvProfileRequested() =>
        bool.TryParse(Environment.GetEnvironmentVariable(MultiAgvProfileEnvironmentVariable), out var enabled) && enabled;
}
