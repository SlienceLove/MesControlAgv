using System.Buffers.Binary;
using MesControlAgv.InstrumentGateway;
using Microsoft.Extensions.Options;

namespace MesControlAgv.InstrumentGateway.Tests;

public sealed class CicD160PlusReadOnlyDriverTests
{
    [Fact]
    public async Task ReadStatus_DecodesCaptureCorrelatedCandidateFields()
    {
        var identityData = new byte[48];
        "YA7261078"u8.CopyTo(identityData);
        var detectorData = new byte[24];
        BinaryPrimitives.WriteSingleLittleEndian(detectorData.AsSpan(0, 4), 261.885712f);
        BinaryPrimitives.WriteSingleLittleEndian(detectorData.AsSpan(12, 4), 261.885712f);
        var processData = new byte[36];
        BinaryPrimitives.WriteUInt16BigEndian(processData.AsSpan(6, 2), 3500);
        BinaryPrimitives.WriteUInt16BigEndian(processData.AsSpan(8, 2), 3123);
        BinaryPrimitives.WriteUInt16BigEndian(processData.AsSpan(14, 2), 700);
        BinaryPrimitives.WriteUInt16BigEndian(processData.AsSpan(16, 2), 300);
        BinaryPrimitives.WriteUInt16BigEndian(processData.AsSpan(18, 2), 98);
        var suppressorData = new byte[20];
        var transport = new CapturedReadTransport(identityData, detectorData, processData, suppressorData);
        var driver = new CicD160PlusReadOnlyDriver(
            transport,
            Options.Create(new CicD160PlusOptions { Enabled = true }));

        var result = await driver.ReadStatusAsync(CancellationToken.None);

        Assert.Equal("YA7261078", result.Status.SerialNumber);
        Assert.Equal(261.885712d, result.Status.Conductivity!.Value, 4);
        Assert.Equal(261.885712d, result.Status.TotalConductivity!.Value, 4);
        Assert.Equal(31.23d, result.Status.ColumnTemperature);
        Assert.Equal(0.3d, result.Status.Flow);
        Assert.Equal(0.7d, result.Status.FlowSetpoint);
        Assert.Equal(9.8d, result.Status.Pressure);
        Assert.Equal(35d, result.Status.ColumnTemperatureSetpoint);
        Assert.Equal(0, result.Status.TemperatureControlStateRaw);
        Assert.Equal(0, result.Status.PumpStateRaw);
        Assert.Equal(98, result.Status.PressureRaw);
        Assert.Equal(0, result.Status.SuppressorEluentStateRaw);
        Assert.Equal(0, result.Status.FaultCode1Raw);
        Assert.Equal(0, result.Status.FaultCode2Raw);
        Assert.Equal("VendorDocumentAndFieldValidated", result.Status.MappingConfidence);
        Assert.False(result.Status.PortOwned);
        Assert.Equal([0x1900, 0x1770, 0x17D4, 0x1838], transport.ReadAddresses);
        Assert.Equal(4, result.Evidence.Count);
    }

    [Fact]
    public async Task ReadStatus_DecodesFieldValidatedProtocolFrames()
    {
        var identityData = ParseData(
            "0104305941373236313037380000000000000000004E2000004E20000000000000000000000002000000000000000FB00B00208681",
            24);
        var detectorData = ParseData(
            "010418A4DE35420003000000000000A4DE3542208F000000000000765D",
            12);
        var processData = ParseData(
            "0104240000C157F7410DAC0BB90000000002BC02BC00000DAC0000000000000000001E000000006A7A",
            18);
        var suppressorData = ParseData(
            "0104140041000000000000000002410096000003DA0000F79D",
            10);
        var driver = new CicD160PlusReadOnlyDriver(
            new CapturedReadTransport(identityData, detectorData, processData, suppressorData),
            Options.Create(new CicD160PlusOptions { Enabled = true }));

        var result = await driver.ReadStatusAsync(CancellationToken.None);

        Assert.Equal("YA7261078", result.Status.SerialNumber);
        Assert.Equal(45.4674225d, result.Status.Conductivity!.Value, 5);
        Assert.Equal(45.4674225d, result.Status.TotalConductivity!.Value, 5);
        Assert.Equal(30.01d, result.Status.ColumnTemperature);
        Assert.Equal(35d, result.Status.ColumnTemperatureSetpoint);
        Assert.Equal(0.7d, result.Status.FlowSetpoint);
        Assert.Equal(0.7d, result.Status.Flow);
        Assert.Equal(0d, result.Status.Pressure);
        Assert.Equal(0, result.Status.TemperatureControlStateRaw);
        Assert.Equal(0, result.Status.PumpStateRaw);
        Assert.Equal(0, result.Status.PressureRaw);
        Assert.Equal(0, result.Status.SuppressorEluentStateRaw);
        Assert.Equal(0, result.Status.FaultCode1Raw);
        Assert.Equal(0, result.Status.FaultCode2Raw);
        Assert.Equal("VendorDocumentAndFieldValidated", result.Status.MappingConfidence);
    }

    private static byte[] ParseData(string responseHex, ushort registerCount) =>
        ModbusRtuCodec.ParseReadInputRegistersResponse(
            Convert.FromHexString(responseHex),
            expectedSlaveAddress: 1,
            expectedRegisterCount: registerCount);

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
