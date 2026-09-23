using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Devices;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Mes.Access;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Mes.Tests;

// Exercise the actual access endpoints and middleware without starting equipment workers.
public sealed class AccessHttpTests
{
    [Fact]
    public async Task Anonymous_requests_are_rejected_and_disabled_mode_preserves_legacy_routes()
    {
        await using var host = await Harness.Start();
        using var client = host.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/tasks")).StatusCode);
        Assert.True((await client.GetFromJsonAsync<AccessMode>("/api/access/mode"))!.Enabled);
        await using var legacy = await Harness.Start(enabled: false);
        using var legacyClient = legacy.Client();
        Assert.Equal(HttpStatusCode.OK, (await legacyClient.GetAsync("/api/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await legacyClient.PostAsJsonAsync("/api/access/login", new AccessLoginRequest("admin", "DemoAdmin2026"))).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await legacyClient.GetAsync("/api/access/users")).StatusCode);
        Assert.False(File.Exists(legacy.DatabasePath));
    }

    [Theory]
    [InlineData("viewer", "GET", "/api/tasks", HttpStatusCode.OK)]
    [InlineData("viewer", "POST", "/api/tasks", HttpStatusCode.Forbidden)]
    [InlineData("viewer", "GET", "/API/ACCESS/USERS/", HttpStatusCode.Forbidden)]
    [InlineData("operator", "POST", "/api/tasks", HttpStatusCode.OK)]
    [InlineData("operator", "POST", "/api/configuration", HttpStatusCode.Forbidden)]
    [InlineData("operator", "POST", "/api/workflow-runs/test/resolve-unknown", HttpStatusCode.Forbidden)]
    [InlineData("operator", "GET", "/api/access/users", HttpStatusCode.Forbidden)]
    [InlineData("admin", "GET", "/api/access/users", HttpStatusCode.OK)]
    [InlineData("admin", "POST", "/api/configuration", HttpStatusCode.OK)]
    public async Task Role_policy_is_enforced_over_http(string username, string method, string route, HttpStatusCode expected)
    {
        await using var host = await Harness.Start();
        using var client = host.Client();
        await Login(client, username);
        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        Assert.Equal(expected, (await client.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/vnd.mes+json")]
    public async Task Body_and_query_identity_are_owned_by_authenticated_user(string contentType)
    {
        await using var host = await Harness.Start();
        using var client = host.Client();
        await Login(client, "operator");
        using var content = new StringContent("""{"actor":"admin","nested":[{"requestedBy":"admin"}]}""", Encoding.UTF8, contentType);
        var response = await client.PostAsync("/api/materials/identity?operatorName=admin", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var identity = (await response.Content.ReadFromJsonAsync<IdentityResponse>())!;
        Assert.Equal("operator", identity.Actor);
        Assert.Equal("operator", identity.RequestedBy);
        Assert.Equal("operator", identity.QueryActor);
    }

    [Theory]
    [InlineData("load")]
    [InlineData("run")]
    [InlineData("stop")]
    public async Task Legacy_operator_alias_cannot_override_the_authenticated_actor(string operation)
    {
        await using var host = await Harness.Start();
        using var admin = host.Client();
        await Login(admin, "admin");
        using var content = new StringContent("""{"operatorName":"admin","operator":"viewer"}""", Encoding.UTF8, "application/json");
        var response = await admin.PostAsync($"/api/robot-arms/ARM-01/program/{operation}", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("admin", await response.Content.ReadFromJsonAsync<string>());
    }

    [Fact]
    public async Task Workflow_condition_operator_is_business_data_not_an_identity_alias()
    {
        await using var host = await Harness.Start();
        using var client = host.Client();
        await Login(client, "operator");
        var response = await client.PostAsJsonAsync("/api/workflows/condition", new ConditionRequest(
            new WorkflowConditionExpression { Operator = WorkflowConditionOperator.Equal }, "forged-user"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var condition = (await response.Content.ReadFromJsonAsync<ConditionRequest>())!;
        Assert.Equal(WorkflowConditionOperator.Equal, condition.ConditionExpression.Operator);
        Assert.Equal("operator", condition.Actor);
    }

    [Fact]
    public async Task Account_role_disable_and_password_reset_revoke_existing_sessions()
    {
        await using var host = await Harness.Start();
        using var admin = host.Client();
        using var member = host.Client();
        await Login(admin, "admin");
        var created = await admin.PostAsJsonAsync("/api/access/users", new AccessCreateUser("member", "演示用户", AccessRoles.Operator, "DemoUser2026"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var user = (await created.Content.ReadFromJsonAsync<AccessUser>())!;
        await Login(member, "member");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/access/users/{user.Id}", new AccessUpdateUser("演示用户", AccessRoles.Viewer, true))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/api/access/me")).StatusCode);
        await Login(member, "member");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync("/api/tasks", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/access/users/{user.Id}", new AccessUpdateUser("演示用户", AccessRoles.Viewer, false))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/api/access/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.PostAsJsonAsync("/api/access/login", new AccessLoginRequest("member", "DemoUser2026"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/access/users/{user.Id}", new AccessUpdateUser("演示用户", AccessRoles.Viewer, true))).StatusCode);
        await Login(member, "member");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/access/users/{user.Id}/password", new AccessResetPassword("ResetUser2026"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/api/access/me")).StatusCode);
        await Login(member, "member", "ResetUser2026");
        Assert.Equal(HttpStatusCode.NoContent, (await member.PostAsync("/api/access/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/api/access/me")).StatusCode);
    }

    [Fact]
    public async Task Personal_password_change_and_last_admin_protection_are_enforced()
    {
        await using var host = await Harness.Start();
        using var admin = host.Client();
        var login = await Login(admin, "admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/access/users/{login.User.Id}", new AccessUpdateUser("管理员", AccessRoles.Viewer, true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/access/password", new AccessChangePassword("wrong-password", "NewAdmin2026"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/access/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync("/api/access/password", new AccessChangePassword("DemoAdmin2026", "NewAdmin2026"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/access/me")).StatusCode);
        await Login(admin, "admin", "NewAdmin2026");
        var usersJson = await admin.GetStringAsync("/api/access/users");
        Assert.DoesNotContain("password", usersJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", usersJson, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<AccessLoginResponse> Login(HttpClient client, string username, string? password = null)
    {
        var response = await client.PostAsJsonAsync("/api/access/login", new AccessLoginRequest(username, password ?? (username == "admin" ? "DemoAdmin2026" : "DemoUser2026")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = (await response.Content.ReadFromJsonAsync<AccessLoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return login;
    }

    private sealed record IdentityRequest(string Actor, NestedIdentity[] Nested);
    private sealed record NestedIdentity(string RequestedBy);
    private sealed record IdentityResponse(string Actor, string RequestedBy, string QueryActor);
    private sealed record ConditionRequest(WorkflowConditionExpression ConditionExpression, string Actor);

    private sealed class Harness(WebApplication app, string directory) : IAsyncDisposable
    {
        public string DatabasePath => Path.Combine(directory, "access.db");
        public HttpClient Client() => app.GetTestClient();
        public static async Task<Harness> Start(bool enabled = true)
        {
            var directory = Path.Combine(Path.GetTempPath(), "mes-access-http-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = directory });
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Access:Enabled"] = enabled.ToString(), ["Access:Demo"] = "true",
                ["Access:DatabasePath"] = Path.Combine(directory, "access.db"),
                ["Access:BootstrapPassword"] = "DemoAdmin2026", ["Access:DemoPassword"] = "DemoUser2026"
            });
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddLocalAccess(builder.Configuration);
            var app = builder.Build();
            app.UseRouting();
            app.UseLocalAccess();
            app.MapGet("/api/tasks", () => Results.Ok());
            app.MapPost("/api/tasks", () => Results.Ok());
            app.MapPost("/api/configuration", () => Results.Ok());
            app.MapPost("/api/robot-arms/{deviceId}/program/load", (AuboArmProgramRequest request) => Results.Ok(request.EffectiveOperatorName));
            app.MapPost("/api/robot-arms/{deviceId}/program/run", (AuboArmProgramRequest request) => Results.Ok(request.EffectiveOperatorName));
            app.MapPost("/api/robot-arms/{deviceId}/program/stop", (AuboArmProgramStopRequest request) => Results.Ok(request.EffectiveOperatorName));
            app.MapPost("/api/workflows/condition", (ConditionRequest request) => Results.Ok(request));
            app.MapPost("/api/workflow-runs/test/resolve-unknown", () => Results.Ok());
            app.MapPost("/api/materials/identity", (IdentityRequest request, HttpContext context) =>
                new IdentityResponse(request.Actor, request.Nested[0].RequestedBy, context.Request.Query["operatorName"].ToString()));
            await app.StartAsync();
            return new Harness(app, directory);
        }
        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            using var pool = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString());
            SqliteConnection.ClearPool(pool);
            foreach (var file in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm" }) File.Delete(file);
            Directory.Delete(directory);
        }
    }
}
