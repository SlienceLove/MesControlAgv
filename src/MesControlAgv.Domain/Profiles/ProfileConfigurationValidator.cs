namespace MesControlAgv.Domain.Profiles;

public sealed class ProfileConfigurationValidator : IProfileConfigurationValidator
{
    public ProfileValidationResult Validate(ProfileConfiguration? configuration)
    {
        var errors = new List<ProfileValidationError>();
        if (configuration is null)
        {
            errors.Add(new("configuration", "Configuration is required."));
            return new ProfileValidationResult(errors);
        }

        ValidateProduct(configuration.Product, errors);
        ValidateStations(configuration.Stations, errors);
        ValidateAgvs(configuration.Agvs, configuration.Stations, errors);
        ValidateMap(configuration.Map, configuration.Stations, errors);
        ValidateFeatures(configuration.Features, errors);
        ValidateTimeouts(configuration.Timeouts, errors);
        ValidatePhysicalAcceptance(configuration, errors);
        return new ProfileValidationResult(errors);
    }

    private static void ValidateProduct(ProductProfile? product, ICollection<ProfileValidationError> errors)
    {
        if (product is null)
        {
            errors.Add(new("product", "Product profile is required."));
            return;
        }

        RequireText(product.ProductId, "product.productId", "Product id is required.", errors);
        RequireText(product.DisplayName, "product.displayName", "Product display name is required.", errors);
        RequireText(product.Version, "product.version", "Product version is required.", errors);
    }

    private static void ValidateStations(IReadOnlyList<StationProfile>? stations, ICollection<ProfileValidationError> errors)
    {
        if (stations is null || stations.Count == 0)
        {
            errors.Add(new("stations", "At least one station profile is required."));
            return;
        }

        var stationIds = new HashSet<string>(StringComparer.Ordinal);
        var agvStationIds = new HashSet<string>(StringComparer.Ordinal);
        var codes = new HashSet<int>();
        for (var index = 0; index < stations.Count; index++)
        {
            var station = stations[index];
            var path = $"stations[{index}]";
            RequireText(station.StationId, $"{path}.stationId", "Station id is required.", errors);
            RequireText(station.AgvStationId, $"{path}.agvStationId", "AGV station id is required.", errors);
            RequireText(station.Name, $"{path}.name", "Station name is required.", errors);
            RequireText(station.Type, $"{path}.type", "Station type is required.", errors);

            if (!string.IsNullOrWhiteSpace(station.StationId) && !stationIds.Add(station.StationId))
                errors.Add(new($"{path}.stationId", $"Station id '{station.StationId}' is duplicated."));
            if (!string.IsNullOrWhiteSpace(station.AgvStationId) && !agvStationIds.Add(station.AgvStationId))
                errors.Add(new($"{path}.agvStationId", $"AGV station id '{station.AgvStationId}' is duplicated."));
            if (!codes.Add(station.Code))
                errors.Add(new($"{path}.code", $"Station code '{station.Code}' is duplicated."));
            if (station.Code < 0)
                errors.Add(new($"{path}.code", "Station code must be zero or greater."));
            if (station.Capacity <= 0)
                errors.Add(new($"{path}.capacity", "Station capacity must be greater than zero."));
        }
    }

    private static void ValidateAgvs(IReadOnlyList<AgvProfile>? agvs, IReadOnlyList<StationProfile>? stations, ICollection<ProfileValidationError> errors)
    {
        if (agvs is null || agvs.Count == 0)
        {
            errors.Add(new("agvs", "At least one AGV profile is required."));
            return;
        }

        var stationIds = (stations ?? []).Select(station => station.StationId).ToHashSet(StringComparer.Ordinal);
        var agvIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < agvs.Count; index++)
        {
            var agv = agvs[index];
            var path = $"agvs[{index}]";
            RequireText(agv.AgvId, $"{path}.agvId", "AGV id is required.", errors);
            RequireText(agv.Model, $"{path}.model", "AGV model is required.", errors);
            RequireText(agv.Driver, $"{path}.driver", "AGV driver is required.", errors);
            RequireText(agv.Endpoint, $"{path}.endpoint", "AGV endpoint is required.", errors);
            RequireText(agv.HomeStationId, $"{path}.homeStationId", "Home station id is required.", errors);
            if (!string.IsNullOrWhiteSpace(agv.AgvId) && !agvIds.Add(agv.AgvId))
                errors.Add(new($"{path}.agvId", $"AGV id '{agv.AgvId}' is duplicated."));
            if (!double.IsFinite(agv.MaxLoadKg) || agv.MaxLoadKg <= 0)
                errors.Add(new($"{path}.maxLoadKg", "Maximum load must be a finite value greater than zero."));
            if (!double.IsFinite(agv.MaxSpeedMetersPerSecond) || agv.MaxSpeedMetersPerSecond <= 0)
                errors.Add(new($"{path}.maxSpeedMetersPerSecond", "Maximum speed must be a finite value greater than zero."));
            if (!string.IsNullOrWhiteSpace(agv.HomeStationId) && !stationIds.Contains(agv.HomeStationId))
                errors.Add(new($"{path}.homeStationId", $"Home station '{agv.HomeStationId}' does not exist."));
            if (!string.IsNullOrWhiteSpace(agv.Endpoint) && !Uri.TryCreate(agv.Endpoint, UriKind.Absolute, out _))
                errors.Add(new($"{path}.endpoint", "AGV endpoint must be an absolute URI."));
            foreach (var parameter in agv.DeviceParameters ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrWhiteSpace(parameter.Key))
                    errors.Add(new($"{path}.deviceParameters", "Device parameter names are required."));
                else if (string.IsNullOrWhiteSpace(parameter.Value))
                    errors.Add(new($"{path}.deviceParameters.{parameter.Key}", "Device parameter values are required."));
            }
        }
    }

    private static void ValidateMap(MapProfile? map, IReadOnlyList<StationProfile>? stations, ICollection<ProfileValidationError> errors)
    {
        if (map is null)
        {
            errors.Add(new("map", "Map profile is required."));
            return;
        }

        var stationIds = map.StationIds ?? [];
        if (stationIds.Count == 0)
            errors.Add(new("map.stationIds", "At least one map station id is required."));

        var mapStationIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < stationIds.Count; index++)
        {
            var stationId = stationIds[index];
            var path = $"map.stationIds[{index}]";
            RequireText(stationId, path, "Map station id is required.", errors);
            if (!string.IsNullOrWhiteSpace(stationId) && !mapStationIds.Add(stationId))
                errors.Add(new(path, $"Map station id '{stationId}' is duplicated."));
        }

        var configuredStationIds = (stations ?? []).Select(station => station.AgvStationId)
            .Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        foreach (var stationId in configuredStationIds.Except(mapStationIds, StringComparer.Ordinal))
            errors.Add(new("map.stationIds", $"Configured station '{stationId}' is missing from the map."));
        foreach (var stationId in mapStationIds.Except(configuredStationIds, StringComparer.Ordinal))
            errors.Add(new("map.stationIds", $"Map station '{stationId}' is not configured."));

        if (map.Edges is null || map.Edges.Count == 0)
        {
            errors.Add(new("map.edges", "At least one map edge is required."));
            return;
        }

        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < map.Edges.Count; index++)
        {
            var edge = map.Edges[index];
            var path = $"map.edges[{index}]";
            RequireText(edge.From, $"{path}.from", "Map edge source is required.", errors);
            RequireText(edge.To, $"{path}.to", "Map edge target is required.", errors);
            if (!string.IsNullOrWhiteSpace(edge.From) && !mapStationIds.Contains(edge.From))
                errors.Add(new($"{path}.from", $"Map edge source '{edge.From}' is not a map station."));
            if (!string.IsNullOrWhiteSpace(edge.To) && !mapStationIds.Contains(edge.To))
                errors.Add(new($"{path}.to", $"Map edge target '{edge.To}' is not a map station."));
            if (StringComparer.Ordinal.Equals(edge.From, edge.To))
                errors.Add(new(path, "Map edge cannot connect a station to itself."));
            if (!double.IsFinite(edge.Cost) || edge.Cost <= 0)
                errors.Add(new($"{path}.cost", "Map edge cost must be a finite value greater than zero."));
            var edgeKey = edge.Bidirectional
                ? string.Join("\u001f", new[] { edge.From, edge.To }.Order(StringComparer.Ordinal))
                : $"{edge.From}\u001f{edge.To}";
            if (!string.IsNullOrWhiteSpace(edge.From) && !string.IsNullOrWhiteSpace(edge.To) && !edgeKeys.Add(edgeKey))
                errors.Add(new(path, "Duplicate map edge."));
        }
    }

    private static void ValidateFeatures(FeatureFlags? features, ICollection<ProfileValidationError> errors)
    {
        if (features is null)
            errors.Add(new("features", "Feature flags are required."));
    }

    private static void ValidatePhysicalAcceptance(ProfileConfiguration configuration, ICollection<ProfileValidationError> errors)
    {
        var physical = configuration.PhysicalAcceptance;
        if (physical is null) return;

        if (configuration.Features?.UseSimulator != false)
            errors.Add(new("features.useSimulator", "Physical acceptance profiles must disable the simulator."));

        var enabledAgvs = (configuration.Agvs ?? []).Where(agv => agv.Enabled).ToArray();
        if (enabledAgvs.Length == 0)
        {
            errors.Add(new("agvs", "Physical acceptance profiles require an enabled AGV."));
        }
        else
        {
            for (var index = 0; index < (configuration.Agvs?.Count ?? 0); index++)
            {
                var agv = configuration.Agvs![index];
                if (agv.Enabled && !string.Equals(agv.Driver, "vendor-tcp", StringComparison.OrdinalIgnoreCase))
                    errors.Add(new($"agvs[{index}].driver", "Physical acceptance profiles require the vendor-tcp driver."));
            }
        }

        RequireText(physical.ExpectedControlOwner, "physicalAcceptance.expectedControlOwner", "Expected control owner is required.", errors);
        var snapshot = physical.MapSnapshot;
        if (snapshot is null)
        {
            errors.Add(new("physicalAcceptance.mapSnapshot", "Controller map snapshot is required."));
        }
        else
        {
            RequireText(snapshot.MapName, "physicalAcceptance.mapSnapshot.mapName", "Controller map name is required.", errors);
            RequireText(snapshot.Version, "physicalAcceptance.mapSnapshot.version", "Controller map version is required.", errors);
            if (!IsMd5(snapshot.Md5))
                errors.Add(new("physicalAcceptance.mapSnapshot.md5", "Controller map MD5 must contain 32 hexadecimal characters."));
            if (snapshot.CapturedAtUtc == default || snapshot.CapturedAtUtc.Offset != TimeSpan.Zero)
                errors.Add(new("physicalAcceptance.mapSnapshot.capturedAtUtc", "Controller map snapshot time must be a non-default UTC value."));

            var configuredStations = (configuration.Stations ?? [])
                .Select(station => station.AgvStationId)
                .Where(stationId => !string.IsNullOrWhiteSpace(stationId))
                .ToHashSet(StringComparer.Ordinal);
            var mapStations = (configuration.Map?.StationIds ?? [])
                .Where(stationId => !string.IsNullOrWhiteSpace(stationId))
                .ToHashSet(StringComparer.Ordinal);
            var snapshotStations = new HashSet<string>(StringComparer.Ordinal);
            var snapshotStationIds = snapshot.StationIds ?? [];
            if (snapshotStationIds.Count == 0)
                errors.Add(new("physicalAcceptance.mapSnapshot.stationIds", "Controller map snapshot requires stations."));
            for (var index = 0; index < snapshotStationIds.Count; index++)
            {
                var stationId = snapshotStationIds[index];
                var path = $"physicalAcceptance.mapSnapshot.stationIds[{index}]";
                RequireText(stationId, path, "Controller map station id is required.", errors);
                if (!string.IsNullOrWhiteSpace(stationId) && !snapshotStations.Add(stationId))
                    errors.Add(new(path, $"Controller map station '{stationId}' is duplicated."));
            }
            if (!snapshotStations.SetEquals(configuredStations) || !snapshotStations.SetEquals(mapStations))
                errors.Add(new("physicalAcceptance.mapSnapshot.stationIds", "Controller map stations must exactly match configured stations and routing map stations."));

            var snapshotEdges = new HashSet<string>(StringComparer.Ordinal);
            var directedEdges = snapshot.DirectedEdges ?? [];
            if (directedEdges.Count == 0)
                errors.Add(new("physicalAcceptance.mapSnapshot.directedEdges", "Controller map snapshot requires direct directed edges."));
            for (var index = 0; index < directedEdges.Count; index++)
            {
                var edge = directedEdges[index];
                var path = $"physicalAcceptance.mapSnapshot.directedEdges[{index}]";
                RequireText(edge.From, $"{path}.from", "Controller edge source is required.", errors);
                RequireText(edge.To, $"{path}.to", "Controller edge target is required.", errors);
                if (!string.IsNullOrWhiteSpace(edge.From) && !snapshotStations.Contains(edge.From))
                    errors.Add(new($"{path}.from", $"Controller edge source '{edge.From}' is not a snapshot station."));
                if (!string.IsNullOrWhiteSpace(edge.To) && !snapshotStations.Contains(edge.To))
                    errors.Add(new($"{path}.to", $"Controller edge target '{edge.To}' is not a snapshot station."));
                if (StringComparer.Ordinal.Equals(edge.From, edge.To))
                    errors.Add(new(path, "Controller edge cannot connect a station to itself."));
                if (!string.IsNullOrWhiteSpace(edge.From) && !string.IsNullOrWhiteSpace(edge.To)
                    && !snapshotEdges.Add(EdgeKey(edge.From, edge.To)))
                    errors.Add(new(path, "Duplicate controller directed edge."));
            }

            var mapEdges = new HashSet<string>(StringComparer.Ordinal);
            var routingEdges = configuration.Map?.Edges ?? [];
            for (var index = 0; index < routingEdges.Count; index++)
            {
                var edge = routingEdges[index];
                if (edge.Bidirectional)
                    errors.Add(new($"map.edges[{index}].bidirectional", "Physical acceptance map edges must be directed."));
                if (!string.IsNullOrWhiteSpace(edge.From) && !string.IsNullOrWhiteSpace(edge.To))
                    mapEdges.Add(EdgeKey(edge.From, edge.To));
            }
            if (!snapshotEdges.SetEquals(mapEdges))
                errors.Add(new("physicalAcceptance.mapSnapshot.directedEdges", "Controller directed edges must exactly match routing map edges."));
        }

        var safety = physical.Safety;
        if (safety is null)
        {
            errors.Add(new("physicalAcceptance.safety", "Physical safety gates are required."));
            return;
        }

        if (!double.IsFinite(safety.MinimumLocalizationConfidence)
            || safety.MinimumLocalizationConfidence <= 0
            || safety.MinimumLocalizationConfidence > 1)
            errors.Add(new("physicalAcceptance.safety.minimumLocalizationConfidence", "Minimum localization confidence must be in (0, 1]."));
        if (!double.IsFinite(safety.MaximumDispatchSpeedMetersPerSecond)
            || safety.MaximumDispatchSpeedMetersPerSecond <= 0)
            errors.Add(new("physicalAcceptance.safety.maximumDispatchSpeedMetersPerSecond", "Maximum dispatch speed must be finite and greater than zero."));
        else if (enabledAgvs.Length > 0 && safety.MaximumDispatchSpeedMetersPerSecond > enabledAgvs.Min(agv => agv.MaxSpeedMetersPerSecond))
            errors.Add(new("physicalAcceptance.safety.maximumDispatchSpeedMetersPerSecond", "Maximum dispatch speed cannot exceed an enabled AGV speed limit."));

        if (!safety.RequireControlOwnership)
            errors.Add(new("physicalAcceptance.safety.requireControlOwnership", "Control ownership gate is required."));
        if (!safety.RequireNoEmergency)
            errors.Add(new("physicalAcceptance.safety.requireNoEmergency", "Emergency-clear gate is required."));
        if (!safety.RequireNoBlocked)
            errors.Add(new("physicalAcceptance.safety.requireNoBlocked", "Blocked-clear gate is required."));
        if (!safety.RequireNoFaults)
            errors.Add(new("physicalAcceptance.safety.requireNoFaults", "Fault-clear gate is required."));
        if (!safety.RequireAutomaticMode)
            errors.Add(new("physicalAcceptance.safety.requireAutomaticMode", "Automatic-mode gate is required."));
    }

    private static bool IsMd5(string? value) =>
        value is { Length: 32 } && value.All(Uri.IsHexDigit);

    private static string EdgeKey(string from, string to) => $"{from}\u001f{to}";

    private static void ValidateTimeouts(TimeoutOptions? timeouts, ICollection<ProfileValidationError> errors)
    {
        if (timeouts is null)
        {
            errors.Add(new("timeouts", "Timeout options are required."));
            return;
        }

        ValidatePositive(timeouts.ConnectionTimeout, "timeouts.connectionTimeout", errors);
        ValidatePositive(timeouts.DispatchTimeout, "timeouts.dispatchTimeout", errors);
        ValidatePositive(timeouts.CommandTimeout, "timeouts.commandTimeout", errors);
        ValidatePositive(timeouts.TaskCompletionTimeout, "timeouts.taskCompletionTimeout", errors);
        ValidatePositive(timeouts.TaskPollingInterval, "timeouts.taskPollingInterval", errors);
    }

    private static void ValidatePositive(TimeSpan value, string path, ICollection<ProfileValidationError> errors)
    {
        if (value <= TimeSpan.Zero)
            errors.Add(new(path, "Duration must be greater than zero."));
    }

    private static void RequireText(string? value, string path, string message, ICollection<ProfileValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add(new(path, message));
    }
}
