using MesControlAgv.Contracts;

namespace MesControlAgv.Application;

/// <summary>
/// Stable application-facing boundary for AGV device protocols.
/// Implementations own connection, protocol translation, and vendor error normalization;
/// they do not own MES task persistence or workflow state transitions.
/// </summary>
public interface IAgvDriver
{
    string DriverId { get; }

    AgvCapabilitiesResponse Capabilities { get; }

    Task ConnectAsync(CancellationToken cancellationToken);

    Task<AgvSnapshotResponse> GetSnapshotAsync(
        string agvId,
        CancellationToken cancellationToken);

    Task<AgvTaskResponse> DispatchAsync(
        AgvDispatchCommand command,
        CancellationToken cancellationToken);

    Task<AgvTaskResponse?> PauseAsync(
        AgvControlCommand command,
        CancellationToken cancellationToken);

    Task<AgvTaskResponse?> ResumeAsync(
        AgvControlCommand command,
        CancellationToken cancellationToken);

    Task<AgvTaskResponse?> CancelAsync(
        AgvControlCommand command,
        CancellationToken cancellationToken);
}

/// <summary>
/// Per-instance options passed by a driver registry to a driver factory.
/// Driver-specific settings can be added without changing the stable driver contract.
/// </summary>
public sealed record AgvDriverOptions(
    string DefaultAgvId = "AGV-01",
    IReadOnlyDictionary<string, string>? Settings = null);

/// <summary>
/// Creates an independent driver instance for a registered driver kind.
/// </summary>
public interface IAgvDriverFactory
{
    string DriverId { get; }

    IAgvDriver Create(AgvDriverOptions options);
}

/// <summary>
/// Registry for named driver factories. It is deliberately independent of DI and
/// configuration so hosts can compose it in their own preferred way.
/// </summary>
public sealed class DriverRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, IAgvDriverFactory> _factories = new(StringComparer.OrdinalIgnoreCase);

    public DriverRegistry(IEnumerable<IAgvDriverFactory>? factories = null)
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

    public void Register(IAgvDriverFactory factory)
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
                throw new InvalidOperationException($"An AGV driver factory for '{factory.DriverId}' is already registered.");
            }
        }
    }

    public bool Contains(string driverId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverId);
        lock (_sync) return _factories.ContainsKey(driverId);
    }

    public IAgvDriver Create(string driverId, AgvDriverOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverId);
        IAgvDriverFactory factory;
        lock (_sync)
        {
            if (!_factories.TryGetValue(driverId, out factory!))
            {
                throw new KeyNotFoundException($"No AGV driver is registered for '{driverId}'.");
            }
        }

        return factory.Create(options ?? new AgvDriverOptions());
    }

    public bool TryCreate(
        string driverId,
        out IAgvDriver? driver,
        AgvDriverOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverId);
        IAgvDriverFactory? factory;
        lock (_sync) _factories.TryGetValue(driverId, out factory);
        if (factory is null)
        {
            driver = null;
            return false;
        }

        driver = factory.Create(options ?? new AgvDriverOptions());
        return true;
    }
}

/// <summary>
/// Exception used when an adapter cannot translate a vendor/device failure into
/// the normalized driver contract.
/// </summary>
public sealed class AgvDriverException(
    string driverId,
    string operation,
    string message,
    Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public string DriverId { get; } = driverId;
    public string Operation { get; } = operation;
}
