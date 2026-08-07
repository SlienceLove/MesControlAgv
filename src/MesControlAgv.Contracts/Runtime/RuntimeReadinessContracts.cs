namespace MesControlAgv.Contracts;

/// <summary>
/// Read-only evidence describing the configuration currently served by MES.
/// Fingerprints identify the normalized profile and routing graph; they are not
/// controller map checksums and must not be used as physical-release evidence.
/// </summary>
public sealed record RuntimeReadinessResponse(
    string ProductId,
    string ProductName,
    string ProductVersion,
    bool UseSimulator,
    bool AutomaticDispatchEnabled,
    bool TaskCancellationEnabled,
    string ProfileFingerprint,
    string MapFingerprint,
    IReadOnlyList<string> StationIds,
    IReadOnlyList<DirectedMapEdgeResponse> DirectedEdges,
    string? ExpectedPhysicalMapName = null,
    string? ExpectedPhysicalMapVersion = null,
    string? ExpectedPhysicalMapMd5 = null);

public sealed record DirectedMapEdgeResponse(
    string From,
    string To,
    double Cost);
