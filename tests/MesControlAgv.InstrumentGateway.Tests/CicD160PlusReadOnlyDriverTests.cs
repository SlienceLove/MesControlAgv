using System.Buffers.Binary;
using MesControlAgv.InstrumentGateway;
using Microsoft.Extensions.Options;

namespace MesControlAgv.InstrumentGateway.Tests;

public sealed class CicD160PlusReadOnlyDriverTests
{
    [Fact]
    public async Task ReadStatus_DecodesCaptureCorrelatedCandidateFields()
    {
        var identityData = new byte[40];
        "YA7261078"u8.CopyTo(identityData);
        var detectorData = new byte[24];
        BinaryPrimitives.WriteSingleLittleEndian(detectorData.AsSpan(0, 4), 261.885712f);
        BinaryPrimitives.WriteSingleLittleEndian(detectorData.AsSpan(12, 4), 261.885712f);
        var processData = new byte[36];
        BinaryPrimitives.WriteUInt16BigEndian(processData.AsSpan(8, 2), 3123);
        BinaryPrimitives.WriteUInt16BigEndian(processData.AsSpan(14, 2), 300);
        BinaryPrimitives.WriteUInt16BigEndian(processData.AsSpan(16, 2), 300);
        var transport = new CapturedReadTransport(identityData, detectorData, processData);
        var driver = new CicD160PlusReadOnlyDriver(
            transport,
            Options.Create(new CicD160PlusOptions { Enabled = true }));

        var result = await driver.ReadStatusAsync(CancellationToken.None);

        Assert.Equal("YA7261078", result.Status.SerialNumber);
        Assert.Equal(261.885712d, result.Status.Conductivity!.Value, 4);
        Assert.Equal(261.885712d, result.Status.TotalConductivity!.Value, 4);
        Assert.Equal(31.23d, result.Status.ColumnTemperature);
        Assert.Equal(0.3d, result.Status.Flow);
        Assert.Equal("CaptureCorrelatedCandidate", result.Status.MappingConfidence);
        Assert.False(result.Status.PortOwned);
        Assert.Equal([0x1900, 0x1770, 0x17D4], transport.ReadAddresses);
        Assert.Equal(3, result.Evidence.Count);
    }

    private sealed class CapturedReadTransport(params byte[][] responses) : IReadOnlyModbusTransport
    {
        private readonly Queue<byte[]> _responses = new(responses);

        public List<int> ReadAddresses { get; } = [];

        public Task<ModbusReadResult> ReadInputRegistersAsync(
            ushort startAddress,
            ushort registerCount,
            CancellationToken cancellationToken)
        {
            ReadAddresses.Add(startAddress);
            var data = _responses.Dequeue();
            var request = ModbusRtuCodec.BuildReadInputRegisters(1, startAddress, registerCount);
            return Task.FromResult(new ModbusReadResult(
                startAddress,
                registerCount,
                request,
                [],
                data,
                1));
        }
    }
}
