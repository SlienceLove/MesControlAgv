using MesControlAgv.Contracts;
using MesControlAgv.Mes.Access;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public void Lockout_recovers_after_five_minutes_and_session_expires_at_eight_hours()
    {
        var path = Path.Combine(Path.GetTempPath(), $"access-{Guid.NewGuid():N}.db");
        var time = new TestTime();
        try
        {
            var store = New(path, demo: false, time);
            for (var i = 0; i < 5; i++) Assert.Null(store.Login(new("admin", "wrong-password")));
            time.Advance(TimeSpan.FromSeconds(299));
            Assert.Null(store.Login(new("admin", "DemoAdmin2026")));
            time.Advance(TimeSpan.FromSeconds(1));
            var login = store.Login(new("admin", "DemoAdmin2026"));
            Assert.NotNull(login);
            time.Advance(TimeSpan.FromHours(8) - TimeSpan.FromSeconds(1));
            Assert.NotNull(store.Authenticate(login.Token));
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.Null(store.Authenticate(login.Token));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Stored_credentials_are_hashed_and_existing_accounts_survive_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"access-{Guid.NewGuid():N}.db");
        try
        {
            var store = New(path, demo: false);
            var login = store.Login(new("admin", "DemoAdmin2026"))!;
            var options = new DbContextOptionsBuilder<AccessDatabase>().UseSqlite(new SqliteConnectionStringBuilder { DataSource = path }.ToString()).Options;
            using (var db = new AccessDatabase(options))
            {
                Assert.NotEqual("DemoAdmin2026", Assert.Single(db.Accounts).PasswordHash);
                Assert.NotEqual(login.Token, Assert.Single(db.Sessions).Id);
                Assert.DoesNotContain(db.Events, e => e.Target.Contains(login.Token));
            }
            var restarted = New(path, demo: false);
            Assert.Equal(login.User.Id, Assert.Single(restarted.List()).Id);
            Assert.NotNull(restarted.Authenticate(login.Token));
        }
        finally { TryDelete(path); }
    }

    private sealed class TestTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    private static AccessStore New(string path, bool demo, TimeProvider? time = null) => new(new AccessSettings
    {
        Enabled = true, Demo = demo, DatabasePath = path,
        BootstrapPassword = "DemoAdmin2026", DemoPassword = "DemoUser2026"
    }, new HostEnvironment { ContentRootPath = Path.GetDirectoryName(path)! }, time ?? TimeProvider.System);

    private static void TryDelete(string path)
    {
        using var pool = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        SqliteConnection.ClearPool(pool);
        foreach (var file in new[] { path, path + "-wal", path + "-shm" }) File.Delete(file);
    }

    private sealed class HostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
