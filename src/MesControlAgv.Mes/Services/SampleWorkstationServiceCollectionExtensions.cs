using MesControlAgv.Application;

namespace MesControlAgv.Mes.Services;

public static class SampleWorkstationServiceCollectionExtensions
{
    public static IServiceCollection AddSampleWorkstationGateway(
        this IServiceCollection services, IConfiguration configuration)
    {
        var address = configuration["SampleWorkstationGateway:AdapterBaseUrl"]
            ?? configuration["Adapter:BaseUrl"] ?? "http://localhost:5041/";
        if (!Uri.TryCreate(address.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("SampleWorkstationGateway:AdapterBaseUrl must be an HTTP(S) address.");
        // Exceeds the Adapter's maximum configurable timeout (60s) by default.
        var timeoutSeconds = configuration.GetValue("SampleWorkstationGateway:TimeoutSeconds", 70);
        if (timeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException("SampleWorkstationGateway:TimeoutSeconds must be between 1 and 300.");
        services.AddHttpClient<SampleWorkstationAdapterClient>(client =>
        {
            client.BaseAddress = baseUri;
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<ISampleWorkstationReader>(provider => provider.GetRequiredService<SampleWorkstationAdapterClient>());
        services.AddScoped<ISampleWorkstationCommands>(provider => provider.GetRequiredService<SampleWorkstationAdapterClient>());
        services.AddScoped<ISampleWorkstationCapabilityReader>(provider => provider.GetRequiredService<SampleWorkstationAdapterClient>());
        return services;
    }
}
