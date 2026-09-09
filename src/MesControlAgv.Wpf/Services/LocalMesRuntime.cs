using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace MesControlAgv.Wpf.Services;

internal sealed class LocalMesRuntime : IDisposable
{
    private static readonly TimeSpan HealthRequestTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private readonly List<Process> _ownedProcesses;
    private bool _disposed;

    private LocalMesRuntime(List<Process> ownedProcesses) => _ownedProcesses = ownedProcesses;

    internal static async Task<LocalMesRuntime> StartAsync(
        Uri clientUrl,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!clientUrl.IsLoopback || !clientUrl.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("本机 MES 地址必须是 HTTP 回环地址。\n");
        }

        using var healthClient = new HttpClient { Timeout = HealthRequestTimeout };
        var service = new LocalMesService(
            clientUrl,
            Path.Combine(AppContext.BaseDirectory, "services", "Mes", "MesControlAgv.Mes.dll"),
            ResolveEnvironment());

        progress?.Report("正在检查本机 MES...");
        if (await IsHealthyAsync(healthClient, service.ClientUrl, cancellationToken))
        {
            progress?.Report("本机 MES 已在运行");
            return new LocalMesRuntime([]);
        }

        if (IsPortInUse(clientUrl.Port))
        {
            throw new InvalidOperationException(
                $"MES 端口 {clientUrl.Port} 已被占用，但未返回 MES 健康响应。请检查占用进程后再启动 WPF。");
        }

        if (!File.Exists(service.AssemblyPath))
        {
            throw new FileNotFoundException(
                "MES 运行文件不存在，请先重新构建或发布 WPF。",
                service.AssemblyPath);
        }

        progress?.Report("正在启动本机 MES...");
        var process = StartService(service);
        try
        {
            progress?.Report("正在等待 MES 就绪...");
            await WaitForHealthAsync(healthClient, service.ClientUrl, process, cancellationToken);
            progress?.Report("本机 MES 已就绪");
            return new LocalMesRuntime([process]);
        }
        catch
        {
            StopProcesses([process]);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopProcesses(_ownedProcesses);
        _ownedProcesses.Clear();
    }

    private static string ResolveEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("WPF_LOCAL_MES_ENVIRONMENT");
        if (string.IsNullOrWhiteSpace(value)) return "PhysicalAcceptance";
        var normalized = value.Trim();
        if (normalized is not ("Development" or "FieldSimulation" or "PhysicalAcceptance"))
        {
            throw new InvalidOperationException(
                "WPF_LOCAL_MES_ENVIRONMENT 必须是 Development、FieldSimulation 或 PhysicalAcceptance。" );
        }

        return normalized;
    }

    private static Process StartService(LocalMesService service)
    {
        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(dotnetHost)) dotnetHost = "dotnet";
        var dataDirectory = EnsureDataDirectory(service.AssemblyPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetHost,
            WorkingDirectory = Path.GetDirectoryName(service.AssemblyPath)!,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(service.AssemblyPath);
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add($"http://0.0.0.0:{service.ClientUrl.Port}");
        startInfo.ArgumentList.Add("--environment");
        startInfo.ArgumentList.Add(service.EnvironmentName);
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = service.EnvironmentName;
        startInfo.Environment["DOTNET_ENVIRONMENT"] = service.EnvironmentName;
        startInfo.Environment["ShineLabTcp__Enabled"] = "true";
        startInfo.Environment["ShineLabTcp__ListenAddress"] = "0.0.0.0";
        startInfo.Environment["ShineLabTcp__Port"] = "5500";
        startInfo.Environment["Adapter__BaseUrl"] = "http://127.0.0.1:5141/";
        // The published WPF bundle does not contain a writable `data` folder
        // next to the copied MES runtime. Use an absolute path so SQLite can
        // create the database before the health check runs.
        startInfo.Environment["ConnectionStrings__Mes"] =
            $"Data Source={Path.Combine(dataDirectory, "mes.db")}";

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("MES 进程无法启动。" );
    }

    internal static string EnsureDataDirectory(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        var assemblyDirectory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))
            ?? throw new InvalidOperationException("MES 程序目录无法解析。" );
        var dataDirectory = Path.Combine(assemblyDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        return dataDirectory;
    }

    private static async Task WaitForHealthAsync(
        HttpClient client,
        Uri url,
        Process process,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(StartupTimeout);
        do
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"MES 在健康检查前退出，退出码 {process.ExitCode}。" );
            }

            if (await IsHealthyAsync(client, url, cancellationToken)) return;
            await Task.Delay(250, cancellationToken);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"MES 未能在 {StartupTimeout.TotalSeconds:0} 秒内就绪。" );
    }

    private static async Task<bool> IsHealthyAsync(HttpClient client, Uri baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(new Uri(baseUrl, "health"), cancellationToken);
            if (!response.IsSuccessStatusCode) return false;
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty("service", out var service) &&
                   string.Equals(service.GetString(), "mes", StringComparison.OrdinalIgnoreCase) &&
                   document.RootElement.TryGetProperty("status", out var status) &&
                   string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (JsonException) { return false; }
    }

    private static bool IsPortInUse(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);

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
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
        }
    }

    private sealed record LocalMesService(Uri ClientUrl, string AssemblyPath, string EnvironmentName);
}
