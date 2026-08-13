using MesControlAgv.Contracts;

namespace MesControlAgv.Application;

/// <summary>
/// Stable application-facing boundary for vision system device protocols.
/// Implementations own connection, protocol translation, and vendor error normalization.
/// </summary>
public interface IVisionDriver
{
    string DriverId { get; }

    VisionCapabilities Capabilities { get; }

    Task ConnectAsync(CancellationToken cancellationToken);

    Task<VisionCaptureResponse> CaptureAsync(
        VisionCaptureCommand command,
        CancellationToken cancellationToken);

    Task<VisionRecognitionResponse> RecognizeAsync(
        VisionRecognitionCommand command,
        CancellationToken cancellationToken);

    Task<VisionLocalizationResponse> LocalizeAsync(
        VisionLocalizationCommand command,
        CancellationToken cancellationToken);

    Task<VisionCalibrationResponse> GetCalibrationAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Per-instance options passed by a driver registry to a vision driver factory.
/// </summary>
public sealed record VisionDriverOptions(
    string DefaultVisionId = "VISION-01",
    IReadOnlyDictionary<string, string>? Settings = null);

/// <summary>
/// Creates an independent vision driver instance for a registered driver kind.
/// </summary>
public interface IVisionDriverFactory
{
    string DriverId { get; }

    IVisionDriver Create(VisionDriverOptions options);
}

/// <summary>
/// Registry for named vision driver factories.
/// </summary>
public sealed class VisionDriverRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, IVisionDriverFactory> _factories = new(StringComparer.OrdinalIgnoreCase);

    public VisionDriverRegistry(IEnumerable<IVisionDriverFactory>? factories = null)
    {
        if (factories is null) return;
        foreach (var factory in factories) Register(factory);
    }

    public IReadOnlyCollection<string> DriverIds
    {
        get
        {
            lock (_sync) return _factories.Keys.ToArray();
        }
    }

    public void Register(IVisionDriverFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (string.IsNullOrWhiteSpace(factory.DriverId))
        {
            throw new ArgumentException("A driver factory must declare a non-empty DriverId.", nameof(factory));
        }

        lock (_sync)
        {
            if (!_factories.TryAdd(factory.DriverId, factory))
            {
                throw new InvalidOperationException($"A vision driver factory for '{factory.DriverId}' is already registered.");
            }
        }
    }

    public bool Contains(string driverId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverId);
        lock (_sync) return _factories.ContainsKey(driverId);
    }

    public IVisionDriver Create(string driverId, VisionDriverOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverId);
        lock (_sync)
        {
            if (!_factories.TryGetValue(driverId, out var visionFactory))
            {
                throw new KeyNotFoundException($"No vision driver is registered for '{driverId}'.");
            }
            return visionFactory.Create(options ?? new VisionDriverOptions());
        }
    }

    public bool TryCreate(
        string driverId,
        out IVisionDriver? driver,
        VisionDriverOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverId);
        IVisionDriverFactory? factory;
        lock (_sync) _factories.TryGetValue(driverId, out factory);
        if (factory is null)
        {
            driver = null;
            return false;
        }

        driver = factory.Create(options ?? new VisionDriverOptions());
        return true;
    }
}

/// <summary>
/// Exception used when a vision driver cannot translate a vendor/device failure.
/// </summary>
public sealed class VisionDriverException(
    string driverId,
    string operation,
    string message,
    Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public string DriverId { get; } = driverId;
    public string Operation { get; } = operation;
}
