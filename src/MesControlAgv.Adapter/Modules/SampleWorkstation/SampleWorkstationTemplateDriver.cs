using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;

namespace MesControlAgv.Adapter.Modules.SampleWorkstation;

public sealed partial class SampleWorkstationDriver
{
    public async Task<SampleWorkstationTemplateResponse> GetTaskTemplateAsync(
        string deviceId, string taskNo, CancellationToken cancellationToken)
    {
        EnsureDeviceId(deviceId);
        taskNo = RequireTaskNo(taskNo);
        var response = await vendor.GetAsync("GetExperimentalTaskTemplate",
            new Dictionary<string, string?> { ["TaskNo"] = taskNo }, cancellationToken);
        var file = response.Data.ValueKind == JsonValueKind.Object
            ? response.Data.Deserialize<VendorTemplateFile>(SerializerOptions) : null;
        if (file is null || string.IsNullOrWhiteSpace(file.FileName) || string.IsNullOrWhiteSpace(file.FileData))
            throw new SampleWorkstationProtocolException("Template download did not return FileName/FileData.");
        byte[] bytes;
        SampleWorkstationTaskTemplate template;
        try
        {
            if (file.FileData.Length > (SampleWorkstationTemplateFile.MaximumFileBytes + 2) / 3 * 4)
                throw new ArgumentException("Template exceeds 4 MiB.");
            bytes = Convert.FromBase64String(file.FileData);
            template = SampleWorkstationTemplateFile.Read(bytes);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        { throw new SampleWorkstationProtocolException("Invalid downloaded template: " + exception.Message); }
        if (!string.Equals(template.TaskNo, taskNo, StringComparison.Ordinal))
            throw new SampleWorkstationProtocolException(
                $"Requested task '{taskNo}', but workbook contains '{template.TaskNo}'.",
                SampleWorkstationErrorCodes.TemplateMismatch, response.Code);
        return new(options.DeviceId, file.FileName, bytes, Convert.ToHexString(SHA256.HashData(bytes)),
            template, timeProvider.GetUtcNow());
    }

    public async Task<SampleWorkstationTaskImportResponse> ImportTasksAsync(
        string deviceId, string fileName, Stream content, CancellationToken cancellationToken)
    {
        EnsureControlEnabled(deviceId);
        var bytes = await SampleWorkstationTemplateFile.ReadBytesAsync(content, cancellationToken);
        var expected = SampleWorkstationTemplateFile.Read(bytes);
        using var body = new ByteArrayContent(SampleWorkstationTemplateFile.EncodeUpload(fileName, bytes));
        // Vendor WCFTestToolsDLH sends this raw Base64 frame with application/json;
        // it is deliberately NOT JSON-serialized or multipart-wrapped.
        body.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        // Exactly one POST. A failed/timed-out import may already have changed the vendor DB.
        var response = await vendor.PostAsync("ImportExperimentalTask", body, cancellationToken);
        if (response.Data.ValueKind != JsonValueKind.String || response.Data.GetString()?.Trim() != "导入成功")
            throw new SampleWorkstationProtocolException("Import returned no recognized success acknowledgement.",
                SampleWorkstationErrorCodes.CommandUnconfirmed, response.Code, response.Data);
        var actual = await GetTaskTemplateAsync(deviceId, expected.TaskNo, cancellationToken);
        if (!SampleWorkstationTemplateFile.SameContent(expected, actual.Template))
            throw new SampleWorkstationProtocolException("Imported task readback differs from the submitted transfer table.",
                SampleWorkstationErrorCodes.TemplateMismatch, response.Code);
        return new(options.DeviceId, [expected.TaskNo], timeProvider.GetUtcNow())
        {
            ReadbackVerified = true,
            FileSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            Template = expected
        };
    }

    private sealed record VendorTemplateFile(string FileName, string FileData);
}
