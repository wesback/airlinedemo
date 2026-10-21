using AirlineDemo.Api;
using AirlineDemo.Api.Admission;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Diagnostics;
using AirlineDemo.Api.Dashboard;
using AirlineDemo.Api.Decisions;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Evaluation;
using AirlineDemo.Api.Finance;
using AirlineDemo.Api.Fixtures;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Security;
using AirlineDemo.Api.SourceSystems;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Projection;
using AirlineDemo.Api.Runs;
using AirlineDemo.Api.Reviews;
using AirlineDemo.Api.Requests;
using Microsoft.Extensions.AI;

if (args.Length > 0 && args[0] == AirlineDemo.Api.Evidence.PdfWorker.PdfWorkerEntry.Switch)
{
    Environment.Exit(AirlineDemo.Api.Evidence.PdfWorker.PdfWorkerEntry.Run());
}

var evaluationCommand = args.Length > 0 && args[0] == "--evaluate-v2";
var builder = WebApplication.CreateBuilder(evaluationCommand ? [] : args);
builder.Services.AddHostedService<TransportSecurityStartupValidator>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAirlineDiagnostics(builder.Configuration);
builder.Services.AddSingleton<DemoSessionStore>();
builder.Services.AddSingleton<PasscodeThrottle>();
builder.Services.AddCaseStateStore(builder.Configuration);
builder.Services.AddCasePackRequestLimiter(builder.Configuration);
builder.Services.AddSourceReadBudget(builder.Configuration);
builder.Services.AddSingleton<InvestigationExecutionService>();
builder.Services.AddSingleton<AssessmentStore>();
builder.Services.AddSingleton<AssessmentApiRecordStore>();
builder.Services.AddSingleton<MockRequestInbox>();
builder.Services.AddSingleton<AssessmentExecutionService>();
builder.Services.AddEvidenceBlobStore(builder.Configuration);
builder.Services.AddSingleton<IAircraftCasePackSnapshotStore, InMemoryAircraftCasePackSnapshotStore>();
builder.Services.AddSingleton<IArrCaseHistorySource>(sp =>
    new ArrCaseHistorySource(sp.GetRequiredService<ICaseStateStore>()));
builder.Services.AddSingleton<IAircraftCasePackCaseStager, AircraftCasePackCaseStager>();
builder.Services.AddSingleton<IAircraftCasePackDocumentReferenceStager, AircraftCasePackDocumentReferenceStager>();
builder.Services.AddSingleton(new AircraftCasePackLoader(
    System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "aircraft-case-pack")));
builder.Services.AddSingleton<AircraftCasePackPublisher>();
builder.Services.AddSingleton<DemoRunReset>();
builder.Services.AddSingleton<MockResponseService>();
builder.Services.AddSingleton<SourceModeStore>();
builder.Services.AddSingleton<SourceSummaryService>();
builder.Services.AddSingleton<IDecisionDossierService, DecisionDossierService>();
builder.Services.AddSingleton<EvidenceBasisStore>();
builder.Services.AddSingleton<EvidencePackageStore>();
builder.Services.AddSingleton<WorkloadAdmissionLimits>();
builder.Services.AddTransient<FourArmEvaluationRunner>();
builder.Services.AddSingleton<EvidenceCaseReadSourceFactory>();
builder.Services.AddSingleton(SyntheticContentRegistry.FromCommittedFixtures());
builder.Services.AddSingleton<Func<ApprovedModelManifest, Microsoft.Extensions.AI.IChatClient>>(sp => manifest =>
{
    var endpoint = builder.Configuration["AzureOpenAI:Endpoint"]
        ?? throw new InvalidOperationException("AzureOpenAI:Endpoint is not configured; accepted evidence cannot be investigated.");
    var deployment = manifest.DeploymentName
        ?? throw new InvalidOperationException("The approved model manifest has no deployment name.");
    var createCredential = sp.GetRequiredService<Func<Azure.Core.TokenCredential>>();
    var credential = new LazyTokenCredential(createCredential);
    var options = new Azure.AI.OpenAI.AzureOpenAIClientOptions();
    var handler = sp.GetService<System.Net.Http.HttpMessageHandler>();
    if (handler is not null)
    {
        options.Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new System.Net.Http.HttpClient(handler));
    }
    return new Azure.AI.OpenAI.AzureOpenAIClient(new Uri(endpoint), credential, options)
        .GetChatClient(deployment)
        .AsIChatClient();
});
var approvedModelManifestPath = builder.Configuration["Investigation:ManifestPath"];
if (!string.IsNullOrEmpty(approvedModelManifestPath))
{
    builder.Services.AddSingleton(ApprovedModelManifest.Load(approvedModelManifestPath));
}
// The only place a DefaultAzureCredential is constructed; it runs on the first token request, never while registering the client.
builder.Services.AddSingleton<Func<Azure.Core.TokenCredential>>(_ => () => new Azure.Identity.DefaultAzureCredential());
// Resolved lazily: constructing the client makes no network request and fetches no token.
// The model gate still refuses any request while the manifest is unapproved.
builder.Services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(sp =>
{
    var manifest = sp.GetService<ApprovedModelManifest>()
        ?? throw new InvalidOperationException("No approved model manifest is configured; accepted evidence cannot be investigated.");
    return sp.GetRequiredService<Func<ApprovedModelManifest, Microsoft.Extensions.AI.IChatClient>>()(manifest);
});
Program.RegisterProductionEvaluationExecutors(builder.Services);
builder.Services.AddSingleton(FinanceDemoData.CreateStore());
// Source implementations are registered here; pinned fixtures are verified before the app accepts requests.
builder.Services.AddSingleton<ISourceSystem<TechnicalPayload>, TechnicalSource>();
builder.Services.AddSingleton<ISourceSystem<FinancePayload>, FinanceSource>();
builder.Services.AddSingleton<ISourceSystem<MaintenancePayload>, MaintenanceSource>();
builder.Services.AddSingleton<ISourceSystem<RecordsPayload>, RecordsSource>();
builder.Services.AddSingleton<ISourceSystem<ContractsPayload>, ContractsSource>();
builder.Services.AddSingleton(IsolatedPdfParserOptions.Default);
DemoFixture.LoadEmbedded();
await using var app = builder.Build();
SourceFixtureLoader.LoadPinned(
    System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "sources"),
    app.Logger,
    app.Environment.EnvironmentName);
await app.Services.GetRequiredService<AircraftCasePackPublisher>().PublishAsync(
    app.Services.GetRequiredService<AircraftCasePackLoader>(), 0);
if (evaluationCommand) { Environment.ExitCode = await RunEvaluationCommandAsync(args[1..], app.Services.GetRequiredService<FourArmEvaluationRunner>(), Console.Out, Console.Error); return; }

app.UseTransportSecurityHeaders();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapDemoAuth();
app.MapCaseEndpoints();
app.MapCasePackEndpoints();
app.MapDashboardEndpoints();
app.MapReadinessSummaryEndpoints();
app.MapTechnicalReviewEndpoints();
app.MapDecisionDossierEndpoints();
app.MapEvidenceEndpoints();
app.MapAssessmentEndpoints();
app.MapMockResponseEndpoints();
app.MapFinanceEndpoints();
app.MapCaseHistoryEndpoints();
app.MapDemoRunReset();
app.MapSourceModeEndpoints();
app.MapSourceReadEndpoints();
app.MapSourceSummaryEndpoints();
app.MapStaticClient();

app.Run();

public partial class Program
{
    internal static void RegisterProductionEvaluationExecutors(IServiceCollection services)
    {
        foreach (var arm in Enum.GetValues<EvaluationArm>())
        {
            services.AddTransient<IEvaluationArmExecutor>(sp => new ProductionEvaluationArmExecutor(
                arm, sp.GetRequiredService<Func<ApprovedModelManifest, Microsoft.Extensions.AI.IChatClient>>()));
        }
    }

    internal sealed class TransportSecurityStartupValidator(
        IConfiguration configuration,
        IHostEnvironment environment) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            TransportSecurity.ValidateDeploymentConfiguration(configuration, environment);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    internal static async Task<int> RunEvaluationCommandAsync(
        string[] args, FourArmEvaluationRunner runner, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        try
        {
            if (args.Length != 8) throw new EvaluationInputException("four explicit suite and arm inputs are required");
            var values = args.Chunk(2).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
            if (!values.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(
                    ["--suite-manifest", "--cases", "--arm-b-manifest", "--arm-c-manifest"]) ||
                values.Values.Any(string.IsNullOrWhiteSpace))
                throw new EvaluationInputException("evaluation arguments are invalid");
            var options = new EvaluationRunOptions(values["--suite-manifest"], values["--cases"], values["--arm-b-manifest"], values["--arm-c-manifest"]);
            var result = await runner.RunAsync(options, cancellationToken);
            await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception ex) when (ex is EvaluationInputException or AirlineDemo.Api.Investigation.ModelGateException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { await error.WriteLineAsync("evaluation rejected: " + ex.GetType().Name); return 2; }
    }
}
