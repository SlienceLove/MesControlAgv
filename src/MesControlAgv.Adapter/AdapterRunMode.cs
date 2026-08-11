namespace MesControlAgv.Adapter;

/// <summary>
/// Adapter process mode selected once from deployment configuration at startup.
/// It is intentionally not mutable through WPF or an HTTP endpoint.
/// </summary>
public sealed class AdapterRunMode
{
    public const string StandardValue = "standard";
    public const string ReadOnlyPreflightValue = "read-only-preflight";

    public static AdapterRunMode Standard { get; } = new(StandardValue);
    public static AdapterRunMode ReadOnlyPreflight { get; } = new(ReadOnlyPreflightValue);

    private AdapterRunMode(string value) => Value = value;

    public string Value { get; }
    public bool IsReadOnlyPreflight => string.Equals(Value, ReadOnlyPreflightValue, StringComparison.Ordinal);

    public static AdapterRunMode Parse(string? configuredValue)
    {
        var value = string.IsNullOrWhiteSpace(configuredValue)
            ? StandardValue
            : configuredValue.Trim().ToLowerInvariant();

        return value switch
        {
            StandardValue => Standard,
            ReadOnlyPreflightValue => ReadOnlyPreflight,
            _ => throw new InvalidOperationException(
                $"Unsupported Adapter:RunMode '{configuredValue}'. Configure '{StandardValue}' or '{ReadOnlyPreflightValue}'.")
        };
    }

    public void ThrowIfMutationIsBlocked(string operation)
    {
        if (IsReadOnlyPreflight)
        {
            throw new ReadOnlyPreflightModeException(operation);
        }
    }
}

public sealed class ReadOnlyPreflightModeException(string operation)
    : InvalidOperationException($"Adapter read-only preflight mode rejects {operation}.");
