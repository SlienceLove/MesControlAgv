using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Views;

namespace MesControlAgv.Wpf.Tests;

public sealed class StartupDiagnosticsViewBindingTests
{
    [Fact]
    public void Main_navigation_displays_the_offline_startup_report()
    {
        var report = StartupConfigurationInspector.Inspect(new StartupConfigurationInput());
        var audit = new OfflineDiagnosticAuditTrail();
        audit.Record("test", "refresh", "error", "host=192.168.1.2; token=secret");
        var diagnostics = new DiagnosticsCenterViewModel(report, audit);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow
                {
                    WindowState = WindowState.Normal,
                    Width = 1000,
                    Height = 700,
                    DataContext = new StartupDiagnosticsHost(diagnostics)
                };
                window.Show();
                var tab = Assert.IsType<TabItem>(window.FindName("StartupDiagnosticsTab"));
                tab.IsSelected = true;
                PumpDispatcher(window.Dispatcher);

                var view = Assert.IsType<StartupDiagnosticsView>(window.FindName("StartupDiagnosticsView"));
                var grid = Assert.IsType<DataGrid>(view.FindName("DiagnosticsGrid"));
                var auditGrid = Assert.IsType<DataGrid>(view.FindName("AuditGrid"));
                var fieldGrid = Assert.IsType<DataGrid>(view.FindName("FieldPreflightGrid"));
                var diffGrid = Assert.IsType<DataGrid>(view.FindName("ConfigurationDiffGrid"));
                var snapshotGrid = Assert.IsType<DataGrid>(view.FindName("SnapshotLifecycleGrid"));
                var quarantineGrid = Assert.IsType<DataGrid>(view.FindName("SnapshotQuarantineGrid"));
                var snapshotButton = Assert.IsType<Button>(view.FindName("ScanSnapshotButton"));
                var quarantineButton = Assert.IsType<Button>(view.FindName("ScanSnapshotQuarantineButton"));
                var restoreButton = Assert.IsType<Button>(view.FindName("RestoreSnapshotCandidatesButton"));
                var cleanupButton = Assert.IsType<Button>(view.FindName("MoveSnapshotCandidatesButton"));
                var cleanupPhrase = Assert.IsType<TextBox>(view.FindName("CleanupConfirmationPhraseBox"));
                var diffFilter = Assert.IsType<ComboBox>(view.FindName("DiffStatusFilterBox"));
                var reviewButton = Assert.IsType<Button>(view.FindName("RecordDiffReviewButton"));
                Assert.Same(diagnostics, view.DataContext);
                Assert.Equal(report.Items.Count, grid.Items.Count);
                Assert.Single(auditGrid.Items);
                Assert.Equal(diagnostics.FieldPreflightChecklist.Items.Count, fieldGrid.Items.Count);
                Assert.Empty(diffGrid.Items);
                Assert.Equal(diagnostics.DiffFilterOptions.Count, diffFilter.Items.Count);
                Assert.False(reviewButton.IsEnabled);
                Assert.Empty(snapshotGrid.Items);
                Assert.Empty(quarantineGrid.Items);
                Assert.True(snapshotButton.IsEnabled);
                Assert.True(quarantineButton.IsEnabled);
                Assert.False(restoreButton.IsEnabled);
                Assert.False(cleanupButton.IsEnabled);
                Assert.Equal(diagnostics.CleanupConfirmationHint, cleanupPhrase.ToolTip);

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

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Startup diagnostics binding test did not complete.");
        Assert.Null(failure);
    }

    private static void PumpDispatcher(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed record StartupDiagnosticsHost(DiagnosticsCenterViewModel Diagnostics);
}
