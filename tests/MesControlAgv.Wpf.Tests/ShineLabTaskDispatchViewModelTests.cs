using MesControlAgv.Contracts;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class ShineLabTaskDispatchViewModelTests
{
    [Fact]
    public async Task Sends_config_with_sample_and_method_fields_through_mes()
    {
        var mes = new FakeMesClient([]);
        var viewModel = new ShineLabTaskDispatchViewModel(mes)
        {
            EquipmentCode = "SHA18I",
            TaskUuid = "task-001",
            SampleId = "S-01",
            SampleName = "标准样",
            Position = 11,
            Channel = "A",
            InstrumentMethod = "AS18-METHOD-01",
            ProcessingMethod = "IC-PROCESS-01",
            DetectionMethod = "Normal",
            InjectionVolume = "25"
        };

        await viewModel.SendConfigAsync();

        Assert.NotNull(mes.LastShineLabTaskCreate);
        var sample = Assert.Single(mes.LastShineLabTaskCreate!.SampleData);
        Assert.Equal("S-01", sample.SampleId);
        Assert.Equal(11, sample.Position);
        Assert.Equal("A", sample.Channel);
        Assert.Equal("AS18-METHOD-01", sample.InstrumentMethod);
        Assert.Equal("IC-PROCESS-01", sample.ProcessingMethod);
        Assert.Equal(25, sample.InjectionVolume);
        Assert.Equal("Configured", viewModel.LastTask?.Status);
        Assert.Contains("任务已持久化", viewModel.Message);
    }

    [Fact]
    public async Task Sends_command_and_surfaces_rejection_without_retrying()
    {
        var mes = new FakeMesClient([])
        {
            LastShineLabTaskResult = new ShineLabTaskResponse(
                Guid.NewGuid(), "task-002", "SHA18I", "Failed", "CommandRejected",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, "设备忙")
        };
        var viewModel = new ShineLabTaskDispatchViewModel(mes)
        {
            TaskUuid = "task-002",
            Action = "1"
        };

        await viewModel.SendCommandAsync();

        Assert.NotNull(mes.LastShineLabCommand);
        Assert.Equal(1, mes.LastShineLabCommand!.Action);
        Assert.Equal("Failed", viewModel.LastTask?.Status);
        Assert.Contains("Command 结果", viewModel.Message);
    }
}
