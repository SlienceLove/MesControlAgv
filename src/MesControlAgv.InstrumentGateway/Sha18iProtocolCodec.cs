namespace MesControlAgv.InstrumentGateway;

/// <summary>
/// Offline SHA-18i/AS18 Modbus-RTU frame codec.
///
/// The register meanings in this type come from the vendor's
/// <c>18i双通道自动进样器通讯协议.xlsx</c> and were cross-checked against
/// the 2026-08-27 COM3 capture.  Frame builders only return byte arrays; this
/// class never opens a serial port and never sends a frame to equipment.
/// </summary>
public static class Sha18iProtocolCodec
{
    public const byte DefaultSlaveAddress = 1;

    // Device identification (the vendor table calls 0x06A4 the device
    // serial/model word; 0xC2 denotes 18i).
    public const ushort AutoDetectStartAddress = 0x06A4;
    public const ushort AutoDetectRegisterCount = 1;

    public const ushort DeviceIdentityStartAddress = AutoDetectStartAddress;
    public const ushort DeviceIdentityRegisterCount = 3;

    // Normal ShineLab runtime method blocks.
    public const ushort RuntimeMethodStartAddress = 0x0640;
    public const ushort RuntimeMethodRegisterCount = 27;
    public const ushort RuntimeMethodTailStartAddress = 0x065B;
    public const ushort RuntimeMethodTailRegisterCount = 3;

    // Normal ShineLab runtime state is split into two input-register reads.
    public const ushort RuntimeStateStartAddress = 0x076C;
    public const ushort RuntimeStateRegisterCount = 6;
    public const ushort RuntimeValveStateStartAddress = 0x0772;
    public const ushort RuntimeValveStateRegisterCount = 2;

    // 0x044C/11 is not the normal runtime state block.  The vendor's
    // "用户程序" sheet assigns it to user-program command completion state.
    public const ushort UserProgramStateStartAddress = 0x044C;
    public const ushort UserProgramStateRegisterCount = 11;

    // Runtime single-register control commands.  There is no separately
    // documented "stop" register in the vendor table; 0x0708 is labelled
    // initialization, so callers must not rename it to Stop without a live
    // state/firmware verification.
    public const ushort InitializeCommandAddress = 0x0708;
    public const ushort InjectionCommandAddress = 0x0709;
    public const ushort WashCommandAddress = 0x070A;
    public const ushort PushTrayCommandAddress = 0x070B;
    public const ushort ClearEmptyVialFlagCommandAddress = 0x070C;
    public const ushort LightCommandAddress = 0x070F;
    public const ushort OnlineDilutionCommandAddress = 0x0710;
    public const ushort AntibacterialWashCommandAddress = 0x0711;

    // Backward-compatible names used by the first offline capture tests.
    public const ushort StatePollStartAddress = RuntimeStateStartAddress;
    public const ushort StatePollRegisterCount = RuntimeStateRegisterCount;
    public const ushort CapturedStatusBlockStartAddress = RuntimeStateStartAddress;
    public const ushort CapturedStatusBlockRegisterCount = RuntimeStateRegisterCount;
    public const ushort CapturedStatusFlagsStartAddress = RuntimeValveStateStartAddress;
    public const ushort CapturedStatusFlagsRegisterCount = RuntimeValveStateRegisterCount;

    public static byte[] BuildAutoDetectRequest(byte slaveAddress = DefaultSlaveAddress) =>
        ModbusRtuCodec.BuildReadInputRegisters(
            slaveAddress,
            AutoDetectStartAddress,
            AutoDetectRegisterCount);

    public static byte[] BuildDeviceIdentityRequest(byte slaveAddress = DefaultSlaveAddress) =>
        ModbusRtuCodec.BuildReadInputRegisters(
            slaveAddress,
            DeviceIdentityStartAddress,
            DeviceIdentityRegisterCount);

    /// <summary>Builds the normal six-word runtime state read.</summary>
    public static byte[] BuildRuntimeStateRequest(byte slaveAddress = DefaultSlaveAddress) =>
        ModbusRtuCodec.BuildReadInputRegisters(
            slaveAddress,
            RuntimeStateStartAddress,
            RuntimeStateRegisterCount);

    /// <summary>Builds the normal two-word A/B valve state read.</summary>
    public static byte[] BuildRuntimeValveStateRequest(byte slaveAddress = DefaultSlaveAddress) =>
        ModbusRtuCodec.BuildReadInputRegisters(
            slaveAddress,
            RuntimeValveStateStartAddress,
            RuntimeValveStateRegisterCount);

    /// <summary>
    /// Builds the user-program completion-state read from the fourth vendor
    /// worksheet.  This is intentionally separate from normal runtime state.
    /// </summary>
    public static byte[] BuildUserProgramStateRequest(byte slaveAddress = DefaultSlaveAddress) =>
        ModbusRtuCodec.BuildReadInputRegisters(
            slaveAddress,
            UserProgramStateStartAddress,
            UserProgramStateRegisterCount);

    public static byte[] BuildRuntimeMethodReadRequest(byte slaveAddress = DefaultSlaveAddress) =>
        ModbusRtuCodec.BuildReadInputRegisters(
            slaveAddress,
            RuntimeMethodStartAddress,
            RuntimeMethodRegisterCount);

    public static byte[] BuildRuntimeMethodTailReadRequest(byte slaveAddress = DefaultSlaveAddress) =>
        ModbusRtuCodec.BuildReadInputRegisters(
            slaveAddress,
            RuntimeMethodTailStartAddress,
            RuntimeMethodTailRegisterCount);

    public static byte[] BuildStatePollRequest(byte slaveAddress = DefaultSlaveAddress) =>
        BuildRuntimeStateRequest(slaveAddress);

    public static byte[] BuildCapturedStatusBlockRequest(byte slaveAddress = DefaultSlaveAddress) =>
        ModbusRtuCodec.BuildReadInputRegisters(
            slaveAddress,
            CapturedStatusBlockStartAddress,
            CapturedStatusBlockRegisterCount);

    public static byte[] BuildCapturedStatusFlagsRequest(byte slaveAddress = DefaultSlaveAddress) =>
        BuildRuntimeValveStateRequest(slaveAddress);

    /// <summary>
    /// Parses the vendor-defined six-word runtime state response.  The values
    /// remain unsigned raw words so callers can apply an explicit firmware
    /// policy before taking action.
    /// </summary>
    public static Sha18iRuntimeState ParseRuntimeStateResponse(
        ReadOnlySpan<byte> frame,
        byte expectedSlaveAddress = DefaultSlaveAddress)
    {
        var data = ParseReadResponse(frame, expectedSlaveAddress, RuntimeStateRegisterCount);
        return new(
            ReadWord(data, 0),
            ReadWord(data, 1),
            ReadWord(data, 2),
            ReadWord(data, 3),
            ReadWord(data, 4),
            ReadWord(data, 5));
    }

    public static Sha18iRuntimeValveState ParseRuntimeValveStateResponse(
        ReadOnlySpan<byte> frame,
        byte expectedSlaveAddress = DefaultSlaveAddress)
    {
        var data = ParseReadResponse(frame, expectedSlaveAddress, RuntimeValveStateRegisterCount);
        return new(ReadWord(data, 0), ReadWord(data, 1));
    }

    public static Sha18iDeviceIdentity ParseDeviceIdentityResponse(
        ReadOnlySpan<byte> frame,
        byte expectedSlaveAddress = DefaultSlaveAddress)
    {
        var data = ParseReadResponse(frame, expectedSlaveAddress, DeviceIdentityRegisterCount);
        return new(ReadWord(data, 0), ReadWord(data, 1), ReadWord(data, 2));
    }

    /// <summary>
    /// Decodes the 27-word normal-runtime method block in the exact order of
    /// the vendor worksheet (0x0640..0x065A).
    /// </summary>
    public static Sha18iRuntimeMethod ParseRuntimeMethodResponse(
        ReadOnlySpan<byte> frame,
        byte expectedSlaveAddress = DefaultSlaveAddress)
    {
        var data = ParseReadResponse(frame, expectedSlaveAddress, RuntimeMethodRegisterCount);
        return DecodeRuntimeMethodData(data);
    }

    /// <summary>
    /// Decodes a captured function 0x10 method request.  This is useful for
    /// offline PCAP analysis and does not replay the request.
    /// </summary>
    public static Sha18iRuntimeMethod ParseRuntimeMethodRequest(
        ReadOnlySpan<byte> frame,
        byte expectedSlaveAddress = DefaultSlaveAddress)
    {
        var request = ParseCapturedWriteMultipleRequest(frame, expectedSlaveAddress);
        if (request.StartAddress != RuntimeMethodStartAddress ||
            request.RegisterCount != RuntimeMethodRegisterCount)
        {
            throw new FormatException(
                $"Expected SHA-18i runtime method block 0x{RuntimeMethodStartAddress:X4}/{RuntimeMethodRegisterCount}, " +
                $"received 0x{request.StartAddress:X4}/{request.RegisterCount}.");
        }
        return DecodeRuntimeMethodData(request.Data);
    }

    private static Sha18iRuntimeMethod DecodeRuntimeMethodData(ReadOnlySpan<byte> data) => new(
            ReadWord(data, 0),
            ReadWord(data, 1),
            ReadWord(data, 2),
            ReadWord(data, 3),
            ReadWord(data, 4),
            ReadWord(data, 5),
            ReadWord(data, 6),
            ReadWord(data, 7),
            ReadWord(data, 8),
            ReadWord(data, 9),
            ReadWord(data, 10),
            ReadWord(data, 11),
            ReadWord(data, 12),
            ReadWord(data, 13),
            ReadWord(data, 14),
            ReadWord(data, 15),
            ReadWord(data, 16),
            ReadWord(data, 17),
            ReadWord(data, 18),
            ReadWord(data, 19),
            ReadWord(data, 20),
            ReadWord(data, 21),
            ReadWord(data, 22),
            ReadWord(data, 23),
            ReadWord(data, 24),
            ReadWord(data, 25),
            ReadWord(data, 26));

    /// <summary>Decodes the three-word 0x065B..0x065D method tail.</summary>
    public static Sha18iRuntimeMethodTail ParseRuntimeMethodTailResponse(
        ReadOnlySpan<byte> frame,
        byte expectedSlaveAddress = DefaultSlaveAddress)
    {
        var data = ParseReadResponse(frame, expectedSlaveAddress, RuntimeMethodTailRegisterCount);
        return DecodeRuntimeMethodTailData(data);
    }

    public static Sha18iRuntimeMethodTail ParseRuntimeMethodTailRequest(
        ReadOnlySpan<byte> frame,
        byte expectedSlaveAddress = DefaultSlaveAddress)
    {
        var request = ParseCapturedWriteMultipleRequest(frame, expectedSlaveAddress);
        if (request.StartAddress != RuntimeMethodTailStartAddress ||
            request.RegisterCount != RuntimeMethodTailRegisterCount)
        {
            throw new FormatException(
                $"Expected SHA-18i runtime method tail 0x{RuntimeMethodTailStartAddress:X4}/{RuntimeMethodTailRegisterCount}, " +
                $"received 0x{request.StartAddress:X4}/{request.RegisterCount}.");
        }
        return DecodeRuntimeMethodTailData(request.Data);
    }

    private static Sha18iRuntimeMethodTail DecodeRuntimeMethodTailData(ReadOnlySpan<byte> data) =>
        new(ReadWord(data, 0), ReadWord(data, 1), ReadWord(data, 2));

    /// <summary>
    /// Builds an offline Modbus function 0x06 frame.  It does not send the
    /// frame and is deliberately kept in this codec rather than a transport.
    /// </summary>
    public static byte[] BuildWriteSingleRegisterRequest(
        ushort registerAddress,
        ushort value,
        byte slaveAddress = DefaultSlaveAddress)
    {
        if (slaveAddress == 0) throw new ArgumentOutOfRangeException(nameof(slaveAddress));

        var frame = new byte[8]
        {
            slaveAddress,
            0x06,
            (byte)(registerAddress >> 8),
            (byte)registerAddress,
            (byte)(value >> 8),
            (byte)value,
            0,
            0
        };
        WriteCrc(frame);
        return frame;
    }

    /// <summary>Builds one of the documented runtime action frames offline.</summary>
    public static byte[] BuildControlCommandRequest(
        Sha18iControlCommand command,
        byte slaveAddress = DefaultSlaveAddress) =>
        BuildWriteSingleRegisterRequest(ControlAddress(command), 1, slaveAddress);

    /// <summary>
    /// The protocol table defines light value 0 as on and 1 as off.
    /// </summary>
    public static byte[] BuildLightRequest(bool on, byte slaveAddress = DefaultSlaveAddress) =>
        BuildWriteSingleRegisterRequest(LightCommandAddress, on ? (ushort)0 : (ushort)1, slaveAddress);

    /// <summary>
    /// Builds a function 0x10 frame from already validated 16-bit words.  The
    /// method only constructs bytes; it has no serial or network side effect.
    /// </summary>
    public static byte[] BuildWriteMultipleRegistersRequest(
        ushort startAddress,
        ReadOnlySpan<ushort> values,
        byte slaveAddress = DefaultSlaveAddress)
    {
        if (slaveAddress == 0) throw new ArgumentOutOfRangeException(nameof(slaveAddress));
        if (values.Length is 0 or > 123)
            throw new ArgumentOutOfRangeException(nameof(values), "Modbus function 0x10 supports 1..123 registers.");

        var byteCount = checked(values.Length * 2);
        var frame = new byte[9 + byteCount];
        frame[0] = slaveAddress;
        frame[1] = 0x10;
        frame[2] = (byte)(startAddress >> 8);
        frame[3] = (byte)startAddress;
        frame[4] = (byte)(values.Length >> 8);
        frame[5] = (byte)values.Length;
        frame[6] = (byte)byteCount;
        for (var index = 0; index < values.Length; index++)
        {
            frame[7 + index * 2] = (byte)(values[index] >> 8);
            frame[8 + index * 2] = (byte)values[index];
        }
        WriteCrc(frame);
        return frame;
    }

    public static byte[] BuildRuntimeMethodRequest(
        ReadOnlySpan<ushort> values,
        byte slaveAddress = DefaultSlaveAddress)
    {
        EnsureCount(values, RuntimeMethodRegisterCount, nameof(values));
        return BuildWriteMultipleRegistersRequest(RuntimeMethodStartAddress, values, slaveAddress);
    }

    public static byte[] BuildRuntimeMethodTailRequest(
        ReadOnlySpan<ushort> values,
        byte slaveAddress = DefaultSlaveAddress)
    {
        EnsureCount(values, RuntimeMethodTailRegisterCount, nameof(values));
        return BuildWriteMultipleRegistersRequest(RuntimeMethodTailStartAddress, values, slaveAddress);
    }

    public static void ValidateWriteSingleRegisterResponse(
        ReadOnlySpan<byte> frame,
        ushort expectedRegisterAddress,
        ushort expectedValue,
        byte expectedSlaveAddress = DefaultSlaveAddress)
    {
        if (frame.Length != 8)
            throw new FormatException($"Expected 8 response bytes, received {frame.Length}.");
        if (frame[0] != expectedSlaveAddress || frame[1] != 0x06)
            throw new FormatException("SHA-18i function 0x06 response address or function mismatch.");
        EnsureValidCrc(frame);
        var registerAddress = (ushort)((frame[2] << 8) | frame[3]);
        var value = (ushort)((frame[4] << 8) | frame[5]);
        if (registerAddress != expectedRegisterAddress || value != expectedValue)
            throw new FormatException("SHA-18i function 0x06 response did not echo the expected register/value.");
    }

    /// <summary>
    /// Validates a raw function 0x04 response and returns its big-endian
    /// register bytes. Callers choose the appropriate vendor worksheet
    /// (runtime or user-program) before assigning field semantics.
    /// </summary>
    public static byte[] ParseReadResponse(
        ReadOnlySpan<byte> frame,
        byte expectedSlaveAddress,
        ushort expectedRegisterCount) =>
        ModbusRtuCodec.ParseReadInputRegistersResponse(
            frame,
            expectedSlaveAddress,
            expectedRegisterCount);

    /// <summary>
    /// Parses a captured function 0x10 request for offline evidence review.
    /// It does not send or replay the request and does not assign meanings to
    /// the register words.
    /// </summary>
    public static Sha18iCapturedWriteMultipleRequest ParseCapturedWriteMultipleRequest(
        ReadOnlySpan<byte> frame,
        byte expectedSlaveAddress = DefaultSlaveAddress)
    {
        if (frame.Length < 9)
            throw new FormatException("SHA-18i function 0x10 request is too short.");
        if (frame[0] != expectedSlaveAddress)
            throw new FormatException($"Expected slave 0x{expectedSlaveAddress:X2}, received 0x{frame[0]:X2}.");
        if (frame[1] != 0x10)
            throw new FormatException($"Expected function 0x10, received 0x{frame[1]:X2}.");

        var startAddress = (ushort)((frame[2] << 8) | frame[3]);
        var registerCount = (ushort)((frame[4] << 8) | frame[5]);
        if (registerCount == 0 || registerCount > 123)
            throw new FormatException($"Invalid SHA-18i function 0x10 register count {registerCount}.");
        var expectedByteCount = checked(registerCount * 2);
        if (frame[6] != expectedByteCount)
            throw new FormatException($"Expected {expectedByteCount} payload bytes, received {frame[6]}.");
        var expectedLength = checked(9 + expectedByteCount);
        if (frame.Length != expectedLength)
            throw new FormatException($"Expected {expectedLength} request bytes, received {frame.Length}.");

        EnsureValidCrc(frame);
        return new(
            expectedSlaveAddress,
            startAddress,
            registerCount,
            frame.Slice(7, expectedByteCount).ToArray(),
            frame.ToArray());
    }

    public static void ValidateCapturedWriteMultipleResponse(
        ReadOnlySpan<byte> frame,
        Sha18iCapturedWriteMultipleRequest expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (frame.Length != 8)
            throw new FormatException($"Expected 8 response bytes, received {frame.Length}.");
        if (frame[0] != expected.SlaveAddress || frame[1] != 0x10)
            throw new FormatException("SHA-18i function 0x10 response address or function mismatch.");
        EnsureValidCrc(frame);
        var start = (ushort)((frame[2] << 8) | frame[3]);
        var count = (ushort)((frame[4] << 8) | frame[5]);
        if (start != expected.StartAddress || count != expected.RegisterCount)
            throw new FormatException("SHA-18i function 0x10 response did not echo the captured range.");
    }

    private static ushort ReadWord(ReadOnlySpan<byte> data, int wordIndex)
    {
        var offset = checked(wordIndex * 2);
        if (offset + 2 > data.Length)
            throw new FormatException($"SHA-18i response word index {wordIndex} is outside the payload.");
        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    private static ushort ControlAddress(Sha18iControlCommand command) => command switch
    {
        Sha18iControlCommand.Initialize => InitializeCommandAddress,
        Sha18iControlCommand.StartInjection => InjectionCommandAddress,
        Sha18iControlCommand.WashNeedle => WashCommandAddress,
        Sha18iControlCommand.PushTray => PushTrayCommandAddress,
        Sha18iControlCommand.ClearEmptyVialFlag => ClearEmptyVialFlagCommandAddress,
        Sha18iControlCommand.OnlineDilution => OnlineDilutionCommandAddress,
        Sha18iControlCommand.AntibacterialWash => AntibacterialWashCommandAddress,
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown SHA-18i control command.")
    };

    private static void EnsureCount(ReadOnlySpan<ushort> values, int expected, string parameterName)
    {
        if (values.Length != expected)
            throw new ArgumentException($"Expected {expected} words, received {values.Length}.", parameterName);
    }

    private static void WriteCrc(Span<byte> frame)
    {
        var crc = ModbusRtuCodec.ComputeCrc(frame[..^2]);
        frame[^2] = (byte)crc;
        frame[^1] = (byte)(crc >> 8);
    }

    private static void EnsureValidCrc(ReadOnlySpan<byte> frame)
    {
        var supplied = (ushort)(frame[^2] | (frame[^1] << 8));
        var computed = ModbusRtuCodec.ComputeCrc(frame[..^2]);
        if (supplied != computed)
            throw new FormatException($"CRC mismatch: expected 0x{computed:X4}, received 0x{supplied:X4}.");
    }
}

public sealed record Sha18iCapturedWriteMultipleRequest(
    byte SlaveAddress,
    ushort StartAddress,
    ushort RegisterCount,
    byte[] Data,
    byte[] Frame);

/// <summary>
/// Runtime single-register commands documented by the vendor.  A dedicated
/// stop command is intentionally absent: the runtime table labels 0x0708 as
/// initialization, so stopping remains a separate firmware/UI verification
/// item rather than an enum value here.
/// </summary>
public enum Sha18iControlCommand
{
    Initialize,
    StartInjection,
    WashNeedle,
    PushTray,
    ClearEmptyVialFlag,
    OnlineDilution,
    AntibacterialWash
}

public sealed record Sha18iRuntimeState(
    ushort CurrentTriggerChannelRaw,
    ushort CurrentInjectionPositionRaw,
    ushort NeedleErrorRaw,
    ushort CurrentRunStateRaw,
    ushort VialStateRaw,
    ushort ErrorFlagsRaw);

public sealed record Sha18iRuntimeValveState(
    ushort ChannelAValveStateRaw,
    ushort ChannelBValveStateRaw);

public sealed record Sha18iDeviceIdentity(
    ushort DeviceSerialRaw,
    ushort HardwareVersionRaw,
    ushort SoftwareVersionRaw);

public sealed record Sha18iRuntimeMethod(
    ushort InjectionModeRaw,
    ushort WashModeRaw,
    ushort WashCountRaw,
    ushort SyringeVolumeCodeRaw,
    ushort FlushVolumeRaw,
    ushort PreCarrierFluidVolumeRaw,
    ushort PostCarrierFluidVolumeRaw,
    ushort CarrierFluidVialPositionRaw,
    ushort LeftTraySpecificationRaw,
    ushort RightTraySpecificationRaw,
    ushort ChannelSelectionRaw,
    ushort InjectionPositionRaw,
    ushort InjectionVolumeRaw,
    ushort InjectionCountRaw,
    ushort CurrentInjectionNeedleRaw,
    ushort NeedleHeightRaw,
    ushort AspirationSpeedRaw,
    ushort InjectionWaitTimeRaw,
    ushort VialDetectionRaw,
    ushort OnlineDilutionSamplePositionRaw,
    ushort OnlineDilutionAspirationVolumeRaw,
    ushort OnlineDilutionPositionRaw,
    ushort PureWaterVolumeRaw,
    ushort BufferTubeVolumeRaw,
    ushort NeedleVolumeRaw,
    ushort ValveFrontTubeVolumeRaw,
    ushort QuantitativeLoopVolumeRaw);

public sealed record Sha18iRuntimeMethodTail(
    ushort OnlineDilutionMixCountRaw,
    ushort AntibacterialWashIntervalRaw,
    ushort AntibacterialWashCountRaw);
