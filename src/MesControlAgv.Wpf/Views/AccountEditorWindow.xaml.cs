using System.Net;
using System.Windows;
using System.Windows.Controls;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Views;

public enum AccountEditorMode { Create, Edit, ResetPassword, ChangePassword }

public partial class AccountEditorWindow : Window
{
    private readonly AccessClient _client;
    private readonly AccountEditorMode _mode;
    private readonly AccessUser? _target;
    private bool _busy;
    public bool SessionInvalidated { get; private set; }
    public bool Saved { get; private set; }
    public AccountEditorWindow(AccessClient client, AccountEditorMode mode, AccessUser? target = null)
    {
        InitializeComponent(); _client = client; _mode = mode; _target = target;
        if (mode is AccountEditorMode.Edit or AccountEditorMode.ResetPassword && target is null) throw new ArgumentException("请选择账号。");
        Title = Heading.Text = mode switch { AccountEditorMode.Create => "创建账号", AccountEditorMode.Edit => "编辑账号", AccountEditorMode.ResetPassword => "重置账号密码", _ => "修改我的密码" };
        Description.Text = mode switch
        {
            AccountEditorMode.Create => "分配固定角色后，用户即可使用新账号登录。",
            AccountEditorMode.Edit => $"修改 {target!.Username} 的资料、角色或状态；保存后该账号需重新登录。",
            AccountEditorMode.ResetPassword => $"重置 {target!.Username} 的密码，旧会话将失效。",
            _ => "修改成功后返回登录页，后台任务继续运行。"
        };
        IdentityFields.Visibility = mode is AccountEditorMode.Create or AccountEditorMode.Edit ? Visibility.Visible : Visibility.Collapsed;
        NewPasswordFields.Visibility = mode == AccountEditorMode.Edit ? Visibility.Collapsed : Visibility.Visible;
        CurrentPasswordField.Visibility = mode == AccountEditorMode.ChangePassword ? Visibility.Visible : Visibility.Collapsed;
        Enabled.Visibility = mode == AccountEditorMode.Edit ? Visibility.Visible : Visibility.Collapsed;
        Username.Text = target?.Username ?? ""; Username.IsReadOnly = mode == AccountEditorMode.Edit;
        DisplayName.Text = target?.DisplayName ?? "";
        Role.SelectedIndex = target?.Role switch { AccessRoles.Administrator => 0, AccessRoles.Viewer => 2, _ => 1 };
        Enabled.IsChecked = target?.Enabled ?? true;
        Closing += (_, e) => { if (_busy) { e.Cancel = true; Status.Text = "正在保存，请等待服务返回后关闭。"; } };
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { if (!_busy) Close(); }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_mode != AccountEditorMode.Edit && (NewPassword.Password != ConfirmPassword.Password || NewPassword.Password.Length < 10))
        { Status.Text = "两次新密码须一致，且至少为10位。"; return; }
        _busy = true; Form.IsEnabled = SaveButton.IsEnabled = CancelButton.IsEnabled = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            switch (_mode)
            {
                case AccountEditorMode.Create:
                {
                    var createRole = ((ComboBoxItem)Role.SelectedItem).Tag.ToString()!;
                    await _client.CreateUserAsync(new(Username.Text.Trim(), DisplayName.Text.Trim(), createRole, NewPassword.Password), timeout.Token); break;
                }
                case AccountEditorMode.Edit:
                {
                    var editRole = ((ComboBoxItem)Role.SelectedItem).Tag.ToString()!;
                    await _client.UpdateUserAsync(_target!.Id, new(DisplayName.Text.Trim(), editRole, Enabled.IsChecked == true), timeout.Token); break;
                }
                case AccountEditorMode.ResetPassword:
                    await _client.ResetPasswordAsync(_target!.Id, NewPassword.Password, timeout.Token); break;
                case AccountEditorMode.ChangePassword:
                    await _client.ChangePasswordAsync(CurrentPassword.Password, NewPassword.Password, timeout.Token); break;
            }
            Saved = true;
            SessionInvalidated = _mode == AccountEditorMode.ChangePassword || (_target is not null && _target.Id == _client.CurrentUser?.Id);
            CurrentPassword.Clear(); NewPassword.Clear(); ConfirmPassword.Clear();
            _busy = false; DialogResult = true;
        }
        catch (AccessRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        { SessionInvalidated = true; _busy = false; DialogResult = false; }
        catch (OperationCanceledException) { Status.Text = "未在时限内取得确认，请先刷新核对结果，不要重复提交。"; }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { _busy = false; Form.IsEnabled = SaveButton.IsEnabled = CancelButton.IsEnabled = true; }
    }
}
