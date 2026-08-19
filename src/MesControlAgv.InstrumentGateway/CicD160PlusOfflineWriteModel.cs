using System.Buffers.Binary;

namespace MesControlAgv.InstrumentGateway;

public enum CicD160PlusWriteOperation
{
    SetPumpFlow,
    EnablePump,
    DisablePump,
    EnableColumnTemperatureControl,
    DisableColumnTemperatureControl,
    SetColumnTemperature
}

public abstract record CicD160PlusWriteCommand;

public sealed record CicD160PlusSetPumpFlow(decimal MillilitersPerMinute)
    : CicD160PlusWriteCommand;

public sealed record CicD160PlusSetPumpEnabled(bool Enabled)
    : CicD160PlusWriteCommand;

public sealed record CicD160PlusSetColumnTemperatureControlEnabled(bool Enabled)
    : CicD160PlusWriteCommand;

public sealed record CicD160PlusSetColumnTemperature(decimal Celsius)
    : CicD160PlusWriteCommand;

public sealed record CicD160PlusWriteAllowance(
    CicD160PlusWriteOperation Operation,
    IReadOnlyCollection<ushort> AllowedRawValues);

public sealed class CicD160PlusOfflineWritePolicy
{
    private readonly IReadOnlyDictionary<CicD160PlusWriteOperation, HashSet<ushort>> _allowedRawValues;

    public CicD160PlusOfflineWritePolicy(
        bool enabled = false,
        IEnumerable<CicD160PlusWriteAllowance>? allowances = null)
    {
        Enabled = enabled;
        var values = new Dictionary<CicD160PlusWriteOperation, HashSet<ushort>>();
        foreach (var allowance in allowances ?? [])
        {
            ArgumentNullException.ThrowIfNull(allowance.AllowedRawValues);
            if (allowance.AllowedRawValues.Count == 0)
                throw new ArgumentException("Every write allowance must contain at least one exact raw value.", nameof(allowances));
            if (!values.TryAdd(allowance.Operation, allowance.AllowedRawValues.ToHashSet()))
                throw new ArgumentException($"Duplicate allowance for {allowance.Operation}.", nameof(allowances));
        }
        _allowedRawValues = values;
    }

    public bool Enabled { get; }

    public void EnsureAllowed(CicD160PlusWriteOperation operation, ushort rawValue)
    {
        if (!Enabled)
            throw new InvalidOperationException("The offline D160+ write policy is disabled.");
        if (!_allowedRawValues.TryGetValue(operation, out var allowedValues) || !allowedValues.Contains(rawValue))
        {
            throw new InvalidOperationException(
                $"D160+ operation {operation} with raw value {rawValue} is not in the exact-value write allowlist.");
        }
    }
}

public sealed class CicD160PlusWriteFrame
{
    private readonly byte[] _bytes;

    internal CicD160PlusWriteFrame(
        CicD160PlusWriteOperation operation,
        ushort register,
        ushort rawValue,
        byte[] bytes)
    {
        Operation = operation;
        Register = register;
        RawValue = rawValue;
        _bytes = bytes.ToArray();
    }

    public CicD160PlusWriteOperation Operation { get; }
    public ushort Register { get; }
    public ushort RawValue { get; }
    public ReadOnlyMemory<byte> Bytes => _bytes;
}

public static class CicD160PlusOfflineWriteCodec
{
    private const byte WriteSingleRegisterFunction = 0x06;
    private const ushort PumpFlowRegister = 0x13DA;
    private const ushort ColumnTemperatureRegister = 0x1389;
    private const ushort TemperatureControlRegister = 0x157C;
    private const ushort PumpEnabledRegister = 0x157D;

    public static CicD160PlusWriteFrame BuildFrame(
        CicD160PlusWriteCommand command,
        CicD160PlusOfflineWritePolicy policy,
        byte slaveAddress = 1)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(policy);
        if (slaveAddress == 0) throw new ArgumentOutOfRangeException(nameof(slaveAddress));

        var instruction = command switch
        {
            CicD160PlusSetPumpFlow flow => new WriteInstruction(
                CicD160PlusWriteOperation.SetPumpFlow,
                PumpFlowRegister,
                ScaleExact(flow.MillilitersPerMinute, 1000m, nameof(flow.MillilitersPerMinute))),
            CicD160PlusSetPumpEnabled { Enabled: true } => new WriteInstruction(
                CicD160PlusWriteOperation.EnablePump,
                PumpEnabledRegister,
                1),
            CicD160PlusSetPumpEnabled => new WriteInstruction(
                CicD160PlusWriteOperation.DisablePump,
                PumpEnabledRegister,
                0),
            CicD160PlusSetColumnTemperatureControlEnabled { Enabled: true } => new WriteInstruction(
                CicD160PlusWriteOperation.EnableColumnTemperatureControl,
                TemperatureControlRegister,
                2),
            CicD160PlusSetColumnTemperatureControlEnabled => new WriteInstruction(
                CicD160PlusWriteOperation.DisableColumnTemperatureControl,
                TemperatureControlRegister,
                0),
            CicD160PlusSetColumnTemperature temperature => new WriteInstruction(
                CicD160PlusWriteOperation.SetColumnTemperature,
                ColumnTemperatureRegister,
                ScaleExact(temperature.Celsius, 100m, nameof(temperature.Celsius))),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command.GetType().Name, "Unsupported D160+ command type.")
        };

        policy.EnsureAllowed(instruction.Operation, instruction.RawValue);
        var frame = new byte[8];
        frame[0] = slaveAddress;
        frame[1] = WriteSingleRegisterFunction;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), instruction.Register);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), instruction.RawValue);
        var crc = ModbusRtuCodec.ComputeCrc(frame.AsSpan(0, 6));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6, 2), crc);
        return new(instruction.Operation, instruction.Register, instruction.RawValue, frame);
    }

    public static void ValidateEcho(ReadOnlySpan<byte> response, CicD160PlusWriteFrame expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (response.Length == 5 && response[1] == (WriteSingleRegisterFunction | 0x80))
        {
            EnsureValidCrc(response);
            if (response[0] != expected.Bytes.Span[0])
                throw new FormatException($"Expected slave 0x{expected.Bytes.Span[0]:X2}, received 0x{response[0]:X2}.");
            throw new InvalidOperationException($"D160+ returned Modbus write exception 0x{response[2]:X2}.");
        }
        if (response.Length != expected.Bytes.Length)
            throw new FormatException($"Expected an 8-byte D160+ write echo, received {response.Length} bytes.");

        EnsureValidCrc(response);
        if (!response.SequenceEqual(expected.Bytes.Span))
            throw new FormatException("D160+ write response did not exactly echo the expected request.");
    }

    private static void EnsureValidCrc(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4) throw new FormatException("D160+ Modbus frame is too short.");
        var suppliedCrc = BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..]);
        var computedCrc = ModbusRtuCodec.ComputeCrc(frame[..^2]);
        if (suppliedCrc != computedCrc)
            throw new FormatException($"CRC mismatch: expected 0x{computedCrc:X4}, received 0x{suppliedCrc:X4}.");
    }

    private static ushort ScaleExact(decimal value, decimal scale, string parameterName)
    {
        if (value < 0 || value > ushort.MaxValue / scale)
            throw new ArgumentOutOfRangeException(parameterName);
        var scaled = value * scale;
        if (scaled != decimal.Truncate(scaled) || scaled > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Value must map exactly to an unsigned 16-bit raw value at scale {scale}.");
        }
        return (ushort)scaled;
    }

    private sealed record WriteInstruction(
        CicD160PlusWriteOperation Operation,
        ushort Register,
        ushort RawValue);
}
