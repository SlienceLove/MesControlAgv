namespace MesControlAgv.Domain.Profiles;

public sealed record ProfileValidationError(string Path, string Message);

public sealed class ProfileValidationResult
{
    public ProfileValidationResult(IEnumerable<ProfileValidationError> errors)
    {
        Errors = errors.ToArray();
    }

    public IReadOnlyList<ProfileValidationError> Errors { get; }
    public bool IsValid => Errors.Count == 0;

    public string ToErrorMessage() => string.Join(
        Environment.NewLine,
        Errors.Select(error => $"{error.Path}: {error.Message}"));
}

public sealed class ProfileConfigurationValidationException(ProfileValidationResult result)
    : InvalidOperationException($"Profile configuration is invalid.{Environment.NewLine}{result.ToErrorMessage()}")
{
    public ProfileValidationResult Result { get; } = result;
}

public sealed class ProfileConfigurationLoadException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);
