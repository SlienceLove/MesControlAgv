var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { service = "simulator", status = "ok" }));

app.Run();

public partial class Program;
