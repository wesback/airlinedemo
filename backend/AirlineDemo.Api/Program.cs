using AirlineDemo.Api;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Security;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Runs;
using AirlineDemo.Api.Reviews;

if (args.Length > 0 && args[0] == AirlineDemo.Api.Evidence.PdfWorker.PdfWorkerEntry.Switch)
{
    Environment.Exit(AirlineDemo.Api.Evidence.PdfWorker.PdfWorkerEntry.Run());
}

if (args.Length > 0 && args[0] == CaseStateServices.MigrateSwitch)
{
    CaseStateServices.Migrate(Environment.GetEnvironmentVariable("ConnectionStrings__CaseStateMigration"));
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DemoSessionStore>();
builder.Services.AddSingleton<PasscodeThrottle>();
builder.Services.AddSingleton<CaseStore>();
builder.Services.AddSqlCaseState(builder.Configuration);
builder.Services.AddSingleton<DemoRunReset>();
var app = builder.Build();
TransportSecurity.ValidateDeploymentConfiguration(app.Configuration, app.Environment);
app.UseTransportSecurityHeaders();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapDemoAuth();
app.MapCaseEndpoints();
app.MapTechnicalReviewEndpoints();
app.MapDemoRunReset();
app.MapStaticClient();

app.Run();

public partial class Program { }
