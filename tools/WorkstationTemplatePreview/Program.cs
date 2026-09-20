using System.Security.Cryptography;
using System.Text.Json;
using MesControlAgv.Application;

// Offline file transform only: deliberately no HTTP client, MES admission, or device calls.
if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: WorkstationTemplatePreview <captured-template-response.json> <new-preview.xlsx>");
    return 2;
}

try
{
    var inputPath = Path.GetFullPath(args[0]);
    var outputPath = Path.GetFullPath(args[1]);
    if (!string.Equals(Path.GetExtension(outputPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("The preview output must have an .xlsx extension.");
    using var response = JsonDocument.Parse(File.ReadAllText(inputPath));
    if (response.RootElement.GetProperty("code").GetInt32() != 200)
        throw new ArgumentException("Only a captured successful template response can be previewed.");
    var bytes = Convert.FromBase64String(response.RootElement.GetProperty("data").GetProperty("FileData").GetString()!);
    var original = SampleWorkstationTemplateFile.Read(bytes);
    var sources = original.Transfers.GroupBy(row => (row.SourceModule, row.SourceX, row.SourceY)).ToArray();
    if (original.TaskNo != "test" || original.Transfers.Count != 16 || sources.Length != 2 || sources.Any(group => group.Count() != 8) ||
        original.Transfers.Select(row => (row.TargetModule, row.TargetX, row.TargetY)).Distinct().Count() != 16)
        throw new ArgumentException("This preview expects the confirmed test template with two sources and 16 distinct targets.");

    var preview = original with
    {
        TaskNo = $"LOCAL-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..38],
        TaskName = "LOCAL-PREVIEW-50uL",
        Transfers = original.Transfers.Select(row => row with { VolumeMicroliters = 50 }).ToArray()
    };
    var output = SampleWorkstationTemplateFile.Write(preview);
    if (!SampleWorkstationTemplateFile.SameContent(preview, SampleWorkstationTemplateFile.Read(output)))
        throw new InvalidOperationException("The local preview failed its file roundtrip check.");
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    // Never overwrite the original capture or a previously generated preview.
    using (var file = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write)) file.Write(output);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        previewOnly = true,
        sourceTaskNo = original.TaskNo,
        taskNo = preview.TaskNo,
        outputPath,
        sourceSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
        outputSha256 = Convert.ToHexString(SHA256.HashData(output)),
        transfers = preview.Transfers.Count,
        microlitersPerTarget = 50,
        totalMicroliters = preview.Transfers.Sum(row => row.VolumeMicroliters),
        sources = preview.Transfers.GroupBy(row => (row.SourceModule, row.SourceX, row.SourceY)).Select(group => new
        {
            module = group.Key.SourceModule, x = group.Key.SourceX, y = group.Key.SourceY,
            targets = group.Count(), microliters = group.Sum(row => row.VolumeMicroliters)
        })
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception exception) when (exception is ArgumentException or IOException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
{
    Console.Error.WriteLine("Preview not created: " + exception.Message);
    return 1;
}
