using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Experiments;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MesControlAgv.Wpf.Tests;

public sealed partial class ExperimentWorkstationPreparationViewModelTests
{
    [Theory]
    [InlineData(1380, 780)]
    [InlineData(1024, 768)]
    public async Task Bulk_editor_binds_real_controls_and_scrolls_without_hiding_timeline(int width, int height)
    {
        var client = new PreparationClient { Template = SixteenHoleTemplate() };
        var vm = CreateViewModel(client);
        var f = Fixture();
        await vm.LoadAsync(f.Job, f.Schedule, f.Workflow, f.Verification, f.Samples, CancellationToken.None);
        await vm.BeginTemplateModeAsync();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                var view = new ExperimentSchedulingView { DataContext = new { WorkstationPreparation = vm } };
                window = new Window { Content = view, Width = width, Height = height, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
                window.Show();
                var panel = Assert.IsType<Expander>(view.FindName("WorkstationPreparationPanel"));
                panel.IsExpanded = true;
                var timeline = Assert.IsType<ScrollViewer>(view.FindName("ResourceTimeline"));
                Pump();
                var timelineHeight = timeline.ActualHeight;
                var input = Assert.IsType<TextBox>(view.FindName("WorkstationUniformVolumeInput"));
                var button = Assert.IsType<Button>(view.FindName("ApplyWorkstationUniformVolumeButton"));
                input.Text = "75";
                Pump();
                Assert.Equal("75", vm.UniformVolumeText);
                Assert.Equal(16000L, vm.TotalVolumeMicroliters);
                Assert.True(button.IsEnabled);
                button.Command.Execute(null);
                Pump();
                Assert.Contains("计划总量 1200 µL", Assert.IsType<TextBlock>(view.FindName("WorkstationVolumeSummaryText")).Text);
                Assert.Equal(2, Assert.IsType<ItemsControl>(view.FindName("WorkstationSourceVolumeSummaries")).Items.Count);
                Assert.Contains("未保存修改", Assert.IsType<TextBlock>(view.FindName("WorkstationEditStatusText")).Text);
                input.Text = "bad";
                Pump();
                Assert.False(button.IsEnabled);
                Assert.Equal(1200L, vm.TotalVolumeMicroliters);
                var scroll = Assert.IsType<ScrollViewer>(view.FindName("SchedulingActionsScrollViewer"));
                input.BringIntoView();
                Pump();
                var applyBounds = button.TransformToAncestor(scroll).TransformBounds(new Rect(new Point(), button.RenderSize));
                Assert.InRange(applyBounds.Left, 0, scroll.ActualWidth);
                Assert.InRange(applyBounds.Right, 0, scroll.ActualWidth + 1);
                scroll.ScrollToEnd();
                Pump();
                var save = Assert.IsType<Button>(view.FindName("SaveWorkstationPreparationButton"));
                var saveBounds = save.TransformToAncestor(scroll).TransformBounds(new Rect(new Point(), save.RenderSize));
                Assert.True(saveBounds.Top >= 0 && saveBounds.Bottom <= scroll.ActualHeight + 1);
                Assert.True(timeline.IsVisible && timeline.ActualHeight > 0);
                Assert.Equal(timelineHeight, timeline.ActualHeight);
                Assert.Equal(0, client.PrepareCalls + client.ImportCalls + client.StartOrAdmitCalls);
            }
            catch (Exception exception) { failure = exception; }
            finally { window?.Close(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }

            void Pump()
            {
                window?.UpdateLayout();
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Offline bulk editor layout test timed out.");
        Assert.Null(failure);
    }

    [Fact]
    public async Task Uniform_volume_updates_all_sixteen_rows_and_plan_totals_without_any_write()
    {
        var client = new PreparationClient { Template = SixteenHoleTemplate() };
        var vm = CreateViewModel(client);
        var f = Fixture();
        Assert.False(vm.ApplyUniformVolumeCommand.CanExecute(null));
        await vm.LoadAsync(f.Job, f.Schedule, f.Workflow, f.Verification, f.Samples, CancellationToken.None);
        await vm.BeginTemplateModeAsync();
        Assert.Equal(2, vm.SourceCount);
        Assert.Equal(16, vm.TargetCount);
        Assert.Equal(16000L, vm.TotalVolumeMicroliters);
        Assert.True(vm.HasUnsavedChanges);
        Assert.Contains("尚未保存", vm.EditStatusText);
        var before = vm.Transfers.Select(row => row.ToContract()).ToArray();
        var command = vm.ApplyUniformVolumeCommand;
        vm.UniformVolumeText = "50";
        Assert.False(vm.IsDirty); // Typing does not edit the table.
        command.Execute(null);
        Assert.Same(command, vm.ApplyUniformVolumeCommand);
        Assert.Equal(before.Select(row => row with { VolumeMicroliters = 50 }), vm.Transfers.Select(row => row.ToContract()));
        Assert.Equal(800L, vm.TotalVolumeMicroliters);
        Assert.All(vm.SourceVolumeSummaries, summary => { Assert.Equal(8, summary.TargetCount); Assert.Equal(400L, summary.VolumeMicroliters); });
        Assert.Contains("计划总量 800 µL", vm.VolumeSummary);
        Assert.True(vm.IsDirty);
        Assert.False(vm.CanImport);
        vm.BottleBindings[0].SelectedSample = f.Samples[0];
        vm.BottleBindings[0].SelectedSource = vm.SourceKeys[0];
        Assert.Contains("1号瓶", vm.SourceVolumeSummaries[0].Display);
        Assert.Contains("S-1 / BC-1", vm.SourceVolumeSummaries[0].Display);
        Assert.Equal(1, client.TemplateReads);
        Assert.Equal(0, client.PrepareCalls + client.ImportCalls + client.StartOrAdmitCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("abc")]
    [InlineData("2147483648")]
    public async Task Invalid_uniform_input_never_reuses_previous_valid_value(string invalid)
    {
        var client = new PreparationClient();
        var vm = CreateViewModel(client);
        var f = Fixture();
        await vm.LoadAsync(f.Job, f.Schedule, f.Workflow, f.Verification, f.Samples, CancellationToken.None);
        await vm.BeginTemplateModeAsync();
        var notifications = 0;
        vm.ApplyUniformVolumeCommand.CanExecuteChanged += (_, _) => notifications++;
        vm.UniformVolumeText = "25";
        vm.UniformVolumeText = invalid;
        Assert.False(vm.CanApplyUniformVolume);
        Assert.NotEmpty(vm.UniformVolumeValidationMessage);
        vm.ApplyUniformVolumeCommand.Execute(null); // Public command also rechecks.
        Assert.Equal(10, Assert.Single(vm.Transfers).VolumeMicroliters);
        Assert.False(vm.IsDirty);
        Assert.True(notifications >= 2);
        Assert.Equal(0, client.PrepareCalls + client.ImportCalls + client.StartOrAdmitCalls);
    }

    [Fact]
    public async Task Row_edits_refresh_distinct_holes_and_long_totals_and_flag_invalid_volume()
    {
        var client = new PreparationClient { Template = SixteenHoleTemplate() };
        var vm = CreateViewModel(client);
        var f = Fixture();
        await vm.LoadAsync(f.Job, f.Schedule, f.Workflow, f.Verification, f.Samples, CancellationToken.None);
        await vm.BeginTemplateModeAsync();
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        vm.UniformVolumeText = int.MaxValue.ToString();
        vm.ApplyUniformVolumeCommand.Execute(null);
        Assert.Equal(16L * int.MaxValue, vm.TotalVolumeMicroliters);
        vm.Transfers[1].TargetY = vm.Transfers[0].TargetY;
        Assert.Equal(15, vm.TargetCount);
        Assert.Equal(7, vm.SourceVolumeSummaries[0].TargetCount);
        Assert.Contains("分液 16 条", vm.VolumeSummary);
        vm.Transfers[0].VolumeMicroliters = 0;
        Assert.Null(vm.TotalVolumeMicroliters);
        Assert.Null(vm.SourceVolumeSummaries[0].VolumeMicroliters);
        Assert.Contains("体积待修正", vm.VolumeSummary);
        vm.Transfers[0].VolumeMicroliters = 1;
        Assert.Equal(15L * int.MaxValue + 1, vm.TotalVolumeMicroliters);
        Assert.Contains(nameof(vm.SourceVolumeSummaries), notifications);
        Assert.Contains(nameof(vm.VolumeSummary), notifications);
    }

    [Fact]
    public async Task Noop_preserves_saved_state_and_changed_volume_requires_explicit_save_before_import()
    {
        var client = new PreparationClient();
        var vm = CreateViewModel(client);
        var f = Fixture();
        var prepared = Prepared(ExperimentWorkstationPreparationStatus.Prepared, f.Job, f.Verification);
        client.CurrentByJob[f.Job.JobId] = prepared;
        await vm.LoadAsync(f.Job, f.Schedule, f.Workflow, f.Verification, f.Samples, CancellationToken.None);
        Assert.False(vm.HasUnsavedChanges);
        vm.UniformVolumeText = "10";
        vm.ApplyUniformVolumeCommand.Execute(null);
        Assert.False(vm.IsDirty);
        Assert.True(vm.CanImport);
        vm.UniformVolumeText = "50";
        vm.ApplyUniformVolumeCommand.Execute(null);
        Assert.True(vm.HasUnsavedChanges);
        Assert.Contains("未保存修改", vm.EditStatusText);
        Assert.False(vm.CanImport);
        await vm.ImportAsync();
        Assert.Equal(0, client.ImportCalls);
        var pending = client.HoldNextPrepare();
        var save = vm.SaveAsync();
        await pending.Requested.Task;
        Assert.False(vm.CanApplyUniformVolume);
        vm.UniformVolumeText = "100";
        vm.ApplyUniformVolumeCommand.Execute(null);
        Assert.Equal(50, Assert.Single(vm.Transfers).VolumeMicroliters);
        Assert.Equal(50, Assert.Single(client.LastPrepare!.Transfers!).VolumeMicroliters);
        pending.Gate.SetResult(prepared with { Revision = 4, Payload = prepared.Payload with
        {
            Transfers = prepared.Payload.Transfers.Select(row => row with { Transfer = row.Transfer with { VolumeMicroliters = 50 } }).ToArray()
        } });
        await save;
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(50L, vm.TotalVolumeMicroliters);
        Assert.True(vm.CanImport);
        Assert.Equal(0, client.ImportCalls + client.StartOrAdmitCalls);
    }

    [Theory]
    [InlineData(ExperimentWorkstationPreparationStatus.Unknown, ExperimentJobStatus.Scheduled)]
    [InlineData(ExperimentWorkstationPreparationStatus.Importing, ExperimentJobStatus.Scheduled)]
    [InlineData(ExperimentWorkstationPreparationStatus.Imported, ExperimentJobStatus.Completed)]
    public async Task Readonly_or_unresolved_preparation_keeps_persisted_summary_and_blocks_bulk_edit(ExperimentWorkstationPreparationStatus status, ExperimentJobStatus jobStatus)
    {
        var client = new PreparationClient();
        var vm = CreateViewModel(client);
        var f = Fixture();
        client.CurrentByJob[f.Job.JobId] = Prepared(status, f.Job, f.Verification, sourceBusinessId: "S-FROZEN", sourceBarcode: "BC-FROZEN");
        await vm.LoadAsync(f.Job with { Status = jobStatus }, f.Schedule, f.Workflow, f.Verification, [], CancellationToken.None);
        Assert.Contains("S-FROZEN / BC-FROZEN", Assert.Single(vm.SourceVolumeSummaries).Display);
        Assert.Equal(1, vm.SourceCount); // Unused second slot is not a source.
        Assert.False(vm.CanApplyUniformVolume);
        vm.ApplyUniformVolumeCommand.Execute(null);
        Assert.Equal(10L, vm.TotalVolumeMicroliters);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Switching_tasks_clears_previous_rows_summary_and_uniform_input()
    {
        var client = new PreparationClient { Template = SixteenHoleTemplate() };
        var vm = CreateViewModel(client);
        var f = Fixture();
        await vm.LoadAsync(f.Job, f.Schedule, f.Workflow, f.Verification, f.Samples, CancellationToken.None);
        await vm.BeginTemplateModeAsync();
        vm.UniformVolumeText = "75";
        vm.ApplyUniformVolumeCommand.Execute(null);
        await vm.LoadAsync(null, null, null, null, [], CancellationToken.None);
        Assert.Empty(vm.SourceVolumeSummaries);
        Assert.Equal(0, vm.SourceCount);
        Assert.Equal(0, vm.TargetCount);
        Assert.Equal(0L, vm.TotalVolumeMicroliters);
        Assert.Equal("50", vm.UniformVolumeText);
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.CanApplyUniformVolume);
    }

    private static SampleWorkstationTaskTemplate SixteenHoleTemplate() => new("TASK", "two bottles, 16 holes",
        Enumerable.Range(1, 2).SelectMany(bottle => Enumerable.Range(0, 8).Select(hole =>
            new SampleWorkstationTransferRow("CGRJ-001", "QT-001", 0, 0, $"CYC-00{bottle}-1000", 1, 1,
                $"FYB-00{bottle}", hole / 4 + 1, hole % 4 + 1, 1000))).ToArray());
}
