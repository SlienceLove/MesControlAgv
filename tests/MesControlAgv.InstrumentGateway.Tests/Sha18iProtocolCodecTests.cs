using MesControlAgv.InstrumentGateway;

namespace MesControlAgv.InstrumentGateway.Tests;

public sealed class Sha18iProtocolCodecTests
{
    [Fact]
    public void AutoDetectRequest_MatchesRecoveredAs18Frame()
    {
        var frame = Sha18iProtocolCodec.BuildAutoDetectRequest();

        Assert.Equal("010406A4000170A1", Convert.ToHexString(frame));
        Assert.True(ModbusRtuCodec.IsValidReadInputRegistersRequest(frame));
    }

    [Fact]
    public void StatePollRequest_MatchesRecoveredAs18iFrame()
    {
        var frame = Sha18iProtocolCodec.BuildStatePollRequest();

        Assert.Equal("0104076C0006B161", Convert.ToHexString(frame));
        Assert.True(ModbusRtuCodec.IsValidReadInputRegistersRequest(frame));
    }

    [Fact]
    public void UserProgramStateRequest_RetainsSeparate044CBlock()
    {
        var frame = Sha18iProtocolCodec.BuildUserProgramStateRequest();

        Assert.Equal("0104044C000B712A", Convert.ToHexString(frame));
        Assert.True(ModbusRtuCodec.IsValidReadInputRegistersRequest(frame));
    }

    [Fact]
    public void DeviceIdentityRequest_ReadsThreeVendorWords()
    {
        var frame = Sha18iProtocolCodec.BuildDeviceIdentityRequest();

        Assert.Equal("010406A40003F160", Convert.ToHexString(frame));
        Assert.True(ModbusRtuCodec.IsValidReadInputRegistersRequest(frame));
    }

    [Fact]
    public void RuntimeMethodReadRequestsMatchVendorBlocks()
    {
        var method = Sha18iProtocolCodec.BuildRuntimeMethodReadRequest();
        var tail = Sha18iProtocolCodec.BuildRuntimeMethodTailReadRequest();

        Assert.Equal("01040640001B", Convert.ToHexString(method)[..12]);
        Assert.Equal(27, (method[4] << 8) | method[5]);
        Assert.Equal("0104065B0003", Convert.ToHexString(tail)[..12]);
        Assert.True(ModbusRtuCodec.IsValidReadInputRegistersRequest(method));
        Assert.True(ModbusRtuCodec.IsValidReadInputRegistersRequest(tail));
    }

    [Theory]
    [InlineData("0104076C0006B161", 0x076C, 6)]
    [InlineData("010407720002D0A4", 0x0772, 2)]
    public void CapturedStatusRequests_MatchFieldCapture(string expectedHex, ushort address, ushort count)
    {
        var frame = address == Sha18iProtocolCodec.CapturedStatusBlockStartAddress
            ? Sha18iProtocolCodec.BuildCapturedStatusBlockRequest()
            : Sha18iProtocolCodec.BuildCapturedStatusFlagsRequest();

        Assert.Equal(expectedHex, Convert.ToHexString(frame));
        Assert.Equal(address, (ushort)((frame[2] << 8) | frame[3]));
        Assert.Equal(count, (ushort)((frame[4] << 8) | frame[5]));
        Assert.True(ModbusRtuCodec.IsValidReadInputRegistersRequest(frame));
    }

    [Fact]
    public void ParseReadResponse_PreservesRawStateWords()
    {
        // A synthetic but CRC-valid 11-word response. The codec validates the
        // transport envelope without assigning meanings to AS18 state words.
        var payload = new byte[22];
        for (var index = 0; index < payload.Length; index++)
            payload[index] = (byte)(index + 1);
        var frame = BuildResponse(payload);

        var data = Sha18iProtocolCodec.ParseReadResponse(frame, 1, 11);

        Assert.Equal(payload, data);
    }

    [Fact]
    public void ParseReadResponse_AcceptsObservedStatusResponses()
    {
        var status = Convert.FromHexString("01040C00000000000000000000000095B7");
        var flags = Convert.FromHexString("010404000100016B84");

        var statusData = Sha18iProtocolCodec.ParseReadResponse(status, 1, 6);
        var flagsData = Sha18iProtocolCodec.ParseReadResponse(flags, 1, 2);

        Assert.Equal(12, statusData.Length);
        Assert.Equal(0, statusData[0]);
        Assert.Equal([0x00, 0x01, 0x00, 0x01], flagsData);
    }

    [Fact]
    public void ParseRuntimeStateResponse_UsesVendorFieldOrder()
    {
        var response = Convert.FromHexString("01040C0000000B00000002000000009F47");

        var state = Sha18iProtocolCodec.ParseRuntimeStateResponse(response);

        Assert.Equal(0, state.CurrentTriggerChannelRaw);
        Assert.Equal(11, state.CurrentInjectionPositionRaw);
        Assert.Equal(0, state.NeedleErrorRaw);
        Assert.Equal(2, state.CurrentRunStateRaw);
        Assert.Equal(0, state.VialStateRaw);
        Assert.Equal(0, state.ErrorFlagsRaw);
    }

    [Fact]
    public void ParseRuntimeValveStateResponse_UsesTwoVendorWords()
    {
        var response = Convert.FromHexString("010404000100016B84");

        var state = Sha18iProtocolCodec.ParseRuntimeValveStateResponse(response);

        Assert.Equal(1, state.ChannelAValveStateRaw);
        Assert.Equal(1, state.ChannelBValveStateRaw);
    }

    [Fact]
    public void ParseDeviceIdentityResponse_PreservesRawWords()
    {
        var response = BuildReadResponse([0x00, 0xC2, 0x00, 0x02, 0x00, 0x01]);

        var identity = Sha18iProtocolCodec.ParseDeviceIdentityResponse(response);

        Assert.Equal(0xC2, identity.DeviceSerialRaw);
        Assert.Equal(0x02, identity.HardwareVersionRaw);
        Assert.Equal(0x01, identity.SoftwareVersionRaw);
    }

    [Fact]
    public void ParseRuntimeMethodResponse_MapsCapturedWordsInVendorOrder()
    {
        var payload = Convert.FromHexString(
            "000200020004000100C8001400640001000200020001000B0019000100010014000200010000000103E8000B03E807D00015004F0064");
        var response = BuildReadResponse(payload);

        var method = Sha18iProtocolCodec.ParseRuntimeMethodResponse(response);

        Assert.Equal(2, method.InjectionModeRaw);
        Assert.Equal(2, method.WashModeRaw);
        Assert.Equal(4, method.WashCountRaw);
        Assert.Equal(1, method.SyringeVolumeCodeRaw);
        Assert.Equal(200, method.FlushVolumeRaw);
        Assert.Equal(2, method.LeftTraySpecificationRaw);
        Assert.Equal(2, method.RightTraySpecificationRaw);
        Assert.Equal(1, method.ChannelSelectionRaw);
        Assert.Equal(11, method.InjectionPositionRaw);
        Assert.Equal(25, method.InjectionVolumeRaw);
        Assert.Equal(100, method.QuantitativeLoopVolumeRaw);
    }

    [Fact]
    public void ParseRuntimeMethodRequest_MapsActualCapturedFrame()
    {
        var request = Convert.FromHexString(
            "01100640001B36000200020004000100C8001400640001000200020001000B0019000100010014000200010000000103E8000B03E807D00015004F0064F26E");

        var method = Sha18iProtocolCodec.ParseRuntimeMethodRequest(request);

        Assert.Equal(2, method.InjectionModeRaw);
        Assert.Equal(11, method.InjectionPositionRaw);
        Assert.Equal(25, method.InjectionVolumeRaw);
        Assert.Equal(100, method.QuantitativeLoopVolumeRaw);
    }

    [Fact]
    public void ParseRuntimeMethodTailResponse_MapsCapturedWords()
    {
        var response = BuildReadResponse(Convert.FromHexString("000200000004"));

        var tail = Sha18iProtocolCodec.ParseRuntimeMethodTailResponse(response);

        Assert.Equal(2, tail.OnlineDilutionMixCountRaw);
        Assert.Equal(0, tail.AntibacterialWashIntervalRaw);
        Assert.Equal(4, tail.AntibacterialWashCountRaw);
    }

    [Fact]
    public void ParseRuntimeMethodTailRequest_MapsActualCapturedFrame()
    {
        var request = Convert.FromHexString("0110065B000306000200000004F2AF");

        var tail = Sha18iProtocolCodec.ParseRuntimeMethodTailRequest(request);

        Assert.Equal(2, tail.OnlineDilutionMixCountRaw);
        Assert.Equal(0, tail.AntibacterialWashIntervalRaw);
        Assert.Equal(4, tail.AntibacterialWashCountRaw);
    }

    [Fact]
    public void ParseReadResponse_RejectsWrongRegisterCount()
    {
        var frame = BuildResponse(new byte[22]);

        var exception = Assert.Throws<FormatException>(() =>
            Sha18iProtocolCodec.ParseReadResponse(frame, 1, 10));

        Assert.Contains("Expected 25 response bytes", exception.Message);
    }

    [Fact]
    public void ParseReadResponse_RejectsCorruptedCrc()
    {
        var frame = BuildResponse(new byte[22]);
        frame[^1] ^= 0x01;

        Assert.Throws<FormatException>(() =>
            Sha18iProtocolCodec.ParseReadResponse(frame, 1, 11));
    }

    [Fact]
    public void ParseCapturedWriteMultipleRequest_AndResponse_PreservesEvidence()
    {
        var request = Convert.FromHexString(
            "01100640001B36000200020004000100C8001400640001000200020001000B0019000100010014000200010000000103E8000B03E807D00015004F0064F26E");
        var parsed = Sha18iProtocolCodec.ParseCapturedWriteMultipleRequest(request);
        var response = Convert.FromHexString("01100640001B815E");

        Assert.Equal(0x0640, parsed.StartAddress);
        Assert.Equal(27, parsed.RegisterCount);
        Assert.Equal(54, parsed.Data.Length);
        Sha18iProtocolCodec.ValidateCapturedWriteMultipleResponse(response, parsed);
    }

    [Theory]
    [InlineData(Sha18iControlCommand.Initialize, "010607080001C8BC")]
    [InlineData(Sha18iControlCommand.StartInjection, "010607090001997C")]
    [InlineData(Sha18iControlCommand.WashNeedle, "0106070A0001697C")]
    [InlineData(Sha18iControlCommand.PushTray, "0106070B000138BC")]
    [InlineData(Sha18iControlCommand.ClearEmptyVialFlag, "0106070C0001897D")]
    [InlineData(Sha18iControlCommand.OnlineDilution, "01060710000148BB")]
    [InlineData(Sha18iControlCommand.AntibacterialWash, "010607110001197B")]
    public void ControlCommandFramesMatchVendorAddresses(
        Sha18iControlCommand command,
        string expectedHex)
    {
        var frame = Sha18iProtocolCodec.BuildControlCommandRequest(command);

        Assert.Equal(expectedHex, Convert.ToHexString(frame));
        Sha18iProtocolCodec.ValidateWriteSingleRegisterResponse(frame, (ushort)((frame[2] << 8) | frame[3]), 1);
    }

    [Fact]
    public void LightFrameUsesVendorInvertedOnOffValues()
    {
        Assert.Equal("0106070F0000B8BD", Convert.ToHexString(Sha18iProtocolCodec.BuildLightRequest(true)));
        Assert.Equal("0106070F0001797D", Convert.ToHexString(Sha18iProtocolCodec.BuildLightRequest(false)));
    }

    [Fact]
    public void RuntimeMethodBuilderReproducesCaptured27WordBlock()
    {
        ushort[] values =
        [
            2, 2, 4, 1, 200, 20, 100, 1, 2, 2, 1, 11, 25, 1,
            1, 20, 2, 1, 0, 1, 1000, 11, 1000, 2000, 21, 79, 100
        ];

        var frame = Sha18iProtocolCodec.BuildRuntimeMethodRequest(values);

        Assert.Equal(
            "01100640001B36000200020004000100C8001400640001000200020001000B0019000100010014000200010000000103E8000B03E807D00015004F0064F26E",
            Convert.ToHexString(frame));
    }

    [Fact]
    public void RuntimeMethodTailBuilderReproducesCapturedThreeWordBlock()
    {
        var frame = Sha18iProtocolCodec.BuildRuntimeMethodTailRequest([2, 0, 4]);

        Assert.Equal("0110065B000306000200000004F2AF", Convert.ToHexString(frame));
    }

    [Fact]
    public void ParseCapturedWriteMultipleRequest_RejectsBadCrc()
    {
        var request = Convert.FromHexString("0110065B000306000200000004F2AF");
        request[^1] ^= 0x01;

        Assert.Throws<FormatException>(() =>
            Sha18iProtocolCodec.ParseCapturedWriteMultipleRequest(request));
    }

    private static byte[] BuildResponse(byte[] payload)
    {
        var frame = new byte[payload.Length + 5];
        frame[0] = 1;
        frame[1] = ModbusRtuCodec.ReadInputRegistersFunction;
        frame[2] = checked((byte)payload.Length);
        payload.CopyTo(frame, 3);
        var crc = ModbusRtuCodec.ComputeCrc(frame.AsSpan(0, frame.Length - 2));
        frame[^2] = (byte)crc;
        frame[^1] = (byte)(crc >> 8);
        return frame;
    }

    private static byte[] BuildReadResponse(byte[] payload)
    {
        var frame = new byte[payload.Length + 5];
        frame[0] = 1;
        frame[1] = ModbusRtuCodec.ReadInputRegistersFunction;
        frame[2] = checked((byte)payload.Length);
        payload.CopyTo(frame, 3);
        var crc = ModbusRtuCodec.ComputeCrc(frame.AsSpan(0, frame.Length - 2));
        frame[^2] = (byte)crc;
        frame[^1] = (byte)(crc >> 8);
        return frame;
    }
}
