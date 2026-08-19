using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MesControlAgv.Mes.Tests;

public sealed class IonChromatographyApiTests(MesWebApplicationFactory factory)
    : IClassFixture<MesWebApplicationFactory>
{
    [Fact]
    public async Task Status_endpoint_projects_gateway_observation_and_read_only_policy()
    {
        var observedAt = new DateTimeOffset(2026, 8, 17, 7, 40, 59, TimeSpan.Zero);
        var reader = new StubStatusReader(new IonChromatographyStatusSnapshot(
            "CIC-D160-01",
            "CIC-D160+",
            "YA7261078",
            true,
            "ReadOnlyObserved",
            false,
            observedAt,
            Pressure: 9.8,
            ColumnTemperature: 31.23,
            Conductivity: 261.885712,
            TotalConductivity: 261.885712,
            Flow: 0.3,
            MappingConfidence: "VendorDocumentAndCaptureCorrelated",
            FlowSetpoint: 0.7,
            ColumnTemperatureSetpoint: 35,
            TemperatureControlStateRaw: 0,
            PumpStateRaw: 0,
            PressureRaw: 0,
            SuppressorEluentStateRaw: 0,
            FaultCode1Raw: 0,
            FaultCode2Raw: 0));
        using var configuredFactory = ConfigureReader(reader);
        using var client = configuredFactory.CreateClient();

        var response = await client.GetAsync("/api/instruments/CIC-D160-01/status");
        var result = await response.Content.ReadFromJsonAsync<IonChromatographyControlCenterStatusResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(result);
        Assert.Equal("YA7261078", result.Status.SerialNumber);
        Assert.Equal(31.23, result.Status.ColumnTemperature);
        Assert.Equal(0.3, result.Status.Flow);
        Assert.Equal(0.7, result.Status.FlowSetpoint);
        Assert.Equal(9.8, result.Status.Pressure);
        Assert.Equal(35, result.Status.ColumnTemperatureSetpoint);
        Assert.Equal(0, result.Status.TemperatureControlStateRaw);
        Assert.Equal(0, result.Status.PumpStateRaw);
        Assert.Equal(0, result.Status.PressureRaw);
        Assert.Equal(0, result.Status.SuppressorEluentStateRaw);
        Assert.Equal(0, result.Status.FaultCode1Raw);
        Assert.Equal(0, result.Status.FaultCode2Raw);
        Assert.False(result.TaskAdmissionEnabled);
        Assert.Equal(["Identify", "ReadStatus"], result.EnabledOperations);
        Assert.Equal("ReadOnlyCaptureCorrelated", result.ControlPolicy);
        Assert.Equal("CIC-D160-01", reader.LastInstrumentId);
    }

    [Fact]
    public async Task Status_endpoint_maps_gateway_failure_to_service_unavailable()
    {
        var reader = new StubStatusReader(new HttpRequestException("gateway disabled"));
        using var configuredFactory = ConfigureReader(reader);
        using var client = configuredFactory.CreateClient();

        var response = await client.GetAsync("/api/instruments/CIC-D160-01/status");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Status_endpoint_has_no_mutating_route()
    {
        var reader = new StubStatusReader(new InvalidOperationException("must not be called"));
        using var configuredFactory = ConfigureReader(reader);
        using var client = configuredFactory.CreateClient();

        var response = await client.PostAsync("/api/instruments/CIC-D160-01/status", content: null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Null(reader.LastInstrumentId);
    }

    private WebApplicationFactory<Program> ConfigureReader(IIonChromatographyStatusReader reader) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IIonChromatographyStatusReader>();
            services.AddSingleton(reader);
        }));

    private sealed class StubStatusReader : IIonChromatographyStatusReader
    {
        private readonly IonChromatographyStatusSnapshot? _status;
        private readonly Exception? _exception;

        public StubStatusReader(IonChromatographyStatusSnapshot status) => _status = status;
        public StubStatusReader(Exception exception) => _exception = exception;

        public string? LastInstrumentId { get; private set; }

        public Task<IonChromatographyStatusSnapshot> GetStatusAsync(
            string instrumentId,
            CancellationToken cancellationToken)
        {
            LastInstrumentId = instrumentId;
            return _exception is null
                ? Task.FromResult(_status!)
                : Task.FromException<IonChromatographyStatusSnapshot>(_exception);
        }
    }
}
