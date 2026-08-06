using System.Text.Json;

namespace MesControlAgv.Domain.Profiles;

/// <summary>
/// Loads JSON profile documents and validates them before returning them to callers.
/// </summary>
public sealed class JsonProfileConfigurationLoader : IProfileConfigurationLoader
{
    private readonly IProfileConfigurationValidator _validator;
    private readonly JsonSerializerOptions _serializerOptions;

    public JsonProfileConfigurationLoader(
        IProfileConfigurationValidator? validator = null,
        JsonSerializerOptions? serializerOptions = null)
    {
        _validator = validator ?? new ProfileConfigurationValidator();
        _serializerOptions = serializerOptions ?? CreateDefaultSerializerOptions();
    }

    public async Task<ProfileConfiguration> LoadAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        try
        {
            using var document = await JsonDocument.ParseAsync(source, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ProfileConfigurationLoadException(
                    "The profile document must be a JSON object.",
                    new InvalidDataException("The JSON root is not an object."));
            }

            if (root.TryGetProperty("Profile", out var wrappedProfile)) root = wrappedProfile;
            var configuration = root.Deserialize<ProfileConfiguration>(_serializerOptions);
            if (configuration is null)
            {
                throw new ProfileConfigurationLoadException(
                    "The profile document did not contain a configuration object.",
                    new InvalidDataException("The JSON document resolved to null."));
            }

            var validation = _validator.Validate(configuration);
            if (!validation.IsValid) throw new ProfileConfigurationValidationException(validation);
            return configuration;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProfileConfigurationValidationException)
        {
            throw;
        }
        catch (ProfileConfigurationLoadException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ProfileConfigurationLoadException("The profile document is not valid JSON.", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new ProfileConfigurationLoadException("The profile document uses an unsupported JSON shape.", exception);
        }
    }

    public async Task<ProfileConfiguration> LoadFromFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using var source = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await LoadAsync(source, cancellationToken);
    }

    private static JsonSerializerOptions CreateDefaultSerializerOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
