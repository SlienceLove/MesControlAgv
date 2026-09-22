using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Views;

public partial class LoginWindow : Window
{
    private readonly AccessClient _access;
    public AccessLoginResponse? Result { get; private set; }
    public LoginWindow(AccessClient access, bool demo)
    {
        InitializeComponent(); _access = access; DemoBanner.Visibility = demo ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => Username.Focus();
    }
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (LoginButton.IsEnabled == false) return;
        LoginButton.IsEnabled = false; Hint.Text = "正在验证账号…"; Hint.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#667085"));
        try
        {
            Result = await _access.LoginAsync(Username.Text.Trim(), Password.Password, CancellationToken.None);
            DialogResult = true;
        }
        catch (Exception ex) { Hint.Text = ex.Message; Hint.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D92D20")); LoginButton.IsEnabled = true; Password.Clear(); Password.Focus(); }
    }
    private void Password_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Login_Click(sender, e); }
}
