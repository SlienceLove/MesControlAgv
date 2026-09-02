using MesControlAgv.Contracts;

namespace MesControlAgv.Application;

/// <summary>
/// Typed read-only boundary for the AUBO arm controller.
/// JSON-RPC method names, ARCS enum integers, and vendor error codes stay in Adapter.
/// </summary>
public interface IAuboArmReader
{
    Task<AuboArmStatusResponse> GetStatusAsync(
        string deviceId,
        CancellationToken cancellationToken);

    Task<AuboArmReadinessResponse> GetReadinessAsync(
        string deviceId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one named variable from RegisterControl. Only keys on the configured
    /// allowlist are readable, so a typo cannot enumerate the controller's store.
    /// </summary>
    Task<AuboArmVariableResponse> GetVariableAsync(
        string deviceId,
        string key,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the command/ack/result variable triple as one observation.
    /// </summary>
    Task<AuboArmHandshakeSnapshotResponse> GetHandshakeSnapshotAsync(
        string deviceId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional read-only bridge to the AUBO Dashboard Server's textual
/// <c>get loaded program</c> command. RuntimeMachine's preload table and the
/// currently loaded project are separate controller concepts, so callers may
/// use this source to present the real loaded filename without granting any
/// control capability.
/// </summary>
public interface IAuboArmLoadedProgramReader
{
    Task<string?> GetLoadedProgramAsync(
        string deviceId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Control-side handshake. Deliberately separate from <see cref="IAuboArmReader"/> so
/// that a write is not expressible through the read-only driver: the read-only driver
/// does not implement this interface, and nothing registers it while control is off.
/// </summary>
public interface IAuboArmHandshakeWriter
{
    /// <summary>
    /// Writes the command variable that the resident Lua project branches on, then
    /// waits for the Lua side to publish its result variable.
    /// </summary>
    Task<AuboArmHandshakeResultResponse> DispatchAsync(
        string deviceId,
        Guid operationId,
        int commandCode,
        CancellationToken cancellationToken);
}

/// <summary>
/// Independent program-control boundary.  It deliberately contains no vendor
/// JSON-RPC method names or register addresses; those remain inside Adapter.
/// </summary>
public interface IAuboArmProgramController
{
    Task<AuboArmProgramStatusResponse> GetProgramAsync(
        string deviceId,
        CancellationToken cancellationToken);

    Task<AuboArmProgramCatalogResponse> GetProgramCatalogAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        Task.FromException<AuboArmProgramCatalogResponse>(
            new NotSupportedException("AUBO program catalog is not supported by this controller."));

    Task<AuboArmProgramOperationResponse> LoadProgramAsync(
        string deviceId,
        string programName,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken);

    Task<AuboArmProgramOperationResponse> RunProgramAsync(
        string deviceId,
        string? programName,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken);

    Task<AuboArmProgramOperationResponse> StopProgramAsync(
        string deviceId,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken);

    Task<AuboArmProgramOperationResponse> LoadProgramAsync(
        string deviceId,
        string programName,
        string operatorName,
        CancellationToken cancellationToken) =>
        LoadProgramAsync(deviceId, programName, operatorName, Guid.NewGuid(), cancellationToken);

    Task<AuboArmProgramOperationResponse> RunProgramAsync(
        string deviceId,
        string? programName,
        string operatorName,
        CancellationToken cancellationToken) =>
        RunProgramAsync(deviceId, programName, operatorName, Guid.NewGuid(), cancellationToken);

    Task<AuboArmProgramOperationResponse> StopProgramAsync(
        string deviceId,
        string operatorName,
        CancellationToken cancellationToken) =>
        StopProgramAsync(deviceId, operatorName, Guid.NewGuid(), cancellationToken);

    Task<AuboArmProgramOperationResponse> LoadProgramAsync(
        string deviceId,
        AuboArmProgramRequest request,
        CancellationToken cancellationToken) =>
        LoadProgramAsync(
            deviceId,
            request.EffectiveProgramName,
            request.EffectiveOperatorName,
            request.OperationId.GetValueOrDefault(Guid.NewGuid()),
            cancellationToken);

    Task<AuboArmProgramOperationResponse> RunProgramAsync(
        string deviceId,
        AuboArmProgramRunRequest request,
        CancellationToken cancellationToken) =>
        RunProgramAsync(
            deviceId,
            request.EffectiveProgramName,
            request.EffectiveOperatorName,
            request.OperationId.GetValueOrDefault(Guid.NewGuid()),
            cancellationToken);

    Task<AuboArmProgramOperationResponse> StopProgramAsync(
        string deviceId,
        AuboArmProgramStopRequest request,
        CancellationToken cancellationToken) =>
        StopProgramAsync(
            deviceId,
            request.EffectiveOperatorName,
            request.OperationId.GetValueOrDefault(Guid.NewGuid()),
            cancellationToken);
}

/// <summary>Alias used by Adapter registrations and tests.</summary>
public interface IAuboArmProgramDriver : IAuboArmProgramController;

/// <summary>
/// MES-side program gateway.  Default implementations preserve compatibility with
/// existing read-only test doubles until they opt into the new program surface.
/// </summary>
public interface IAuboArmProgramGateway
{
    Task<AuboArmProgramStatusResponse> GetProgramAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        Task.FromException<AuboArmProgramStatusResponse>(
            new NotSupportedException("AUBO program control is not supported by this gateway."));

    Task<AuboArmProgramCatalogResponse> GetProgramCatalogAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        Task.FromException<AuboArmProgramCatalogResponse>(
            new NotSupportedException("AUBO program catalog is not supported by this gateway."));

    Task<AuboArmProgramOperationResponse> LoadProgramAsync(
        string deviceId,
        string programName,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken) =>
        Task.FromException<AuboArmProgramOperationResponse>(
            new NotSupportedException("AUBO program control is not supported by this gateway."));

    Task<AuboArmProgramOperationResponse> LoadProgramAsync(
        string deviceId,
        string programName,
        string operatorName,
        CancellationToken cancellationToken) =>
        LoadProgramAsync(deviceId, programName, operatorName, Guid.NewGuid(), cancellationToken);

    Task<AuboArmProgramOperationResponse> RunProgramAsync(
        string deviceId,
        string? programName,
        string operatorName,
        CancellationToken cancellationToken) =>
        RunProgramAsync(deviceId, programName, operatorName, Guid.NewGuid(), cancellationToken);

    Task<AuboArmProgramOperationResponse> StopProgramAsync(
        string deviceId,
        string operatorName,
        CancellationToken cancellationToken) =>
        StopProgramAsync(deviceId, operatorName, Guid.NewGuid(), cancellationToken);

    Task<AuboArmProgramOperationResponse> RunProgramAsync(
        string deviceId,
        string? programName,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken) =>
        Task.FromException<AuboArmProgramOperationResponse>(
            new NotSupportedException("AUBO program control is not supported by this gateway."));

    Task<AuboArmProgramOperationResponse> StopProgramAsync(
        string deviceId,
        string operatorName,
        Guid operationId,
        CancellationToken cancellationToken) =>
        Task.FromException<AuboArmProgramOperationResponse>(
            new NotSupportedException("AUBO program control is not supported by this gateway."));

    Task<AuboArmProgramOperationResponse> LoadProgramAsync(
        string deviceId,
        AuboArmProgramRequest request,
        CancellationToken cancellationToken) =>
        LoadProgramAsync(
            deviceId,
            request.EffectiveProgramName,
            request.EffectiveOperatorName,
            request.OperationId.GetValueOrDefault(Guid.NewGuid()),
            cancellationToken);

    Task<AuboArmProgramOperationResponse> RunProgramAsync(
        string deviceId,
        AuboArmProgramRunRequest request,
        CancellationToken cancellationToken) =>
        RunProgramAsync(
            deviceId,
            request.EffectiveProgramName,
            request.EffectiveOperatorName,
            request.OperationId.GetValueOrDefault(Guid.NewGuid()),
            cancellationToken);

    Task<AuboArmProgramOperationResponse> StopProgramAsync(
        string deviceId,
        AuboArmProgramStopRequest request,
        CancellationToken cancellationToken) =>
        StopProgramAsync(
            deviceId,
            request.EffectiveOperatorName,
            request.OperationId.GetValueOrDefault(Guid.NewGuid()),
            cancellationToken);
}

public interface IAuboArmDriver : IAuboArmReader;

/// <summary>
/// MES-side normalized gateway to the AUBO Adapter module. The Adapter remains
/// responsible for JSON-RPC framing and for enforcing whether control was enabled
/// at process startup.
/// </summary>
public interface IAuboArmGateway : IAuboArmReader, IAuboArmProgramGateway
{
    Task<AuboArmHandshakeResultResponse> DispatchAsync(
        string deviceId,
        Guid operationId,
        int commandCode,
        CancellationToken cancellationToken);
}

/// <summary>
/// Raised when the controller answered but the answer cannot be trusted as an outcome.
/// The caller must surface Unknown and must not automatically resend the command.
/// </summary>
public sealed class AuboArmOutcomeUnknownException : InvalidOperationException
{
    public AuboArmOutcomeUnknownException(string message, bool mayHaveWritten = true)
        : base($"{message} Do not retry automatically; reconcile the arm state first.")
    {
        MayHaveWritten = mayHaveWritten;
    }

    public bool MayHaveWritten { get; }
}
