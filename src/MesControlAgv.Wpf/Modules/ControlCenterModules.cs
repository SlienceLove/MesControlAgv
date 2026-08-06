using MesControlAgv.Wpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Wpf.Modules;

public static class ControlCenterModuleIds
{
    public const string TaskMonitor = "task-monitor";
    public const string AgvCommunication = "agv-communication";
    public const string BatchImport = "batch-import";
    public const string KpiDashboard = "kpi-dashboard";
    public const string WorkflowDesigner = "workflow-designer";
}

public sealed record ControlCenterViewRegistration(
    string Id,
    Type ViewType,
    Type? ViewModelType = null,
    int Order = 0,
    bool Enabled = true,
    IReadOnlyList<string>? RequiredPermissions = null);

public sealed record ControlCenterViewModelRegistration(
    string Id,
    Type ViewModelType,
    int Order = 0,
    bool Enabled = true,
    IReadOnlyList<string>? RequiredPermissions = null);

public sealed record ControlCenterServiceRegistration(
    string Id,
    Type ServiceType,
    Type ImplementationType,
    ServiceLifetime Lifetime = ServiceLifetime.Transient,
    bool Enabled = true);

public sealed record ControlCenterCommandRegistration(
    string Id,
    Type CommandType,
    int Order = 0,
    bool Enabled = true,
    IReadOnlyList<string>? RequiredPermissions = null);

public sealed record ControlCenterPermissionRegistration(
    string Id,
    string DisplayName,
    int Order = 0,
    bool Enabled = true);

public sealed class ControlCenterModuleRegistrations
{
    public ControlCenterModuleRegistrations(
        IEnumerable<ControlCenterViewRegistration>? views = null,
        IEnumerable<ControlCenterViewModelRegistration>? viewModels = null,
        IEnumerable<ControlCenterServiceRegistration>? services = null,
        IEnumerable<ControlCenterCommandRegistration>? commands = null,
        IEnumerable<ControlCenterPermissionRegistration>? permissions = null)
    {
        Views = Normalize(views, static item => item.Id, "view");
        ViewModels = Normalize(viewModels, static item => item.Id, "view-model");
        Services = Normalize(services, static item => item.Id, "service");
        Commands = Normalize(commands, static item => item.Id, "command");
        Permissions = Normalize(permissions, static item => item.Id, "permission");
    }

    public static ControlCenterModuleRegistrations Empty { get; } = new();

    public IReadOnlyList<ControlCenterViewRegistration> Views { get; }
    public IReadOnlyList<ControlCenterViewModelRegistration> ViewModels { get; }
    public IReadOnlyList<ControlCenterServiceRegistration> Services { get; }
    public IReadOnlyList<ControlCenterCommandRegistration> Commands { get; }
    public IReadOnlyList<ControlCenterPermissionRegistration> Permissions { get; }

    private static IReadOnlyList<T> Normalize<T>(
        IEnumerable<T>? items,
        Func<T, string> idSelector,
        string kind)
    {
        var normalized = (items ?? []).ToArray();
        foreach (var item in normalized)
        {
            var id = idSelector(item);
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException($"A control-center {kind} registration must have a non-empty ID.", nameof(items));
            }
        }

        var duplicate = normalized
            .GroupBy(idSelector, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new ArgumentException($"Control-center {kind} registration '{duplicate}' is duplicated.", nameof(items));
        }

        return normalized;
    }
}

public sealed record ControlCenterModuleDescriptor(
    string Id,
    string DisplayName,
    int Order,
    bool Enabled = true,
    ControlCenterModuleRegistrations? Registrations = null)
{
    public ControlCenterModuleRegistrations RegistrationSet => Registrations ?? ControlCenterModuleRegistrations.Empty;

    public IReadOnlyList<ControlCenterViewRegistration> Views => RegistrationSet.Views;
    public IReadOnlyList<ControlCenterViewModelRegistration> ViewModels => RegistrationSet.ViewModels;
    public IReadOnlyList<ControlCenterServiceRegistration> Services => RegistrationSet.Services;
    public IReadOnlyList<ControlCenterCommandRegistration> Commands => RegistrationSet.Commands;
    public IReadOnlyList<ControlCenterPermissionRegistration> Permissions => RegistrationSet.Permissions;
}

public interface IControlCenterModule
{
    ControlCenterModuleDescriptor Descriptor { get; }

    // Default implementation keeps existing metadata-only modules source-compatible.
    ControlCenterModuleRegistrations Registrations => Descriptor.RegistrationSet;
}

public sealed class ControlCenterModuleRegistry
{
    private readonly List<RegisteredModule> _modules = [];

    public IReadOnlyList<ControlCenterModuleDescriptor> Modules =>
        _modules
            .OrderBy(item => item.Descriptor.Order)
            .ThenBy(item => item.Descriptor.Id, StringComparer.Ordinal)
            .Select(item => item.Descriptor)
            .ToArray();

    public IReadOnlyList<ControlCenterModuleDescriptor> EnabledModules =>
        Modules.Where(module => module.Enabled).ToArray();

    public void Register(IControlCenterModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var descriptor = module.Descriptor ?? throw new InvalidOperationException("A control-center module must provide a descriptor.");
        ValidateDescriptor(descriptor);

        if (_modules.Any(item => string.Equals(item.Descriptor.Id, descriptor.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Control-center module '{descriptor.Id}' is already registered.");
        }

        _modules.Add(new RegisteredModule(module, descriptor));
    }

    public bool TryGetModule(string moduleId, out ControlCenterModuleDescriptor? descriptor)
    {
        var module = Find(moduleId);
        descriptor = module;
        return module is not null;
    }

    public ControlCenterModuleDescriptor? Find(string moduleId) =>
        _modules.FirstOrDefault(item => string.Equals(item.Descriptor.Id, moduleId, StringComparison.Ordinal))?.Descriptor;

    public bool IsEnabled(string moduleId) =>
        Find(moduleId)?.Enabled == true;

    public void SetEnabled(string moduleId, bool enabled)
    {
        var index = FindIndex(moduleId);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Control-center module '{moduleId}' is not registered.");
        }

        var current = _modules[index];
        _modules[index] = current with { Descriptor = current.Descriptor with { Enabled = enabled } };
    }

    public IReadOnlyList<ControlCenterViewRegistration> GetViews(string moduleId, bool includeDisabled = false) =>
        GetRegistrations(moduleId, item => item.Views, includeDisabled);

    public IReadOnlyList<ControlCenterViewModelRegistration> GetViewModels(string moduleId, bool includeDisabled = false) =>
        GetRegistrations(moduleId, item => item.ViewModels, includeDisabled);

    public IReadOnlyList<ControlCenterServiceRegistration> GetServices(string moduleId, bool includeDisabled = false) =>
        GetRegistrations(moduleId, item => item.Services, includeDisabled);

    public IReadOnlyList<ControlCenterCommandRegistration> GetCommands(string moduleId, bool includeDisabled = false) =>
        GetRegistrations(moduleId, item => item.Commands, includeDisabled);

    public IReadOnlyList<ControlCenterPermissionRegistration> GetPermissions(string moduleId, bool includeDisabled = false) =>
        GetRegistrations(moduleId, item => item.Permissions, includeDisabled);

    public bool HasPermission(string moduleId, string permissionId) =>
        GetPermissions(moduleId).Any(permission => string.Equals(permission.Id, permissionId, StringComparison.Ordinal));

    public bool IsPermissionEnabled(string moduleId, string permissionId) =>
        IsEnabled(moduleId) && HasPermission(moduleId, permissionId);

    public bool CanAccess(string moduleId, IEnumerable<string>? requiredPermissions)
    {
        if (!IsEnabled(moduleId))
        {
            return false;
        }

        return (requiredPermissions ?? []).All(permission => IsPermissionEnabled(moduleId, permission));
    }

    public static ControlCenterModuleRegistry CreateStandard()
    {
        var registry = new ControlCenterModuleRegistry();
        registry.Register(new StandardControlCenterModule(new(
            ControlCenterModuleIds.TaskMonitor,
            "任务监控",
            10,
            Registrations: new ControlCenterModuleRegistrations(
                viewModels: [new(ControlCenterModuleIds.TaskMonitor, typeof(TaskMonitorViewModel), 10)]))));
        registry.Register(new StandardControlCenterModule(new(
            ControlCenterModuleIds.AgvCommunication,
            "AGV 通讯",
            20,
            Registrations: new ControlCenterModuleRegistrations(
                viewModels: [new(ControlCenterModuleIds.AgvCommunication, typeof(AgvCommunicationViewModel), 20)]))));
        registry.Register(new StandardControlCenterModule(new(
            ControlCenterModuleIds.BatchImport,
            "批量导入",
            30,
            Registrations: new ControlCenterModuleRegistrations(
                viewModels: [new(ControlCenterModuleIds.BatchImport, typeof(BatchImportViewModel), 30)]))));
        registry.Register(new StandardControlCenterModule(new(
            ControlCenterModuleIds.KpiDashboard,
            "KPI 看板",
            40,
            Registrations: new ControlCenterModuleRegistrations(
                viewModels: [new(ControlCenterModuleIds.KpiDashboard, typeof(KpiDashboardViewModel), 40)]))));
        registry.Register(new StandardControlCenterModule(new(
            ControlCenterModuleIds.WorkflowDesigner,
            "流程设计",
            50,
            Registrations: new ControlCenterModuleRegistrations(
                viewModels: [new(ControlCenterModuleIds.WorkflowDesigner, typeof(WorkflowEditorViewModel), 50)]))));
        return registry;
    }

    private int FindIndex(string moduleId) =>
        _modules.FindIndex(item => string.Equals(item.Descriptor.Id, moduleId, StringComparison.Ordinal));

    private IReadOnlyList<T> GetRegistrations<T>(
        string moduleId,
        Func<ControlCenterModuleRegistrations, IReadOnlyList<T>> selector,
        bool includeDisabled)
        where T : class
    {
        var module = Find(moduleId);
        if (module is null || (!module.Enabled && !includeDisabled))
        {
            return [];
        }

        var registrations = selector(module.RegistrationSet);
        return registrations
            .Where(item => includeDisabled || IsRegistrationEnabled(item))
            .OrderBy(item => GetRegistrationOrder(item))
            .ThenBy(item => GetRegistrationId(item), StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsRegistrationEnabled<T>(T item) => item switch
    {
        ControlCenterViewRegistration view => view.Enabled,
        ControlCenterViewModelRegistration viewModel => viewModel.Enabled,
        ControlCenterServiceRegistration service => service.Enabled,
        ControlCenterCommandRegistration command => command.Enabled,
        ControlCenterPermissionRegistration permission => permission.Enabled,
        _ => true
    };

    private static int GetRegistrationOrder<T>(T item) => item switch
    {
        ControlCenterViewRegistration view => view.Order,
        ControlCenterViewModelRegistration viewModel => viewModel.Order,
        ControlCenterCommandRegistration command => command.Order,
        ControlCenterPermissionRegistration permission => permission.Order,
        _ => 0
    };

    private static string GetRegistrationId<T>(T item) => item switch
    {
        ControlCenterViewRegistration view => view.Id,
        ControlCenterViewModelRegistration viewModel => viewModel.Id,
        ControlCenterServiceRegistration service => service.Id,
        ControlCenterCommandRegistration command => command.Id,
        ControlCenterPermissionRegistration permission => permission.Id,
        _ => string.Empty
    };

    private static void ValidateDescriptor(ControlCenterModuleDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Id))
        {
            throw new ArgumentException("A control-center module must have a non-empty ID.", nameof(descriptor));
        }

        if (string.IsNullOrWhiteSpace(descriptor.DisplayName))
        {
            throw new ArgumentException($"Control-center module '{descriptor.Id}' must have a display name.", nameof(descriptor));
        }
    }

    private sealed record RegisteredModule(IControlCenterModule Module, ControlCenterModuleDescriptor Descriptor);

    private sealed class StandardControlCenterModule(ControlCenterModuleDescriptor descriptor) : IControlCenterModule
    {
        public ControlCenterModuleDescriptor Descriptor { get; } = descriptor;
    }
}
