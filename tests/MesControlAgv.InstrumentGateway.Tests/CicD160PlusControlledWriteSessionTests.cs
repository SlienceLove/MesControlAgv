using System.Buffers.Binary;
using MesControlAgv.InstrumentGateway;

namespace MesControlAgv.InstrumentGateway.Tests;

public sealed class CicD160PlusControlledWriteSessionTests
{
    [Theory]
    [MemberData(nameof(CurrentValueWrites))]
    public async Task RewriteCurrentValueOnce_RequiresSafePreflightExactEchoAndReadback(
        CicD160PlusWriteCommand command,
        CicD160PlusWriteOperation expectedOperation,
        ushort expectedRegister,
        ushort expectedRawValue,
        string expectedRequestHex)
    {
        var transport = new FakeControlledTransport();
        var session = CreateSession(transport);

        var result = await session.RewriteCurrentValueOnceAsync(command, CancellationToken.None);

        Assert.Equal(expectedOperation, result.Operation);
        Assert.Equal(expectedRegister, result.Register);
        Assert.Equal(expectedRawValue, result.RawValue);
        Assert.Equal(expectedRequestHex, result.WriteRequestHex);
        Assert.Equal(expectedRequestHex, result.WriteResponseHex);
        Assert.Equal("YA7261078", result.Preflight.ObservedIdentifier);
        Assert.Equal(700, result.Readback.Process.FlowSetpointRaw);
        Assert.Equal(3500, result.Readback.Process.ColumnTemperatureSetpointRaw);
        Assert.Equal(1, transport.WriteCalls);
        Assert.Equal(6, transport.ReadAddresses.Count);
        Assert.Equal(6, result.ReadEvidence.Count);
    }

    [Fact]
    public async Task RewriteCurrentValueOnce_DefaultPolicyRejectsBeforeAnyIo()
    {
        var transport = new FakeControlledTransport();
        var session = new CicD160PlusControlledWriteSession(transport, "YA7261078");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.RewriteCurrentValueOnceAsync(new CicD160PlusSetPumpFlow(0.700m), CancellationToken.None));

        Assert.Empty(transport.ReadAddresses);
        Assert.Equal(0, transport.WriteCalls);
    }

    [Fact]
    public async Task RewriteCurrentValueOnce_RejectsRawValueOutsideAllowlistBeforeAnyIo()
    {
        var transport = new FakeControlledTransport();
        var session = CreateSession(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.RewriteCurrentValueOnceAsync(new CicD160PlusSetPumpFlow(0.300m), CancellationToken.None));

        Assert.Empty(transport.ReadAddresses);
        Assert.Equal(0, transport.WriteCalls);
    }

    [Fact]
    public async Task RewriteCurrentValueOnce_RejectsActivationOperationBeforeAnyIo()
    {
        var transport = new FakeControlledTransport();
        var policy = new CicD160PlusOfflineWritePolicy(true,
        [
            new(CicD160PlusWriteOperation.DisablePump, [0])
        ]);
        var session = new CicD160PlusControlledWriteSession(transport, "YA7261078", policy);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.RewriteCurrentValueOnceAsync(new CicD160PlusSetPumpEnabled(false), CancellationToken.None));

        Assert.Contains("current-value setpoint rewrites only", exception.Message);
        Assert.Empty(transport.ReadAddresses);
        Assert.Equal(0, transport.WriteCalls);
    }

    [Fact]
    public async Task RewriteCurrentValueOnce_NonzeroSafetyStatePreventsWrite()
    {
        var unsafeProcess = CreateProcessData();
        BinaryPrimitives.WriteUInt16BigEndian(unsafeProcess.AsSpan(22, 2), 1);
        var transport = new FakeControlledTransport(processBeforeWrite: unsafeProcess);
        var session = CreateSession(transport);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.RewriteCurrentValueOnceAsync(new CicD160PlusSetPumpFlow(0.700m), CancellationToken.None));

        Assert.Contains("pump state raw=1", exception.Message);
        Assert.Equal(0, transport.WriteCalls);
    }

    [Fact]
    public async Task RewriteCurrentValueOnce_DifferentCurrentSetpointPreventsWrite()
    {
        var process = CreateProcessData();
        BinaryPrimitives.WriteUInt16BigEndian(process.AsSpan(14, 2), 300);
        var transport = new FakeControlledTransport(processBeforeWrite: process);
        var session = CreateSession(transport);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.RewriteCurrentValueOnceAsync(new CicD160PlusSetPumpFlow(0.700m), CancellationToken.None));

        Assert.Contains("not a no-op", exception.Message);
        Assert.Equal(0, transport.WriteCalls);
    }

    [Fact]
    public async Task RewriteCurrentValueOnce_WrongEchoStopsWithoutReadback()
    {
        var transport = new FakeControlledTransport
        {
            WriteResponse = Convert.FromHexString("010613890DAC5849")
        };
        var session = CreateSession(transport);

        await Assert.ThrowsAsync<FormatException>(() =>
            session.RewriteCurrentValueOnceAsync(new CicD160PlusSetPumpFlow(0.700m), CancellationToken.None));

        Assert.Equal(1, transport.WriteCalls);
        Assert.Equal(3, transport.ReadAddresses.Count);
    }

    [Fact]
    public async Task RewriteCurrentValueOnce_TimeoutIsOutcomeUnknownAndIsNeverRetried()
    {
        var transport = new FakeControlledTransport
        {
            WriteException = new TimeoutException("response deadline")
        };
        var session = CreateSession(transport);

        var exception = await Assert.ThrowsAsync<CicD160PlusWriteOutcomeUnknownException>(() =>
            session.RewriteCurrentValueOnceAsync(new CicD160PlusSetPumpFlow(0.700m), CancellationToken.None));

        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Contains("Do not retry automatically", exception.Message);
        Assert.Equal(1, transport.WriteCalls);
        Assert.Equal(3, transport.ReadAddresses.Count);
    }

    [Fact]
    public async Task RewriteCurrentValueOnce_ExplicitUnknownOutcomeIsNeverRetried()
    {
        var expected = new CicD160PlusWriteOutcomeUnknownException("transport outcome unknown");
        var transport = new FakeControlledTransport { WriteException = expected };
        var session = CreateSession(transport);

        var actual = await Assert.ThrowsAsync<CicD160PlusWriteOutcomeUnknownException>(() =>
            session.RewriteCurrentValueOnceAsync(new CicD160PlusSetPumpFlow(0.700m), CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.Equal(1, transport.WriteCalls);
    }

    [Fact]
    public async Task RewriteCurrentValueOnce_ChangedReadbackFailsAfterSingleWrite()
    {
        var changedReadback = CreateProcessData();
        BinaryPrimitives.WriteUInt16BigEndian(changedReadback.AsSpan(14, 2), 701);
        var transport = new FakeControlledTransport(processAfterWrite: changedReadback);
        var session = CreateSession(transport);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.RewriteCurrentValueOnceAsync(new CicD160PlusSetPumpFlow(0.700m), CancellationToken.None));

        Assert.Contains("not a no-op", exception.Message);
        Assert.Equal(1, transport.WriteCalls);
        Assert.Equal(6, transport.ReadAddresses.Count);
    }

    [Fact]
    public async Task RewriteCurrentValueOnce_ReadbackTimeoutRequiresManualReconciliation()
    {
        var transport = new FakeControlledTransport
        {
            ReadbackException = new TimeoutException("readback deadline")
        };
        var session = CreateSession(transport);

        var exception = await Assert.ThrowsAsync<CicD160PlusWriteOutcomeUnknownException>(() =>
            session.RewriteCurrentValueOnceAsync(new CicD160PlusSetPumpFlow(0.700m), CancellationToken.None));

        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Contains("safety readback is unavailable", exception.Message);
        Assert.Equal(1, transport.WriteCalls);
        Assert.Equal(4, transport.ReadAddresses.Count);
    }

    public static TheoryData<CicD160PlusWriteCommand, CicD160PlusWriteOperation, ushort, ushort, string> CurrentValueWrites => new()
    {
        { new CicD160PlusSetPumpFlow(0.700m), CicD160PlusWriteOperation.SetPumpFlow, 0x13DA, 700, "010613DA02BCAC64" },
        { new CicD160PlusSetColumnTemperature(35.00m), CicD160PlusWriteOperation.SetColumnTemperature, 0x1389, 3500, "010613890DAC5849" }
    };

    private static CicD160PlusControlledWriteSession CreateSession(FakeControlledTransport transport) => new(
        transport,
        "YA7261078",
        new CicD160PlusOfflineWritePolicy(true,
        [
            new(CicD160PlusWriteOperation.SetPumpFlow, [700]),
            new(CicD160PlusWriteOperation.SetColumnTemperature, [3500])
        ]));

    private static byte[] CreateProcessData()
    {
        var data = new byte[36];
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(6, 2), 3500);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8, 2), 3001);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(14, 2), 700);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16, 2), 700);
        return data;
    }

    private sealed class FakeControlledTransport : ICicD160PlusControlledWriteTransport
    {
        private readonly byte[] _identity;
        private readonly byte[] _processBeforeWrite;
        private readonly byte[] _processAfterWrite;
        private readonly byte[] _suppressor = new byte[20];

        public FakeControlledTransport(byte[]? processBeforeWrite = null, byte[]? processAfterWrite = null)
        {
            _identity = new byte[48];
            "YA7261078"u8.CopyTo(_identity);
            _processBeforeWrite = processBeforeWrite ?? CreateProcessData();
            _processAfterWrite = processAfterWrite ?? _processBeforeWrite;
        }

        public List<ushort> ReadAddresses { get; } = [];
        public int WriteCalls { get; private set; }
        public byte[]? WriteResponse { get; init; }
        public Exception? WriteException { get; init; }
        public Exception? ReadbackException { get; init; }

        public Task<ModbusReadResult> ReadInputRegistersAsync(
            ushort startAddress,
            ushort registerCount,
            CancellationToken cancellationToken)
        {
            ReadAddresses.Add(startAddress);
            if (WriteCalls > 0 && ReadbackException is not null)
                return Task.FromException<ModbusReadResult>(ReadbackException);
            var data = startAddress switch
            {
                0x1900 => _identity,
                0x17D4 => WriteCalls == 0 ? _processBeforeWrite : _processAfterWrite,
                0x1838 => _suppressor,
                _ => throw new InvalidOperationException($"Unexpected read address 0x{startAddress:X4}.")
            };
            return Task.FromResult(new ModbusReadResult(
                startAddress,
                registerCount,
                ModbusRtuCodec.BuildReadInputRegisters(1, startAddress, registerCount),
                BuildReadResponse(data),
                data.ToArray(),
                1));
        }

        public Task<byte[]> ExchangeWriteFrameOnceAsync(
            ReadOnlyMemory<byte> request,
            CancellationToken cancellationToken)
        {
            WriteCalls++;
            if (WriteException is not null) return Task.FromException<byte[]>(WriteException);
            return Task.FromResult(WriteResponse ?? request.ToArray());
        }

        private static byte[] BuildReadResponse(byte[] data)
        {
            var response = new byte[data.Length + 5];
            response[0] = 1;
            response[1] = 4;
            response[2] = checked((byte)data.Length);
            data.CopyTo(response, 3);
            var crc = ModbusRtuCodec.ComputeCrc(response.AsSpan(0, response.Length - 2));
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(response.Length - 2), crc);
            return response;
        }
    }
}
