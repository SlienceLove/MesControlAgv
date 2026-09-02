using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Views;

namespace MesControlAgv.Wpf.Tests;

public sealed class ViewLifecycleContractTests
{
    [Fact]
    public void Map_and_workflow_views_release_runtime_handles_when_unloaded()
    {
        using var viewModel = new MainViewModel(new FakeMesClient([]));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var mapView = new MapDashboardView { DataContext = viewModel };
                using (Show(mapView)) { }
                Assert.Null(GetPrivateField(mapView, "_mapAnimationTimer"));

                var workflowView = new WorkflowManagementView { DataContext = viewModel };
                using (Show(workflowView)) { }
                Assert.Null(GetPrivateField(workflowView, "_workflowEditor"));
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

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "View lifecycle test did not complete.");
        Assert.Null(failure);
    }

    [Fact]
    public void Recreating_operational_views_keeps_one_local_theme_dictionary_per_view()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var views = new (Func<FrameworkElement> Factory, bool HasTheme)[]
                {
                    (() => new TaskMonitorView(), true),
                    (() => new KpiDashboardView(), true),
                    (() => new MapDashboardView(), true),
                    (() => new WorkflowManagementView(), true),
                    (() => new ShineLabSequenceImportView(), false),
                    (() => new BatchTaskImportView(), false),
                    (() => new StartupDiagnosticsView(), false)
                };

                foreach (var (factory, hasTheme) in views)
                {
                    for (var iteration = 0; iteration < 3; iteration++)
                    {
                        var view = factory();
                        if (hasTheme)
                        {
                            Assert.Single(view.Resources.MergedDictionaries);
                            Assert.Equal("MesTheme.xaml", view.Resources.MergedDictionaries[0].Source?.OriginalString.Split('/').Last());
                        }
                        else
                        {
                            Assert.Empty(view.Resources.MergedDictionaries);
                            Assert.NotEmpty(view.Resources);
                        }
                    }
                }
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

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "View resource lifecycle test did not complete.");
        Assert.Null(failure);
    }

    private static IDisposable Show(FrameworkElement view)
    {
        var window = new Window
        {
            Width = 1000,
            Height = 700,
            Content = view,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };
        window.Show();
        PumpDispatcher(window.Dispatcher);
        window.Close();
        PumpDispatcher(window.Dispatcher);
        return new DelegateDisposable(() => { });
    }

    private static object? GetPrivateField(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance);

    private static void PumpDispatcher(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class DelegateDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
