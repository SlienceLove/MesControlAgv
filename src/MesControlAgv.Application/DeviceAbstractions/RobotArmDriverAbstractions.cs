using MesControlAgv.Contracts;

namespace MesControlAgv.Application;

/// <summary>
/// Stable application-facing boundary for robot arm device protocols.
/// Implementations own connection, protocol translation, and vendor error normalization.
/// </summary>
public interface IRobotArmDriver
{
    string DriverId { get; }

    RobotArmCapabilities Capabilities { get; }

    Task ConnectAsync(CancellationToken cancellationToken);

    Task<RobotArmStatusResponse> GetStatusAsync(CancellationToken cancellationToken);

    Task<RobotArmOperationResponse> PickAsync(
        RobotArmPickCommand command,
        CancellationToken cancellationToken);

    Task<RobotArmOperationResponse> PlaceAsync(
        RobotArmPlaceCommand command,
        CancellationToken cancellationToken);

    Task<RobotArmOperationResponse> MoveToAsync(
        RobotArmMoveCommand command,
        CancellationToken cancellationToken);

    Task<RobotArmOperationResponse> HomeAsync(CancellationToken cancellationToken);

    Task EmergencyStopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Per-instance options passed by a driver registry to a robot arm driver factory.
/// </summary>
public sealed record RobotArmDriverOptions(
    string DefaultArmId = "ARM-01",
    IReadOnlyDictionary<string, string>? Settings = null);

/// <summary>
/// Creates an independent robot arm driver instance for a registered driver kind.
/// </summary>
public interface IRobotArmDriverFactory
{
    string DriverId { get; }

    IRobotArmDriver Create(RobotArmDriverOptions options);
}

/// <summary>
/// Registry for named robot arm driver factories.
/// </summary>
public sealed class RobotArmDriverRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, IRobotArmDriverFactory> _factories = new(StringComparer.OrdinalIgnoreCase);

    public RobotArmDriverRegistry(IEnumerable<IRobotArmDriverFactory>? factories = null)
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

    public void Register(IRobotArmDriverFactory factory)
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
                throw new InvalidOperationException($"A robot arm driver factory for '{factory.DriverId}' is already registered.");
            }
        }
    }

    public bool Contains(string driverId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverId);
        lock (_sync) return _factories.ContainsKey(driverId);
    }

    public IRobotArmDriver Create(string driverId, RobotArmDriverOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverId);
        lock (_sync)
        {
            if (!_factories.TryGetValue(driverId, out var armFactory))
            {
                throw new KeyNotFoundException($"No robot arm driver is registered for '{driverId}'.");
            }
            return armFactory.Create(options ?? new RobotArmDriverOptions());
        }
    }

    public bool TryCreate(
        string driverId,
        out IRobotArmDriver? driver,
        RobotArmDriverOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverId);
        IRobotArmDriverFactory? factory;
        lock (_sync) _factories.TryGetValue(driverId, out factory);
        if (factory is null)
        {
            driver = null;
            return false;
        }

        driver = factory.Create(options ?? new RobotArmDriverOptions());
        return true;
    }
}

/// <summary>
/// Exception used when a robot arm driver cannot translate a vendor/device failure.
/// </summary>
public sealed class RobotArmDriverException(
    string driverId,
    string operation,
    string message,
    Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public string DriverId { get; } = driverId;
    public string Operation { get; } = operation;
}
