using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DemoSessionStore>();
builder.Services.AddSingleton<PasscodeThrottle>();
builder.Services.AddSingleton<CaseStore>();
builder.Services.AddSingleton(_ => new CaseStateStore(
    builder.Configuration["Cases:StatePath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "case-state.json")));
var app = builder.Build();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapDemoAuth();
app.MapCaseEndpoints();
app.MapTechnicalReviewEndpoints();

app.Run();

public partial class Program { }
