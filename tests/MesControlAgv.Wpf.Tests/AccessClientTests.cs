using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class AccessClientTests
{
    [Fact]
    public async Task Mode_treats_only_404_as_legacy_disabled_and_surfaces_service_errors()
    {
        using var notFound = new HttpClient(new Handler(_ => new(HttpStatusCode.NotFound))) { BaseAddress = new("http://localhost/") };
        Assert.False((await new AccessClient(notFound).GetModeAsync(CancellationToken.None)).Enabled);
        using var error = new HttpClient(new Handler(_ => new(HttpStatusCode.ServiceUnavailable))) { BaseAddress = new("http://localhost/") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AccessClient(error).GetModeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Session_and_password_requests_use_authenticated_client()
    {
        var user = new AccessUser(Guid.NewGuid(), "admin", "管理员", AccessRoles.Administrator, true, null);
        var token = "AABB";
        var handler = new Handler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(token, request.Headers.Authorization?.Parameter);
            Assert.Equal(HttpMethod.Post, request.Method);
            return new(HttpStatusCode.NoContent);
        });
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        var client = new AccessClient(http);
        client.SetSession(new(token, DateTimeOffset.UtcNow.AddHours(1), user));
        Assert.Equal(user, client.CurrentUser);
        await client.ChangePasswordAsync("old-password", "NewPassword2026", CancellationToken.None);
        client.ClearSession();
        Assert.Null(client.CurrentUser);
    }

    [Fact]
    public async Task Session_monitor_retries_network_failures_but_stops_on_unauthorized()
    {
        var attempts = 0; var updated = 0; var canceled = new CancellationTokenSource();
        var user = new AccessUser(Guid.NewGuid(), "operator", "操作员", AccessRoles.Operator, true, null);
        var monitor = new AccessSessionMonitor(_ =>
        {
            attempts++;
            if (attempts < 3) throw new HttpRequestException("offline");
            canceled.Cancel(); return Task.FromResult(user);
        }, _ => Task.CompletedTask);
        await monitor.RunAsync(_ => updated++, () => Assert.True(false), canceled.Token);
        Assert.Equal(3, attempts); Assert.Equal(0, updated);

        var revoked = false;
        var unauthorized = new AccessSessionMonitor(_ => Task.FromException<AccessUser>(
            new AccessRequestException(System.Net.HttpStatusCode.Unauthorized, "expired")), _ => Task.CompletedTask);
        await unauthorized.RunAsync(_ => { }, () => revoked = true, CancellationToken.None);
        Assert.True(revoked);
    }

    [Fact]
    public void Viewer_presentation_exposes_only_read_pages_and_actions()
    {
        Assert.True(AccessUiPolicy.CanNavigate(AccessRoles.Viewer, "KpiDashboardTab"));
        Assert.False(AccessUiPolicy.CanNavigate(AccessRoles.Viewer, "AgvCommunicationTab"));
        Assert.True(AccessUiPolicy.CanUseAction(AccessRoles.Viewer, "刷新"));
        Assert.False(AccessUiPolicy.CanUseAction(AccessRoles.Viewer, "创建任务"));
        Assert.True(AccessUiPolicy.CanNavigate(AccessRoles.Operator, "ExperimentSchedulingTab"));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null) _ = await request.Content.ReadAsStringAsync(cancellationToken);
            return response(request);
        }
    }
}
