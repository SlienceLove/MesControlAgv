namespace MesControlAgv.Contracts;

public static class AccessRoles
{
    public const string Administrator = "Administrator";
    public const string Operator = "Operator";
    public const string Viewer = "Viewer";
    public static bool IsValid(string role) => role is Administrator or Operator or Viewer;
    public static string Display(string role) => role switch { Administrator => "管理员", Operator => "操作员", Viewer => "只读用户", _ => "未知角色" };
}
public sealed record AccessMode(bool Enabled, bool Demo);
public sealed record AccessUser(Guid Id, string Username, string DisplayName, string Role, bool Enabled, DateTimeOffset? LastLogin);
public sealed record AccessLoginRequest(string Username, string Password);
public sealed record AccessLoginResponse(string Token, DateTimeOffset ExpiresAt, AccessUser User);
public sealed record AccessCreateUser(string Username, string DisplayName, string Role, string Password);
public sealed record AccessUpdateUser(string DisplayName, string Role, bool Enabled);
public sealed record AccessChangePassword(string CurrentPassword, string NewPassword);
public sealed record AccessResetPassword(string NewPassword);
