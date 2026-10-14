using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Security;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Runs;

if (args.Length > 0 && args[0] == AirlineDemo.Api.Evidence.PdfWorker.PdfWorkerEntry.Switch)
{
    Environment.Exit(AirlineDemo.Api.Evidence.PdfWorker.PdfWorkerEntry.Run());
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DemoSessionStore>();
builder.Services.AddSingleton<PasscodeThrottle>();
builder.Services.AddSingleton<CaseStore>();
builder.Services.AddSingleton(_ => new CaseStateStore(
    builder.Configuration["CaseState:Path"] ?? Path.Combine(AppContext.BaseDirectory, "case-state.json")));
builder.Services.AddSingleton<DemoRunReset>();
var app = builder.Build();
TransportSecurity.ValidateDeploymentConfiguration(app.Configuration, app.Environment);
app.UseTransportSecurityHeaders();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapDemoAuth();
app.MapCaseEndpoints();
app.MapDemoRunReset();

app.Run();

public partial class Program { }
