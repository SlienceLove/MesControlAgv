using MesControlAgv.InstrumentGateway;

namespace MesControlAgv.InstrumentGateway.Tests;

public sealed class ModbusRtuCodecTests
{
    private const string CapturedIdentityResponse =
        "0104285941373236313037380000000000000000004E2000004E2000000000000000000000000200000000CB25";

    [Fact]
    public void BuildReadInputRegisters_ReproducesKnownD160Request()
    {
        var request = ModbusRtuCodec.BuildReadInputRegisters(1, 0x1900, 20);

        Assert.Equal("010419000014F759", Convert.ToHexString(request));
        Assert.True(ModbusRtuCodec.IsValidReadInputRegistersRequest(request));
    }

    [Fact]
    public void ParseReadInputRegistersResponse_AcceptsCapturedIdentityFrame()
    {
        var frame = Convert.FromHexString(CapturedIdentityResponse);

        var data = ModbusRtuCodec.ParseReadInputRegistersResponse(frame, 1, 20);

        Assert.Equal(40, data.Length);
        Assert.Equal("YA7261078", System.Text.Encoding.ASCII.GetString(data, 0, 9));
    }

    [Fact]
    public void ParseReadInputRegistersResponse_RejectsBadCrc()
    {
        var frame = Convert.FromHexString(CapturedIdentityResponse);
        frame[^1] ^= 0x01;

        var exception = Assert.Throws<FormatException>(() =>
            ModbusRtuCodec.ParseReadInputRegistersResponse(frame, 1, 20));

        Assert.Contains("CRC mismatch", exception.Message);
    }

    [Fact]
    public void ParseReadInputRegistersResponse_RejectsNonReadFunction()
    {
        var frame = AppendCrc([0x01, 0x06, 0x02, 0x00, 0x01]);

        var exception = Assert.Throws<FormatException>(() =>
            ModbusRtuCodec.ParseReadInputRegistersResponse(frame, 1, 1));

        Assert.Contains("Expected function 0x04", exception.Message);
    }

    [Fact]
    public void RequestValidator_RejectsCapturedWriteFrame()
    {
        var capturedWrite = Convert.FromHexString("010613E45AA537A2");

        Assert.False(ModbusRtuCodec.IsValidReadInputRegistersRequest(capturedWrite));
    }

    [Theory]
    [InlineData(0x1900, 20, true)]
    [InlineData(0x1770, 12, true)]
    [InlineData(0x17D4, 18, true)]
    [InlineData(0x1838, 10, true)]
    [InlineData(0x13E4, 1, false)]
    [InlineData(0x1900, 24, true)]
    public void RegisterPolicy_AllowsOnlyGatewayReadRanges(int startAddress, int count, bool expected)
    {
        Assert.Equal(expected, CicD160PlusReadOnlyRegisterPolicy.IsAllowed((ushort)startAddress, (ushort)count));
    }

    private static byte[] AppendCrc(byte[] payload)
    {
        var frame = new byte[payload.Length + 2];
        payload.CopyTo(frame, 0);
        var crc = ModbusRtuCodec.ComputeCrc(payload);
        frame[^2] = (byte)crc;
        frame[^1] = (byte)(crc >> 8);
        return frame;
    }
}
