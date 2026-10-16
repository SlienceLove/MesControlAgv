using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MesControlAgv.Contracts;

namespace MesControlAgv.Wpf.Views;

public partial class WorkstationTaskWindow
{
    private readonly ObservableCollection<WorkstationReviewRow> _reviewRows = [];
    private bool _reviewUncertain;
    internal Func<WorkstationBatchReviewItem, bool>? ConfirmBatchReview { get; set; }
    internal string ReviewReceiptDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MesControlAgv", "workstation-reconciliations");

    private async void LoadReviews_Click(object sender, RoutedEventArgs e) => await LoadReviewsAsync();
    internal async Task LoadReviewsAsync()
    {
        if (_busy) return;
        _busy = true; WorkspaceTabs.IsEnabled = false; UpdateReviewButton();
        try
        {
            var rows = await _client.GetWorkstationBatchReviewsAsync(_deviceId, CancellationToken.None);
            if (rows.Any(row => row.DeviceId != _deviceId || row.RequestId == Guid.Empty))
                throw new InvalidOperationException("核对列表身份不匹配。");
            _reviewRows.Clear();
            foreach (var row in rows) _reviewRows.Add(new(row));
            _reviewUncertain = false;
            Status.Text = rows.Count == 0 ? "暂无异常批次核对记录。" : "请选择一条记录，核对设备任务与明细后再人工确认。";
        }
        catch (Exception ex) { _reviewUncertain = true; Status.Text = "核对列表读取失败；禁止确认。" + ex.Message; }
        finally { _busy = false; WorkspaceTabs.IsEnabled = true; UpdateReviewButton(); }
    }

    private void ReviewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReviewCreated is null) return;
        ReviewCreated.IsChecked = false; ReviewTransfers.IsChecked = false; ReviewReason.Clear();
        var row = ReviewGrid.SelectedItem as WorkstationReviewRow;
        ReviewDetail.Text = row is null ? "" : $"原请求：{row.Item.RequestId}\n{row.Item.OriginalMessage}" +
            (row.Item.Reconciliation is { } audit ? $"\n人工确认：{audit.Confirmation.Actor}，{audit.ConfirmedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}；{audit.Confirmation.Reason}" : "");
        UpdateReviewButton();
    }

    private void ReviewInputChanged(object sender, TextChangedEventArgs e) => UpdateReviewButton();
    private void ReviewCheckChanged(object sender, RoutedEventArgs e) => UpdateReviewButton();
    private void UpdateReviewButton()
    {
        if (ConfirmReview is null) return;
        ConfirmReview.IsEnabled = !_busy && !_reviewUncertain &&
            ReviewGrid.SelectedItem is WorkstationReviewRow { Item.Reconciliation: null } &&
            ReviewCreated.IsChecked == true && ReviewTransfers.IsChecked == true &&
            !string.IsNullOrWhiteSpace(ReviewActor.Text) && !string.IsNullOrWhiteSpace(ReviewReason.Text);
    }

    private async void ConfirmReview_Click(object sender, RoutedEventArgs e) => await ConfirmReviewAsync();
    internal async Task ConfirmReviewAsync()
    {
        UpdateReviewButton();
        if (!ConfirmReview.IsEnabled || ReviewGrid.SelectedItem is not WorkstationReviewRow row) return;
        var item = row.Item;
        var request = new WorkstationBatchReconcileRequest(Guid.NewGuid(), item.TaskNo, item.PayloadHash, item.JournalSha256,
            ReviewActor.Text.Trim(), ReviewReason.Text.Trim(), true, true);
        var approved = ConfirmBatchReview?.Invoke(item) ?? MessageBox.Show(this,
            $"确认设备已有任务 {item.TaskNo}，且你已检查全部分液明细？\n将保留回读异常并记录人工确认，不重发、不启动设备。",
            "人工核对结案", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        if (!approved) return;
        _busy = true; _reviewUncertain = true; WorkspaceTabs.IsEnabled = false; UpdateReviewButton();
        try
        {
            Directory.CreateDirectory(ReviewReceiptDirectory);
            var receipt = Path.Combine(ReviewReceiptDirectory, request.ConfirmationId.ToString("N") + ".json");
            File.WriteAllText(receipt, JsonSerializer.Serialize(new { DeviceId = _deviceId, item.RequestId, Request = request }));
            var audit = await _client.ReconcileWorkstationBatchAsync(_deviceId, item.RequestId, request, CancellationToken.None);
            if (audit.DeviceId != _deviceId || audit.RequestId != item.RequestId || audit.TaskNo != item.TaskNo ||
                audit.Confirmation != request || audit.JournalSha256 != item.JournalSha256 || audit.PayloadHash != item.PayloadHash ||
                audit.Outcome != "CreatedWithReadbackException")
                throw new InvalidOperationException("确认回执身份不符。");
            File.WriteAllText(receipt, JsonSerializer.Serialize(new { DeviceId = _deviceId, item.RequestId, Request = request, Result = audit }));
            var index = _reviewRows.IndexOf(row);
            _reviewRows[index] = new(item with { Reconciliation = audit });
            ReviewGrid.SelectedIndex = index;
            _reviewUncertain = false;
            Status.Text = "已人工确认任务创建，原回读异常保留。若无其他异常条目，可另行准备新批次；本次未发送设备命令。";
        }
        catch (Exception ex)
        { Status.Text = $"确认操作号：{request.ConfirmationId}。结果未确认，请先刷新核对，不会自动重试。{ex.Message}"; }
        finally { _busy = false; WorkspaceTabs.IsEnabled = true; UpdateReviewButton(); }
    }
}

public sealed record WorkstationReviewRow(WorkstationBatchReviewItem Item)
{
    public string Label => Item.Reconciliation is null ? "待人工核对" : "已人工确认（回读异常保留）";
}
