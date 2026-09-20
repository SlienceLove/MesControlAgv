using Microsoft.Extensions.Configuration;
using MesControlAgv.Contracts;

namespace MesControlAgv.Adapter.Modules.SampleWorkstation;

public sealed record SampleWorkstationOptions
{
    public const string SectionName = "Devices:SampleWorkstation";

    public string DeviceId { get; init; } = "SAMPLE-WORKSTATION-01";
    public string EquipmentNo { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = "http://127.0.0.1:8082/Service/";
    public bool Enabled { get; init; }
    public bool ControlEnabled { get; init; }
    /// <summary>
    /// Explicit vendor confirmation gate. It is false for the repository
    /// placeholder and must be enabled only in a site-approved deployment.
    /// </summary>
    public bool ProtocolConfirmed { get; init; }
    /// <summary>Durable reservation journal used to prevent post-restart replays.</summary>
    public string OperationJournalPath { get; init; } = "data/sample-workstation-operations.json";
    public int RequestTimeoutMs { get; init; } = 20000;
    public int MaximumPageSize { get; init; } = 100;
    // Absent in the supplied field DLL; update configuration after a vendor upgrade.
    public SampleWorkstationProtocolOperation[] UnsupportedProtocolOperations { get; init; } =
    [
        SampleWorkstationProtocolOperation.WorkflowList,
        SampleWorkstationProtocolOperation.WorkflowDetails,
        SampleWorkstationProtocolOperation.WorkflowTemplate,
        SampleWorkstationProtocolOperation.MaterialTypeList,
        SampleWorkstationProtocolOperation.PlatformLayoutList,
        SampleWorkstationProtocolOperation.PlatformLayoutDetails,
        SampleWorkstationProtocolOperation.PlatformLayoutTemplate
    ];

    public static SampleWorkstationOptions BindAndValidate(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var options = section.Get<SampleWorkstationOptions>() ?? new();
        // Bind the explicit list separately: the default binder appends to an
        // initialized array. An explicit [] means the vendor supports all reads.
        var unsupported = section.GetChildren().FirstOrDefault(child =>
            string.Equals(child.Key, nameof(UnsupportedProtocolOperations), StringComparison.OrdinalIgnoreCase));
        if (unsupported is not null)
            options = options with
            {
                UnsupportedProtocolOperations = unsupported.Get<SampleWorkstationProtocolOperation[]>(
                    binder => binder.ErrorOnUnknownConfiguration = true) ?? []
            };
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DeviceId);
        if (options.UnsupportedProtocolOperations is null
            || options.UnsupportedProtocolOperations.Any(operation => !Enum.IsDefined(operation)))
            throw new InvalidOperationException($"{SectionName}:UnsupportedProtocolOperations contains an invalid operation.");
        if (options.RequestTimeoutMs is < 100 or > 60000)
            throw new InvalidOperationException($"{SectionName}:RequestTimeoutMs must be between 100 and 60000.");
        if (options.MaximumPageSize is < 1 or > 500)
            throw new InvalidOperationException($"{SectionName}:MaximumPageSize must be between 1 and 500.");
        if (options.ControlEnabled && !options.Enabled)
            throw new InvalidOperationException($"{SectionName}:Enabled must be true before ControlEnabled can be enabled.");

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"{SectionName}:BaseUrl must be an absolute HTTP or HTTPS URL.");
        }

        if (options.Enabled && string.IsNullOrWhiteSpace(options.EquipmentNo))
            throw new InvalidOperationException($"{SectionName}:EquipmentNo is required when the device is enabled.");

        if (options.ControlEnabled && options.BaseUrl.Contains(".invalid", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{SectionName}:BaseUrl must be replaced with the approved workstation endpoint before control is enabled.");

        return options with
        {
            DeviceId = options.DeviceId.Trim(),
            EquipmentNo = options.EquipmentNo.Trim(),
            BaseUrl = options.BaseUrl.TrimEnd('/') + "/",
            OperationJournalPath = string.IsNullOrWhiteSpace(options.OperationJournalPath)
                ? "data/sample-workstation-operations.json"
                : options.OperationJournalPath.Trim()
        };
    }
}
