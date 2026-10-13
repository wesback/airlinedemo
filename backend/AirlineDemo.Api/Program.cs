using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;
using AirlineDemo.Api.Security;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DemoSessionStore>();
builder.Services.AddSingleton<PasscodeThrottle>();
builder.Services.AddSingleton<CaseStore>();
builder.Services.AddSingleton(_ => new CaseStateStore(
    builder.Configuration["CaseState:Path"] ?? Path.Combine(AppContext.BaseDirectory, "case-state", "cases.json")));
var app = builder.Build();
TransportSecurity.ValidateDeploymentConfiguration(app.Configuration, app.Environment);
app.UseTransportSecurityHeaders();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapDemoAuth();
app.MapCaseEndpoints();
app.MapTechnicalReviewEndpoints();

app.Run();

public partial class Program { }
