using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Contracts;

namespace MesControlAgv.Wpf.Services;

public sealed class AccessClient(HttpClient client)
{
    public async Task<AccessMode> GetModeAsync(CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("api/access/mode", cancellationToken);
        if (!response.IsSuccessStatusCode) return new(false, false);
        return await response.Content.ReadFromJsonAsync<AccessMode>(cancellationToken) ?? new(false, false);
    }
    public async Task<AccessLoginResponse> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("api/access/login", new AccessLoginRequest(username, password), cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("账号或密码错误、账号已停用或暂时锁定。");
        return await response.Content.ReadFromJsonAsync<AccessLoginResponse>(cancellationToken)
            ?? throw new InvalidOperationException("登录服务未返回会话。");
    }
    public void ApplyToken(string token) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    public Task<IReadOnlyList<AccessUser>> ListUsersAsync(CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyList<AccessUser>>("api/access/users", cancellationToken);
    public Task<AccessUser> CreateUserAsync(AccessCreateUser request, CancellationToken cancellationToken) =>
        SendAsync<AccessUser>(HttpMethod.Post, "api/access/users", request, cancellationToken);
    public Task<AccessUser> UpdateUserAsync(Guid id, AccessUpdateUser request, CancellationToken cancellationToken) =>
        SendAsync<AccessUser>(HttpMethod.Put, $"api/access/users/{id}", request, cancellationToken);
    public Task ResetPasswordAsync(Guid id, string password, CancellationToken cancellationToken) =>
        SendNoContentAsync(HttpMethod.Post, $"api/access/users/{id}/password", new AccessResetPassword(password), cancellationToken);
    public Task LogoutAsync(CancellationToken cancellationToken) => SendNoContentAsync(HttpMethod.Post, "api/access/logout", null, cancellationToken);
    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    { using var response = await client.GetAsync(path, ct); if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await Error(response, "读取失败")); return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new InvalidOperationException("服务未返回数据。"); }
    private async Task<T> SendAsync<T>(HttpMethod method, string path, object body, CancellationToken ct)
    { using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) }; using var response = await client.SendAsync(request, ct); if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await Error(response, "操作失败")); return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new InvalidOperationException("服务未返回数据。"); }
    private async Task SendNoContentAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    { using var request = new HttpRequestMessage(method, path); if (body is not null) request.Content = JsonContent.Create(body); using var response = await client.SendAsync(request, ct); if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await Error(response, "操作失败")); }
    private static async Task<string> Error(HttpResponseMessage response, string fallback)
    { try { var json = await response.Content.ReadFromJsonAsync<JsonElement>(); return json.TryGetProperty("error", out var e) ? e.GetString() ?? fallback : fallback; } catch { return fallback; } }
}
