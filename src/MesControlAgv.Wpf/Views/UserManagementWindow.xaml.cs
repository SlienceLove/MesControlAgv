using System.Collections.ObjectModel;
using System.Windows;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Views;

public partial class UserManagementWindow : Window
{
    private readonly AccessClient _client;
    private readonly ObservableCollection<UserRow> _users = [];
    public UserManagementWindow(AccessClient client) { InitializeComponent(); _client = client; UsersGrid.ItemsSource = _users; Loaded += async (_, _) => await LoadAsync(); }
    private async Task LoadAsync()
    { try { _users.Clear(); foreach (var user in await _client.ListUsersAsync(CancellationToken.None)) _users.Add(new(user)); Status.Text = $"共 {_users.Count} 个账号。"; } catch (Exception ex) { Status.Text = ex.Message; } }
    private async void Create_Click(object sender, RoutedEventArgs e)
    { try { var role = (NewRole.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? AccessRoles.Operator; await _client.CreateUserAsync(new(NewUsername.Text, NewDisplayName.Text, role, NewPassword.Password), CancellationToken.None); NewUsername.Clear(); NewDisplayName.Clear(); NewPassword.Clear(); await LoadAsync(); } catch (Exception ex) { Status.Text = ex.Message; } }
    private async void Toggle_Click(object sender, RoutedEventArgs e)
    { if (UsersGrid.SelectedItem is not UserRow row) { Status.Text = "请先选择账号。"; return; } try { var updated = await _client.UpdateUserAsync(row.User.Id, new(row.User.DisplayName, row.User.Role, !row.User.Enabled), CancellationToken.None); row.User = updated; UsersGrid.Items.Refresh(); Status.Text = "账号状态已更新。"; } catch (Exception ex) { Status.Text = ex.Message; } }
    private async void Reset_Click(object sender, RoutedEventArgs e)
    { if (UsersGrid.SelectedItem is not UserRow row) { Status.Text = "请先选择账号。"; return; } if (string.IsNullOrWhiteSpace(NewPassword.Password)) { Status.Text = "请在初始密码框输入重置密码。"; return; } try { await _client.ResetPasswordAsync(row.User.Id, NewPassword.Password, CancellationToken.None); NewPassword.Clear(); Status.Text = "密码已重置，旧会话已失效。"; } catch (Exception ex) { Status.Text = ex.Message; } }
    private sealed class UserRow(AccessUser user)
    { public AccessUser User { get; set; } = user; public string Username => User.Username; public string DisplayName => User.DisplayName; public string RoleDisplay => AccessRoles.Display(User.Role); public bool Enabled => User.Enabled; public DateTimeOffset? LastLogin => User.LastLogin; }
}
