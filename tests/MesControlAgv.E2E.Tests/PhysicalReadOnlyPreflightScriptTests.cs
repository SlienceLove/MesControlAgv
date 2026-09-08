using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
namespace MesControlAgv.E2E.Tests;

public sealed class PhysicalReadOnlyPreflightScriptTests
{
    [Fact]
    public async Task Script_replays_full_preflight_against_local_tcp_and_always_stops_owned_adapter()
    {
        if (!OperatingSystem.IsWindows()) return;

        await using var controller = new CommittedPhysicalProfileFakeController();
        var repositoryRoot = FindRepositoryRoot();
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"mes-physical-readonly-script-{Guid.NewGuid():N}");
        var artifactRoot = Path.Combine(temporaryRoot, "evidence");
        var runId = $"offline-replay-{Guid.NewGuid():N}";
        var adapterPort = ReserveEphemeralPort();
        var statePath = Path.Combine(artifactRoot, runId, "adapter-state.json");
        var evidencePath = Path.Combine(artifactRoot, runId, "physical-readonly-evidence.json");
        var completed = false;

        try
        {
            var startInfo = CreateStartInfo(repositoryRoot, artifactRoot, runId, adapterPort, controller);
            var execution = await ExecuteScriptAsync(startInfo, artifactRoot);
            var stdout = execution.StandardOutput;
            var stderr = execution.StandardError;
            Assert.True(
                execution.ExitCode == 0,
                $"Script exited with {execution.ExitCode}.{Environment.NewLine}STDOUT:{Environment.NewLine}{stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{stderr}");
            Assert.True(File.Exists(evidencePath), $"Evidence was not written. Output:{Environment.NewLine}{stdout}");
            Assert.False(File.Exists(statePath));

            using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(evidencePath));
            var root = evidence.RootElement;
            Assert.Equal(
                "mes.physical-read-only-preflight-session/1.0",
                root.GetProperty("schema").GetString());
            Assert.Equal("READ-ONLY-PREFLIGHT-COMPLETED", root.GetProperty("status").GetString());
            Assert.True(root.GetProperty("adapter").GetProperty("health").GetProperty("ok").GetBoolean());
            Assert.Equal(
                "read-only-preflight",
                root.GetProperty("adapter").GetProperty("health").GetProperty("value")
                    .GetProperty("runMode").GetString());
            Assert.True(root.GetProperty("agv").GetProperty("preflight").GetProperty("ok").GetBoolean());
            var preflight = root.GetProperty("agv").GetProperty("preflight").GetProperty("value");
            Assert.False(preflight.GetProperty("dispatchPermitted").GetBoolean());
            Assert.True(preflight.GetProperty("mapEvidence").GetProperty("isControllerAuthoritative").GetBoolean());
            Assert.Contains(
                "adapter_does_not_hold_control",
                preflight.GetProperty("blockingReasons").EnumerateArray().Select(item => item.GetString()));
            Assert.Contains(
                "automatic_dispatch_disabled",
                preflight.GetProperty("blockingReasons").EnumerateArray().Select(item => item.GetString()));
            Assert.True(root.GetProperty("cleanup").GetProperty("attempted").GetBoolean());
            Assert.True(root.GetProperty("cleanup").GetProperty("success").GetBoolean());
            Assert.False(root.GetProperty("safetyBoundary").GetProperty("writesAttempted").GetBoolean());
            Assert.False(root.GetProperty("safetyBoundary").GetProperty("agvCommandPortAttempted").GetBoolean());
            Assert.False(root.GetProperty("safetyBoundary").GetProperty("agvControlAcquisitionAttempted").GetBoolean());
            Assert.False(root.GetProperty("safetyBoundary").GetProperty("modbusAttempted").GetBoolean());

            Assert.Equal(
                [1060, 1110, 1101, 1021, 1000, 1300, 1301, 1302],
                controller.StatusApiIds);
            Assert.Equal([4011], controller.ControlApiIds);
            Assert.Empty(controller.CommandApiIds);
            Assert.Empty(controller.OtherApiIds);
            await AssertPortClosedAsync(adapterPort);
            completed = true;
        }
        finally
        {
            if (completed && Directory.Exists(temporaryRoot))
            {
                try { Directory.Delete(temporaryRoot, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    [Fact]
    public async Task Script_preserves_failed_preflight_evidence_and_still_stops_owned_adapter()
    {
        if (!OperatingSystem.IsWindows()) return;

        await using var controller = new CommittedPhysicalProfileFakeController(failPreflight: true);
        var repositoryRoot = FindRepositoryRoot();
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"mes-physical-readonly-script-{Guid.NewGuid():N}");
        var artifactRoot = Path.Combine(temporaryRoot, "evidence");
        var runId = $"offline-failure-{Guid.NewGuid():N}";
        var adapterPort = ReserveEphemeralPort();
        var statePath = Path.Combine(artifactRoot, runId, "adapter-state.json");
        var evidencePath = Path.Combine(artifactRoot, runId, "physical-readonly-evidence.json");
        var completed = false;

        try
        {
            var startInfo = CreateStartInfo(repositoryRoot, artifactRoot, runId, adapterPort, controller);
            var execution = await ExecuteScriptAsync(startInfo, artifactRoot);
            Assert.Equal(2, execution.ExitCode);
            Assert.True(
                File.Exists(evidencePath),
                $"Failure evidence was not written. Output:{Environment.NewLine}{execution.StandardOutput}");
            Assert.False(File.Exists(statePath));

            using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(evidencePath));
            var root = evidence.RootElement;
            Assert.Equal("FAILED", root.GetProperty("status").GetString());
            Assert.Equal("read_agv_preflight", root.GetProperty("failureStage").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("decision").ValueKind);
            Assert.False(root.GetProperty("agv").GetProperty("preflight").GetProperty("ok").GetBoolean());
            Assert.True(root.GetProperty("cleanup").GetProperty("attempted").GetBoolean());
            Assert.True(root.GetProperty("cleanup").GetProperty("success").GetBoolean());
            Assert.False(root.GetProperty("safetyBoundary").GetProperty("writesAttempted").GetBoolean());
            Assert.False(root.GetProperty("safetyBoundary").GetProperty("agvCommandPortAttempted").GetBoolean());
            Assert.Equal([1060], controller.StatusApiIds);
            Assert.Empty(controller.ControlApiIds);
            Assert.Empty(controller.CommandApiIds);
            Assert.Empty(controller.OtherApiIds);
            await AssertPortClosedAsync(adapterPort);
            completed = true;
        }
        finally
        {
            if (completed && Directory.Exists(temporaryRoot))
            {
                try { Directory.Delete(temporaryRoot, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        string repositoryRoot,
        string artifactRoot,
        string runId,
        int adapterPort,
        CommittedPhysicalProfileFakeController controller)
    {
        var buildConfiguration = typeof(PhysicalReadOnlyPreflightScriptTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        var adapterDll = Path.Combine(
            repositoryRoot,
            "src",
            "MesControlAgv.Adapter",
            "bin",
            buildConfiguration,
            "net8.0",
            "MesControlAgv.Adapter.dll");
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        AddArguments(
            startInfo,
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", Path.Combine(repositoryRoot, "scripts", "Invoke-PhysicalReadOnlyPreflight.ps1"),
            "-ControllerHost", IPAddress.Loopback.ToString(),
            "-AdapterUrl", $"http://127.0.0.1:{adapterPort}",
            "-ArtifactRoot", artifactRoot,
            "-RunId", runId,
            "-DllPath", adapterDll,
            "-StartupTimeoutSeconds", "30",
            "-RequestTimeoutSeconds", "15",
            "-AgvStatusPort", controller.StatusPort.ToString(),
            "-AgvCommandPort", controller.CommandPort.ToString(),
            "-AgvControlPort", controller.ControlPort.ToString(),
            "-AgvOtherPort", controller.OtherPort.ToString(),
            "-AgvPushPort", controller.PushPort.ToString());
        return startInfo;
    }

    private static async Task<ScriptExecution> ExecuteScriptAsync(
        ProcessStartInfo startInfo,
        string artifactRoot)
    {
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync(); } catch (InvalidOperationException) { }
            var timedOutStdout = await stdoutTask;
            var timedOutStderr = await stderrTask;
            throw new Xunit.Sdk.XunitException(
                $"Physical read-only preflight script exceeded 20 seconds. Evidence root: {artifactRoot}" +
                $"{Environment.NewLine}STDOUT:{Environment.NewLine}{timedOutStdout}" +
                $"{Environment.NewLine}STDERR:{Environment.NewLine}{timedOutStderr}");
        }

        return new ScriptExecution(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static void AddArguments(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
    }

    private static int ReserveEphemeralPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static async Task AssertPortClosedAsync(int port)
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        }
        catch (SocketException)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Assert.Fail($"Adapter port {port} was still accepting connections after script cleanup.");
    }

    private static string FindRepositoryRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var candidates = new[]
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
            Path.GetDirectoryName(sourceFilePath) ?? string.Empty
        };

        foreach (var candidate in candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate)))
        {
            DirectoryInfo? directory = new(candidate);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "MesControlAgv.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException("MesControlAgv.sln was not found from the test or working directory.");
    }

    private sealed record ScriptExecution(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}

internal sealed class CommittedPhysicalProfileFakeController : IAsyncDisposable
{
    private static readonly string[] StationIds = ["LM1", "LM2", "LM4", "LM5", "LM6", "LM7"];
    private static readonly (string From, string To)[] DirectedEdges =
    [
        ("LM1", "LM4"),
        ("LM4", "LM1"),
        ("LM4", "LM5"),
        ("LM5", "LM4"),
        ("LM1", "LM5"),
        ("LM6", "LM2"),
        ("LM6", "LM1"),
        ("LM7", "LM6"),
        ("LM1", "LM7"),
        ("LM2", "LM1")
    ];

    private readonly object _gate = new();
    private readonly List<int> _statusApiIds = [];
    private readonly List<int> _commandApiIds = [];
    private readonly List<int> _controlApiIds = [];
    private readonly List<int> _otherApiIds = [];
    private readonly TcpApiListener _status;
    private readonly TcpApiListener _command;
    private readonly TcpApiListener _control;
    private readonly TcpApiListener _other;

    private readonly bool _failPreflight;

    public CommittedPhysicalProfileFakeController(bool failPreflight = false)
    {
        _failPreflight = failPreflight;
        _status = new TcpApiListener(packet => HandleStatusAsync(packet.ApiId));
        _command = new TcpApiListener(packet => HandleUnexpectedAsync(packet.ApiId, _commandApiIds));
        _control = new TcpApiListener(packet => HandleControlAsync(packet.ApiId, packet.Payload));
        _other = new TcpApiListener(packet => HandleUnexpectedAsync(packet.ApiId, _otherApiIds));
    }

    public int StatusPort => _status.Port;
    public int CommandPort => _command.Port;
    public int ControlPort => _control.Port;
    public int OtherPort => _other.Port;
    public int PushPort => _other.Port;
    public IReadOnlyList<int> StatusApiIds => Snapshot(_statusApiIds);
    public IReadOnlyList<int> CommandApiIds => Snapshot(_commandApiIds);
    public IReadOnlyList<int> ControlApiIds => Snapshot(_controlApiIds);
    public IReadOnlyList<int> OtherApiIds => Snapshot(_otherApiIds);

    private Task<byte[]> HandleStatusAsync(ushort apiId)
    {
        Record(_statusApiIds, apiId);
        return Task.FromResult(apiId switch
        {
            1060 when _failPreflight => Json("{\"ret_code\":500,\"err_msg\":\"offline injected preflight failure\"}"),
            1060 => Json("{\"ret_code\":0,\"locked\":false,\"nick_name\":null}"),
            1110 => Json("{\"ret_code\":0,\"current_station\":\"LM1\",\"task_status_list\":[]}"),
            1101 => Json("{\"ret_code\":0,\"mode\":1,\"reloc_status\":1,\"confidence\":0.9566,\"emergency\":false,\"blocked\":false,\"fatals\":[],\"errors\":[],\"fork_auto_flag\":true}"),
            1021 => Json("{\"ret_code\":0,\"reloc_status\":1}"),
            1000 => Json("{\"ret_code\":0,\"model\":\"W500-SZ\",\"version\":\"offline-fake-controller\"}"),
            1300 => Json("{\"ret_code\":0,\"current_map\":\"guangzhou606\",\"maps\":[\"guangzhou606.smap\"]}"),
            1301 => JsonSerializer.SerializeToUtf8Bytes(new
            {
                ret_code = 0,
                stations = StationIds.Select(id => new { id, type = "LocationMark" })
            }),
            1302 => Json("{\"ret_code\":0,\"map_info\":[{\"name\":\"guangzhou606.smap\",\"md5\":\"9bd67a8b01f4da2617ce67e5f8a8d6b1\"}]}"),
            _ => throw new InvalidOperationException($"Unexpected status API {apiId}.")
        });
    }

    private Task<byte[]> HandleControlAsync(ushort apiId, byte[] payload)
    {
        Record(_controlApiIds, apiId);
        if (apiId != 4011) throw new InvalidOperationException($"Unexpected control API {apiId}.");
        using var request = JsonDocument.Parse(payload);
        if (request.RootElement.GetProperty("map_name").GetString() != "guangzhou606")
            throw new InvalidOperationException("Unexpected map name in 4011 request.");

        return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(new
        {
            header = new
            {
                mapType = "smap",
                mapName = "guangzhou606",
                minPos = new { x = 0, y = 0 },
                maxPos = new { x = 10, y = 10 },
                resolution = 0.05,
                version = "1.0.6"
            },
            advancedPointList = StationIds.Select((stationId, index) => new
            {
                instanceName = stationId,
                pos = new { x = index, y = index }
            }),
            advancedCurveList = DirectedEdges.Select((edge, index) => new
            {
                instanceName = $"edge-{index + 1}",
                startPos = new { instanceName = edge.From, pos = new { x = index, y = index } },
                endPos = new { instanceName = edge.To, pos = new { x = index + 1, y = index + 1 } },
                controlPos1 = new { x = index, y = index },
                controlPos2 = new { x = index + 1, y = index + 1 },
                property = Array.Empty<object>()
            })
        }));
    }

    private Task<byte[]> HandleUnexpectedAsync(ushort apiId, List<int> target)
    {
        Record(target, apiId);
        throw new InvalidOperationException($"Unexpected API {apiId} on a non-read channel.");
    }

    private void Record(List<int> target, ushort apiId)
    {
        lock (_gate) target.Add(apiId);
    }

    private IReadOnlyList<int> Snapshot(List<int> source)
    {
        lock (_gate) return source.ToArray();
    }

    private static byte[] Json(string value) => Encoding.UTF8.GetBytes(value);

    public async ValueTask DisposeAsync()
    {
        await _status.DisposeAsync();
        await _command.DisposeAsync();
        await _control.DisposeAsync();
        await _other.DisposeAsync();
    }
}
