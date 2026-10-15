using System.Security.Cryptography;
using System.Text;
using AirlineDemo.Api.Auth;
using Microsoft.AspNetCore.Http.Features;

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
        CancellationToken ct)
    {
        var session = DemoAuthEndpoints.CurrentSession(request.HttpContext, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();
        if (!DemoAuthEndpoints.HasValidAntiForgery(request.HttpContext, session)) return DemoAuthEndpoints.AntiForgeryFailed();
        if (session.User.Role != PresenterRole) return Results.Json(new { error = "Role not permitted." }, statusCode: StatusCodes.Status403Forbidden);
        if (!session.User.CaseScopes.Contains(caseId)) return Results.NotFound();

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

        var packageId = PackageIdFor(caseId, documents);
        var (record, created) = packages.Submit(packageId, caseId,
            () => EvidenceIntake.ProcessPackage(caseId, documents, basis, parserOptions));
        return created ? Results.Created($"/api/cases/{caseId}/packages/{packageId}", record) : Results.Ok(record);
    }

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
