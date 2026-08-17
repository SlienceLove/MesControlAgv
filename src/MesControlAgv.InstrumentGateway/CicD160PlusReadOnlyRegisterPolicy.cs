namespace MesControlAgv.InstrumentGateway;

public static class CicD160PlusReadOnlyRegisterPolicy
{
    private static readonly HashSet<(ushort StartAddress, ushort RegisterCount)> AllowedReads =
    [
        (0x1900, 20),
        (0x1770, 12),
        (0x17D4, 18)
    ];

    public static bool IsAllowed(ushort startAddress, ushort registerCount) =>
        AllowedReads.Contains((startAddress, registerCount));

    public static void EnsureAllowed(ushort startAddress, ushort registerCount)
    {
        if (!IsAllowed(startAddress, registerCount))
        {
            throw new InvalidOperationException(
                $"Read 0x{startAddress:X4}/{registerCount} is not in the captured D160+ read-only allowlist.");
        }
    }
}
