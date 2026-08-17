using System.Buffers.Binary;

namespace MesControlAgv.InstrumentGateway;

public static class ModbusRtuCodec
{
    public const byte ReadInputRegistersFunction = 0x04;

    public static byte[] BuildReadInputRegisters(byte slaveAddress, ushort startAddress, ushort registerCount)
    {
        if (slaveAddress == 0) throw new ArgumentOutOfRangeException(nameof(slaveAddress));
        if (registerCount is 0 or > 125) throw new ArgumentOutOfRangeException(nameof(registerCount));

        var frame = new byte[8];
        frame[0] = slaveAddress;
        frame[1] = ReadInputRegistersFunction;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), registerCount);
        var crc = ComputeCrc(frame.AsSpan(0, 6));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6, 2), crc);
        return frame;
    }

    public static byte[] ParseReadInputRegistersResponse(
        ReadOnlySpan<byte> frame,
        byte expectedSlaveAddress,
        ushort expectedRegisterCount)
    {
        var expectedDataLength = checked(expectedRegisterCount * 2);
        var expectedFrameLength = expectedDataLength + 5;
        if (frame.Length != expectedFrameLength)
            throw new FormatException($"Expected {expectedFrameLength} response bytes, received {frame.Length}.");
        if (frame[0] != expectedSlaveAddress)
            throw new FormatException($"Expected slave 0x{expectedSlaveAddress:X2}, received 0x{frame[0]:X2}.");
        if (frame[1] != ReadInputRegistersFunction)
            throw new FormatException($"Expected function 0x04, received 0x{frame[1]:X2}.");
        if (frame[2] != expectedDataLength)
            throw new FormatException($"Expected {expectedDataLength} data bytes, received {frame[2]}.");

        var suppliedCrc = BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..]);
        var computedCrc = ComputeCrc(frame[..^2]);
        if (suppliedCrc != computedCrc)
            throw new FormatException($"CRC mismatch: expected 0x{computedCrc:X4}, received 0x{suppliedCrc:X4}.");

        return frame.Slice(3, expectedDataLength).ToArray();
    }

    public static bool IsValidReadInputRegistersRequest(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != 8 || frame[0] == 0 || frame[1] != ReadInputRegistersFunction) return false;
        var count = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(4, 2));
        if (count is 0 or > 125) return false;
        var suppliedCrc = BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..]);
        return suppliedCrc == ComputeCrc(frame[..^2]);
    }

    public static ushort ComputeCrc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }
}
