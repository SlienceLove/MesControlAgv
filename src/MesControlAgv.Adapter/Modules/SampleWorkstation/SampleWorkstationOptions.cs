using Microsoft.Extensions.Configuration;

namespace MesControlAgv.Adapter.Modules.SampleWorkstation;

public sealed record SampleWorkstationOptions
{
    public const string SectionName = "Devices:SampleWorkstation";

    public string DeviceId { get; init; } = "SAMPLE-WORKSTATION-01";
    public string EquipmentNo { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = "http://127.0.0.1:8082/Service/";
    public bool Enabled { get; init; }
    public bool ControlEnabled { get; init; }
    public int RequestTimeoutMs { get; init; } = 3000;
    public int MaximumPageSize { get; init; } = 100;

    public static SampleWorkstationOptions BindAndValidate(IConfiguration configuration)
    {
        var options = configuration.GetSection(SectionName).Get<SampleWorkstationOptions>() ?? new();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DeviceId);
        if (options.RequestTimeoutMs is < 100 or > 60000)
            throw new InvalidOperationException($"{SectionName}:RequestTimeoutMs must be between 100 and 60000.");
        if (options.MaximumPageSize is < 1 or > 500)
            throw new InvalidOperationException($"{SectionName}:MaximumPageSize must be between 1 and 500.");
        if (options.ControlEnabled)
        {
            throw new InvalidOperationException(
                $"{SectionName}:ControlEnabled must remain false while the workstation module is read-only.");
        }

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"{SectionName}:BaseUrl must be an absolute HTTP or HTTPS URL.");
        }

        if (options.Enabled && string.IsNullOrWhiteSpace(options.EquipmentNo))
            throw new InvalidOperationException($"{SectionName}:EquipmentNo is required when the device is enabled.");

        return options with
        {
            DeviceId = options.DeviceId.Trim(),
            EquipmentNo = options.EquipmentNo.Trim(),
            BaseUrl = options.BaseUrl.TrimEnd('/') + "/"
        };
    }
}
