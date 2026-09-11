using System.Globalization;
using System.Text.Json;
using MesControlAgv.Contracts;

namespace MesControlAgv.Wpf.Services;

/// <summary>
/// One row from the direct ShineLab Config contract. This model is separate
/// from the legacy CSV/RPA task model so adding direct TCP fields cannot alter
/// the existing append-import path.
/// </summary>
public sealed record ShineLabDirectSampleTask(
    int SourceRowNumber,
    string SampleId,
    string SampleName,
    string SampleType,
    int SampleTypeCode,
    string SampleLevel,
    int Position,
    string? MPos,
    string Channel,
    string InstrumentMethod,
    string ProcessingMethod,
    string? DetectionMethod,
    decimal InjectionVolume,
    string InjectionVolumeUnit,
    int CycleCount);

public sealed record ShineLabDirectSampleTaskIssue(int SourceRowNumber, string Message);

public sealed class ShineLabDirectSequenceResult
{
    public ShineLabDirectSequenceResult(
        IReadOnlyList<ShineLabDirectSampleTask> tasks,
        IReadOnlyList<ShineLabDirectSampleTaskIssue> issues,
        bool hasDirectColumns)
    {
        Tasks = tasks;
        Issues = issues;
        HasDirectColumns = hasDirectColumns;
    }

    public IReadOnlyList<ShineLabDirectSampleTask> Tasks { get; }
    public IReadOnlyList<ShineLabDirectSampleTaskIssue> Issues { get; }
    public bool HasDirectColumns { get; }
    public bool CanBuildPreview => HasDirectColumns && Issues.Count == 0 && Tasks.Count > 0;
}

public static class ShineLabDirectProtocolPreview
{
    public static string Build(
        IReadOnlyList<ShineLabDirectSampleTask> tasks,
        string equipmentCode,
        string taskUuid,
        string configStrId,
        string commandStrId,
        int commandAction = 0)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentException.ThrowIfNullOrWhiteSpace(equipmentCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskUuid);
        ArgumentException.ThrowIfNullOrWhiteSpace(configStrId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandStrId);
        if (tasks.Count == 0) throw new ArgumentException("At least one direct task is required.", nameof(tasks));

        var first = tasks[0];
        var configRequest = new ShineLabConfigRequest(
            taskUuid,
            tasks.Select(task => new ShineLabSampleData(
                task.SampleId,
                task.SampleName,
                task.SampleTypeCode.ToString(CultureInfo.InvariantCulture),
                task.Position,
                task.MPos,
                task.Channel,
                task.InstrumentMethod,
                task.ProcessingMethod,
                task.DetectionMethod,
                task.InjectionVolume,
                task.InjectionVolumeUnit)).ToArray(),
            first.InstrumentMethod,
            first.ProcessingMethod,
            first.DetectionMethod);
        var config = new
        {
            strID = configStrId,
            strMethod = "Config",
            equipmentCode,
            body = ShineLabConfigPayloadBuilder.Build(configRequest)
        };

        var commandRequest = new ShineLabCommandRequest(
            taskUuid,
            commandAction,
            SampleId: first.SampleId,
            SampleName: first.SampleName,
            Channel: first.Channel,
            DetectionMethod: first.DetectionMethod);
        var command = new
        {
            strID = commandStrId,
            strMethod = "Command",
            equipmentCode,
            body = ShineLabCommandPayloadBuilder.Build(commandRequest)
        };

        return JsonSerializer.Serialize(new { config, command }, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }
}
