using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;

namespace MesControlAgv.Adapter.Tests;

public sealed class WorkstationImportEvidenceCliTests : IDisposable
{
    private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("workstation-evidence-tests-");
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("WS0123456789ABCDEF01")]
    [InlineData("MES-20260920014125514-84cac287ab024f2ebd33")]
    public async Task Real_cli_exports_exact_file_frame_and_manifest_without_changing_snapshot(string taskNo)
    {
        var fixture = CreateSnapshot(taskNo);
        var snapshotBefore = File.ReadAllBytes(fixture.Input);
        var result = await RunAsync(fixture.Input, fixture.Output);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Equal(snapshotBefore, File.ReadAllBytes(fixture.Input));
        Assert.Equal(3, Directory.GetFiles(fixture.Output).Length);
        var file = File.ReadAllBytes(Path.Combine(fixture.Output, taskNo + ".xlsx"));
        Assert.Equal(fixture.Bytes, file);
        Assert.True(SampleWorkstationTemplateFile.SameContent(fixture.Template, SampleWorkstationTemplateFile.Read(file)));

        // Decode the frame independently instead of calling EncodeUpload again.
        var frameBytes = File.ReadAllBytes(Path.Combine(fixture.Output, "original-request-body.txt"));
        var frame = Encoding.ASCII.GetString(frameBytes);
        var nameLength = Assert.Single(Convert.FromBase64String(frame[..4]));
        Assert.Equal(taskNo + ".xlsx", Encoding.UTF8.GetString(Convert.FromBase64String(frame.Substring(4, nameLength))));
        Assert.Equal(file, Convert.FromBase64String(frame[(4 + nameLength)..]));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Output, "manifest.json")));
        var root = manifest.RootElement;
        Assert.True(root.GetProperty("evidenceOnly").GetBoolean());
        Assert.Equal(taskNo, root.GetProperty("taskNo").GetString());
        Assert.Equal(taskNo.Length, root.GetProperty("taskNoLength").GetInt32());
        Assert.Equal(Hash(file), root.GetProperty("fileSha256").GetString());
        Assert.Equal(Hash(frameBytes), root.GetProperty("requestBodySha256").GetString());
        Assert.Equal("application/json", root.GetProperty("contentType").GetString());
        Assert.Equal(fixture.Template.Transfers, root.GetProperty("transfers").Deserialize<SampleWorkstationTransferRow[]>(WebJson));
        Assert.Equal(2, root.GetProperty("bottleBindings").GetArrayLength());
        using var summary = JsonDocument.Parse(result.Output);
        Assert.Equal(16, summary.RootElement.GetProperty("transfers").GetInt32());
    }

    [Theory]
    [InlineData("hash", "frozen SHA256")]
    [InlineData("task", "task numbers disagree")]
    [InlineData("filename", "bare .xlsx filename")]
    public async Task Invalid_snapshot_is_rejected_before_output_directory_is_created(string invalidField, string message)
    {
        var fixture = CreateSnapshot("WS0123456789ABCDEF01", invalidField);
        var before = File.ReadAllBytes(fixture.Input);
        var result = await RunAsync(fixture.Input, fixture.Output);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(message, result.Error);
        Assert.False(Directory.Exists(fixture.Output));
        Assert.Equal(before, File.ReadAllBytes(fixture.Input));
        Assert.Single(Directory.GetFiles(_workspace.FullName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_output_file_or_directory_is_preserved(bool directory)
    {
        var fixture = CreateSnapshot("WS0123456789ABCDEF01");
        var sentinel = directory
            ? Path.Combine(Directory.CreateDirectory(fixture.Output).FullName, "keep.txt")
            : fixture.Output;
        File.WriteAllText(sentinel, "Existing user evidence");
        var result = await RunAsync(fixture.Input, fixture.Output);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("must not already exist", result.Error);
        Assert.Equal("Existing user evidence", File.ReadAllText(sentinel));
        if (directory) Assert.Single(Directory.GetFileSystemEntries(fixture.Output));
    }

    [Fact]
    public async Task Repeated_export_refuses_to_overwrite_any_generated_evidence()
    {
        var fixture = CreateSnapshot("WS0123456789ABCDEF01");
        Assert.Equal(0, (await RunAsync(fixture.Input, fixture.Output)).ExitCode);
        var before = Directory.GetFiles(fixture.Output).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        var repeated = await RunAsync(fixture.Input, fixture.Output);
        Assert.Equal(1, repeated.ExitCode);
        Assert.Contains("must not already exist", repeated.Error);
        Assert.Equal(before.Count, Directory.GetFiles(fixture.Output).Length);
        foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(Path.Combine(fixture.Output, pair.Key)));
    }

    [Fact]
    public async Task Missing_arguments_returns_usage_without_creating_files()
    {
        var result = await RunAsync();
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Usage:", result.Error);
        Assert.Empty(Directory.GetFileSystemEntries(_workspace.FullName));
    }

    private Snapshot CreateSnapshot(string taskNo, string? invalidField = null)
    {
        var source = SampleWorkstationTemplateFile.Read(SampleWorkstationZeroTipTemplateTests.CapturedBytes());
        var template = source with
        {
            TaskNo = taskNo,
            Transfers = source.Transfers.Select(row => row with { VolumeMicroliters = 50 }).ToArray()
        };
        var bytes = SampleWorkstationTemplateFile.Write(template);
        var input = Path.Combine(_workspace.FullName, "prepared snapshot.json");
        File.WriteAllText(input, JsonSerializer.Serialize(new
        {
            prepared = new
            {
                vendorTaskNo = invalidField == "task" ? "OTHER" : taskNo,
                preparationId = Guid.NewGuid(), experimentJobId = Guid.NewGuid(),
                payload = new
                {
                    generatedTemplate = template,
                    generatedTemplateFileName = invalidField == "filename" ? "../outside.xlsx" : taskNo + ".xlsx",
                    generatedTemplateFileSha256 = invalidField == "hash" ? new string('0', 64) : Hash(bytes),
                    bottleBindings = new[] { new { bottleNumber = 1, sampleBarcode = "SOURCE-1" }, new { bottleNumber = 2, sampleBarcode = "SOURCE-2" } }
                }
            }
        }, WebJson));
        return new(input, Path.Combine(_workspace.FullName, "new output"), template, bytes);
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = _workspace.FullName
        };
        // All paths come from build output, not a repository-root search or a nested build.
        foreach (var argument in new[]
        {
            "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "MesControlAgv.Adapter.Tests.runtimeconfig.json"),
            "--depsfile", Path.Combine(AppContext.BaseDirectory, "MesControlAgv.Adapter.Tests.deps.json"),
            Path.Combine(AppContext.BaseDirectory, "WorkstationImportEvidence.dll")
        }.Concat(arguments)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new Xunit.Sdk.XunitException("Offline evidence CLI exceeded 20 seconds: " + await stderr);
        }
        return (process.ExitCode, await stdout, await stderr);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private sealed record Snapshot(string Input, string Output, SampleWorkstationTaskTemplate Template, byte[] Bytes);

    public void Dispose()
    {
        // Only remove this test's freshly allocated directory directly under the temp root.
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(_workspace.Parent?.FullName, tempRoot, comparison) ||
            !_workspace.Name.StartsWith("workstation-evidence-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected temporary test path.");
        _workspace.Delete(recursive: true);
    }
}
