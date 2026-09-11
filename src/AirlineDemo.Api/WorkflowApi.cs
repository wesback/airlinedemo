using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;

namespace AirlineDemo.Api;

public static class WorkflowApi
{
    public static WebApplication Create(
        string stateDirectory,
        IDocumentStorage? documentStorage = null,
        bool useDevelopmentErrors = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.Configure<JsonOptions>(options =>
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        var stateStore = new JsonStateStore(stateDirectory);
        var storage = documentStorage ?? new FileDocumentStorage(Path.Combine(stateDirectory, "documents"));
        builder.Services.AddSingleton(stateStore);
        builder.Services.AddSingleton(storage);
        builder.Services.AddSingleton<WorkflowService>();

        var app = builder.Build();
        if (useDevelopmentErrors)
        {
            app.UseDeveloperExceptionPage();
        }

        app.MapPost("/api/packages", async (
            PackageSubmissionRequest request,
            HttpContext httpContext,
            WorkflowService service,
            CancellationToken cancellationToken) =>
            await service.SubmitPackageAsync(request, httpContext, cancellationToken));

        app.MapGet("/api/operations/{id}", async (
            string id,
            HttpContext httpContext,
            WorkflowService service,
            CancellationToken cancellationToken) =>
            await service.GetOperationAsync(id, httpContext, cancellationToken));

        app.MapGet("/api/cases/{id}", async (
            string id,
            HttpContext httpContext,
            WorkflowService service,
            CancellationToken cancellationToken) =>
            await service.GetCaseAsync(id, httpContext, cancellationToken));

        app.MapGet("/api/cases/{id}/evidence/{documentId}", async (
            string id,
            string documentId,
            int? version,
            int? page,
            HttpContext httpContext,
            WorkflowService service,
            CancellationToken cancellationToken) =>
            await service.GetEvidencePreviewAsync(
                id, documentId, version, page, httpContext, cancellationToken));

        return app;
    }
}

internal sealed class WorkflowService
{
    private readonly JsonStateStore stateStore;
    private readonly IDocumentStorage documentStorage;

    public WorkflowService(JsonStateStore stateStore, IDocumentStorage documentStorage)
    {
        this.stateStore = stateStore;
        this.documentStorage = documentStorage;
    }

    public async Task<IResult> SubmitPackageAsync(
        PackageSubmissionRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var correlationId = request.Event?.CorrelationId ?? NewCorrelationId();
        var caller = CallerScopeParser.Parse(httpContext.Request.Headers.Authorization);
        if (caller is null)
        {
            return Results.Json(new SafeError("AUTHENTICATION_REQUIRED", correlationId), statusCode: 401);
        }

        if (request.Event is null || request.Package is null ||
            !ScopeMatches(request.Event, request.Package) ||
            !caller.Matches(request.Event))
        {
            return Results.Json(new SafeError("ACTION_FORBIDDEN", correlationId), statusCode: 403);
        }

        var validationError = ValidateSubmission(request);
        if (validationError is not null)
        {
            return Results.Json(new SafeError("INVALID_PAYLOAD", correlationId, validationError), statusCode: 400);
        }

        var canonicalHash = CanonicalHash(request);
        var receiptKey = $"{request.Event.RunId}:{request.Event.EventId}";
        var existing = stateStore.Read(state =>
            state.Receipts.TryGetValue(receiptKey, out var receipt) ? receipt : null);
        if (existing is not null)
        {
            if (!StringComparer.Ordinal.Equals(existing.CanonicalHash, canonicalHash))
            {
                return Results.Json(new SafeError("EVENT_PAYLOAD_CONFLICT", correlationId), statusCode: 409);
            }

            return Results.Json(
                new OperationAccepted(existing.OperationId, existing.CaseId),
                statusCode: 202);
        }

        var operationId = NewId("OP");
        var context = new CaseContext(
            request.Package.RunId,
            request.Package.CaseId,
            request.Package.AirlineId,
            request.Package.AircraftId,
            request.Package.LeaseId);
        var operation = new PersistedOperation(
            new OperationStatus(
                operationId,
                context.CaseId,
                context.RunId,
                context.AirlineId,
                context.AircraftId,
                context.LeaseId,
                "queued"),
            canonicalHash);
        var package = new PersistedPackage(
            request.Package,
            context,
            operationId,
            "queued",
            null);

        stateStore.Update(state =>
        {
            var caseKey = CaseKey(context);
            if (!state.Cases.TryGetValue(caseKey, out var existingCase))
            {
                existingCase = new PersistedCase(context, 1, "active", [], []);
                state.Cases.Add(caseKey, existingCase);
            }
            else if (!ScopeMatches(existingCase.Context, context))
            {
                throw new InvalidOperationException("A case ID cannot be reused across scopes.");
            }
            else
            {
                existingCase = existingCase with
                {
                    CaseRevision = checked(existingCase.CaseRevision + 1)
                };
                state.Cases[caseKey] = existingCase;
            }

            existingCase.PackageIds.Add(request.Package.PackageId);
            existingCase.OperationIds.Add(operationId);
            state.Operations.Add(operationId, operation);
            state.Packages.Add(PackageKey(context, request.Package.PackageId), package);
            foreach (var metadata in request.Package.Manifest)
            {
                var locator = $"{context.RunId}/{context.CaseId}/{metadata.DocumentId}/v{metadata.Version}";
                state.Documents[DocumentKey(context, metadata.DocumentId, metadata.Version)] =
                    new PersistedDocument(metadata, context, locator);
            }

            state.Receipts.Add(receiptKey, new PersistedReceipt(
                context.RunId,
                request.Event.EventId,
                canonicalHash,
                operationId,
                context.CaseId));
        });

        await Task.CompletedTask.WaitAsync(cancellationToken);
        return Results.Json(new OperationAccepted(operationId, context.CaseId), statusCode: 202);
    }

    public Task<IResult> GetOperationAsync(
        string operationId,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var caller = CallerScopeParser.Parse(httpContext.Request.Headers.Authorization);
        if (caller is null)
        {
            return Task.FromResult<IResult>(Results.Json(
                new SafeError("AUTHENTICATION_REQUIRED", NewCorrelationId()), statusCode: 401));
        }

        var operation = stateStore.Read(state =>
            state.Operations.TryGetValue(operationId, out var value) ? value.Status : null);
        if (operation is null || !caller.Matches(operation))
        {
            return Task.FromResult<IResult>(Results.Json(
                new SafeError("OPERATION_NOT_FOUND", NewCorrelationId()), statusCode: 404));
        }

        return Task.FromResult<IResult>(Results.Ok(operation));
    }

    public Task<IResult> GetCaseAsync(
        string caseId,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var caller = CallerScopeParser.Parse(httpContext.Request.Headers.Authorization);
        if (caller is null)
        {
            return Task.FromResult<IResult>(Results.Json(
                new SafeError("AUTHENTICATION_REQUIRED", NewCorrelationId()), statusCode: 401));
        }

        var summary = stateStore.Read(state =>
        {
            if (!state.Cases.TryGetValue(CaseKey(caller, caseId), out var persistedCase) ||
                !caller.Matches(persistedCase.Context))
            {
                return null;
            }

            var packages = persistedCase.PackageIds
                .Select(packageId => PackageKey(persistedCase.Context, packageId))
                .Where(state.Packages.ContainsKey)
                .Select(packageKey => state.Packages[packageKey])
                .Select(package => new PackageProcessingStatus(
                    package.Package.PackageId,
                    package.OperationId,
                    package.ProcessingStatus,
                    package.Error))
                .ToArray();
            return new CaseSummary(
                persistedCase.Context.CaseId,
                persistedCase.Context.RunId,
                persistedCase.Context.AirlineId,
                persistedCase.Context.AircraftId,
                persistedCase.Context.LeaseId,
                persistedCase.CaseRevision,
                persistedCase.Status,
                packages);
        });

        return Task.FromResult<IResult>(summary is null
            ? Results.Json(new SafeError("CASE_NOT_FOUND", NewCorrelationId()), statusCode: 404)
            : Results.Ok(summary));
    }

    public async Task<IResult> GetEvidencePreviewAsync(
        string caseId,
        string documentId,
        int? version,
        int? page,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var caller = CallerScopeParser.Parse(httpContext.Request.Headers.Authorization);
        if (caller is null)
        {
            return Results.Json(new SafeError("AUTHENTICATION_REQUIRED", NewCorrelationId()), statusCode: 401);
        }

        if (version is null || page is null || version < 1 || page < 1)
        {
            return Results.Json(new SafeError("INVALID_PAYLOAD", NewCorrelationId()), statusCode: 400);
        }

        // Resolve the case and exact owned version before asking storage for any content.
        var document = stateStore.Read(state =>
        {
            if (!state.Cases.TryGetValue(CaseKey(caller, caseId), out var persistedCase) ||
                !caller.Matches(persistedCase.Context))
            {
                return null;
            }

            var key = DocumentKey(persistedCase.Context, documentId, version.Value);
            return state.Documents.TryGetValue(key, out var candidate) &&
                StringComparer.Ordinal.Equals(candidate.Context.CaseId, caseId) &&
                ScopeMatches(candidate.Context, persistedCase.Context)
                ? candidate
                : null;
        });
        if (document is null)
        {
            return Results.Json(new SafeError("EVIDENCE_NOT_FOUND", NewCorrelationId()), statusCode: 404);
        }

        var excerpt = await documentStorage.ReadPagePreviewAsync(
            document.StorageLocator, page.Value, cancellationToken);
        if (excerpt is null)
        {
            return Results.Json(new SafeError("EVIDENCE_NOT_FOUND", NewCorrelationId()), statusCode: 404);
        }

        return Results.Ok(new EvidencePreview(
            document.Metadata.DocumentId,
            document.Metadata.Version,
            page.Value,
            document.Metadata.MediaType,
            excerpt));
    }

    private static string? ValidateSubmission(PackageSubmissionRequest request)
    {
        if (request.Event.SchemaVersion != "1.0" ||
            request.Package.SchemaVersion != "1.0" ||
            request.Event.Type != "package.submitted" ||
            request.Event.Payload.ValueKind != JsonValueKind.Object ||
            !request.Event.Payload.TryGetProperty("packageId", out var packageId) ||
            packageId.GetString() != request.Package.PackageId ||
            request.Event.CaseId != request.Package.CaseId ||
            request.Event.RunId != request.Package.RunId ||
            request.Package.Manifest is null ||
            request.Package.Manifest.Count == 0 ||
            !IsValidId(request.Event.EventId) ||
            !IsValidId(request.Event.RunId) ||
            !IsValidId(request.Event.CaseId) ||
            !IsValidId(request.Event.AirlineId) ||
            !IsValidId(request.Event.AircraftId) ||
            !IsValidId(request.Event.LeaseId) ||
            !IsValidId(request.Package.PackageId) ||
            request.Package.Manifest.Any(document =>
                !IsValidId(document.DocumentId) ||
                !IsValidId(document.SourceRecordId) ||
                document.SourceSystem is null ||
                document.SourceSystem.Length is < 1 or > 128 ||
                document.FileName is null ||
                document.FileName.Length is < 1 or > 255 ||
                document.FileName.Contains('/') ||
                document.FileName.Contains('\\') ||
                document.Sha256 is null ||
                document.Sha256.Length != 64 ||
                document.Sha256.Any(character => !Uri.IsHexDigit(character))))
        {
            return "The package submission does not match the published contract.";
        }

        return null;
    }

    private static bool IsValidId(string? value) =>
        value is not null &&
        value.Length is > 0 and <= 128 &&
        value.All(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool ScopeMatches(EventEnvelope eventEnvelope, SubmissionPackage package) =>
        eventEnvelope.RunId == package.RunId &&
        eventEnvelope.CaseId == package.CaseId &&
        eventEnvelope.AirlineId == package.AirlineId &&
        eventEnvelope.AircraftId == package.AircraftId &&
        eventEnvelope.LeaseId == package.LeaseId;

    private static bool ScopeMatches(CaseContext left, CaseContext right) =>
        left.RunId == right.RunId &&
        left.CaseId == right.CaseId &&
        left.AirlineId == right.AirlineId &&
        left.AircraftId == right.AircraftId &&
        left.LeaseId == right.LeaseId;

    private static string CaseKey(CaseContext context) =>
        CaseKey(new CallerScope(context.RunId, context.AirlineId, context.AircraftId, context.LeaseId),
            context.CaseId);

    private static string CaseKey(CallerScope caller, string caseId) =>
        $"{caller.RunId}:{caller.AirlineId}:{caller.AircraftId}:{caller.LeaseId}:{caseId}";

    private static string PackageKey(CaseContext context, string packageId) =>
        $"{context.RunId}:{context.AirlineId}:{context.AircraftId}:{context.LeaseId}:{context.CaseId}:{packageId}";

    private static string DocumentKey(CaseContext context, string documentId, int version) =>
        $"{context.RunId}:{context.AirlineId}:{context.AircraftId}:{context.LeaseId}:{context.CaseId}:{documentId}:v{version}";

    private static string CanonicalHash(PackageSubmissionRequest request)
    {
        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static string NewCorrelationId() => NewId("CORR");
}

internal static class CallerScopeParser
{
    public static CallerScope? Parse(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization) ||
            !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = authorization["Bearer ".Length..]
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
            .ToArray();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts)
        {
            if (part.Length != 2 || !values.TryAdd(part[0], part[1]))
            {
                return null;
            }
        }

        return values.TryGetValue("run", out var run) &&
               values.TryGetValue("airline", out var airline) &&
               values.TryGetValue("aircraft", out var aircraft) &&
               values.TryGetValue("lease", out var lease) &&
               new[] { run, airline, aircraft, lease }.All(IsSafeId)
            ? new CallerScope(run, airline, aircraft, lease)
            : null;
    }

    private static bool IsSafeId(string value) =>
        value.Length > 0 && value.All(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.');
}

internal static class CallerScopeExtensions
{
    public static bool Matches(this CallerScope caller, EventEnvelope eventEnvelope) =>
        caller.RunId == eventEnvelope.RunId &&
        caller.AirlineId == eventEnvelope.AirlineId &&
        caller.AircraftId == eventEnvelope.AircraftId &&
        caller.LeaseId == eventEnvelope.LeaseId;

    public static bool Matches(this CallerScope caller, CaseContext context) =>
        caller.RunId == context.RunId &&
        caller.AirlineId == context.AirlineId &&
        caller.AircraftId == context.AircraftId &&
        caller.LeaseId == context.LeaseId;

    public static bool Matches(this CallerScope caller, OperationStatus operation) =>
        caller.RunId == operation.RunId &&
        caller.AirlineId == operation.AirlineId &&
        caller.AircraftId == operation.AircraftId &&
        caller.LeaseId == operation.LeaseId;
}
