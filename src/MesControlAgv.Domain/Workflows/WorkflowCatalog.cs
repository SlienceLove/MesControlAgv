using System.Collections.ObjectModel;
using System.Globalization;
using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Domain.Workflows;

public enum WorkflowCatalogResolutionStatus
{
    Supported,
    UnknownType,
    UnsupportedSchemaVersion,
    FutureSchemaVersion
}

public enum WorkflowCatalogEditMode
{
    Typed,
    PreserveReadOnly
}

public enum WorkflowCatalogPublishDisposition
{
    EligibleForValidation,
    BlockedByCatalogCompatibility
}

public sealed record WorkflowNodeTypeResolution(
    WorkflowCatalogResolutionStatus Status,
    WorkflowNodeTypeDefinition? Definition = null)
{
    public WorkflowCatalogEditMode EditMode => Status == WorkflowCatalogResolutionStatus.Supported
        ? WorkflowCatalogEditMode.Typed
        : WorkflowCatalogEditMode.PreserveReadOnly;

    /// <summary>Every resolution is saved without replacing or dropping the original node payload.</summary>
    public bool PreserveOnSave => true;

    public WorkflowCatalogPublishDisposition PublishDisposition =>
        Status == WorkflowCatalogResolutionStatus.Supported && Definition?.Enabled == true
            ? WorkflowCatalogPublishDisposition.EligibleForValidation
            : WorkflowCatalogPublishDisposition.BlockedByCatalogCompatibility;
}

public sealed record WorkflowCapabilityResolution(
    WorkflowCatalogResolutionStatus Status,
    DeviceCapabilityDefinition? Definition = null)
{
    public WorkflowCatalogPublishDisposition PublishDisposition =>
        Status == WorkflowCatalogResolutionStatus.Supported &&
        Definition?.Enabled == true &&
        (!RequiresControl(Definition) || Definition.ControlEnabled)
            ? WorkflowCatalogPublishDisposition.EligibleForValidation
            : WorkflowCatalogPublishDisposition.BlockedByCatalogCompatibility;

    private static bool RequiresControl(DeviceCapabilityDefinition definition) =>
        definition.SafetyClassification is
            WorkflowSafetyClassification.ControlledDeviceAction or
            WorkflowSafetyClassification.RestrictedDeviceWrite;
}

public interface IWorkflowNodeTypeCatalog
{
    string CatalogVersion { get; }
    IReadOnlyList<WorkflowNodeTypeDefinition> Definitions { get; }
    IReadOnlyList<WorkflowNodeMigrationDefinition> Migrations { get; }
    bool TryGet(string nodeTypeId, string schemaVersion, out WorkflowNodeTypeDefinition? definition);
    WorkflowNodeTypeDefinition? GetLatest(string nodeTypeId);
    WorkflowNodeTypeResolution Resolve(string nodeTypeId, string schemaVersion);
    IReadOnlyList<WorkflowNodeMigrationDefinition> GetMigrations(string sourceNodeTypeId, string sourceSchemaVersion);
}

public interface IDeviceCapabilityCatalog
{
    string CatalogVersion { get; }
    IReadOnlyList<DeviceCapabilityDefinition> Definitions { get; }
    bool TryGet(string capabilityId, string schemaVersion, out DeviceCapabilityDefinition? definition);
    DeviceCapabilityDefinition? GetLatest(string capabilityId);
    WorkflowCapabilityResolution Resolve(string capabilityId, string schemaVersion);
}

public sealed record WorkflowCatalogSet(
    IWorkflowNodeTypeCatalog NodeTypes,
    IDeviceCapabilityCatalog Capabilities);

public sealed class WorkflowNodeTypeCatalog : IWorkflowNodeTypeCatalog
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, WorkflowNodeTypeDefinition>> _byType;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<WorkflowNodeMigrationDefinition>> _migrationsBySource;

    public WorkflowNodeTypeCatalog(
        string catalogVersion,
        IEnumerable<WorkflowNodeTypeDefinition> definitions,
        IEnumerable<WorkflowNodeMigrationDefinition>? migrations = null,
        IDeviceCapabilityCatalog? capabilities = null)
    {
        CatalogVersion = CatalogContractValidator.RequireCatalogVersion(catalogVersion);
        ArgumentNullException.ThrowIfNull(definitions);

        var frozenDefinitions = definitions
            .Select(CatalogContractValidator.FreezeAndValidate)
            .OrderBy(definition => definition.NodeTypeId, StringComparer.Ordinal)
            .ThenBy(definition => CatalogContractValidator.ParseSchemaVersion(definition.SchemaVersion))
            .ToArray();
        if (frozenDefinitions.Length == 0)
            throw new ArgumentException("A workflow node catalog must contain at least one definition.", nameof(definitions));

        var byType = new Dictionary<string, IReadOnlyDictionary<string, WorkflowNodeTypeDefinition>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var typeGroup in frozenDefinitions.GroupBy(
                     definition => definition.NodeTypeId,
                     StringComparer.OrdinalIgnoreCase))
        {
            var versions = new Dictionary<string, WorkflowNodeTypeDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in typeGroup)
            {
                if (!versions.TryAdd(definition.SchemaVersion, definition))
                {
                    throw new ArgumentException(
                        $"Workflow node type '{definition.NodeTypeId}' schema '{definition.SchemaVersion}' is duplicated.",
                        nameof(definitions));
                }

                if (capabilities is null) continue;
                foreach (var capabilityId in definition.RequiredCapabilityIds)
                {
                    if (capabilities.GetLatest(capabilityId) is null)
                    {
                        throw new ArgumentException(
                            $"Workflow node type '{definition.NodeTypeId}' requires unknown capability '{capabilityId}'.",
                            nameof(definitions));
                    }
                }
            }

            byType.Add(typeGroup.Key, new ReadOnlyDictionary<string, WorkflowNodeTypeDefinition>(versions));
        }

        _byType = new ReadOnlyDictionary<string, IReadOnlyDictionary<string, WorkflowNodeTypeDefinition>>(byType);
        Definitions = Array.AsReadOnly(frozenDefinitions);

        var frozenMigrations = (migrations ?? [])
            .Select(CatalogContractValidator.FreezeAndValidate)
            .OrderBy(migration => migration.SourceNodeTypeId, StringComparer.Ordinal)
            .ThenBy(migration => CatalogContractValidator.ParseSchemaVersion(migration.SourceSchemaVersion))
            .ThenBy(migration => migration.TargetNodeTypeId, StringComparer.Ordinal)
            .ToArray();
        ValidateMigrations(frozenMigrations);
        Migrations = Array.AsReadOnly(frozenMigrations);
        _migrationsBySource = new ReadOnlyDictionary<string, IReadOnlyList<WorkflowNodeMigrationDefinition>>(
            frozenMigrations
                .GroupBy(MigrationSourceKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<WorkflowNodeMigrationDefinition>)Array.AsReadOnly(group.ToArray()),
                    StringComparer.OrdinalIgnoreCase));
    }

    public string CatalogVersion { get; }
    public IReadOnlyList<WorkflowNodeTypeDefinition> Definitions { get; }
    public IReadOnlyList<WorkflowNodeMigrationDefinition> Migrations { get; }

    public bool TryGet(
        string nodeTypeId,
        string schemaVersion,
        out WorkflowNodeTypeDefinition? definition)
    {
        definition = null;
        return !string.IsNullOrWhiteSpace(nodeTypeId) &&
               !string.IsNullOrWhiteSpace(schemaVersion) &&
               _byType.TryGetValue(nodeTypeId, out var versions) &&
               versions.TryGetValue(schemaVersion, out definition);
    }

    public WorkflowNodeTypeDefinition? GetLatest(string nodeTypeId)
    {
        if (string.IsNullOrWhiteSpace(nodeTypeId) || !_byType.TryGetValue(nodeTypeId, out var versions))
            return null;
        return versions.Values.MaxBy(definition => CatalogContractValidator.ParseSchemaVersion(definition.SchemaVersion));
    }

    public WorkflowNodeTypeResolution Resolve(string nodeTypeId, string schemaVersion)
    {
        if (TryGet(nodeTypeId, schemaVersion, out var exact))
            return new WorkflowNodeTypeResolution(WorkflowCatalogResolutionStatus.Supported, exact);
        if (string.IsNullOrWhiteSpace(nodeTypeId) || !_byType.ContainsKey(nodeTypeId))
            return new WorkflowNodeTypeResolution(WorkflowCatalogResolutionStatus.UnknownType);

        var latest = GetLatest(nodeTypeId)!;
        return CatalogContractValidator.IsFutureSchema(schemaVersion, latest.SchemaVersion)
            ? new WorkflowNodeTypeResolution(WorkflowCatalogResolutionStatus.FutureSchemaVersion)
            : new WorkflowNodeTypeResolution(WorkflowCatalogResolutionStatus.UnsupportedSchemaVersion);
    }

    public IReadOnlyList<WorkflowNodeMigrationDefinition> GetMigrations(
        string sourceNodeTypeId,
        string sourceSchemaVersion)
    {
        if (string.IsNullOrWhiteSpace(sourceNodeTypeId) || string.IsNullOrWhiteSpace(sourceSchemaVersion))
            return Array.Empty<WorkflowNodeMigrationDefinition>();
        return _migrationsBySource.TryGetValue(MigrationSourceKey(sourceNodeTypeId, sourceSchemaVersion), out var values)
            ? values
            : Array.Empty<WorkflowNodeMigrationDefinition>();
    }

    private void ValidateMigrations(IReadOnlyList<WorkflowNodeMigrationDefinition> migrations)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var migration in migrations)
        {
            var identity = string.Join(
                '\u001f',
                migration.SourceNodeTypeId,
                migration.SourceSchemaVersion,
                migration.TargetNodeTypeId,
                migration.TargetSchemaVersion);
            if (!identities.Add(identity))
                throw new ArgumentException($"Workflow node migration '{identity}' is duplicated.", nameof(migrations));
            if (!migration.RequiresExplicitConfirmation)
                throw new ArgumentException("G3 node migrations must require explicit confirmation.", nameof(migrations));
            if (!TryGet(migration.TargetNodeTypeId, migration.TargetSchemaVersion, out _))
            {
                throw new ArgumentException(
                    $"Workflow node migration target '{migration.TargetNodeTypeId}' schema " +
                    $"'{migration.TargetSchemaVersion}' is not in the catalog.",
                    nameof(migrations));
            }
        }
    }

    private static string MigrationSourceKey(WorkflowNodeMigrationDefinition migration) =>
        MigrationSourceKey(migration.SourceNodeTypeId, migration.SourceSchemaVersion);

    private static string MigrationSourceKey(string nodeTypeId, string schemaVersion) =>
        $"{nodeTypeId}\u001f{schemaVersion}";
}

public sealed class DeviceCapabilityCatalog : IDeviceCapabilityCatalog
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, DeviceCapabilityDefinition>> _byCapability;

    public DeviceCapabilityCatalog(string catalogVersion, IEnumerable<DeviceCapabilityDefinition> definitions)
    {
        CatalogVersion = CatalogContractValidator.RequireCatalogVersion(catalogVersion);
        ArgumentNullException.ThrowIfNull(definitions);

        var frozenDefinitions = definitions
            .Select(CatalogContractValidator.FreezeAndValidate)
            .OrderBy(definition => definition.CapabilityId, StringComparer.Ordinal)
            .ThenBy(definition => CatalogContractValidator.ParseSchemaVersion(definition.SchemaVersion))
            .ToArray();
        if (frozenDefinitions.Length == 0)
            throw new ArgumentException("A device capability catalog must contain at least one definition.", nameof(definitions));

        var byCapability = new Dictionary<string, IReadOnlyDictionary<string, DeviceCapabilityDefinition>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var capabilityGroup in frozenDefinitions.GroupBy(
                     definition => definition.CapabilityId,
                     StringComparer.OrdinalIgnoreCase))
        {
            var versions = new Dictionary<string, DeviceCapabilityDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in capabilityGroup)
            {
                if (!versions.TryAdd(definition.SchemaVersion, definition))
                {
                    throw new ArgumentException(
                        $"Device capability '{definition.CapabilityId}' schema '{definition.SchemaVersion}' is duplicated.",
                        nameof(definitions));
                }
            }

            byCapability.Add(
                capabilityGroup.Key,
                new ReadOnlyDictionary<string, DeviceCapabilityDefinition>(versions));
        }

        _byCapability = new ReadOnlyDictionary<string, IReadOnlyDictionary<string, DeviceCapabilityDefinition>>(
            byCapability);
        Definitions = Array.AsReadOnly(frozenDefinitions);
    }

    public string CatalogVersion { get; }
    public IReadOnlyList<DeviceCapabilityDefinition> Definitions { get; }

    public bool TryGet(
        string capabilityId,
        string schemaVersion,
        out DeviceCapabilityDefinition? definition)
    {
        definition = null;
        return !string.IsNullOrWhiteSpace(capabilityId) &&
               !string.IsNullOrWhiteSpace(schemaVersion) &&
               _byCapability.TryGetValue(capabilityId, out var versions) &&
               versions.TryGetValue(schemaVersion, out definition);
    }

    public DeviceCapabilityDefinition? GetLatest(string capabilityId)
    {
        if (string.IsNullOrWhiteSpace(capabilityId) ||
            !_byCapability.TryGetValue(capabilityId, out var versions))
        {
            return null;
        }

        return versions.Values.MaxBy(definition => CatalogContractValidator.ParseSchemaVersion(definition.SchemaVersion));
    }

    public WorkflowCapabilityResolution Resolve(string capabilityId, string schemaVersion)
    {
        if (TryGet(capabilityId, schemaVersion, out var exact))
            return new WorkflowCapabilityResolution(WorkflowCatalogResolutionStatus.Supported, exact);
        if (string.IsNullOrWhiteSpace(capabilityId) || !_byCapability.ContainsKey(capabilityId))
            return new WorkflowCapabilityResolution(WorkflowCatalogResolutionStatus.UnknownType);

        var latest = GetLatest(capabilityId)!;
        return CatalogContractValidator.IsFutureSchema(schemaVersion, latest.SchemaVersion)
            ? new WorkflowCapabilityResolution(WorkflowCatalogResolutionStatus.FutureSchemaVersion)
            : new WorkflowCapabilityResolution(WorkflowCatalogResolutionStatus.UnsupportedSchemaVersion);
    }
}

internal static class CatalogContractValidator
{
    public static WorkflowNodeTypeDefinition FreezeAndValidate(WorkflowNodeTypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        RequireStableId(definition.NodeTypeId, nameof(definition.NodeTypeId));
        ParseSchemaVersion(definition.SchemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Category);

        var configurationSchema = FreezeAndValidate(definition.ConfigurationSchema, "configuration");
        var resultSchema = FreezeAndValidate(definition.ResultSchema, "result");
        var ports = FreezePorts(definition.Ports);
        var capabilities = FreezeStableIds(definition.RequiredCapabilityIds, "required capability");
        return definition with
        {
            ConfigurationSchema = configurationSchema,
            ResultSchema = resultSchema,
            Ports = ports,
            RequiredCapabilityIds = capabilities,
            ProfileSupport = FreezeAndValidate(definition.ProfileSupport)
        };
    }

    public static DeviceCapabilityDefinition FreezeAndValidate(DeviceCapabilityDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        RequireStableId(definition.CapabilityId, nameof(definition.CapabilityId));
        ParseSchemaVersion(definition.SchemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.DisplayName);
        RequireStableId(definition.DeviceFamily, nameof(definition.DeviceFamily));
        if (definition.ControlEnabled && !definition.Enabled)
            throw new ArgumentException($"Disabled capability '{definition.CapabilityId}' cannot enable control.");
        if (definition.SafetyClassification == WorkflowSafetyClassification.ReadOnlyDeviceObservation &&
            definition.ControlEnabled)
        {
            throw new ArgumentException($"Read-only capability '{definition.CapabilityId}' cannot enable control.");
        }
        if (!definition.Enabled && string.IsNullOrWhiteSpace(definition.UnavailableReason))
            throw new ArgumentException($"Disabled capability '{definition.CapabilityId}' requires an unavailable reason.");

        return definition with
        {
            ConfigurationSchema = FreezeAndValidate(definition.ConfigurationSchema, "configuration"),
            ResultSchema = FreezeAndValidate(definition.ResultSchema, "result"),
            ProfileSupport = FreezeAndValidate(definition.ProfileSupport),
            UnavailableReason = string.IsNullOrWhiteSpace(definition.UnavailableReason)
                ? null
                : definition.UnavailableReason.Trim()
        };
    }

    public static WorkflowNodeMigrationDefinition FreezeAndValidate(WorkflowNodeMigrationDefinition migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        RequireStableId(migration.SourceNodeTypeId, nameof(migration.SourceNodeTypeId));
        RequireStableId(migration.TargetNodeTypeId, nameof(migration.TargetNodeTypeId));
        ParseSchemaVersion(migration.SourceSchemaVersion);
        ParseSchemaVersion(migration.TargetSchemaVersion);
        var names = FreezeTextValues(migration.RequiredParameterNames, "required parameter");
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in migration.RequiredParameterValues ?? new Dictionary<string, string?>())
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
            if (!values.TryAdd(pair.Key, pair.Value))
                throw new ArgumentException($"Required migration parameter '{pair.Key}' is duplicated.");
        }

        return migration with
        {
            RequiredParameterNames = names,
            RequiredParameterValues = new ReadOnlyDictionary<string, string?>(values)
        };
    }

    public static string RequireCatalogVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }

    public static Version ParseSchemaVersion(string value)
    {
        if (!Version.TryParse(value, out var version) || version.Major <= 0)
            throw new ArgumentException($"Schema version '{value}' is not a supported dotted numeric version.");
        return version;
    }

    public static bool IsFutureSchema(string candidate, string latest) =>
        Version.TryParse(candidate, out var candidateVersion) &&
        Version.TryParse(latest, out var latestVersion) &&
        candidateVersion > latestVersion;

    private static WorkflowObjectSchema FreezeAndValidate(WorkflowObjectSchema? schema, string schemaName)
    {
        schema ??= new WorkflowObjectSchema();
        var fields = (schema.Fields ?? [])
            .Select(field => FreezeAndValidate(field, schemaName))
            .ToArray();
        var duplicate = fields
            .GroupBy(field => field.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"The {schemaName} schema field '{duplicate.Key}' is duplicated.");
        return schema with { Fields = Array.AsReadOnly(fields) };
    }

    private static WorkflowFieldSchema FreezeAndValidate(WorkflowFieldSchema field, string schemaName)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentException.ThrowIfNullOrWhiteSpace(field.Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(field.DisplayName);
        if (field.Minimum > field.Maximum)
            throw new ArgumentException($"Field '{field.Key}' has a minimum greater than its maximum.");
        if (field.ReferenceKind == WorkflowSchemaReferenceKind.Device && string.IsNullOrWhiteSpace(field.DeviceFamily))
            throw new ArgumentException($"Device field '{field.Key}' requires a device family.");
        if (field.ReferenceKind != WorkflowSchemaReferenceKind.Device && !string.IsNullOrWhiteSpace(field.DeviceFamily))
            throw new ArgumentException($"Non-device field '{field.Key}' cannot declare a device family.");
        if (field.ValueType is not (WorkflowSchemaValueType.Integer or WorkflowSchemaValueType.Decimal) &&
            (field.Minimum is not null || field.Maximum is not null))
        {
            throw new ArgumentException($"Non-numeric field '{field.Key}' cannot declare a numeric range.");
        }

        var allowed = FreezeTextValues(field.AllowedValues, $"allowed value for field '{field.Key}'");
        var legacy = (field.LegacyBindings ?? [])
            .Select(binding =>
            {
                ArgumentNullException.ThrowIfNull(binding);
                ArgumentException.ThrowIfNullOrWhiteSpace(binding.Key);
                return binding with { Key = binding.Key.Trim() };
            })
            .ToArray();
        var duplicateLegacy = legacy
            .GroupBy(binding => $"{binding.Source}\u001f{binding.Key}", StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateLegacy is not null)
            throw new ArgumentException($"Field '{field.Key}' contains a duplicate legacy binding.");

        ValidateDefault(field, allowed, schemaName);
        return field with
        {
            AllowedValues = allowed,
            LegacyBindings = Array.AsReadOnly(legacy),
            DeviceFamily = string.IsNullOrWhiteSpace(field.DeviceFamily) ? null : field.DeviceFamily.Trim()
        };
    }

    private static IReadOnlyList<WorkflowPortDefinition> FreezePorts(
        IReadOnlyList<WorkflowPortDefinition>? source)
    {
        var ports = (source ?? [])
            .Select(port =>
            {
                ArgumentNullException.ThrowIfNull(port);
                ArgumentException.ThrowIfNullOrWhiteSpace(port.Key);
                ArgumentException.ThrowIfNullOrWhiteSpace(port.DisplayName);
                ArgumentException.ThrowIfNullOrWhiteSpace(port.DataType);
                if (port.Direction == WorkflowPortDirection.Input && port.EdgeKind is not null)
                    throw new ArgumentException($"Input port '{port.Key}' cannot emit an edge kind.");
                if (port.Direction == WorkflowPortDirection.Output && port.EdgeKind is null)
                    throw new ArgumentException($"Output port '{port.Key}' must declare an edge kind.");
                return port;
            })
            .ToArray();
        var duplicate = ports
            .GroupBy(port => port.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Workflow port '{duplicate.Key}' is duplicated.");
        return Array.AsReadOnly(ports);
    }

    private static WorkflowProfileSupport FreezeAndValidate(WorkflowProfileSupport? support)
    {
        support ??= new WorkflowProfileSupport();
        var productIds = FreezeTextValues(support.SupportedProductIds, "supported profile product id");
        if (support.IsProfileIndependent && productIds.Count > 0)
            throw new ArgumentException("A profile-independent definition cannot declare supported product ids.");
        if (!support.IsProfileIndependent && productIds.Count == 0)
            throw new ArgumentException("A profile-dependent definition must declare a supported product id.");
        return support with { SupportedProductIds = productIds };
    }

    private static IReadOnlyList<string> FreezeStableIds(IReadOnlyList<string>? values, string description)
    {
        var frozen = FreezeTextValues(values, description);
        foreach (var value in frozen) RequireStableId(value, description);
        return frozen;
    }

    private static IReadOnlyList<string> FreezeTextValues(IReadOnlyList<string>? values, string description)
    {
        var result = new List<string>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values ?? [])
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            var trimmed = value.Trim();
            if (!unique.Add(trimmed))
                throw new ArgumentException($"The {description} '{trimmed}' is duplicated.");
            result.Add(trimmed);
        }

        return Array.AsReadOnly(result.ToArray());
    }

    private static void ValidateDefault(
        WorkflowFieldSchema field,
        IReadOnlyList<string> allowed,
        string schemaName)
    {
        if (field.DefaultValue is null) return;
        var valid = field.ValueType switch
        {
            WorkflowSchemaValueType.String => true,
            WorkflowSchemaValueType.Integer => long.TryParse(
                field.DefaultValue,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _),
            WorkflowSchemaValueType.Decimal => decimal.TryParse(
                field.DefaultValue,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out _),
            WorkflowSchemaValueType.Boolean => bool.TryParse(field.DefaultValue, out _),
            WorkflowSchemaValueType.DateTimeOffset => DateTimeOffset.TryParse(
                field.DefaultValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _),
            _ => false
        };
        if (!valid)
            throw new ArgumentException($"The {schemaName} field '{field.Key}' has an invalid default value.");
        if (allowed.Count > 0 && !allowed.Contains(field.DefaultValue, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"The default for field '{field.Key}' is not an allowed value.");
        if (field.ValueType is not (WorkflowSchemaValueType.Integer or WorkflowSchemaValueType.Decimal)) return;
        var numeric = decimal.Parse(field.DefaultValue, NumberStyles.Number, CultureInfo.InvariantCulture);
        if (field.Minimum is { } minimum && numeric < minimum ||
            field.Maximum is { } maximum && numeric > maximum)
        {
            throw new ArgumentException($"The default for field '{field.Key}' is outside its allowed range.");
        }
    }

    private static void RequireStableId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal) ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-'))
        {
            throw new ArgumentException(
                $"Stable id '{value}' must use lower-case ASCII letters, digits, dots, or hyphens.",
                parameterName);
        }
    }
}
