using System.Threading;
using System.Windows.Controls;
using MesControlAgv.Wpf.Views;

namespace MesControlAgv.Wpf.Tests;

public sealed class DetectionStandardsViewTests
{
    [Fact]
    public void Material_page_contains_standard_tab_and_sample_assessment_dialog_loads()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var page = new MaterialManagementView();
                var tabs = Assert.IsType<TabControl>(page.FindName("MaterialTabs"));
                Assert.Contains(tabs.Items.OfType<TabItem>(), item =>
                    (string?)item.Header == "检测标准" && item.Content is DetectionStandardsView);
                var dialog = new DetectionAssessmentDialog();
                Assert.Equal("样品检测判定", dialog.Title);
                dialog.Close();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "检测标准界面加载超时。");
        if (failure is not null) throw failure;
    }
}
