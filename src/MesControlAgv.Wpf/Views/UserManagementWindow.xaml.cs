using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Views;

public partial class UserManagementWindow : Window
{
    private readonly AccessClient _client;
    private readonly ObservableCollection<UserRow> _users = [];
    private bool _busy;
    public bool SessionInvalidated { get; private set; }
    public UserManagementWindow(AccessClient client)
    {
        InitializeComponent(); _client = client; UsersGrid.ItemsSource = _users;
        Loaded += async (_, _) => await LoadAsync();
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
    }
    private async Task LoadAsync()
    {
        if (_busy) return;
        _busy = true; Actions.IsEnabled = false;
        try
        {
            var selectedId = (UsersGrid.SelectedItem as UserRow)?.User.Id;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var users = await _client.ListUsersAsync(timeout.Token);
            _users.Clear(); foreach (var user in users) _users.Add(new(user));
            UsersGrid.SelectedItem = _users.FirstOrDefault(r => r.User.Id == selectedId);
            Status.Text = $"共 {_users.Count} 个账号。选中账号后可编辑资料；不能停用或降级最后一个管理员。";
        }
        catch (AccessRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized) { EndSession(); }
        catch (Exception ex) { Status.Text = "刷新失败，保留已显示的数据：" + ex.Message; }
        finally { _busy = false; Actions.IsEnabled = true; }
    }
    private void EndSession() { _busy = false; SessionInvalidated = true; Close(); }
    private async Task EditAsync(AccountEditorMode mode)
    {
        if (_busy) return;
        var target = (UsersGrid.SelectedItem as UserRow)?.User;
        if (mode != AccountEditorMode.Create && target is null) { Status.Text = "请先选择账号。"; return; }
        var dialog = new AccountEditorWindow(_client, mode, target) { Owner = this };
        dialog.ShowDialog();
        if (dialog.SessionInvalidated) { EndSession(); return; }
        if (dialog.Saved) await LoadAsync();
    }
    private async void Create_Click(object s, RoutedEventArgs e) => await EditAsync(AccountEditorMode.Create);
    private async void Edit_Click(object s, RoutedEventArgs e) => await EditAsync(AccountEditorMode.Edit);
    private async void Reset_Click(object s, RoutedEventArgs e) => await EditAsync(AccountEditorMode.ResetPassword);
    private async void Refresh_Click(object s, RoutedEventArgs e) => await LoadAsync();
    private async void Toggle_Click(object s, RoutedEventArgs e)
    {
        if (_busy) return;
        if (UsersGrid.SelectedItem is not UserRow row) { Status.Text = "请先选择账号。"; return; }
        var action = row.User.Enabled ? "停用" : "启用";
        if (MessageBox.Show(this, $"确认{action}账号 {row.Username}？该账号的旧会话将失效。", "确认账号状态", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _busy = true; Actions.IsEnabled = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _client.UpdateUserAsync(row.User.Id, new(row.User.DisplayName, row.User.Role, !row.User.Enabled), timeout.Token);
            if (row.User.Id == _client.CurrentUser?.Id) { EndSession(); return; }
        }
        catch (AccessRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized) { EndSession(); return; }
        catch (Exception ex) { Status.Text = ex.Message; return; }
        finally { _busy = false; Actions.IsEnabled = true; }
        await LoadAsync();
    }
    public sealed record UserRow(AccessUser User)
    {
        public string Username => User.Username;
        public string DisplayName => User.DisplayName;
        public string RoleDisplay => AccessRoles.Display(User.Role);
        public string StatusDisplay => User.Enabled ? "已启用" : "已停用";
        public string LastLoginDisplay => User.LastLogin?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚未登录";
    }
}
