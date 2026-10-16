using AirlineDemo.Api;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Diagnostics;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Finance;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Security;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Runs;
using AirlineDemo.Api.Reviews;

if (args.Length > 0 && args[0] == AirlineDemo.Api.Evidence.PdfWorker.PdfWorkerEntry.Switch)
{
    Environment.Exit(AirlineDemo.Api.Evidence.PdfWorker.PdfWorkerEntry.Run());
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAirlineDiagnostics(builder.Configuration);
builder.Services.AddSingleton<DemoSessionStore>();
builder.Services.AddSingleton<PasscodeThrottle>();
builder.Services.AddSingleton<CaseStore>();
builder.Services.AddCaseStateStore(builder.Configuration);
builder.Services.AddSingleton<InvestigationExecutionService>();
builder.Services.AddSingleton<DemoRunReset>();
builder.Services.AddSingleton<EvidenceBasisStore>();
builder.Services.AddSingleton<EvidencePackageStore>();
builder.Services.AddSingleton<EvidenceCaseReadSourceFactory>();
builder.Services.AddSingleton(SyntheticContentRegistry.FromCommittedFixtures());
var approvedModelManifestPath = builder.Configuration["Investigation:ManifestPath"];
if (!string.IsNullOrEmpty(approvedModelManifestPath))
{
    builder.Services.AddSingleton(ApprovedModelManifest.Load(approvedModelManifestPath));
}
builder.Services.AddSingleton(FinanceDemoData.CreateStore());
builder.Services.AddSingleton(IsolatedPdfParserOptions.Default);
DemoFixture.LoadEmbedded();
var app = builder.Build();
TransportSecurity.ValidateDeploymentConfiguration(app.Configuration, app.Environment);
app.UseTransportSecurityHeaders();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapDemoAuth();
app.MapCaseEndpoints();
app.MapTechnicalReviewEndpoints();
app.MapEvidenceEndpoints();
app.MapFinanceEndpoints();
app.MapDemoRunReset();
app.MapStaticClient();

app.Run();

public partial class Program { }
