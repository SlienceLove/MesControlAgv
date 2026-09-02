using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace MesControlAgv.Wpf.Services;

internal sealed class LocalSimulatorRuntime : IDisposable
{
    private static readonly TimeSpan HealthRequestTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private readonly List<Process> _ownedProcesses;
    private bool _disposed;

    private LocalSimulatorRuntime(List<Process> ownedProcesses)
    {
        _ownedProcesses = ownedProcesses;
    }

    internal static bool ShouldManageLocalServices(
        string runtimeMode,
        Uri simulatorUrl,
        Uri adapterUrl,
        Uri mesUrl,
        string? configuredValue)
    {
        if (!runtimeMode.Equals("simulator", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var allLocal = simulatorUrl.IsLoopback && adapterUrl.IsLoopback && mesUrl.IsLoopback;
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return allLocal;
        }

        if (!bool.TryParse(configuredValue, out var manageLocalServices))
        {
            throw new InvalidOperationException("WPF_MANAGE_LOCAL_SERVICES must be either 'true' or 'false'.");
        }

        if (manageLocalServices && !allLocal)
        {
            throw new InvalidOperationException("WPF can manage only loopback Simulator, Adapter, and MES endpoints.");
        }

        return manageLocalServices;
    }

    internal static async Task<LocalSimulatorRuntime> StartAsync(
        Uri simulatorUrl,
        Uri adapterUrl,
        Uri mesUrl,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        simulatorUrl = NormalizeLoopbackUrl(simulatorUrl, "Simulator");
        adapterUrl = NormalizeLoopbackUrl(adapterUrl, "Adapter");
        mesUrl = NormalizeLoopbackUrl(mesUrl, "MES");
        var serviceEnvironment = ResolveServiceEnvironment();

        var dataDirectory = ResolveDataDirectory();
        var services = new[]
        {
            CreateService(
                "Simulator",
                "simulator",
                simulatorUrl,
                "Simulator",
                "MesControlAgv.Simulator.dll",
                new Dictionary<string, string>(),
                serviceEnvironment),
            CreateService(
                "Adapter",
                "adapter",
                adapterUrl,
                "Adapter",
                "MesControlAgv.Adapter.dll",
                new Dictionary<string, string>
                {
                    ["Simulator__BaseUrl"] = simulatorUrl.AbsoluteUri,
                    ["ConnectionStrings__Adapter"] = $"Data Source={Path.Combine(dataDirectory, "adapter.db")}"
                },
                serviceEnvironment),
            CreateService(
                "MES",
                "mes",
                mesUrl,
                "Mes",
                "MesControlAgv.Mes.dll",
                new Dictionary<string, string>
                {
                    ["Adapter__BaseUrl"] = adapterUrl.AbsoluteUri,
                    ["ConnectionStrings__Mes"] = $"Data Source={Path.Combine(dataDirectory, "mes.db")}"
                },
                serviceEnvironment)
        };

        var ownedProcesses = new List<Process>();
        using var healthClient = new HttpClient { Timeout = HealthRequestTimeout };
        try
        {
            foreach (var service in services)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"正在检查 {service.Name}...");
                if (await IsHealthyAsync(healthClient, service, cancellationToken))
                {
                    progress?.Report($"{service.Name} 已就绪");
                    continue;
                }

                if (IsPortInUse(service.Url.Port))
                {
                    throw new InvalidOperationException(
                        $"{service.Name} port {service.Url.Port} is already in use but did not return the expected health response.");
                }

                progress?.Report($"正在启动 {service.Name}...");
                var process = StartService(service);
                ownedProcesses.Add(process);

                progress?.Report($"正在等待 {service.Name} 就绪...");
                await WaitForHealthAsync(healthClient, service, process, cancellationToken);
            }

            return new LocalSimulatorRuntime(ownedProcesses);
        }
        catch
        {
            StopProcesses(ownedProcesses);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopProcesses(_ownedProcesses);
        _ownedProcesses.Clear();
    }

    private static LocalServiceDefinition CreateService(
        string name,
        string healthName,
        Uri url,
        string runtimeFolder,
        string assemblyName,
        IReadOnlyDictionary<string, string> environmentVariables,
        string environmentName)
    {
        var workingDirectory = Path.Combine(AppContext.BaseDirectory, "services", runtimeFolder);
        var assemblyPath = Path.Combine(workingDirectory, assemblyName);
        return new LocalServiceDefinition(
            name,
            healthName,
            url,
            workingDirectory,
            assemblyPath,
            environmentVariables,
            environmentName);
    }

    private static string ResolveServiceEnvironment()
    {
        var configured = Environment.GetEnvironmentVariable("WPF_LOCAL_SERVICE_ENVIRONMENT");
        if (string.IsNullOrWhiteSpace(configured)) return "FieldSimulation";

        var normalized = configured.Trim();
        if (!normalized.Equals("Development", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Equals("FieldSimulation", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "WPF_LOCAL_SERVICE_ENVIRONMENT must be Development or FieldSimulation.");
        }

        return normalized;
    }

    private static string ResolveDataDirectory()
    {
        var configuredPath = Environment.GetEnvironmentVariable("WPF_LOCAL_DATA_PATH");
        var dataDirectory = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MesControlAgv", "local-simulator")
            : configuredPath;

        dataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        return dataDirectory;
    }

    private static Uri NormalizeLoopbackUrl(Uri url, string serviceName)
    {
        if (!url.IsAbsoluteUri || !url.IsLoopback || !url.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{serviceName} local service URL must use HTTP on a loopback address.");
        }

        if (url.Port <= 0 || !string.IsNullOrWhiteSpace(url.Query) || !string.IsNullOrWhiteSpace(url.Fragment) || url.AbsolutePath != "/")
        {
            throw new InvalidOperationException($"{serviceName} local service URL must contain only an HTTP loopback host and port.");
        }

        return new Uri($"{url.Scheme}://{url.Authority}/", UriKind.Absolute);
    }

    private static Process StartService(LocalServiceDefinition service)
    {
        if (!File.Exists(service.AssemblyPath))
        {
            throw new FileNotFoundException(
                $"{service.Name} runtime files are missing. Build MesControlAgv.Wpf before starting the desktop client.",
                service.AssemblyPath);
        }

        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(dotnetHost))
        {
            dotnetHost = "dotnet";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetHost,
            WorkingDirectory = service.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(service.AssemblyPath);
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(service.Url.AbsoluteUri.TrimEnd('/'));
        startInfo.ArgumentList.Add("--environment");
        startInfo.ArgumentList.Add(service.EnvironmentName);
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = service.EnvironmentName;
        startInfo.Environment["DOTNET_ENVIRONMENT"] = service.EnvironmentName;

        foreach (var entry in service.EnvironmentVariables)
        {
            startInfo.Environment[entry.Key] = entry.Value;
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException($"{service.Name} process could not be started.");
    }

    private static async Task WaitForHealthAsync(
        HttpClient healthClient,
        LocalServiceDefinition service,
        Process process,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(StartupTimeout);
        do
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"{service.Name} exited before becoming healthy (exit code {process.ExitCode}).");
            }

            if (await IsHealthyAsync(healthClient, service, cancellationToken))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"{service.Name} did not become healthy at {service.Url} within {StartupTimeout.TotalSeconds:0} seconds.");
    }

    private static async Task<bool> IsHealthyAsync(
        HttpClient healthClient,
        LocalServiceDefinition service,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await healthClient.GetAsync(new Uri(service.Url, "health"), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty("service", out var serviceProperty) &&
                   document.RootElement.TryGetProperty("status", out var statusProperty) &&
                   string.Equals(serviceProperty.GetString(), service.HealthName, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(statusProperty.GetString(), "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsPortInUse(int port)
    {
        return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);
    }

    private static void StopProcesses(IEnumerable<Process> processes)
    {
        foreach (var process in processes.Reverse())
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private sealed record LocalServiceDefinition(
        string Name,
        string HealthName,
        Uri Url,
        string WorkingDirectory,
        string AssemblyPath,
        IReadOnlyDictionary<string, string> EnvironmentVariables,
        string EnvironmentName);
}
