using MesControlAgv.Contracts;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class IonChromatographyViewModelTests
{
    [Fact]
    public async Task Refresh_displays_candidate_values_and_keeps_task_admission_disabled()
    {
        var client = new FakeMesClient([])
        {
            IonChromatographyStatus = new IonChromatographyControlCenterStatusResponse(
                new IonChromatographyStatusResponse(
                    "CIC-D160-01",
                    "CIC-D160+",
                    "YA7261078",
                    true,
                    "ReadOnlyObserved",
                    false,
                    DateTimeOffset.Parse("2026-08-17T07:40:59Z"),
                    ColumnTemperature: 31.23,
                    Conductivity: 261.885712,
                    TotalConductivity: 261.885712,
                    Flow: 0.3,
                    MappingConfidence: "CaptureCorrelatedCandidate"),
                false,
                ["Identify", "ReadStatus"],
                "ReadOnlyCaptureCorrelated")
        };
        var viewModel = new IonChromatographyViewModel(client);

        await viewModel.RefreshAsync();

        Assert.Equal("在线", viewModel.ConnectionStatus);
        Assert.Equal("YA7261078", viewModel.SerialNumber);
        Assert.Equal("261.88571 uS/cm", viewModel.Conductivity);
        Assert.Equal("31.23 C", viewModel.ColumnTemperature);
        Assert.Equal("0.300 mL/min", viewModel.Flow);
        Assert.Equal("禁用", viewModel.TaskAdmissionStatus);
        Assert.Equal("Identify / ReadStatus", viewModel.EnabledOperations);
        Assert.Equal("CaptureCorrelatedCandidate", viewModel.MappingConfidence);
        Assert.Equal("已释放", viewModel.PortStatus);
    }

    [Fact]
    public async Task Missing_status_is_shown_as_unavailable_without_enabling_actions()
    {
        var viewModel = new IonChromatographyViewModel(new FakeMesClient([]));

        await viewModel.RefreshAsync();

        Assert.Equal("不可用", viewModel.ConnectionStatus);
        Assert.Equal("禁用", viewModel.TaskAdmissionStatus);
        Assert.Equal("无", viewModel.EnabledOperations);
    }
}
