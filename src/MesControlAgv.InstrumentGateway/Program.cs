using MesControlAgv.InstrumentGateway;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOptions<CicD160PlusOptions>()
    .Bind(builder.Configuration.GetSection(CicD160PlusOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.InstrumentId), "InstrumentId is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ComPort), "ComPort is required.")
    .Validate(options => options.BaudRate > 0, "BaudRate must be positive.")
    .Validate(options => options.DataBits is >= 5 and <= 8, "DataBits must be between 5 and 8.")
    .Validate(options => options.TimeoutMs > 0, "TimeoutMs must be positive.")
    .Validate(options => options.SlaveAddress > 0, "SlaveAddress must be between 1 and 255.")
    .ValidateOnStart();
builder.Services.AddSingleton<IReadOnlyModbusTransport, SerialReadOnlyModbusTransport>();
builder.Services.AddSingleton<CicD160PlusReadOnlyDriver>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
    {
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        context.Response.Headers.Allow = "GET, HEAD";
        await context.Response.WriteAsJsonAsync(new
        {
            detail = "The CIC-D160+ gateway is read-only and rejects every state-changing HTTP method."
        });
        return;
    }
    await next();
});

app.MapMethods("/health", [HttpMethods.Get, HttpMethods.Head], (IOptions<CicD160PlusOptions> configuredOptions) =>
{
    var options = configuredOptions.Value;
    return Results.Ok(new
    {
        service = "instrument-gateway",
        status = options.Enabled ? "ready" : "disabled",
        mode = "read-only",
        options.InstrumentId,
        options.Model,
        options.ComPort
    });
});

app.MapMethods("/api/instruments/{instrumentId}/identity", [HttpMethods.Get, HttpMethods.Head], async (
    string instrumentId,
    CicD160PlusReadOnlyDriver driver,
    IOptions<CicD160PlusOptions> configuredOptions,
    CancellationToken cancellationToken) =>
{
    var options = configuredOptions.Value;
    if (!string.Equals(instrumentId, options.InstrumentId, StringComparison.OrdinalIgnoreCase))
        return Results.NotFound();
    if (!options.Enabled) return Results.Problem("The instrument gateway is disabled.", statusCode: 503);
    try { return Results.Ok(await driver.IdentifyAsync(cancellationToken)); }
    catch (Exception exception) when (IsDeviceFailure(exception))
    {
        return Results.Problem(exception.Message, statusCode: 503);
    }
});

app.MapMethods("/api/instruments/{instrumentId}/status", [HttpMethods.Get, HttpMethods.Head], async (
    string instrumentId,
    CicD160PlusReadOnlyDriver driver,
    IOptions<CicD160PlusOptions> configuredOptions,
    CancellationToken cancellationToken) =>
{
    var options = configuredOptions.Value;
    if (!string.Equals(instrumentId, options.InstrumentId, StringComparison.OrdinalIgnoreCase))
        return Results.NotFound();
    if (!options.Enabled) return Results.Problem("The instrument gateway is disabled.", statusCode: 503);
    try { return Results.Ok(await driver.ReadStatusAsync(cancellationToken)); }
    catch (Exception exception) when (IsDeviceFailure(exception))
    {
        return Results.Problem(exception.Message, statusCode: 503);
    }
});

app.Run();

static bool IsDeviceFailure(Exception exception) => exception is
    IOException or
    UnauthorizedAccessException or
    TimeoutException or
    FormatException or
    InvalidOperationException;

public partial class Program;
