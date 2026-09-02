using Microsoft.Extensions.Configuration;

namespace MesControlAgv.Mes;

/// <summary>Loads the complete LM field-simulation profile without array merging.</summary>
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
