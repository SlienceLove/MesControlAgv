using Microsoft.Extensions.Configuration;

namespace MesControlAgv.Mes;

/// <summary>
/// Loads the complete physical-acceptance profile without merging legacy
/// Development arrays into the field station/map catalog.
/// </summary>
public static class PhysicalAcceptanceConfiguration
{
    public const string EnvironmentName = "PhysicalAcceptance";
    public const string FileName = "appsettings.PhysicalAcceptance.json";

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
