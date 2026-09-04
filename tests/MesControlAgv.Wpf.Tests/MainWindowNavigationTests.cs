using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace MesControlAgv.Wpf.Tests;

public sealed class MainWindowNavigationTests
{
    [Fact]
    public void Grouped_navigation_switches_existing_tab_content()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow
                {
                    WindowState = WindowState.Normal,
                    Width = 1200,
                    Height = 760
                };
                window.Show();
                window.UpdateLayout();

                var tabs = Assert.IsType<TabControl>(window.FindName("MainTabs"));
                var agvTab = Assert.IsType<TabItem>(window.FindName("AgvCommunicationTab"));
                var sidebar = Assert.IsType<Border>(window.FindName("NavigationSidebar"));
                var groupHeaders = FindVisualChildren<Expander>(sidebar)
                    .Select(expander => expander.Header?.ToString())
                    .Where(header => header is not null)
                    .ToArray();
                var agvButton = FindVisualChildren<RadioButton>(sidebar)
                    .Single(button => string.Equals(button.Tag as string, "AgvCommunicationTab", StringComparison.Ordinal));

                Assert.Equal(4, groupHeaders.Length);
                Assert.Contains("运营总览", groupHeaders);
                Assert.Contains("任务与设备", groupHeaders);
                Assert.Contains("实验工作流", groupHeaders);
                Assert.Contains("系统与诊断", groupHeaders);

                agvButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                Assert.Same(agvTab, tabs.SelectedItem);
                Assert.Equal("AGV 通讯与调度", ((TabItem)tabs.SelectedItem).Header);

                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF navigation test did not complete.");
        Assert.Null(failure);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
