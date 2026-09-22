using MesControlAgv.Contracts;
using MesControlAgv.Mes.Access;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace MesControlAgv.Mes.Tests;

public sealed class AccessStoreTests
{
    [Fact]
    public void Login_uses_hashed_password_and_revokes_session_after_password_change()
    {
        var path = Path.Combine(Path.GetTempPath(), $"access-{Guid.NewGuid():N}.db");
        try
        {
            var store = New(path, demo: true);
            var login = store.Login(new("admin", "DemoAdmin2026"));
            Assert.NotNull(login);
            Assert.Equal(AccessRoles.Administrator, login.User.Role);
            Assert.Null(store.Login(new("admin", "wrong-password")));
            store.ChangePassword(login.User.Id, "DemoAdmin2026", "NewAdmin2026", "admin");
            Assert.Null(store.Authenticate(login.Token));
            Assert.NotNull(store.Login(new("admin", "NewAdmin2026")));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Five_failures_lock_account_and_last_admin_cannot_be_disabled()
    {
        var path = Path.Combine(Path.GetTempPath(), $"access-{Guid.NewGuid():N}.db");
        try
        {
            var store = New(path, demo: false);
            for (var i = 0; i < 5; i++) Assert.Null(store.Login(new("admin", "bad-password")));
            Assert.Null(store.Login(new("admin", "DemoAdmin2026")));
            var admin = Assert.Single(store.List());
            var error = Assert.Throws<ArgumentException>(() => store.Update(admin.Id, new("系统管理员", AccessRoles.Operator, false), "admin"));
            Assert.Contains("最后一个管理员", error.Message);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Access_policy_keeps_viewers_read_only_and_operators_out_of_configuration()
    {
        Assert.True(AccessPolicy.Allows(AccessRoles.Viewer, "GET", "/api/dashboard/kpi"));
        Assert.False(AccessPolicy.Allows(AccessRoles.Viewer, "POST", "/api/tasks"));
        Assert.True(AccessPolicy.Allows(AccessRoles.Operator, "POST", "/api/tasks"));
        Assert.False(AccessPolicy.Allows(AccessRoles.Operator, "POST", "/api/access/users"));
        Assert.True(AccessPolicy.Allows(AccessRoles.Administrator, "POST", "/api/access/users"));
    }

    private static AccessStore New(string path, bool demo) => new(new AccessSettings
    {
        Enabled = true, Demo = demo, DatabasePath = path,
        BootstrapPassword = "DemoAdmin2026", DemoPassword = "DemoUser2026"
    }, new HostEnvironment { ContentRootPath = Path.GetDirectoryName(path)! }, TimeProvider.System);

    private static void TryDelete(string path)
    { foreach (var file in new[] { path, path + "-wal", path + "-shm" }) try { File.Delete(file); } catch { } }

    private sealed class HostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
