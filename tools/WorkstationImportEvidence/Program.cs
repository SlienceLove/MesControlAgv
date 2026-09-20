using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;

// Reconstruct evidence from an already saved snapshot. No network or device calls.
if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: WorkstationImportEvidence <prepared-snapshot.json> <new-output-directory>");
    return 2;
}
try
{
    using var document = JsonDocument.Parse(File.ReadAllText(args[0]));
    var prepared = document.RootElement.GetProperty("prepared");
    var payload = prepared.GetProperty("payload");
    var template = payload.GetProperty("generatedTemplate").Deserialize<SampleWorkstationTaskTemplate>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new ArgumentException("Missing generated template.");
    if (template.TaskNo != prepared.GetProperty("vendorTaskNo").GetString())
        throw new ArgumentException("Snapshot task numbers disagree.");
    var bytes = SampleWorkstationTemplateFile.Write(template);
    var hash = Convert.ToHexString(SHA256.HashData(bytes));
    if (hash != payload.GetProperty("generatedTemplateFileSha256").GetString())
        throw new ArgumentException("Reconstructed file does not match the frozen SHA256; no evidence exported.");
    var name = payload.GetProperty("generatedTemplateFileName").GetString()!;
    // Validates a bare vendor filename as well as reproducing the original frame.
    var frame = SampleWorkstationTemplateFile.EncodeUpload(name, bytes);
    var output = Path.GetFullPath(args[1]);
    if (Directory.Exists(output) || File.Exists(output))
        throw new IOException("The output directory must not already exist.");
    Directory.CreateDirectory(output);
    void Save(string fileName, byte[] content)
    {
        using var file = new FileStream(Path.Combine(output, fileName), FileMode.CreateNew, FileAccess.Write);
        file.Write(content);
    }
    Save(name, bytes);
    Save("original-request-body.txt", frame);
    var manifest = JsonSerializer.Serialize(new
    {
        evidenceOnly = true, taskNo = template.TaskNo, taskNoLength = template.TaskNo.Length,
        fileName = name, fileSha256 = hash,
        requestBodySha256 = Convert.ToHexString(SHA256.HashData(frame)),
        contentType = "application/json", transfers = template.Transfers,
        bottleBindings = payload.GetProperty("bottleBindings"),
        preparationId = prepared.GetProperty("preparationId"),
        experimentJobId = prepared.GetProperty("experimentJobId")
    }, new JsonSerializerOptions { WriteIndented = true });
    Save("manifest.json", Encoding.UTF8.GetBytes(manifest));
    Console.WriteLine(JsonSerializer.Serialize(new { output, taskNo = template.TaskNo, fileSha256 = hash, transfers = template.Transfers.Count }));
    return 0;
}
catch (Exception exception) when (exception is ArgumentException or IOException or JsonException or InvalidOperationException or KeyNotFoundException)
{
    Console.Error.WriteLine("Evidence export failed: " + exception.Message);
    return 1;
}
