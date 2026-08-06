namespace MesControlAgv.Domain.Profiles;

public interface IProfileConfigurationValidator
{
    ProfileValidationResult Validate(ProfileConfiguration? configuration);
}

public interface IProfileConfigurationLoader
{
    Task<ProfileConfiguration> LoadAsync(Stream source, CancellationToken cancellationToken = default);
    Task<ProfileConfiguration> LoadFromFileAsync(string filePath, CancellationToken cancellationToken = default);
}
