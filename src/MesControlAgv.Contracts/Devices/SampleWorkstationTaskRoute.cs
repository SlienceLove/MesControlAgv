namespace MesControlAgv.Contracts;

/// <summary>Decodes the opaque task segment of /tasks/{taskNo}/{command} exactly once.</summary>
public static class SampleWorkstationTaskRoute
{
    public static string ReadCommandTaskNo(string? rawTarget, string boundTaskNo)
    {
        if (string.IsNullOrEmpty(rawTarget))
        {
            // Some in-memory/proxy hosts omit RawTarget. A plain route value is
            // unambiguous; an encoded slash cannot safely be decoded a second time.
            if (boundTaskNo.Contains("%2F", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("An encoded task number requires the original request target.");
            return boundTaskNo;
        }
        // ASP.NET leaves %2F encoded in bound route values. Decode the original segment,
        // not the already partially decoded value, to distinguish '/' from literal '%2F'.
        var query = rawTarget.IndexOf('?');
        var path = (query < 0 ? rawTarget : rawTarget[..query]).TrimEnd('/');
        var commandSeparator = path.LastIndexOf('/');
        if (commandSeparator <= 0) throw new ArgumentException("Missing workstation command path.");
        var taskSeparator = path.LastIndexOf('/', commandSeparator - 1);
        if (taskSeparator < 0 || taskSeparator == commandSeparator - 1)
            throw new ArgumentException("Missing workstation task number.");
        return Uri.UnescapeDataString(path[(taskSeparator + 1)..commandSeparator]);
    }
}
