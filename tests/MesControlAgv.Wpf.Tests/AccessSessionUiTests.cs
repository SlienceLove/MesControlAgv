using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.Views;

namespace MesControlAgv.Wpf.Tests;

public sealed class AccessSessionUiTests
{
    private static readonly AccessUser Viewer = new(Guid.NewGuid(), "viewer", "客户访客", AccessRoles.Viewer, true, null);
    [Fact]
    public void Viewer_can_access_own_account_controls_while_write_navigation_and_actions_are_disabled() => Sta(() =>
    {
        using var http = Http();
        var window = new MainWindow { Width = 1200, Height = 760, WindowState = WindowState.Normal };
        try
        {
            window.SetAccessSession(new AccessClient(http), Viewer, true);
            window.Show(); Pump();
            var password = Assert.IsType<Button>(window.FindName("AccessPasswordButton"));
            var logout = Assert.IsType<Button>(window.FindName("AccessLogoutButton"));
            Assert.True(password.IsEnabled); Assert.True(logout.IsEnabled);
            // Header actions must not pass through the page's read-only mouse guard.
            var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent };
            logout.RaiseEvent(click);
            Assert.False(click.Handled);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Button>(window.FindName("AccessManageButton")).Visibility);
            var nav = Descendants<RadioButton>(window).Single(b => Equals(b.Tag, "AgvCommunicationTab"));
            Assert.False(nav.IsEnabled);
            var tabs = Assert.IsType<TabControl>(window.FindName("MainTabs"));
            tabs.SelectedItem = window.FindName("MaterialManagementTab"); Pump();
            var page = Assert.IsType<Border>(window.FindName("PageFrame"));
            var writeButtons = Descendants<Button>(page).Where(b => b.Content is string label && !AccessUiPolicy.CanUseAction(Viewer.Role, label)).ToArray();
            Assert.NotEmpty(writeButtons);
            Assert.All(writeButtons, b => Assert.False(b.IsEnabled));
            Assert.All(Descendants<DataGrid>(page), g => Assert.True(g.IsReadOnly));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Reauthentication_is_coalesced_and_never_reuses_the_old_shell() => Sta(() =>
    {
        using var http = Http();
        var client = new AccessClient(http);
        client.SetSession(new("token", DateTimeOffset.UtcNow.AddHours(1), Viewer));
        var window = new MainWindow();
        try
        {
            var ended = 0; window.SessionEnded += (_, _) => ended++;
            window.SetAccessSession(client, Viewer);
            var method = typeof(MainWindow).GetMethod("Reauthenticate", BindingFlags.NonPublic | BindingFlags.Instance)!;
            method.Invoke(window, null); method.Invoke(window, null);
            Assert.Equal(1, ended); Assert.Null(client.CurrentUser);
            Assert.Null(http.DefaultRequestHeaders.Authorization); Assert.False(window.IsEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Non_demo_login_hides_demo_credentials() => Sta(() =>
    {
        using var http = Http();
        var window = new LoginWindow(new AccessClient(http), false);
        try
        {
            Assert.Equal(Visibility.Collapsed, Assert.IsType<StackPanel>(window.FindName("DemoCredentials")).Visibility);
            Assert.Empty(Assert.IsType<TextBox>(window.FindName("Username")).Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task Monitor_can_recover_and_then_observe_revocation_without_duplicate_callbacks()
    {
        var calls = 0; var updates = 0; var expired = 0;
        var monitor = new AccessSessionMonitor(_ => ++calls switch
        {
            1 => Task.FromException<AccessUser>(new HttpRequestException("offline")),
            2 => Task.FromResult(Viewer),
            _ => Task.FromException<AccessUser>(new AccessRequestException(HttpStatusCode.Unauthorized, "revoked"))
        }, _ => Task.CompletedTask);
        await monitor.RunAsync(_ => updates++, () => expired++, CancellationToken.None);
        Assert.Equal(3, calls); Assert.Equal(1, updates); Assert.Equal(1, expired);
    }

    private static HttpClient Http() => new(new NoNetwork()) { BaseAddress = new Uri("http://localhost/") };
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(new InvalidOperationException("No network permitted in window test."));
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); } catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15))); Assert.Null(failure);
    }
}
