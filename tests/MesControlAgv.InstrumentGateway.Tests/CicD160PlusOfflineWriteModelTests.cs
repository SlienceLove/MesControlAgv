using MesControlAgv.InstrumentGateway;

namespace MesControlAgv.InstrumentGateway.Tests;

public sealed class CicD160PlusOfflineWriteModelTests
{
    [Fact]
    public void DefaultPolicy_RejectsCapturedCommand()
    {
        var policy = new CicD160PlusOfflineWritePolicy();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CicD160PlusOfflineWriteCodec.BuildFrame(new CicD160PlusSetPumpEnabled(false), policy));

        Assert.Contains("disabled", exception.Message);
    }

    [Theory]
    [MemberData(nameof(CapturedFrames))]
    public void ExactAllowlist_ReproducesCapturedFrame(
        CicD160PlusWriteCommand command,
        CicD160PlusWriteOperation operation,
        ushort rawValue,
        string expectedHex)
    {
        var policy = AllowExactly(operation, rawValue);

        var frame = CicD160PlusOfflineWriteCodec.BuildFrame(command, policy);

        Assert.Equal(operation, frame.Operation);
        Assert.Equal(rawValue, frame.RawValue);
        Assert.Equal(expectedHex, Convert.ToHexString(frame.Bytes.Span));
        CicD160PlusOfflineWriteCodec.ValidateEcho(frame.Bytes.Span, frame);
    }

    [Fact]
    public void ExactAllowlist_RejectsUnobservedFlowValue()
    {
        var policy = AllowExactly(CicD160PlusWriteOperation.SetPumpFlow, 700);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CicD160PlusOfflineWriteCodec.BuildFrame(new CicD160PlusSetPumpFlow(0.300m), policy));

        Assert.Contains("raw value 300", exception.Message);
    }

    [Fact]
    public void ExactAllowlist_CanPermitDisableWithoutPermittingEnable()
    {
        var policy = AllowExactly(CicD160PlusWriteOperation.DisablePump, 0);

        var disabled = CicD160PlusOfflineWriteCodec.BuildFrame(new CicD160PlusSetPumpEnabled(false), policy);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CicD160PlusOfflineWriteCodec.BuildFrame(new CicD160PlusSetPumpEnabled(true), policy));

        Assert.Equal("0106157D00001DDE", Convert.ToHexString(disabled.Bytes.Span));
        Assert.Contains(nameof(CicD160PlusWriteOperation.EnablePump), exception.Message);
    }

    [Theory]
    [InlineData("0.7001")]
    [InlineData("65.536")]
    public void FlowEncoding_RejectsValuesThatDoNotFitExactScale(string value)
    {
        var policy = AllowExactly(CicD160PlusWriteOperation.SetPumpFlow, 700);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CicD160PlusOfflineWriteCodec.BuildFrame(
                new CicD160PlusSetPumpFlow(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)),
                policy));
    }

    [Fact]
    public void EchoValidation_RejectsMismatchedEcho()
    {
        var expected = CicD160PlusOfflineWriteCodec.BuildFrame(
            new CicD160PlusSetPumpEnabled(false),
            AllowExactly(CicD160PlusWriteOperation.DisablePump, 0));
        var differentEcho = Convert.FromHexString("0106157D0001DC1E");

        var exception = Assert.Throws<FormatException>(() =>
            CicD160PlusOfflineWriteCodec.ValidateEcho(differentEcho, expected));

        Assert.Contains("did not exactly echo", exception.Message);
    }

    [Fact]
    public void EchoValidation_RejectsBadCrc()
    {
        var expected = CicD160PlusOfflineWriteCodec.BuildFrame(
            new CicD160PlusSetPumpEnabled(false),
            AllowExactly(CicD160PlusWriteOperation.DisablePump, 0));
        var corruptedEcho = expected.Bytes.ToArray();
        corruptedEcho[^1] ^= 0x01;

        var exception = Assert.Throws<FormatException>(() =>
            CicD160PlusOfflineWriteCodec.ValidateEcho(corruptedEcho, expected));

        Assert.Contains("CRC mismatch", exception.Message);
    }

    [Fact]
    public void EchoValidation_ReportsCrcValidModbusException()
    {
        var expected = CicD160PlusOfflineWriteCodec.BuildFrame(
            new CicD160PlusSetPumpEnabled(false),
            AllowExactly(CicD160PlusWriteOperation.DisablePump, 0));
        var response = AppendCrc([0x01, 0x86, 0x02]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CicD160PlusOfflineWriteCodec.ValidateEcho(response, expected));

        Assert.Contains("exception 0x02", exception.Message);
    }

    [Fact]
    public void Policy_RejectsDuplicateOrEmptyAllowances()
    {
        Assert.Throws<ArgumentException>(() => new CicD160PlusOfflineWritePolicy(true,
        [
            new(CicD160PlusWriteOperation.DisablePump, [0]),
            new(CicD160PlusWriteOperation.DisablePump, [0])
        ]));
        Assert.Throws<ArgumentException>(() => new CicD160PlusOfflineWritePolicy(true,
        [
            new(CicD160PlusWriteOperation.DisablePump, [])
        ]));
    }

    public static TheoryData<CicD160PlusWriteCommand, CicD160PlusWriteOperation, ushort, string> CapturedFrames => new()
    {
        { new CicD160PlusSetPumpFlow(0.700m), CicD160PlusWriteOperation.SetPumpFlow, 700, "010613DA02BCAC64" },
        { new CicD160PlusSetPumpEnabled(true), CicD160PlusWriteOperation.EnablePump, 1, "0106157D0001DC1E" },
        { new CicD160PlusSetPumpEnabled(false), CicD160PlusWriteOperation.DisablePump, 0, "0106157D00001DDE" },
        { new CicD160PlusSetColumnTemperatureControlEnabled(true), CicD160PlusWriteOperation.EnableColumnTemperatureControl, 2, "0106157C0002CDDF" },
        { new CicD160PlusSetColumnTemperatureControlEnabled(false), CicD160PlusWriteOperation.DisableColumnTemperatureControl, 0, "0106157C00004C1E" },
        { new CicD160PlusSetColumnTemperature(35.00m), CicD160PlusWriteOperation.SetColumnTemperature, 3500, "010613890DAC5849" }
    };

    private static CicD160PlusOfflineWritePolicy AllowExactly(
        CicD160PlusWriteOperation operation,
        ushort rawValue) => new(true,
        [
            new(operation, [rawValue])
        ]);

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
