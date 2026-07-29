var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { service = "adapter", status = "ok" }));

app.Run();

public partial class Program;
