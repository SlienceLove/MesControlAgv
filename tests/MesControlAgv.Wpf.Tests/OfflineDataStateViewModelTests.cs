using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Converters;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MesControlAgv.Wpf.Tests;

public sealed class OfflineDataStateViewModelTests
{
    [Fact]
    public void State_transitions_expose_consistent_text_and_retry_capability()
    {
        var state = new OfflineDataStateViewModel();

        Assert.Equal(OfflineDataStateKind.NotLoaded, state.Kind);
        Assert.Equal("未加载", state.StateText);
        Assert.True(state.CanRetry);

        state.BeginLoading("正在读取...");
        Assert.True(state.IsBusy);
        Assert.False(state.CanRetry);
        Assert.Equal("刷新中", state.StateText);

        state.MarkReady(hasData: false, "MES 已连接，但暂无记录。");
        Assert.Equal(OfflineDataStateKind.Empty, state.Kind);
        Assert.Equal("暂无数据", state.StateText);
        Assert.Equal("MES 已连接，但暂无记录。", state.Message);
        Assert.True(state.CanRetry);

        state.MarkStale("数据已过期，请重试。");
        Assert.True(state.IsStale);
        Assert.Equal(OfflineDataStateKind.Stale, state.Kind);

        state.MarkError("MES 不可用。");
        Assert.True(state.IsError);
        Assert.Equal("刷新失败", state.StateText);
        Assert.True(state.CanRetry);
    }

    [Fact]
    public void Ready_state_preserves_data_marker_when_a_later_refresh_is_cancelled()
    {
        var state = new OfflineDataStateViewModel();
        state.MarkReady(hasData: true, "已更新");

        state.MarkCancelled();

        Assert.Equal(OfflineDataStateKind.Cancelled, state.Kind);
        Assert.True(state.HasData);
        Assert.True(state.CanRetry);
    }

    [Theory]
    [InlineData("刷新中", "#1677FF")]
    [InlineData("暂无数据", "#F79009")]
    [InlineData("数据过期", "#F79009")]
    [InlineData("刷新失败", "#F04438")]
    [InlineData("已更新", "#12B76A")]
    public void Status_brush_converter_uses_the_same_palette_for_offline_states(string text, string expectedHex)
    {
        var brush = Assert.IsType<SolidColorBrush>(
            new StatusBrushConverter().Convert(text, typeof(SolidColorBrush), null, CultureInfo.InvariantCulture));

        Assert.Equal((Color)ColorConverter.ConvertFromString(expectedHex), brush.Color);
    }
}
