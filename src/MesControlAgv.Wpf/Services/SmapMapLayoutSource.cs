using System.IO;
using System.Security.Cryptography;
using MesControlAgv.Domain.Map;

namespace MesControlAgv.Wpf.Services;

public sealed class SmapMapLayoutSource(Func<string?> smapPathProvider, Func<string?> mappingPathProvider) : IMapLayoutSource
{
    private readonly Func<string?> _smapPathProvider = smapPathProvider ?? throw new ArgumentNullException(nameof(smapPathProvider));
    private readonly Func<string?> _mappingPathProvider = mappingPathProvider ?? throw new ArgumentNullException(nameof(mappingPathProvider));

    public async Task<MapLayoutResult> LoadAsync(CancellationToken ct = default)
    {
        var smapPath = _smapPathProvider();
        if (string.IsNullOrWhiteSpace(smapPath))
        {
            return new MapLayoutResult(null, StationMappingConfig.Empty, Loaded: false, Error: null);
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(smapPath, ct);
            var md5 = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
            using var source = new MemoryStream(bytes, writable: false);
            var document = SmapParser.Parse(source);
            var layout = SmapMapLayoutBuilder.Build(document);
            var mapping = await StationMappingLoader.LoadFileAsync(_mappingPathProvider(), ct);
            var identity = SmapMapIdentity.FromDocument(document, md5);
            return new MapLayoutResult(layout, mapping, Loaded: true, Error: null, identity);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new MapLayoutResult(null, StationMappingConfig.Empty, Loaded: false, exception.Message);
        }
    }
}
