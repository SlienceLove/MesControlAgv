using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Devices;

namespace MesControlAgv.Adapter.Modules.SampleWorkstation;

/// <summary>
/// Durable single-write journal for the workstation's mutating HTTP calls.
/// The reservation is flushed before the vendor request is sent.  A process
/// restart therefore cannot accidentally replay a command whose outcome was
/// not proven.  The journal contains only identifiers and bounded summaries;
/// it is not a raw vendor packet archive.
/// </summary>
public sealed class SampleWorkstationOperationJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _sync = new();
    private readonly string _path;
    private Dictionary<Guid, SampleWorkstationJournalEntry> _entries;

    public SampleWorkstationOperationJournal(SampleWorkstationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _path = Path.GetFullPath(string.IsNullOrWhiteSpace(options.OperationJournalPath)
            ? "data/sample-workstation-operations.json"
            : options.OperationJournalPath);
        _entries = Load(_path);
    }

    public bool TryGet(Guid operationId, out SampleWorkstationJournalEntry entry)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(operationId, out entry!);
        }
    }

    public bool TryReserve(SampleWorkstationJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.OperationId == Guid.Empty) throw new ArgumentException("OperationId is required.", nameof(entry));
        lock (_sync)
        {
            if (_entries.ContainsKey(entry.OperationId)) return false;
            _entries[entry.OperationId] = entry with { JournalStatus = SampleWorkstationJournalStatus.Reserved };
            PersistLocked();
            return true;
        }
    }

    public void Complete(Guid operationId, SampleWorkstationJournalEntry completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        lock (_sync)
        {
            if (!_entries.ContainsKey(operationId))
                throw new InvalidOperationException($"Workstation operation '{operationId:D}' was not reserved.");
            _entries[operationId] = completion with { OperationId = operationId };
            PersistLocked();
        }
    }

    private void PersistLocked()
    {
        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Workstation operation journal path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(_entries.Values.OrderBy(item => item.OperationId), JsonOptions));
        File.Move(temporary, _path, true);
    }

    private static Dictionary<Guid, SampleWorkstationJournalEntry> Load(string path)
    {
        if (!File.Exists(path)) return [];
        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json)) return [];
        var values = JsonSerializer.Deserialize<List<SampleWorkstationJournalEntry>>(json, JsonOptions)
                     ?? throw new InvalidDataException("The workstation operation journal is empty or malformed.");
        return values
            .Where(item => item.OperationId != Guid.Empty)
            .GroupBy(item => item.OperationId)
            .ToDictionary(group => group.Key, group => group.Last());
    }
}

public enum SampleWorkstationJournalStatus
{
    Reserved,
    Completed,
    Failed,
    Unknown
}

public sealed record SampleWorkstationJournalEntry
{
    public Guid OperationId { get; init; }
    public Guid RunId { get; init; }
    public Guid NodeExecutionId { get; init; }
    public string DeviceId { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public string? VendorTaskId { get; init; }
    public DeviceOperationLifecycle Status { get; init; }
    public SampleWorkstationJournalStatus JournalStatus { get; init; }
    public UnknownReason? UnknownReason { get; init; }
    public string? VendorMessage { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}
