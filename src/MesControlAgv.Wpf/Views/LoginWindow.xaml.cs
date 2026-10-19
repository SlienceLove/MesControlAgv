using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Views;

public partial class LoginWindow : Window
{
    private readonly AccessClient _access;
    private bool _closed;
    private readonly CancellationTokenSource _lifetime = new();
    public AccessLoginResponse? Result { get; private set; }
    public LoginWindow(AccessClient access, bool demo)
    {
        InitializeComponent(); _access = access; DemoBanner.Visibility = DemoCredentials.Visibility = demo ? Visibility.Visible : Visibility.Collapsed;
        if (!demo) Username.Clear();
        Loaded += (_, _) => Username.Focus();
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); };
    }
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (LoginButton.IsEnabled == false) return;
        LoginButton.IsEnabled = false; Hint.Text = "正在验证账号…"; Hint.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#667085"));
        try
        {
            Result = await _access.LoginAsync(Username.Text.Trim(), Password.Password, _lifetime.Token);
            if (_closed) return;
            Password.Clear();
            DialogResult = true;
        }
        catch (Exception ex) { if (_closed) return; Hint.Text = ex.Message; Hint.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D92D20")); LoginButton.IsEnabled = true; Password.Clear(); Password.Focus(); }
    }
    private void Password_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Login_Click(sender, e); }
}
