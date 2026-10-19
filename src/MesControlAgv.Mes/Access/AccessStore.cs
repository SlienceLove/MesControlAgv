using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MesControlAgv.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Access;

public sealed class AccessSettings
{
    public bool Enabled { get; init; }
    public bool Demo { get; init; }
    public string? DatabasePath { get; init; }
    public string? BootstrapPassword { get; init; }
    public string? DemoPassword { get; init; }
}
public sealed class AccessAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = AccessRoles.Viewer;
    public bool Enabled { get; set; } = true;
    public string PasswordHash { get; set; } = "";
    public int FailedAttempts { get; set; }
    public long LockUntil { get; set; }
    public long? LastLogin { get; set; }
}
public sealed class AccessSession
{
    public string Id { get; set; } = "";
    public Guid UserId { get; set; }
    public long ExpiresAt { get; set; }
}
public sealed class AccessEvent
{
    public long Id { get; set; }
    public long At { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
}
public sealed class AccessDatabase(DbContextOptions<AccessDatabase> options) : DbContext(options)
{
    public DbSet<AccessAccount> Accounts => Set<AccessAccount>();
    public DbSet<AccessSession> Sessions => Set<AccessSession>();
    public DbSet<AccessEvent> Events => Set<AccessEvent>();
    protected override void OnModelCreating(ModelBuilder model) => model.Entity<AccessAccount>().HasIndex(x => x.Username).IsUnique();
}

public sealed class AccessStore
{
    private readonly object _gate = new();
    private readonly DbContextOptions<AccessDatabase> _options;
    private readonly PasswordHasher<AccessAccount> _hasher = new();
    private readonly AccessAccount _dummy = new();
    private readonly string _dummyHash;
    private readonly TimeProvider _time;
    public AccessStore(AccessSettings settings, IHostEnvironment environment, TimeProvider time)
    {
        _time = time;
        var path = Path.GetFullPath(settings.DatabasePath ?? Path.Combine(environment.ContentRootPath, "data", "access.db"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _options = new DbContextOptionsBuilder<AccessDatabase>().UseSqlite(new SqliteConnectionStringBuilder { DataSource = path }.ToString()).Options;
        _dummyHash = _hasher.HashPassword(_dummy, Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
        using var db = Open(); db.Database.EnsureCreated();
        if (!db.Accounts.Any())
        {
            ValidatePassword(settings.BootstrapPassword ?? "");
            db.Accounts.Add(NewAccount("admin", "系统管理员", AccessRoles.Administrator, settings.BootstrapPassword!));
            if (settings.Demo)
            {
                ValidatePassword(settings.DemoPassword ?? "");
                db.Accounts.Add(NewAccount("operator", "实验操作员", AccessRoles.Operator, settings.DemoPassword!));
                db.Accounts.Add(NewAccount("viewer", "客户访客", AccessRoles.Viewer, settings.DemoPassword!));
            }
            Audit(db, "bootstrap", "accounts.initialized", "local"); db.SaveChanges();
        }
    }
    private AccessDatabase Open() => new(_options);
    private long Now => _time.GetUtcNow().ToUnixTimeSeconds();
    private static string Normalize(string username) => (username ?? "").Trim().ToLowerInvariant();
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static AccessUser View(AccessAccount a) => new(a.Id, a.Username, a.DisplayName, a.Role, a.Enabled,
        a.LastLogin is long last ? DateTimeOffset.FromUnixTimeSeconds(last) : null);
    private void Audit(AccessDatabase db, string actor, string action, string target) =>
        db.Events.Add(new() { At = Now, Actor = actor, Action = action, Target = target });
    public static void ValidatePassword(string password)
    {
        if (password is null || password.Length is < 10 or > 128 || !password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            throw new ArgumentException("密码需为10–128位，并包含字母和数字。");
    }
    private AccessAccount NewAccount(string username, string displayName, string role, string password)
    {
        username = Normalize(username);
        if (!Regex.IsMatch(username, "^[a-z0-9][a-z0-9_.-]{2,31}$")) throw new ArgumentException("账号需为3–32位字母、数字、点、下划线或短横线。");
        ValidateDetails(displayName, role); ValidatePassword(password);
        var a = new AccessAccount { Username = username, DisplayName = displayName.Trim(), Role = role };
        a.PasswordHash = _hasher.HashPassword(a, password); return a;
    }
    private static void ValidateDetails(string displayName, string role)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 64 || !AccessRoles.IsValid(role))
            throw new ArgumentException("请填写64字以内的姓名并选择有效角色。");
    }
    public AccessLoginResponse? Login(AccessLoginRequest request)
    {
        if (request.Username is null || request.Username.Length > 64 || request.Password is null || request.Password.Length > 128) return null;
        lock (_gate)
        {
            using var db = Open(); var name = Normalize(request.Username);
            var a = db.Accounts.SingleOrDefault(x => x.Username == name);
            var result = _hasher.VerifyHashedPassword(a ?? _dummy, a?.PasswordHash ?? _dummyHash, request.Password);
            if (a is null || !a.Enabled || a.LockUntil > Now || result == PasswordVerificationResult.Failed)
            {
                if (a is not null && a.LockUntil <= Now)
                {
                    if (++a.FailedAttempts >= 5) { a.FailedAttempts = 0; a.LockUntil = Now + 300; }
                }
                Audit(db, name, "login.rejected", name); db.SaveChanges(); return null;
            }
            a.FailedAttempts = 0; a.LockUntil = 0; a.LastLogin = Now;
            if (result == PasswordVerificationResult.SuccessRehashNeeded) a.PasswordHash = _hasher.HashPassword(a, request.Password);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var expires = Now + 8 * 3600;
            db.Sessions.Where(x => x.ExpiresAt <= Now).ExecuteDelete();
            db.Sessions.Add(new() { Id = HashToken(token), UserId = a.Id, ExpiresAt = expires });
            Audit(db, a.Username, "login.succeeded", a.Username); db.SaveChanges();
            return new(token, DateTimeOffset.FromUnixTimeSeconds(expires), View(a));
        }
    }
    public AccessUser? Authenticate(string token)
    {
        if (token.Length != 64) return null;
        lock (_gate)
        {
            using var db = Open(); var hash = HashToken(token);
            var session = db.Sessions.SingleOrDefault(x => x.Id == hash);
            if (session is null || session.ExpiresAt <= Now) return null;
            var a = db.Accounts.SingleOrDefault(x => x.Id == session.UserId);
            return a is { Enabled: true } ? View(a) : null;
        }
    }
    public void Logout(string token, string actor)
    {
        lock (_gate) { using var db = Open(); var hash = HashToken(token); db.Sessions.Where(x => x.Id == hash).ExecuteDelete(); Audit(db, actor, "logout", actor); db.SaveChanges(); }
    }
    public IReadOnlyList<AccessUser> List()
    {
        lock (_gate) { using var db = Open(); return db.Accounts.OrderBy(x => x.Username).ToArray().Select(View).ToArray(); }
    }
    public AccessUser Create(AccessCreateUser request, string actor)
    {
        lock (_gate)
        {
            using var db = Open(); var a = NewAccount(request.Username, request.DisplayName, request.Role, request.Password);
            if (db.Accounts.Any(x => x.Username == a.Username)) throw new ArgumentException("账号已存在。");
            db.Accounts.Add(a); Audit(db, actor, "user.created", a.Username); db.SaveChanges(); return View(a);
        }
    }
    public AccessUser Update(Guid id, AccessUpdateUser request, string actor)
    {
        ValidateDetails(request.DisplayName, request.Role);
        lock (_gate)
        {
            using var db = Open(); using var tx = db.Database.BeginTransaction();
            var a = db.Accounts.SingleOrDefault(x => x.Id == id) ?? throw new ArgumentException("账号不存在。");
            if (a.Enabled && a.Role == AccessRoles.Administrator && (!request.Enabled || request.Role != AccessRoles.Administrator)
                && db.Accounts.Count(x => x.Enabled && x.Role == AccessRoles.Administrator) <= 1)
                throw new ArgumentException("不能停用或降级最后一个管理员。");
            a.DisplayName = request.DisplayName.Trim(); a.Role = request.Role; a.Enabled = request.Enabled;
            db.Sessions.Where(x => x.UserId == id).ExecuteDelete();
            Audit(db, actor, "user.updated", a.Username); db.SaveChanges(); tx.Commit(); return View(a);
        }
    }
    public void ChangePassword(Guid id, string? currentPassword, string newPassword, string actor, bool reset = false)
    {
        ValidatePassword(newPassword);
        lock (_gate)
        {
            using var db = Open(); using var tx = db.Database.BeginTransaction();
            var a = db.Accounts.SingleOrDefault(x => x.Id == id) ?? throw new ArgumentException("账号不存在。");
            if (!reset && (currentPassword is null || currentPassword.Length > 128 || _hasher.VerifyHashedPassword(a, a.PasswordHash, currentPassword) == PasswordVerificationResult.Failed))
                throw new ArgumentException("当前密码不正确。");
            a.PasswordHash = _hasher.HashPassword(a, newPassword); a.FailedAttempts = 0; a.LockUntil = 0;
            db.Sessions.Where(x => x.UserId == id).ExecuteDelete();
            Audit(db, actor, reset ? "password.reset" : "password.changed", a.Username); db.SaveChanges(); tx.Commit();
        }
    }
}
