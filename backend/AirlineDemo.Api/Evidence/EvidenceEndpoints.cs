using System.Security.Cryptography;
using System.Text;
using AirlineDemo.Api.Admission;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Evidence;

public sealed record EvidencePackageRecord(string PackageId, string CaseId, string State, IReadOnlyList<EvidenceDocumentResult> Documents);

/// <summary>
/// Package receipts keyed by a digest of case, ordered filenames and exact document bytes. A repeated submission
/// returns the original receipt and never re-runs intake, so it cannot create another basis version.
/// </summary>
public sealed class EvidencePackageStore
{
    private readonly Dictionary<string, EvidencePackageRecord> _packages = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public (EvidencePackageRecord Record, bool Created) Submit(string packageId, string caseId, Func<EvidencePackageOutcome> process)
    {
        lock (_gate)
        {
            if (_packages.TryGetValue(packageId, out var existing))
            {
                return (existing, false);
            }

            var outcome = process();
            var record = new EvidencePackageRecord(packageId, caseId, outcome.State, outcome.Documents);
            _packages[packageId] = record;
            return (record, true);
        }
    }

    public EvidencePackageRecord? Find(string caseId, string packageId)
    {
        lock (_gate)
        {
            return _packages.TryGetValue(packageId, out var record) && record.CaseId == caseId ? record : null;
        }
    }

    public IReadOnlyList<EvidencePackageRecord> ListForCase(string caseId)
    {
        lock (_gate)
        {
            return _packages.Values.Where(record => record.CaseId == caseId).ToArray();
        }
    }
}

public static class EvidenceEndpoints
{
    public const string PresenterRole = "presenter";
    public const string FormFieldName = "files";
    public const long MaxRequestBytes = 64L * 1024 * 1024;

    public static IEndpointRouteBuilder MapEvidenceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/cases/{caseId}/packages", SubmitPackage);
        app.MapGet("/api/cases/{caseId}/packages/{packageId}", GetPackage);
        return app;
    }

    private static async Task<IResult> SubmitPackage(
        string caseId,
        HttpRequest request,
        DemoSessionStore sessions,
        EvidenceBasisStore basis,
        EvidencePackageStore packages,
        IsolatedPdfParserOptions parserOptions,
        ICaseStateStore caseStore,
        EvidenceCaseReadSourceFactory caseSources,
        InvestigationExecutionService investigations,
        WorkloadAdmissionLimits admission,
        IServiceProvider services,
        CancellationToken ct)
    {
        var session = DemoAuthEndpoints.CurrentSession(request.HttpContext, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();
        if (!DemoAuthEndpoints.HasValidAntiForgery(request.HttpContext, session)) return DemoAuthEndpoints.AntiForgeryFailed();
        if (session.User.Role != PresenterRole) return Results.Json(new { error = "Role not permitted." }, statusCode: StatusCodes.Status403Forbidden);
        if (!session.User.CaseScopes.Contains(caseId)) return Results.NotFound();

        // Keyed by the server session identity only; a multipart userId field is never read.
        if (!admission.TryAdmitPackage(session.User.Id))
            return Results.Json(new { error = WorkloadAdmissionLimits.PackageLimitError }, statusCode: StatusCodes.Status429TooManyRequests);

        // Raise Kestrel's default 30 MB body cap so a valid 50 MiB package reaches EvidenceIntake's own limits.
        var bodySize = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is not null && !bodySize.IsReadOnly) bodySize.MaxRequestBodySize = MaxRequestBytes;

        if (!request.HasFormContentType) return Results.BadRequest(new { error = "Submit a multipart form with PDF files." });
        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(ct);
        }
        catch (Exception e) when (e is InvalidDataException or BadHttpRequestException)
        {
            return Results.BadRequest(new { error = "Submit a multipart form with PDF files." });
        }

        var files = form.Files.GetFiles(FormFieldName);
        if (files.Count == 0) return Results.BadRequest(new { error = "Package must contain at least one PDF document." });
        if (files.Count > EvidenceIntake.MaxDocuments)
            return Results.BadRequest(new { error = $"package has {files.Count} documents; limit is {EvidenceIntake.MaxDocuments}" });

        var documents = new List<EvidenceDocument>(files.Count);
        foreach (var file in files)
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            documents.Add(new EvidenceDocument(file.FileName, buffer.ToArray()));
        }

        var invalid = EvidenceIntake.Validate(documents);
        if (invalid is not null) return Results.BadRequest(new { error = invalid });

        // Refused before the case is created or any package, basis entry or investigation result is written; held until the investigation finishes.
        using var investigationSlot = admission.TryAdmitInvestigation();
        if (investigationSlot is null)
            return Results.Json(new { error = WorkloadAdmissionLimits.InvestigationLimitError }, statusCode: StatusCodes.Status429TooManyRequests);

        // Authorize before anything is saved: a non-owner must not write evidence into another user's case.
        CaseScope scope;
        try
        {
            scope = caseStore.OwnerOf(caseId) is not null
                ? caseStore.AuthorizeCase(caseId, session.User.Id)
                : caseStore.CreateCase(caseId, session.User.Id);
        }
        catch (CaseAccessDeniedException)
        {
            return Results.NotFound();
        }

        var packageId = PackageIdFor(caseId, documents);
        var (record, created) = packages.Submit(packageId, caseId,
            () => EvidenceIntake.ProcessPackage(caseId, documents, basis, parserOptions));
        // Runs on every submission until the trace exists, so a retry after a failed append re-runs only the investigation.
        await RunInvestigationAsync(record, caseId, scope, session.User.Id, caseSources, basis, services, investigations, ct);
        return created ? Results.Created($"/api/cases/{caseId}/packages/{packageId}", record) : Results.Ok(record);
    }

    private static async Task RunInvestigationAsync(
        EvidencePackageRecord record,
        string caseId,
        CaseScope scope,
        string actorId,
        EvidenceCaseReadSourceFactory caseSources,
        EvidenceBasisStore basis,
        IServiceProvider services,
        InvestigationExecutionService investigations,
        CancellationToken ct)
    {
        var accepted = record.Documents.Where(d => d.Status == "accepted").Select(d => d.FileName).Distinct(StringComparer.Ordinal).ToList();
        if (accepted.Count == 0) return;

        // Accepted evidence must not succeed without its investigation trace, so a missing model is a failure, not a skip.
        var chat = services.GetService<IChatClient>()
            ?? throw new InvalidOperationException("No investigation model is configured; accepted evidence cannot be investigated.");

        var manifest = services.GetRequiredService<ApprovedModelManifest>();
        var registry = services.GetRequiredService<SyntheticContentRegistry>();
        var caseStore = services.GetRequiredService<ICaseStateStore>();

        var runId = record.PackageId;
        var investigationId = "inv-" + runId;
        if (caseStore.FindTrace(scope, caseId, runId, investigationId) is not null) return;

        // Pin each document to the version this package accepted, so a retry investigates the same evidence.
        // Duplicate accepted filenames pin to the newest accepted version of that name, so the lookup cannot throw.
        var pinned = record.Documents
            .Where(d => d.Status == "accepted" && d.Version is not null)
            .GroupBy(d => d.FileName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(d => d.Version!.Value), StringComparer.Ordinal);
        var documents = new Dictionary<string, BasisDocument>(StringComparer.Ordinal);
        var evidenceTexts = new List<string>(accepted.Count);
        foreach (var name in accepted)
        {
            var text = basis.History($"{caseId}/{name}").First(v => v.Version == pinned[name]).Text;
            documents[name] = BasisDocumentFor(name, text);
            // The model receives page text only; ModelCallGate still refuses text that is not registered synthetic content.
            evidenceTexts.Add(text.TrimEnd('\r', '\n'));
        }

        var basisVersion = accepted.Max(name => pinned[name]);
        var actorToken = "actor-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actorId)))[..16].ToLowerInvariant();
        var context = new InvestigationTraceContext(runId, investigationId, basisVersion,
            manifest.DeploymentName ?? string.Empty, manifest.ApiVersion ?? string.Empty, manifest.Region ?? string.Empty,
            manifest.ContentFilter ?? string.Empty, "escalate", actorToken);
        var investigator = new BoundedInvestigator(caseId, caseSources.Create(caseId, accepted, pinned), manifest, registry,
            manifest.Region ?? string.Empty, $"basis-{basisVersion}", chat);

        // A gate refusal (evidence not in the synthetic registry) or model output that breaks the claim contract is a recorded
        // outcome (escalated, rejected): the trace is already appended with it. Any append failure is a different exception
        // and still reaches the caller.
        try
        {
            await investigations.ExecuteAsync(scope, context, investigator, evidenceTexts, ClaimKind.Baseline, documents, string.Empty, ct);
        }
        catch (Exception ex) when (ex is ModelGateException or InvestigationOutputException)
        {
        }
    }

    /// <summary>The validation basis keeps one entry per page, so a claim citing page 2 is checked against page 2.</summary>
    internal static BasisDocument BasisDocumentFor(string name, string text) =>
        BasisDocument.Create(name, text.Split(EvidenceIntake.PageSeparator));

    private static IResult GetPackage(string caseId, string packageId, HttpContext http, DemoSessionStore sessions, EvidencePackageStore packages)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();
        if (!session.User.CaseScopes.Contains(caseId)) return Results.NotFound();

        var record = packages.Find(caseId, packageId);
        return record is null ? Results.NotFound() : Results.Ok(record);
    }

    internal static string PackageIdFor(string caseId, IReadOnlyList<EvidenceDocument> documents)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendField(hash, caseId);
        foreach (var document in documents)
        {
            AppendField(hash, document.FileName);
            hash.AppendData(BitConverter.GetBytes(document.Content.LongLength));
            hash.AppendData(document.Content);
        }

        return "pkg-" + Convert.ToHexString(hash.GetHashAndReset())[..24].ToLowerInvariant();
    }

    private static void AppendField(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }
}
