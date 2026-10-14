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

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DemoSessionStore>();
builder.Services.AddSingleton<PasscodeThrottle>();
AddPersistence(builder.Services, builder.Configuration);
builder.Services.AddSingleton<CaseStore>(sp =>
{
    var store = sp.GetRequiredService<ICaseStateStore>();
    CaseStore.SeedDemoCases(store);
    return new CaseStore(store);
});
builder.Services.AddSingleton<DemoRunReset>();
var app = builder.Build();
// Resolve the configured stores now so an unknown provider fails at startup, not on the first request.
app.Services.GetRequiredService<ICaseStateStore>();
app.Services.GetRequiredService<IEvidenceBlobStore>();
TransportSecurity.ValidateDeploymentConfiguration(app.Configuration, app.Environment);
app.UseTransportSecurityHeaders();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapDemoAuth();
app.MapCaseEndpoints();
app.MapTechnicalReviewEndpoints();
app.MapDemoRunReset();
app.MapStaticClient();

app.Run();

// Persistence:Provider selects the case-state store (File | Sql); Persistence:EvidenceProvider selects the
// evidence document store (Memory | Blob). Unknown or unavailable values fail startup.
// Settings are read when each store is resolved, so host overrides (including test settings) apply.
static void AddPersistence(IServiceCollection services, IConfiguration config)
{
    services.AddSingleton<ICaseStateStore>(_ => new CaseStateStore(CaseStatePath(config)));
    services.AddSingleton<IEvidenceBlobStore>(_ => EvidenceStore(config));
}

static string CaseStatePath(IConfiguration config)
{
    var provider = config["Persistence:Provider"] ?? "File";
    return provider switch
    {
        "File" => config["CaseState:Path"] ?? Path.Combine(AppContext.BaseDirectory, "case-state.json"),
        "Sql" => throw new InvalidOperationException("Persistence:Provider 'Sql' is not available in this build; use 'File'."),
        _ => throw new InvalidOperationException($"Persistence:Provider '{provider}' is not supported; use 'File' or 'Sql'."),
    };
}

static IEvidenceBlobStore EvidenceStore(IConfiguration config)
{
    var provider = config["Persistence:EvidenceProvider"] ?? "Memory";
    return provider switch
    {
        "Memory" => new InMemoryEvidenceBlobStore(),
        "Blob" => AzureEvidenceBlobStore.Create(config["Persistence:BlobContainerUri"]
            ?? throw new InvalidOperationException("Persistence:BlobContainerUri is required when Persistence:EvidenceProvider is 'Blob'.")),
        _ => throw new InvalidOperationException($"Persistence:EvidenceProvider '{provider}' is not supported; use 'Memory' or 'Blob'."),
    };
}

public partial class Program { }
