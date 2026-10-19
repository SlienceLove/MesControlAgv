using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using MesControlAgv.Contracts;
using Microsoft.AspNetCore.RateLimiting;

namespace MesControlAgv.Mes.Access;

public static class AccessPolicy
{
    public static bool Allows(string role, string method, string path)
    {
        path = path.TrimEnd('/').ToLowerInvariant();
        if (!AccessRoles.IsValid(role)) return false;
        if (path == "/api/access/users" || path.StartsWith("/api/access/users/")) return role == AccessRoles.Administrator;
        if (path is "/api/access/me" or "/api/access/logout" or "/api/access/password") return true;
        if (method is "GET" or "HEAD") return true;
        if (role == AccessRoles.Administrator) return true;
        if (role == AccessRoles.Viewer) return false;
        // Direct controller commands, configuration and unclassified writes are admin-only.
        if (path.Contains("/resolve-unknown") || path.Contains("/manual-close")) return false;
        return new[] { "/api/tasks", "/api/experiment-jobs", "/api/experiment-plans", "/api/experiment-samples",
                "/api/schedule", "/api/materials", "/api/samples", "/api/workflows", "/api/workflow-runs" }
            .Any(prefix => path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal));
    }
}

public static class AccessModule
{
    public static void AddLocalAccess(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(configuration.GetSection("Access").Get<AccessSettings>() ?? new());
        services.AddSingleton<AccessStore>();
        services.AddHttpContextAccessor();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy("access-login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "local",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }
    public static void UseLocalAccess(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<AccessSettings>();
        if (settings.Enabled) _ = app.Services.GetRequiredService<AccessStore>();
        app.UseRateLimiter();
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value?.TrimEnd('/').ToLowerInvariant() ?? "";
            if (!settings.Enabled)
            {
                // Reject before endpoint argument binding can resolve AccessStore and
                // initialize an account database on an instance that never opted in.
                if (path.StartsWith("/api/access/", StringComparison.Ordinal) && path != "/api/access/mode")
                    await Error(context, 503, "此实例未启用登录。");
                else
                    await next(context);
                return;
            }
            if (path is "/health" or "/api/access/mode" or "/api/access/login") { await next(context); return; }
            var store = context.RequestServices.GetRequiredService<AccessStore>();
            var token = Token(context);
            var user = store.Authenticate(token);
            if (user is null) { await Error(context, 401, "请登录，或会话已失效。"); return; }
            context.Items[typeof(AccessUser)] = user;
            context.User = new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.Role, user.Role) }, "LocalSession"));
            if (!AccessPolicy.Allows(user.Role, context.Request.Method, path))
            { await Error(context, 403, "当前角色没有此操作权限。"); return; }
            // Identity is server-owned even for legacy business contracts carrying actor fields.
            if (!path.StartsWith("/api/access/", StringComparison.Ordinal))
            {
                var query = context.Request.Query.ToDictionary(x => x.Key, x => x.Value);
                foreach (var key in query.Keys.Where(IsActor).ToArray()) query[key] = user.Username;
                context.Request.Query = new QueryCollection(query);
                // Match the same JSON media types accepted by ASP.NET endpoint binding,
                // including application/*+json; otherwise identity normalization can be bypassed.
                if (context.Request.HasJsonContentType())
                {
                    if (context.Request.ContentLength is > 8 * 1024 * 1024) { await Error(context, 413, "请求体过大。"); return; }
                    try
                    {
                        using var buffer = new MemoryStream();
                        var chunk = new byte[8192]; int count;
                        while ((count = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
                        {
                            if (buffer.Length + count > 8 * 1024 * 1024) { await Error(context, 413, "请求体过大。"); return; }
                            buffer.Write(chunk, 0, count);
                        }
                        buffer.Position = 0;
                        if (buffer.Length > 0)
                        {
                            var node = await JsonNode.ParseAsync(buffer, cancellationToken: context.RequestAborted);
                            NormalizeActors(node, user.Username);
                            // "operator" is also a workflow comparison field. Only AUBO's
                            // root-level program request alias denotes a user identity.
                            if (node is JsonObject root && IsArmProgramRequest(path))
                                foreach (var key in root.Select(x => x.Key).Where(x => x.Equals("operator", StringComparison.OrdinalIgnoreCase)).ToArray())
                                    root[key] = user.Username;
                            var bytes = Encoding.UTF8.GetBytes(node?.ToJsonString() ?? "null");
                            using var normalized = new MemoryStream(bytes);
                            var original = context.Request.Body;
                            context.Request.Body = normalized; context.Request.ContentLength = bytes.Length;
                            try { await next(context); } finally { context.Request.Body = original; }
                            return;
                        }
                    }
                    catch (System.Text.Json.JsonException) { await Error(context, 400, "无效 JSON 请求。"); return; }
                }
            }
            await next(context);
        });
        app.MapGet("/api/access/mode", () => new AccessMode(settings.Enabled, settings.Demo));
        app.MapPost("/api/access/login", (AccessLoginRequest request, HttpContext context) =>
        {
            if (!settings.Enabled) return Results.Problem("此实例未启用登录。", statusCode: 503);
            var result = context.RequestServices.GetRequiredService<AccessStore>().Login(request);
            return result is null ? Results.Json(new { error = "账号或密码错误、账号已停用或暂时锁定。" }, statusCode: 401) : Results.Ok(result);
        }).RequireRateLimiting("access-login");
        var group = app.MapGroup("/api/access");
        group.AddEndpointFilter(async (context, next) => settings.Enabled
            ? await next(context) : Results.Problem("此实例未启用登录。", statusCode: 503));
        group.MapGet("/me", (HttpContext c) => Results.Ok(Current(c)));
        group.MapPost("/logout", (HttpContext c, AccessStore store) => { store.Logout(Token(c), Current(c).Username); return Results.NoContent(); });
        group.MapPost("/password", (AccessChangePassword r, HttpContext c, AccessStore store) => Try(() =>
        { var u = Current(c); store.ChangePassword(u.Id, r.CurrentPassword, r.NewPassword, u.Username); return Results.NoContent(); }));
        group.MapGet("/users", (AccessStore store) => Results.Ok(store.List()));
        group.MapPost("/users", (AccessCreateUser r, HttpContext c, AccessStore store) => Try(() => Results.Ok(store.Create(r, Current(c).Username))));
        group.MapPut("/users/{id:guid}", (Guid id, AccessUpdateUser r, HttpContext c, AccessStore store) => Try(() => Results.Ok(store.Update(id, r, Current(c).Username))));
        group.MapPost("/users/{id:guid}/password", (Guid id, AccessResetPassword r, HttpContext c, AccessStore store) => Try(() =>
        { store.ChangePassword(id, null, r.NewPassword, Current(c).Username, reset: true); return Results.NoContent(); }));
    }
    public static AccessUser Current(HttpContext c) => (AccessUser)c.Items[typeof(AccessUser)]!;
    private static string Token(HttpContext c) => c.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? c.Request.Headers.Authorization.ToString()[7..].Trim() : "";
    private static bool IsActor(string name) => new[]
        {
            "actor", "operatorName", "operatorAccount", "requestedBy", "createdBy", "updatedBy",
            "submittedBy", "approvedBy", "authorizedBy", "completedBy"
        }.Contains(name, StringComparer.OrdinalIgnoreCase);
    private static bool IsArmProgramRequest(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries)
        is ["api", "robot-arms", _, "program", "load" or "run" or "stop"];
    private static void NormalizeActors(JsonNode? node, string actor)
    {
        if (node is JsonObject obj)
            foreach (var key in obj.Select(x => x.Key).ToArray())
                if (IsActor(key)) obj[key] = actor; else NormalizeActors(obj[key], actor);
        else if (node is JsonArray arr) foreach (var item in arr) NormalizeActors(item, actor);
    }
    private static IResult Try(Func<IResult> action)
    { try { return action(); } catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); } }
    private static Task Error(HttpContext c, int code, string message)
    { c.Response.StatusCode = code; return c.Response.WriteAsJsonAsync(new { error = message }); }
}
