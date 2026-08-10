using System.Collections.ObjectModel;
using MesControlAgv.Domain.Map;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// Keeps independent, refresh-stable animation progress for the AGVs shown on the map.
/// Path segments must be supplied in the same order as each overlay's station path.
/// </summary>
public sealed class MapAnimationCoordinator
{
    public static readonly TimeSpan DefaultSegmentDuration = TimeSpan.FromSeconds(1.6);

    private static readonly HashSet<string> MovingMesStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Moving",
        "MovingToPickup",
        "MovingToDropoff",
        "MovingToTarget"
    };

    private readonly Dictionary<string, AnimationState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _segmentDuration;

    public MapAnimationCoordinator()
        : this(DefaultSegmentDuration)
    {
    }

    public MapAnimationCoordinator(TimeSpan segmentDuration)
    {
        if (segmentDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(segmentDuration),
                segmentDuration,
                "The segment duration must be greater than zero.");
        }

        _segmentDuration = segmentDuration;
    }

    /// <summary>
    /// Reconciles the latest read-only map snapshot without resetting unchanged AGVs.
    /// The operation values should be stable identifiers, such as OperationId strings.
    /// </summary>
    public IReadOnlyDictionary<string, MapAgvAnimationPose> Sync(
        IReadOnlyList<MapAgvOverlayViewModel> overlays,
        IReadOnlyList<MapPathSegmentViewModel> pathSegments,
        IReadOnlyDictionary<string, string?> operationSignatures)
    {
        ArgumentNullException.ThrowIfNull(overlays);
        ArgumentNullException.ThrowIfNull(pathSegments);
        ArgumentNullException.ThrowIfNull(operationSignatures);

        var operationsByAgv = ToCaseInsensitiveLookup(operationSignatures);
        var segmentsByAgv = pathSegments
            .Where(segment => !string.IsNullOrWhiteSpace(segment.AgvId))
            .GroupBy(segment => segment.AgvId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var retainedAgvIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var overlay in overlays)
        {
            if (string.IsNullOrWhiteSpace(overlay.AgvId)) continue;

            var agvId = overlay.AgvId.Trim();
            if (!retainedAgvIds.Add(agvId)) continue;

            operationsByAgv.TryGetValue(agvId, out var operationSignature);
            segmentsByAgv.TryGetValue(agvId, out var agvSegments);
            var signature = AnimationSignature.Create(operationSignature, overlay.CurrentStation, overlay.Path);
            var geometry = FindCurrentSegment(overlay, agvSegments ?? []);
            var followsSegment = geometry is not null &&
                signature.HasActiveOperation &&
                IsMoving(overlay);

            if (!_states.TryGetValue(agvId, out var state) || !state.Signature.Equals(signature))
            {
                _states[agvId] = new AnimationState(
                    signature,
                    overlay.X,
                    overlay.Y,
                    geometry,
                    followsSegment,
                    TimeSpan.Zero);
                continue;
            }

            state.CurrentNodeX = overlay.X;
            state.CurrentNodeY = overlay.Y;
            state.Geometry = geometry;
            state.FollowsSegment = followsSegment;
        }

        foreach (var removedAgvId in _states.Keys.Where(id => !retainedAgvIds.Contains(id)).ToArray())
        {
            _states.Remove(removedAgvId);
        }

        return CreatePoseSnapshot();
    }

    /// <summary>Advances eligible AGVs by elapsed wall time and returns all current poses.</summary>
    public IReadOnlyDictionary<string, MapAgvAnimationPose> Tick(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Elapsed time cannot be negative.");
        }

        foreach (var state in _states.Values)
        {
            if (!state.FollowsSegment || state.Elapsed >= _segmentDuration) continue;

            var remaining = _segmentDuration - state.Elapsed;
            state.Elapsed = elapsed >= remaining ? _segmentDuration : state.Elapsed + elapsed;
        }

        return CreatePoseSnapshot();
    }

    private IReadOnlyDictionary<string, MapAgvAnimationPose> CreatePoseSnapshot()
    {
        var poses = new Dictionary<string, MapAgvAnimationPose>(_states.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (agvId, state) in _states)
        {
            if (!state.FollowsSegment || state.Geometry is null)
            {
                poses[agvId] = new MapAgvAnimationPose(
                    state.CurrentNodeX,
                    state.CurrentNodeY,
                    0,
                    false);
                continue;
            }

            var progress = state.Elapsed.TotalSeconds / _segmentDuration.TotalSeconds;
            var point = AgvPathAnimator.PointOnCurve(
                state.Geometry.Start,
                state.Geometry.Control1,
                state.Geometry.Control2,
                state.Geometry.End,
                progress);
            poses[agvId] = new MapAgvAnimationPose(
                point.X,
                point.Y,
                HeadingRadians(state.Geometry, progress),
                state.Elapsed < _segmentDuration);
        }

        return new ReadOnlyDictionary<string, MapAgvAnimationPose>(poses);
    }

    private static Dictionary<string, string?> ToCaseInsensitiveLookup(
        IReadOnlyDictionary<string, string?> operationSignatures)
    {
        var lookup = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (agvId, signature) in operationSignatures)
        {
            if (!string.IsNullOrWhiteSpace(agvId)) lookup[agvId.Trim()] = signature;
        }

        return lookup;
    }

    private static CurveGeometry? FindCurrentSegment(
        MapAgvOverlayViewModel overlay,
        IReadOnlyList<MapPathSegmentViewModel> segments)
    {
        if (overlay.Path.Count < 2 || segments.Count == 0 || string.IsNullOrWhiteSpace(overlay.CurrentStation))
        {
            return null;
        }

        var currentSegmentIndex = -1;
        for (var index = 0; index < overlay.Path.Count - 1; index++)
        {
            if (!string.Equals(
                    Normalize(overlay.Path[index]),
                    Normalize(overlay.CurrentStation),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A repeated current station is ambiguous without live route progress.
            if (currentSegmentIndex >= 0) return null;
            currentSegmentIndex = index;
        }

        return currentSegmentIndex >= 0 && currentSegmentIndex < segments.Count
            ? CurveGeometry.TryCreate(segments[currentSegmentIndex])
            : null;
    }

    private static bool IsMoving(MapAgvOverlayViewModel overlay) =>
        overlay.Online &&
        MovingMesStatuses.Contains(Normalize(overlay.MesStatus)) &&
        string.Equals(Normalize(overlay.DeviceState), "moving", StringComparison.OrdinalIgnoreCase);

    private static double HeadingRadians(CurveGeometry geometry, double progress)
    {
        var t = Math.Clamp(progress, 0, 1);
        var u = 1 - t;
        var dx =
            (3 * u * u * (geometry.Control1.X - geometry.Start.X)) +
            (6 * u * t * (geometry.Control2.X - geometry.Control1.X)) +
            (3 * t * t * (geometry.End.X - geometry.Control2.X));
        var dy =
            (3 * u * u * (geometry.Control1.Y - geometry.Start.Y)) +
            (6 * u * t * (geometry.Control2.Y - geometry.Control1.Y)) +
            (3 * t * t * (geometry.End.Y - geometry.Control2.Y));
        if ((dx * dx) + (dy * dy) > 1e-18)
        {
            return AgvPathAnimator.HeadingRadians(
                geometry.Start,
                geometry.Control1,
                geometry.Control2,
                geometry.End,
                t);
        }

        const double sampleStep = 1e-5;
        var before = AgvPathAnimator.PointOnCurve(
            geometry.Start,
            geometry.Control1,
            geometry.Control2,
            geometry.End,
            Math.Max(0, t - sampleStep));
        var after = AgvPathAnimator.PointOnCurve(
            geometry.Start,
            geometry.Control1,
            geometry.Control2,
            geometry.End,
            Math.Min(1, t + sampleStep));
        return Math.Atan2(after.Y - before.Y, after.X - before.X);
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private sealed class AnimationState(
        AnimationSignature signature,
        double currentNodeX,
        double currentNodeY,
        CurveGeometry? geometry,
        bool followsSegment,
        TimeSpan elapsed)
    {
        public AnimationSignature Signature { get; } = signature;
        public double CurrentNodeX { get; set; } = currentNodeX;
        public double CurrentNodeY { get; set; } = currentNodeY;
        public CurveGeometry? Geometry { get; set; } = geometry;
        public bool FollowsSegment { get; set; } = followsSegment;
        public TimeSpan Elapsed { get; set; } = elapsed;
    }

    private sealed class AnimationSignature : IEquatable<AnimationSignature>
    {
        private readonly string _operation;
        private readonly string _currentStation;
        private readonly string[] _path;

        private AnimationSignature(string? operation, string? currentStation, IReadOnlyList<string> path)
        {
            _operation = Normalize(operation);
            _currentStation = Normalize(currentStation);
            _path = path.Select(Normalize).ToArray();
        }

        public bool HasActiveOperation => _operation.Length > 0;

        public static AnimationSignature Create(
            string? operation,
            string? currentStation,
            IReadOnlyList<string> path) =>
            new(operation, currentStation, path);

        public bool Equals(AnimationSignature? other) =>
            other is not null &&
            string.Equals(_operation, other._operation, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_currentStation, other._currentStation, StringComparison.OrdinalIgnoreCase) &&
            _path.SequenceEqual(other._path, StringComparer.OrdinalIgnoreCase);

        public override bool Equals(object? obj) => Equals(obj as AnimationSignature);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(_operation, StringComparer.OrdinalIgnoreCase);
            hash.Add(_currentStation, StringComparer.OrdinalIgnoreCase);
            foreach (var station in _path) hash.Add(station, StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }
    }

    private sealed record CurveGeometry(MapPoint Start, MapPoint Control1, MapPoint Control2, MapPoint End)
    {
        public static CurveGeometry? TryCreate(MapPathSegmentViewModel segment)
        {
            if (!double.IsFinite(segment.X1) || !double.IsFinite(segment.Y1) ||
                !double.IsFinite(segment.X2) || !double.IsFinite(segment.Y2))
            {
                return null;
            }

            var start = new MapPoint(segment.X1, segment.Y1);
            var end = new MapPoint(segment.X2, segment.Y2);
            if (double.IsNaN(segment.Control1X) || double.IsNaN(segment.Control1Y) ||
                double.IsNaN(segment.Control2X) || double.IsNaN(segment.Control2Y))
            {
                var deltaX = end.X - start.X;
                var deltaY = end.Y - start.Y;
                return new CurveGeometry(
                    start,
                    new MapPoint(start.X + (deltaX / 3), start.Y + (deltaY / 3)),
                    new MapPoint(start.X + (2 * deltaX / 3), start.Y + (2 * deltaY / 3)),
                    end);
            }

            if (!double.IsFinite(segment.Control1X) || !double.IsFinite(segment.Control1Y) ||
                !double.IsFinite(segment.Control2X) || !double.IsFinite(segment.Control2Y))
            {
                return null;
            }

            return new CurveGeometry(
                start,
                new MapPoint(segment.Control1X, segment.Control1Y),
                new MapPoint(segment.Control2X, segment.Control2Y),
                end);
        }
    }
}

public sealed record MapAgvAnimationPose(
    double X,
    double Y,
    double HeadingRadians,
    bool IsAnimating);
