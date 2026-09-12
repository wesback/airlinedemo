using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;

namespace AirlineDemo.Api;

public static class WorkflowApi
{
    public static WebApplication Create(
        string stateDirectory,
        IDocumentStorage? documentStorage = null,
        bool useDevelopmentErrors = false,
        IInvestigationModel? investigationModel = null,
        InvestigationLimits? investigationLimits = null,
        AutomaticRequestPolicyConfiguration? automaticRequestPolicy = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.Configure<JsonOptions>(options =>
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        var stateStore = new JsonStateStore(stateDirectory);
        var storage = documentStorage ?? new FileDocumentStorage(Path.Combine(stateDirectory, "documents"));
        builder.Services.AddSingleton(stateStore);
        builder.Services.AddSingleton(storage);
        builder.Services.AddSingleton(new WorkflowService(
            stateStore,
            storage,
            investigationModel is null
                ? null
                : new BoundedInvestigationGateway(investigationModel, investigationLimits),
            automaticRequestPolicy));
        builder.Services.AddSingleton(
            automaticRequestPolicy ?? AutomaticRequestPolicyConfiguration.Default);

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

        app.MapPost("/api/partner-responses", async (
            PackageSubmissionRequest? request,
            HttpContext httpContext,
            WorkflowService service,
            CancellationToken cancellationToken) =>
            await service.SubmitPartnerResponseAsync(request, httpContext, cancellationToken));

        app.MapDelete("/api/demo/runs/{runId}", async (
            string runId,
            HttpRequest httpRequest,
            HttpContext httpContext,
            WorkflowService service,
            CancellationToken cancellationToken) =>
            await service.ResetRunAsync(
                runId,
                await httpRequest.ReadFromJsonAsync<RunResetRequest>(cancellationToken),
                httpContext,
                cancellationToken));

        app.MapGet("/api/operations/{id}", async (
            string id,
            HttpContext httpContext,
            WorkflowService service,
            CancellationToken cancellationToken) =>
            await service.GetOperationAsync(id, httpContext, cancellationToken));

        app.MapPost("/api/operations/{id}/process", async (
            string id,
            HttpContext httpContext,
            WorkflowService service,
            CancellationToken cancellationToken) =>
            await service.ProcessOperationAsync(id, false, false, httpContext, cancellationToken));

        app.MapPost("/api/operations/{id}/retry", async (
            string id,
            HttpContext httpContext,
            WorkflowService service,
            CancellationToken cancellationToken) =>
            await service.ProcessOperationAsync(id, true, true, httpContext, cancellationToken));

        app.MapPost("/api/packages/{id}/process", async (
            string id,
            HttpContext httpContext,
            WorkflowService service,
            CancellationToken cancellationToken) =>
            await service.ProcessPackageAsync(id, false, false, httpContext, cancellationToken));

        app.MapPost("/api/packages/{id}/retry", async (
            string id,
            HttpContext httpContext,
            WorkflowService service,
            CancellationToken cancellationToken) =>
            await service.ProcessPackageAsync(id, true, true, httpContext, cancellationToken));

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

        app.MapPost("/api/cases/{id}/reviews", async (
            string id,
            ReviewCommand? command,
            HttpContext httpContext,
            WorkflowService service,
            CancellationToken cancellationToken) =>
            await service.SubmitReviewAsync(
                id, command, httpContext, cancellationToken));

        app.MapPost("/api/cases/{id}/review-tasks/{taskId}/complete", async (
            string id,
            string taskId,
            HttpContext httpContext,
            WorkflowService service,
            CancellationToken cancellationToken) =>
            await service.CompleteReviewTaskAsync(
                id, taskId, httpContext, cancellationToken));

        app.MapGet("/api/cases/{id}/review-tasks", (
            string id,
            HttpContext httpContext,
            WorkflowService service) =>
            service.GetReviewTasks(id, httpContext));

        app.MapGet("/api/cases/{id}/review-tasks/{taskId}", (
            string id,
            string taskId,
            HttpContext httpContext,
            WorkflowService service) =>
            service.GetReviewTask(id, taskId, httpContext));

        app.MapPost("/api/cases/{id}/review-tasks/derive", (
            string id,
            HttpContext httpContext,
            WorkflowService service) =>
            service.DeriveReviewTasks(id, httpContext));

        app.MapPost("/api/cases/{id}/review-tasks/{taskId}/assign", async (
            string id,
            string taskId,
            ReviewTaskAssignment? assignment,
            HttpContext httpContext,
            WorkflowService service,
            CancellationToken cancellationToken) =>
            await service.AssignReviewTaskAsync(
                id, taskId, assignment, httpContext, cancellationToken));

        app.MapPost("/api/dispatch", async (
            DispatchCommand? command,
            HttpContext httpContext,
            WorkflowService service,
            CancellationToken cancellationToken) =>
            await service.DispatchAsync(command, httpContext, cancellationToken));

        app.MapPost("/api/mock-inbox/dispatch", async (
            DispatchCommand? command,
            HttpContext httpContext,
            WorkflowService service,
            CancellationToken cancellationToken) =>
            await service.DispatchAsync(command, httpContext, cancellationToken));

        app.MapGet("/api/mock-inbox", (
            HttpContext httpContext,
            WorkflowService service) =>
            service.GetMockInbox(httpContext));

        return app;
    }
}

internal sealed class WorkflowService
{
    private const string ParserVersion = "deterministic-pdf-parser/1.0";
    private const string ExtractorVersion = "deterministic-text-extractor/1.0";

    private readonly JsonStateStore stateStore;
    private readonly IDocumentStorage documentStorage;
    private readonly BoundedInvestigationGateway? investigationGateway;
    private readonly AutomaticRequestPolicyConfiguration automaticRequestPolicy;

    public WorkflowService(
        JsonStateStore stateStore,
        IDocumentStorage documentStorage,
        BoundedInvestigationGateway? investigationGateway = null,
        AutomaticRequestPolicyConfiguration? automaticRequestPolicy = null)
    {
        this.stateStore = stateStore;
        this.documentStorage = documentStorage;
        this.investigationGateway = investigationGateway;
        this.automaticRequestPolicy =
            automaticRequestPolicy ?? AutomaticRequestPolicyConfiguration.Default;
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

        var canonicalHash = CanonicalHash(request);
        var receiptKey = $"{request.Event.RunId}:{request.Event.EventId}";
        var existing = stateStore.Read(state =>
            state.Receipts.TryGetValue(receiptKey, out var receipt) &&
            (receipt.OperationType != "load" ||
                state.Operations.ContainsKey(receipt.OperationId))
                ? receipt
                : null);
        if (existing is not null)
        {
            if (!StringComparer.Ordinal.Equals(existing.CanonicalHash, canonicalHash))
            {
                return Results.Json(new SafeError("EVENT_PAYLOAD_CONFLICT", correlationId), statusCode: 409);
            }

            return Results.Json(
                new OperationAccepted(existing.OperationId, existing.CaseId, existing.ReceiptId),
                statusCode: 202);
        }

        var validationError = ValidateSubmission(request);
        if (validationError is not null)
        {
            return Results.Json(new SafeError("INVALID_PAYLOAD", correlationId, validationError), statusCode: 400);
        }

        if (documentStorage is IManifestDocumentStorage manifestStorage)
        {
            var validationContext = new CaseContext(
                request.Package.RunId,
                request.Package.CaseId,
                request.Package.AirlineId,
                request.Package.AircraftId,
                request.Package.LeaseId);
            var storageError = await manifestStorage.ValidateManifestAsync(
                validationContext, request.Package.Manifest, cancellationToken);
            if (storageError is not null)
            {
                return Results.Json(
                    new SafeError("INVALID_PAYLOAD", correlationId, storageError),
                    statusCode: 400);
            }
        }

        var manifestSha256 = await GetSelectedManifestSha256Async(
            new CaseContext(
                request.Package.RunId,
                request.Package.CaseId,
                request.Package.AirlineId,
                request.Package.AircraftId,
                request.Package.LeaseId),
            request.Package.Manifest,
            cancellationToken);
        var operationId = NewId("OP");
        var receiptId = NewId("RECEIPT");
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

        PersistedReceipt? replay = null;
        var conflict = false;
        stateStore.Update(state =>
        {
            if (state.Receipts.TryGetValue(receiptKey, out var receipt))
            {
                if (!StringComparer.Ordinal.Equals(receipt.CanonicalHash, canonicalHash))
                {
                    conflict = true;
                }
                else if (state.Operations.ContainsKey(receipt.OperationId))
                {
                    replay = receipt;
                }
                else
                {
                    state.AuditReceipts ??= new(StringComparer.Ordinal);
                    state.AuditReceipts[$"{receipt.RunId}:{receipt.ReceiptId}"] = receipt;
                    state.Receipts.Remove(receiptKey);
                }

                if (conflict || replay is not null)
                {
                    return;
                }
            }

            var caseKey = CaseKey(context);
            var createdCase = false;
            if (!state.Cases.TryGetValue(caseKey, out var existingCase))
            {
                existingCase = new PersistedCase(context, 1, "active", [], []);
                state.Cases.Add(caseKey, existingCase);
                createdCase = true;
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
                receiptId,
                context.RunId,
                request.Event.EventId,
                canonicalHash,
                operationId,
                context.CaseId,
                "load",
                manifestSha256,
                DateTimeOffset.UtcNow,
                (createdCase ? 1 : 0) + 2 + request.Package.Manifest.Count));
        });

        if (conflict)
        {
            return Results.Json(new SafeError("EVENT_PAYLOAD_CONFLICT", correlationId), statusCode: 409);
        }

        if (replay is not null)
        {
            return Results.Json(
                new OperationAccepted(replay.OperationId, replay.CaseId, replay.ReceiptId),
                statusCode: 202);
        }

        await Task.CompletedTask.WaitAsync(cancellationToken);
        return Results.Json(
            new OperationAccepted(operationId, context.CaseId, receiptId),
            statusCode: 202);
    }

    public async Task<IResult> SubmitPartnerResponseAsync(
        PackageSubmissionRequest? request,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var correlationId = request?.Event?.CorrelationId ?? NewCorrelationId();
        var caller = CallerScopeParser.Parse(httpContext.Request.Headers.Authorization);
        if (caller is null)
        {
            return Results.Json(
                new SafeError("AUTHENTICATION_REQUIRED", correlationId), statusCode: 401);
        }

        if (request is null ||
            request.Event is null ||
            request.Package is null ||
            !caller.IsMockPartner() ||
            !caller.Matches(request.Event) ||
            !ScopeMatches(request.Event, request.Package))
        {
            return Results.Json(
                new SafeError("ACTION_FORBIDDEN", correlationId), statusCode: 403);
        }

        var canonicalHash = CanonicalHash(request);
        var receiptKey = $"{request.Event.RunId}:{request.Event.EventId}";
        var existing = stateStore.Read(state =>
            state.Receipts.TryGetValue(receiptKey, out var receipt) &&
            receipt.OperationType == "partner_response"
                ? receipt
                : null);
        if (existing is not null)
        {
            if (!StringComparer.Ordinal.Equals(existing.CanonicalHash, canonicalHash))
            {
                return Results.Json(
                    new SafeError("EVENT_PAYLOAD_CONFLICT", correlationId), statusCode: 409);
            }

            return Results.Json(
                new OperationAccepted(existing.OperationId, existing.CaseId, existing.ReceiptId),
                statusCode: 202);
        }

        var validationError = ValidatePartnerResponse(request);
        if (validationError is not null)
        {
            return Results.Json(
                new SafeError("INVALID_PAYLOAD", correlationId, validationError),
                statusCode: 400);
        }

        var requestId = request.Event.Payload.GetProperty("requestId").GetString()!;
        var context = new CaseContext(
            request.Package.RunId,
            request.Package.CaseId,
            request.Package.AirlineId,
            request.Package.AircraftId,
            request.Package.LeaseId);
        var persistedRequest = stateStore.Read(state =>
            state.EvidenceRequests.TryGetValue(requestId, out var candidate) &&
            candidate.Status is not ("closed" or "cancelled") &&
            StringComparer.Ordinal.Equals(candidate.RecipientRef, "mock-partner-inbox") &&
            IsRequestInCallerScope(state, candidate, caller) &&
            state.EvidenceBases.TryGetValue(candidate.BasisId, out var basis) &&
            ScopeMatches(basis.Context, context)
                ? candidate
                : null);
        if (persistedRequest is null)
        {
            return Results.Json(
                new SafeError("RESPONSE_NOT_FOUND", correlationId), statusCode: 404);
        }

        if (stateStore.Read(state =>
                state.Packages.ContainsKey(PackageKey(context, request.Package.PackageId))))
        {
            return Results.Json(
                new SafeError("INVALID_PAYLOAD", correlationId), statusCode: 409);
        }

        var operationId = NewId("OP");
        var receiptId = NewId("RECEIPT");
        var responseId = NewId("RESPONSE");
        var triggerId = NewId("REASSESS");
        var receivedAt = DateTimeOffset.UtcNow;
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
        var response = new PartnerResponse(
            responseId,
            requestId,
            request.Package.PackageId,
            context,
            request.Event.EventId,
            receivedAt,
            canonicalHash);
        var trigger = new ReassessmentTrigger(
            triggerId,
            requestId,
            request.Package.PackageId,
            context,
            "queued",
            receivedAt,
            correlationId);

        PersistedReceipt? replay = null;
        var conflict = false;
        var invalidRequest = false;
        stateStore.Update(state =>
        {
            if (state.Receipts.TryGetValue(receiptKey, out var receipt))
            {
                if (!StringComparer.Ordinal.Equals(receipt.CanonicalHash, canonicalHash))
                {
                    conflict = true;
                }
                else
                {
                    replay = receipt;
                }

                return;
            }

            if (!state.EvidenceRequests.TryGetValue(requestId, out var currentRequest) ||
                currentRequest.Status is "closed" or "cancelled" ||
                !StringComparer.Ordinal.Equals(
                    currentRequest.RecipientRef, "mock-partner-inbox") ||
                !IsRequestInCallerScope(state, currentRequest, caller) ||
                !state.EvidenceBases.TryGetValue(currentRequest.BasisId, out var basis) ||
                !ScopeMatches(basis.Context, context) ||
                state.Packages.ContainsKey(PackageKey(context, request.Package.PackageId)))
            {
                invalidRequest = true;
                return;
            }

            if (!state.Cases.TryGetValue(CaseKey(context), out var persistedCase))
            {
                invalidRequest = true;
                return;
            }

            state.Cases[CaseKey(context)] = persistedCase with
            {
                CaseRevision = checked(persistedCase.CaseRevision + 1),
                Status = "active"
            };
            state.Operations.Add(operationId, operation);
            state.Packages.Add(PackageKey(context, request.Package.PackageId), package);
            foreach (var metadata in request.Package.Manifest)
            {
                var locator =
                    $"{context.RunId}/{context.CaseId}/{metadata.DocumentId}/v{metadata.Version}";
                state.Documents[DocumentKey(context, metadata.DocumentId, metadata.Version)] =
                    new PersistedDocument(metadata, context, locator);
            }

            state.EvidenceRequests[requestId] = currentRequest with
            {
                Status = "responded",
                ClosureReason = null
            };
            state.PartnerResponses.Add(responseId, response);
            state.ReassessmentTriggers.Add(triggerId, trigger);
            state.Receipts.Add(
                receiptKey,
                new PersistedReceipt(
                    receiptId,
                    context.RunId,
                    request.Event.EventId,
                    canonicalHash,
                    operationId,
                    context.CaseId,
                    "partner_response",
                    string.Empty,
                    receivedAt,
                    7 + request.Package.Manifest.Count));
            var auditId = NewId("AUDIT");
            (state.AuditEntries ??= new(StringComparer.Ordinal)).Add(
                auditId,
                new AuditEntry(
                    auditId,
                    context,
                    "mock_partner",
                    caller.Subject,
                    "partner.response.received",
                    [responseId, requestId, request.Package.PackageId, triggerId],
                    currentRequest.BasisId,
                    receivedAt,
                    correlationId));
        });

        if (conflict)
        {
            return Results.Json(
                new SafeError("EVENT_PAYLOAD_CONFLICT", correlationId), statusCode: 409);
        }

        if (replay is not null)
        {
            return Results.Json(
                new OperationAccepted(replay.OperationId, replay.CaseId, replay.ReceiptId),
                statusCode: 202);
        }

        if (invalidRequest)
        {
            return Results.Json(
                new SafeError("RESPONSE_NOT_FOUND", correlationId), statusCode: 404);
        }

        return Results.Json(
            new OperationAccepted(operationId, context.CaseId, receiptId),
            statusCode: 202);
    }

    public async Task<IResult> ResetRunAsync(
        string? routeRunId,
        RunResetRequest? request,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var correlationId = NewCorrelationId();
        var caller = CallerScopeParser.Parse(httpContext.Request.Headers.Authorization);
        if (caller is null)
        {
            return Results.Json(
                new SafeError("AUTHENTICATION_REQUIRED", correlationId), statusCode: 401);
        }

        var requestedRunId = request?.RunId;
        if (routeRunId is null ||
            requestedRunId is null ||
            !StringComparer.Ordinal.Equals(routeRunId, requestedRunId) ||
            !IsValidId(routeRunId) ||
            !StringComparer.Ordinal.Equals(caller.RunId, routeRunId))
        {
            return Results.Json(
                new SafeError("ACTION_FORBIDDEN", correlationId), statusCode: 403);
        }

        if (!StringComparer.Ordinal.Equals(request?.Confirmation, "RESET"))
        {
            return Results.Json(
                new SafeError(
                    "INVALID_PAYLOAD",
                    correlationId,
                    "An explicit RESET confirmation is required."),
                statusCode: 400);
        }

        var runId = routeRunId;
        var manifestSha256 = stateStore.Read(state =>
            state.Receipts.Values
                .Where(receipt =>
                    receipt.RunId == runId &&
                    receipt.OperationType == "load" &&
                    !string.IsNullOrWhiteSpace(receipt.SelectedManifestSha256))
                .OrderByDescending(receipt => receipt.Timestamp)
                .Select(receipt => receipt.SelectedManifestSha256)
                .FirstOrDefault());
        if (manifestSha256 is null)
        {
            manifestSha256 = await GetSelectedManifestSha256Async(
                new CaseContext(runId, string.Empty, string.Empty, string.Empty, string.Empty),
                [],
                cancellationToken);
        }

        manifestSha256 ??= ManifestHash([]);
        var receiptId = NewId("RECEIPT");
        var affectedRecordCount = 0;
        stateStore.Update(state =>
        {
            var caseKeys = state.Cases
                .Where(entry => entry.Value.Context.RunId == runId)
                .Select(entry => entry.Key)
                .ToArray();
            var packageKeys = state.Packages
                .Where(entry => entry.Value.Context.RunId == runId)
                .Select(entry => entry.Key)
                .ToArray();
            var operationKeys = state.Operations
                .Where(entry => entry.Value.Status.RunId == runId)
                .Select(entry => entry.Key)
                .ToArray();
            var documentKeys = state.Documents
                .Where(entry => entry.Value.Context.RunId == runId)
                .Select(entry => entry.Key)
                .ToArray();
            var extractionKeys = state.ExtractionRecords
                .Where(entry => entry.Value.Context.RunId == runId)
                .Select(entry => entry.Key)
                .ToArray();
            var extractionAttemptKeys = state.ExtractionAttempts
                .Where(entry => entry.Value.Context.RunId == runId)
                .Select(entry => entry.Key)
                .ToArray();
            var evidenceBasisKeys = state.EvidenceBases
                .Where(entry => entry.Value.Context.RunId == runId)
                .Select(entry => entry.Key)
                .ToArray();
            var investigationKeys = state.Investigations
                .Where(entry => entry.Value.Context.RunId == runId)
                .Select(entry => entry.Key)
                .ToArray();
            var findingKeys = state.Findings
                .Where(entry =>
                    state.EvidenceBases.TryGetValue(
                        entry.Value.BasisId,
                        out var basis) &&
                    basis.Context.RunId == runId)
                .Select(entry => entry.Key)
                .ToArray();
            var policyDecisionKeys = state.PolicyDecisions
                .Where(entry => state.EvidenceBases.TryGetValue(
                    entry.Value.BasisId, out var basis) &&
                    basis.Context.RunId == runId)
                .Select(entry => entry.Key)
                .ToArray();
            var evidenceRequestKeys = state.EvidenceRequests
                .Where(entry => state.EvidenceBases.TryGetValue(
                    entry.Value.BasisId, out var basis) &&
                    basis.Context.RunId == runId)
                .Select(entry => entry.Key)
                .ToArray();
            var partnerResponseKeys = state.PartnerResponses
                .Where(entry => entry.Value.Context.RunId == runId)
                .Select(entry => entry.Key)
                .ToArray();
            var reassessmentTriggerKeys = state.ReassessmentTriggers
                .Where(entry => entry.Value.Context.RunId == runId)
                .Select(entry => entry.Key)
                .ToArray();
            var dispatchOutboxKeys = state.DispatchOutbox
                .Where(entry => state.EvidenceRequests.TryGetValue(
                    entry.Value.RequestId, out var request) &&
                    state.EvidenceBases.TryGetValue(
                        request.BasisId, out var basis) &&
                    basis.Context.RunId == runId)
                .Select(entry => entry.Key)
                .ToArray();
            var mockInboxKeys = state.MockInbox
                .Where(entry => entry.Value.Context.RunId == runId)
                .Select(entry => entry.Key)
                .ToArray();
            var deliveryAttemptKeys = state.DeliveryAttempts
                .Where(entry => state.EvidenceRequests.TryGetValue(
                    entry.Value.RequestId, out var request) &&
                    state.EvidenceBases.TryGetValue(
                        request.BasisId, out var basis) &&
                    basis.Context.RunId == runId)
                .Select(entry => entry.Key)
                .ToArray();
            var runOperationIds = state.Operations
                .Where(entry => entry.Value.Status.RunId == runId)
                .Select(entry => entry.Key)
                .ToHashSet(StringComparer.Ordinal);
            policyDecisionKeys = state.PolicyDecisions
                .Where(entry =>
                    state.EvidenceBases.TryGetValue(
                        entry.Value.BasisId, out var basis) &&
                    basis.Context.RunId == runId ||
                    entry.Value.BasisId.StartsWith("BASIS-", StringComparison.Ordinal) &&
                    runOperationIds.Contains(entry.Value.BasisId["BASIS-".Length..]))
                .Select(entry => entry.Key)
                .ToArray();
            var reviewDecisionKeys = state.ReviewDecisions?
                .Where(entry => state.EvidenceBases.TryGetValue(
                    entry.Value.BasisId, out var basis) &&
                    basis.Context.RunId == runId)
                .Select(entry => entry.Key)
                .ToArray() ?? [];
            var findingDispositionKeys = state.FindingDispositions?
                .Where(entry => state.EvidenceBases.TryGetValue(
                    entry.Value.BasisId, out var basis) &&
                    basis.Context.RunId == runId)
                .Select(entry => entry.Key)
                .ToArray() ?? [];
            var reviewTaskKeys = state.ReviewTasks?
                .Where(entry => state.EvidenceBases.TryGetValue(
                    entry.Value.BasisId, out var basis) &&
                    basis.Context.RunId == runId)
                .Select(entry => entry.Key)
                .ToArray() ?? [];
            var auditEntryKeys = state.AuditEntries?
                .Where(entry => entry.Value.Context.RunId == runId)
                .Select(entry => entry.Key)
                .ToArray() ?? [];
            affectedRecordCount =
                caseKeys.Length +
                packageKeys.Length +
                operationKeys.Length +
                documentKeys.Length +
                extractionKeys.Length +
                extractionAttemptKeys.Length +
                evidenceBasisKeys.Length +
                investigationKeys.Length +
                findingKeys.Length +
                policyDecisionKeys.Length +
                evidenceRequestKeys.Length +
                partnerResponseKeys.Length +
                reassessmentTriggerKeys.Length +
                dispatchOutboxKeys.Length +
                mockInboxKeys.Length +
                deliveryAttemptKeys.Length +
                reviewDecisionKeys.Length +
                findingDispositionKeys.Length +
                reviewTaskKeys.Length +
                auditEntryKeys.Length;
            foreach (var key in caseKeys)
            {
                state.Cases.Remove(key);
            }

            foreach (var key in packageKeys)
            {
                state.Packages.Remove(key);
            }

            foreach (var key in operationKeys)
            {
                state.Operations.Remove(key);
            }

            foreach (var key in documentKeys)
            {
                state.Documents.Remove(key);
            }

            foreach (var key in extractionKeys)
            {
                state.ExtractionRecords.Remove(key);
            }

            foreach (var key in extractionAttemptKeys)
            {
                state.ExtractionAttempts.Remove(key);
            }

            foreach (var key in evidenceBasisKeys)
            {
                state.EvidenceBases.Remove(key);
            }

            foreach (var key in investigationKeys)
            {
                state.Investigations.Remove(key);
            }

            foreach (var key in findingKeys)
            {
                state.Findings.Remove(key);
            }

            foreach (var key in policyDecisionKeys)
            {
                state.PolicyDecisions.Remove(key);
            }

            foreach (var key in evidenceRequestKeys)
            {
                state.EvidenceRequests.Remove(key);
            }

            foreach (var key in partnerResponseKeys)
            {
                state.PartnerResponses.Remove(key);
            }

            foreach (var key in reassessmentTriggerKeys)
            {
                state.ReassessmentTriggers.Remove(key);
            }

            foreach (var key in dispatchOutboxKeys)
            {
                state.DispatchOutbox.Remove(key);
            }

            foreach (var key in mockInboxKeys)
            {
                state.MockInbox.Remove(key);
            }

            foreach (var key in deliveryAttemptKeys)
            {
                state.DeliveryAttempts.Remove(key);
            }

            foreach (var key in reviewDecisionKeys)
            {
                state.ReviewDecisions?.Remove(key);
            }

            foreach (var key in findingDispositionKeys)
            {
                state.FindingDispositions?.Remove(key);
            }

            foreach (var key in reviewTaskKeys)
            {
                state.ReviewTasks?.Remove(key);
            }

            foreach (var key in auditEntryKeys)
            {
                state.AuditEntries?.Remove(key);
            }

            state.Receipts.Add(
                $"reset:{runId}:{receiptId}",
                new PersistedReceipt(
                    receiptId,
                    runId,
                    $"RESET-{Guid.NewGuid():N}",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    "reset",
                    manifestSha256,
                    DateTimeOffset.UtcNow,
                    affectedRecordCount));
        });

        return Results.Ok(new ResetAccepted(runId, receiptId, affectedRecordCount));
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

    public async Task<IResult> ProcessOperationAsync(
        string operationId,
        bool isRetry,
        bool startOnly,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var caller = CallerScopeParser.Parse(httpContext.Request.Headers.Authorization);
        var correlationId = NewCorrelationId();
        if (caller is null)
        {
            return Results.Json(
                new SafeError("AUTHENTICATION_REQUIRED", correlationId), statusCode: 401);
        }

        PersistedPackage? package = null;
        string? attemptId = null;
        var attemptNumber = 0;
        PersistedReceipt? receipt = null;
        SafeError? startError = null;
        stateStore.Update(state =>
        {
            if (!state.Operations.TryGetValue(operationId, out var operation) ||
                !caller.Matches(operation.Status))
            {
                startError = new SafeError("OPERATION_NOT_FOUND", correlationId);
                return;
            }

            package = state.Packages.Values.SingleOrDefault(
                candidate => candidate.OperationId == operationId &&
                    caller.Matches(candidate.Context));
            receipt = state.Receipts.Values.SingleOrDefault(
                candidate => candidate.OperationId == operationId &&
                    caller.RunId == candidate.RunId &&
                    caller.Matches(operation.Status));
            if (package is null || receipt is null)
            {
                startError = new SafeError("OPERATION_NOT_FOUND", correlationId);
                return;
            }

            if (operation.Status.Status == "failed" && !isRetry)
            {
                startError = new SafeError(
                    "INVALID_PAYLOAD",
                    correlationId,
                    "A failed package requires an explicit retry.");
                return;
            }

            if (isRetry && operation.Status.Status != "failed")
            {
                startError = new SafeError(
                    "INVALID_PAYLOAD",
                    correlationId,
                    "Only a failed package can be retried.");
                return;
            }

            if (operation.Status.Status == "complete")
            {
                attemptId = string.Empty;
                return;
            }

            var attempts = (operation.Status.Attempts ?? [])
                .Select(attempt => attempt)
                .ToList();
            var currentAttempt = attempts.LastOrDefault();
            if (operation.Status.Status != "processing" ||
                currentAttempt is null ||
                currentAttempt.Status != "processing")
            {
                attemptId = NewId("ATTEMPT");
                attemptNumber = attempts.Count + 1;
                attempts.Add(new ProcessingAttemptStatus(
                    attemptId,
                    attemptNumber,
                    "processing"));
            }
            else
            {
                attemptId = currentAttempt.AttemptId;
                attemptNumber = currentAttempt.AttemptNumber;
            }

            var processingStatus = operation.Status with
            {
                Status = "processing",
                Error = null,
                Attempts = attempts
            };
            state.Operations[operationId] = operation with { Status = processingStatus };
            state.Packages[PackageKey(package.Context, package.Package.PackageId)] =
                package with { ProcessingStatus = "processing", Error = null };
            var caseKey = CaseKey(package.Context);
            if (state.Cases.TryGetValue(caseKey, out var persistedCase))
            {
                state.Cases[caseKey] = persistedCase with { Status = "active" };
            }
        });

        if (startError is not null)
        {
            return Results.Json(
                startError,
                statusCode: startError.SafeCode == "OPERATION_NOT_FOUND" ? 404 : 409);
        }

        if (package is null || receipt is null || attemptId is null)
        {
            return Results.Json(
                new SafeError("OPERATION_NOT_FOUND", correlationId), statusCode: 404);
        }

        if (stateStore.Read(state =>
            state.Operations.TryGetValue(operationId, out var operation) &&
            operation.Status.Status == "complete"))
        {
            return Results.Json(
                new OperationAccepted(operationId, package.Context.CaseId, receipt.ReceiptId),
                statusCode: 202);
        }

        if (startOnly)
        {
            return Results.Json(
                new OperationAccepted(operationId, package.Context.CaseId, receipt.ReceiptId),
                statusCode: 202);
        }

        string? validationError = null;
        try
        {
            if (documentStorage is IManifestDocumentStorage manifestStorage)
            {
                validationError = await manifestStorage.ValidateManifestAsync(
                    package.Context, package.Package.Manifest, cancellationToken);
            }
            else
            {
                validationError = "Package storage cannot account for declared files.";
            }
        }
        catch (FileNotFoundException)
        {
            validationError = "A declared file could not be read.";
        }
        catch (DirectoryNotFoundException)
        {
            validationError = "A declared file could not be read.";
        }
        catch (UnauthorizedAccessException)
        {
            validationError = "A declared file could not be read.";
        }
        catch (IOException)
        {
            validationError = "A declared file could not be read.";
        }
        catch (CryptographicException)
        {
            validationError = "A declared file could not be verified.";
        }

        var extractionRecords = new List<ScopedExtractionRecord>();
        var newExtractionRecords = new List<ScopedExtractionRecord>();
        var hadPreexistingExtractionRecords = stateStore.Read(state =>
            package.Package.Manifest.Any(document => state.ExtractionRecords.ContainsKey(
                DocumentKey(package.Context, document.DocumentId, document.Version))));
        IReadOnlyList<ApprovedRequirementVersion> approvedRequirements = [];
        if (validationError is null)
        {
            foreach (var document in package.Package.Manifest)
            {
                var existingRecord = stateStore.Read(state =>
                    state.ExtractionRecords.TryGetValue(
                        DocumentKey(package.Context, document.DocumentId, document.Version),
                        out var record)
                        ? record
                        : null);
                if (existingRecord is not null &&
                    existingRecord.ProcessingState == "complete" &&
                    StringComparer.OrdinalIgnoreCase.Equals(
                        existingRecord.Sha256,
                        document.Sha256) &&
                    ScopeMatches(existingRecord.Context, package.Context))
                {
                    extractionRecords.Add(existingRecord);
                    continue;
                }

                try
                {
                    var pageInventory = documentStorage is IPageInventoryStorage pageStorage
                        ? await pageStorage.GetPageInventoryAsync(
                            package.Context, document, cancellationToken)
                        : [1];
                    if (pageInventory.Count == 0)
                    {
                        throw new InvalidDataException(
                            "The declared document has no readable pages.");
                    }

                    var record = new ScopedExtractionRecord(
                        package.Context,
                        document.DocumentId,
                        document.Version,
                        document.Sha256,
                        ParserVersion,
                        ExtractorVersion,
                        pageInventory,
                        "complete",
                        attemptId,
                        attemptNumber);
                    extractionRecords.Add(record);
                    newExtractionRecords.Add(record);
                }
                catch (FileNotFoundException)
                {
                    var record = CreateFailedExtractionRecord(
                        package.Context, document, attemptId, attemptNumber, correlationId);
                    extractionRecords.Add(record);
                    newExtractionRecords.Add(record);
                }
                catch (DirectoryNotFoundException)
                {
                    var record = CreateFailedExtractionRecord(
                        package.Context, document, attemptId, attemptNumber, correlationId);
                    extractionRecords.Add(record);
                    newExtractionRecords.Add(record);
                }
                catch (UnauthorizedAccessException)
                {
                    var record = CreateFailedExtractionRecord(
                        package.Context, document, attemptId, attemptNumber, correlationId);
                    extractionRecords.Add(record);
                    newExtractionRecords.Add(record);
                }
                catch (IOException)
                {
                    var record = CreateFailedExtractionRecord(
                        package.Context, document, attemptId, attemptNumber, correlationId);
                    extractionRecords.Add(record);
                    newExtractionRecords.Add(record);
                }
                catch (InvalidDataException)
                {
                    var record = CreateFailedExtractionRecord(
                        package.Context, document, attemptId, attemptNumber, correlationId);
                    extractionRecords.Add(record);
                    newExtractionRecords.Add(record);
                }
            }

            var failedRecord = extractionRecords.FirstOrDefault(
                record => record.ProcessingState == "failed");
            if (failedRecord is not null)
            {
                validationError = "One or more declared documents could not be extracted.";
            }
        }

        if (validationError is null &&
            documentStorage is IApprovedRequirementStorage requirementStorage)
        {
            approvedRequirements =
                await requirementStorage.GetApprovedRequirementVersionsAsync(
                    package.Context, cancellationToken);
        }

        SafeError? processingError = validationError is null
            ? null
            : new SafeError("INVALID_PAYLOAD", correlationId);
        stateStore.Update(state =>
        {
            if (!state.Operations.TryGetValue(operationId, out var operation) ||
                !state.Packages.TryGetValue(
                    PackageKey(package.Context, package.Package.PackageId), out var persistedPackage))
            {
                return;
            }

            var attempts = (operation.Status.Attempts ?? []).ToList();
            var index = attempts.FindIndex(attempt => attempt.AttemptId == attemptId);
            if (index < 0)
            {
                return;
            }

            PersistExtractionAttempts(
                state,
                package,
                hadPreexistingExtractionRecords,
                newExtractionRecords);
            attempts[index] = attempts[index] with
            {
                Status = processingError is null ? "complete" : "failed",
                Error = processingError
            };
            if (processingError is null)
            {
                var assessmentError = PersistAssessment(
                    state,
                    package,
                    extractionRecords,
                    approvedRequirements);
                if (assessmentError is not null)
                {
                    processingError = new SafeError("INVALID_PAYLOAD", correlationId);
                    attempts[index] = attempts[index] with
                    {
                        Status = "failed",
                        Error = processingError
                    };
                }
            }
            if (processingError is not null)
            {
                PersistNonAutomaticDecision(
                    state,
                    $"PROCESSING-{package.OperationId}",
                    $"BASIS-{package.OperationId}",
                    "processing_incomplete",
                    "internal_review");
            }
            var finalStatus = operation.Status with
            {
                Status = processingError is null ? "complete" : "failed",
                Error = processingError,
                Attempts = attempts
            };
            state.Operations[operationId] = operation with { Status = finalStatus };
            state.Packages[PackageKey(package.Context, package.Package.PackageId)] =
                persistedPackage with
                {
                    ProcessingStatus = finalStatus.Status,
                    Error = processingError
                };

            var caseKey = CaseKey(package.Context);
            if (state.Cases.TryGetValue(caseKey, out var persistedCase))
            {
                state.Cases[caseKey] = persistedCase with
                {
                    Status = processingError is null ? "active" : "blocked"
                };
            }
        });

        if (processingError is null && investigationGateway is not null)
        {
            var basis = stateStore.Read(state =>
                state.EvidenceBases.TryGetValue($"BASIS-{package.OperationId}", out var value)
                    ? value
                    : null);
            var investigation = basis is null
                ? new InvestigationOutcome(
                    $"BASIS-{package.OperationId}",
                    package.Context,
                    "blocked",
                    [],
                    new SafeError("INVESTIGATION_BASIS_NOT_FOUND", correlationId),
                    correlationId)
                : await InvestigateBasisAsync(basis, correlationId, cancellationToken);

            stateStore.Update(state =>
            {
                state.Investigations[investigation.BasisId] = investigation;
                if (investigation.Status == "complete")
                {
                    var derivedFindings = FindingDerivation.Derive(basis!, investigation);
                    foreach (var finding in derivedFindings)
                    {
                        state.Findings[FindingKey(finding)] = finding;
                    }
                    PersistPolicyDecisions(
                        state,
                        basis!,
                        derivedFindings,
                        package.OperationId,
                        correlationId);
                    PersistReviewTasks(state, basis!, derivedFindings);
                }
                else
                {
                    PersistNonAutomaticDecision(
                        state,
                        $"INVESTIGATION-{investigation.BasisId}",
                        investigation.BasisId,
                        investigation.Error?.SafeCode == "INVESTIGATION_OUTPUT_INVALID"
                            ? "unsupported_interpretation"
                            : "investigation_blocked",
                        "internal_review");
                }
                if (investigation.Status == "blocked" &&
                    state.Cases.TryGetValue(CaseKey(package.Context), out var persistedCase))
                {
                    state.Cases[CaseKey(package.Context)] =
                        persistedCase with { Status = "blocked" };
                }
            });
        }

        return Results.Json(
            new OperationAccepted(operationId, package.Context.CaseId, receipt.ReceiptId),
            statusCode: 202);
    }

    private static string? PersistAssessment(
        PersistedState state,
        PersistedPackage package,
        IReadOnlyList<ScopedExtractionRecord> records,
        IReadOnlyList<ApprovedRequirementVersion> approvedRequirements)
    {
        if (!state.Cases.TryGetValue(CaseKey(package.Context), out var persistedCase))
        {
            return "The package case is not persisted.";
        }

        var expectedKeys = package.Package.Manifest
            .Select(document => DocumentKey(package.Context, document.DocumentId, document.Version))
            .ToArray();
        var existingRecords = expectedKeys
            .Where(state.ExtractionRecords.ContainsKey)
            .Select(key => state.ExtractionRecords[key])
            .ToArray();
        if (existingRecords.Length > 0)
        {
            if (existingRecords.Length != expectedKeys.Length)
            {
                return "Every declared document must have a terminal extraction record.";
            }
        }
        else
        {
            foreach (var record in records)
            {
                state.ExtractionRecords.Add(
                    DocumentKey(package.Context, record.DocumentId, record.Version),
                    record);
            }
        }

        foreach (var document in package.Package.Manifest)
        {
            var key = DocumentKey(package.Context, document.DocumentId, document.Version);
            if (!state.ExtractionRecords.TryGetValue(key, out var record) ||
                !ScopeMatches(record.Context, package.Context) ||
                record.DocumentId != document.DocumentId ||
                record.Version != document.Version ||
                !StringComparer.OrdinalIgnoreCase.Equals(record.Sha256, document.Sha256) ||
                record.ProcessingState != "complete")
            {
                return "Every declared document must have a matching terminal extraction record.";
            }
        }

        var basisId = $"BASIS-{package.OperationId}";
        if (!state.EvidenceBases.ContainsKey(basisId))
        {
            state.EvidenceBases.Add(
                basisId,
                new EvidenceBasis(
                    basisId,
                    package.Context,
                    package.Package.Manifest
                        .Select(document => new EvidenceDocumentVersion(
                            document.DocumentId,
                            document.Version,
                            document.Sha256))
                        .ToArray(),
                    approvedRequirements.ToArray(),
                    persistedCase.CaseRevision,
                    DateTimeOffset.UtcNow,
                    ParserVersion,
                    ExtractorVersion));
        }

        return null;
    }

    private void PersistPolicyDecisions(
        PersistedState state,
        EvidenceBasis basis,
        IReadOnlyList<Finding> findings,
        string operationId,
        string correlationId)
    {
        foreach (var finding in findings)
        {
            var reasonCodes = new List<string>();
            var outcome = "no_action";
            var canAutomaticallyRequest = finding.Assessment == "missing";

            if (finding.Assessment == "ambiguous")
            {
                outcome = "internal_review";
                reasonCodes.Add("identity_ambiguous");
            }
            else if (finding.Assessment == "conflicting")
            {
                outcome = "internal_review";
                reasonCodes.Add("conflicting_evidence");
            }
            else if (finding.Assessment == "satisfied")
            {
                reasonCodes.Add("evidence_located");
            }
            else if (finding.Assessment != "missing")
            {
                outcome = "internal_review";
                reasonCodes.Add("unsupported_assessment");
                canAutomaticallyRequest = false;
            }

            if (canAutomaticallyRequest && finding.EvidenceRefs.Count > 0)
            {
                reasonCodes.Add("evidence_located");
                canAutomaticallyRequest = false;
            }

            var approvedRequirement = basis.ApprovedRequirementVersions
                .Where(requirement => requirement.RequirementId == finding.RequirementId)
                .ToArray();
            if (canAutomaticallyRequest &&
                (approvedRequirement.Length != 1 ||
                 string.IsNullOrWhiteSpace(approvedRequirement[0].ComponentId)))
            {
                reasonCodes.Add("requirement_not_approved");
                canAutomaticallyRequest = false;
            }
            else if (canAutomaticallyRequest &&
                !StringComparer.Ordinal.Equals(
                    approvedRequirement[0].ComponentId,
                    finding.ComponentId))
            {
                reasonCodes.Add("identity_ambiguous");
                canAutomaticallyRequest = false;
            }

            if (canAutomaticallyRequest &&
                !IsCompleteEvidenceBasis(state, basis, operationId))
            {
                reasonCodes.Add("processing_incomplete");
                canAutomaticallyRequest = false;
            }

            if (canAutomaticallyRequest &&
                !automaticRequestPolicy.ApprovedRecipientRefs.Contains(
                    automaticRequestPolicy.RecipientRef))
            {
                reasonCodes.Add("recipient_not_approved");
                canAutomaticallyRequest = false;
            }

            if (canAutomaticallyRequest &&
                !automaticRequestPolicy.ApprovedTemplateVersions.Contains(
                    automaticRequestPolicy.TemplateVersion))
            {
                reasonCodes.Add("template_not_approved");
                canAutomaticallyRequest = false;
            }

            var requestKey = RequestKey(basis.Context, finding);
            var activeRequest = state.EvidenceRequests.Values.Any(request =>
                request.RequestKey == requestKey &&
                request.Status is not ("closed" or "cancelled"));
            if (canAutomaticallyRequest && activeRequest)
            {
                reasonCodes.Add("active_request_exists");
                canAutomaticallyRequest = false;
            }

            if (canAutomaticallyRequest)
            {
                outcome = "auto_request";
                reasonCodes.Add("missing_evidence");
            }

            var decision = new PolicyDecision(
                NewId("POLICY"),
                finding.FindingId,
                basis.BasisId,
                "automatic-request/1.0",
                outcome,
                reasonCodes.Distinct(StringComparer.Ordinal).ToArray(),
                DateTimeOffset.UtcNow);
            state.PolicyDecisions[PolicyDecisionKey(basis.BasisId, finding.FindingId)] = decision;

            if (!canAutomaticallyRequest)
            {
                continue;
            }

            var requestId = NewId("REQUEST");
            var createdAt = decision.EvaluatedAt;
            var evidenceRequest = new EvidenceRequest(
                requestId,
                requestKey,
                finding.FindingId,
                basis.BasisId,
                finding.RequirementId,
                automaticRequestPolicy.RecipientRef,
                automaticRequestPolicy.TemplateVersion,
                $"We could not locate {finding.RequirementId} evidence in the submitted package. " +
                "Please provide it or identify its location.",
                "pending",
                createdAt);
            state.EvidenceRequests[requestId] = evidenceRequest;

            var intentId = NewId("OUTBOX");
            state.DispatchOutbox[intentId] = new DispatchOutboxIntent(
                intentId,
                requestId,
                requestKey,
                "pending",
                createdAt);

            var auditId = NewId("AUDIT");
            (state.AuditEntries ??= new(StringComparer.Ordinal))[auditId] = new AuditEntry(
                auditId,
                basis.Context,
                "system_policy",
                "automatic-request-policy",
                "policy.auto_request",
                [decision.DecisionId, requestId, intentId],
                basis.BasisId,
                createdAt,
                correlationId);
        }
    }

    private static void PersistReviewTasks(
        PersistedState state,
        EvidenceBasis basis,
        IReadOnlyList<Finding> findings)
    {
        var eligibleFindings = findings
            .Where(finding => finding.BasisId == basis.BasisId)
            .Where(finding => finding.Assessment is "ambiguous" or "conflicting")
            .OrderBy(finding => finding.FindingId, StringComparer.Ordinal)
            .ToArray();
        if (eligibleFindings.Length == 0)
        {
            return;
        }

        var tasks = state.ReviewTasks ??= new(StringComparer.Ordinal);
        var persistedCase = state.Cases.TryGetValue(CaseKey(basis.Context), out var currentCase)
            ? currentCase
            : null;
        var created = false;
        foreach (var finding in eligibleFindings)
        {
            if (tasks.Values.Any(task =>
                    task.BasisId == basis.BasisId &&
                    task.FindingId == finding.FindingId))
            {
                continue;
            }

            var task = new ReviewTask(
                NewId("TASK"),
                finding.FindingId,
                basis.BasisId,
                finding.ReasonCode,
                "open");
            tasks[task.TaskId] = task;
            created = true;
        }

        if (persistedCase is not null)
        {
            if (created)
            {
                state.Cases[CaseKey(basis.Context)] = persistedCase with
                {
                    CaseRevision = checked(persistedCase.CaseRevision + 1)
                };
            }
        }
    }

    private static void PersistNonAutomaticDecision(
        PersistedState state,
        string findingId,
        string basisId,
        string reasonCode,
        string outcome)
    {
        var decisionKey = PolicyDecisionKey(basisId, findingId);
        if (state.PolicyDecisions.ContainsKey(decisionKey))
        {
            return;
        }

        state.PolicyDecisions[decisionKey] = new PolicyDecision(
            NewId("POLICY"),
            findingId,
            basisId,
            "automatic-request/1.0",
            outcome,
            [reasonCode],
            DateTimeOffset.UtcNow);
    }

    private static bool IsCompleteEvidenceBasis(
        PersistedState state,
        EvidenceBasis basis,
        string operationId)
    {
        if (!state.Operations.TryGetValue(operationId, out var operation) ||
            operation.Status.Status != "complete")
        {
            return false;
        }

        return basis.DocumentInventory.All(document =>
        {
            var key = DocumentKey(
                basis.Context,
                document.DocumentId,
                document.Version);
            return state.ExtractionRecords.TryGetValue(key, out var record) &&
                ScopeMatches(record.Context, basis.Context) &&
                record.ProcessingState == "complete" &&
                StringComparer.OrdinalIgnoreCase.Equals(record.Sha256, document.Sha256) &&
                record.PageInventory.Count > 0;
        });
    }

    private static string RequestKey(CaseContext context, Finding finding) =>
        $"{CaseKey(context)}:{finding.RequirementId}:{finding.ComponentId}";

    private static string PolicyDecisionKey(string basisId, string findingId) =>
        $"{basisId}:{findingId}";

    private static void PersistExtractionAttempts(
        PersistedState state,
        PersistedPackage package,
        bool hadPreexistingExtractionRecords,
        IReadOnlyList<ScopedExtractionRecord> records)
    {
        if (hadPreexistingExtractionRecords &&
            !package.Package.Manifest.Any(document =>
                state.ExtractionRecords.TryGetValue(
                    DocumentKey(package.Context, document.DocumentId, document.Version),
                    out var existing) &&
                existing.AttemptId is not null))
        {
            return;
        }

        foreach (var record in records)
        {
            var key = DocumentKey(package.Context, record.DocumentId, record.Version);
            if (state.ExtractionRecords.TryGetValue(key, out var existing) &&
                existing.AttemptId is null)
            {
                continue;
            }

            state.ExtractionRecords[key] = record;
            state.ExtractionAttempts[
                $"{key}:attempt-{record.AttemptNumber}"] = record;
        }
    }

    private static ScopedExtractionRecord CreateFailedExtractionRecord(
        CaseContext context,
        DocumentMetadata document,
        string attemptId,
        int attemptNumber,
        string correlationId) =>
        new(
            context,
            document.DocumentId,
            document.Version,
            document.Sha256,
            ParserVersion,
            ExtractorVersion,
            [],
            "failed",
            attemptId,
            attemptNumber,
            new SafeError("INVALID_PAYLOAD", correlationId));

    public async Task<IResult> ProcessPackageAsync(
        string packageId,
        bool isRetry,
        bool startOnly,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var caller = CallerScopeParser.Parse(httpContext.Request.Headers.Authorization);
        if (caller is null)
        {
            return Results.Json(
                new SafeError("AUTHENTICATION_REQUIRED", NewCorrelationId()), statusCode: 401);
        }

        var operationId = stateStore.Read(state =>
            state.Packages.Values
                .Where(package => package.Package.PackageId == packageId)
                .Where(package => caller.Matches(package.Context))
                .Select(package => package.OperationId)
                .FirstOrDefault());
        return operationId is null
            ? Results.Json(new SafeError("OPERATION_NOT_FOUND", NewCorrelationId()), statusCode: 404)
            : await ProcessOperationAsync(
                operationId, isRetry, startOnly, httpContext, cancellationToken);
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
            var investigation = state.Investigations.Values
                .Where(candidate => ScopeMatches(candidate.Context, persistedCase.Context))
                .OrderByDescending(candidate => candidate.BasisId, StringComparer.Ordinal)
                .FirstOrDefault();
            var openReviewTasks = state.ReviewTasks?.Values
                .Where(task => task.Status == "open")
                .Where(task => state.EvidenceBases.TryGetValue(task.BasisId, out var basis) &&
                    ScopeMatches(basis.Context, persistedCase.Context) &&
                    state.Findings.TryGetValue(FindingKeyForTask(task), out var finding) &&
                    finding.BasisId == task.BasisId)
                .OrderBy(task => task.TaskId, StringComparer.Ordinal)
                .ToArray() ?? [];
            var activeEvidenceRequests = state.EvidenceRequests.Values
                .Where(request => request.Status is not ("closed" or "cancelled"))
                .Where(request => state.EvidenceBases.TryGetValue(request.BasisId, out var basis) &&
                    ScopeMatches(basis.Context, persistedCase.Context))
                .OrderBy(request => request.CreatedAt)
                .ToArray();
            return new CaseSummary(
                persistedCase.Context.CaseId,
                persistedCase.Context.RunId,
                persistedCase.Context.AirlineId,
                persistedCase.Context.AircraftId,
                persistedCase.Context.LeaseId,
                persistedCase.CaseRevision,
                CoordinationStatus(
                    state,
                    persistedCase,
                    investigation,
                    openReviewTasks,
                    activeEvidenceRequests),
                packages,
                investigation,
                openReviewTasks,
                activeEvidenceRequests);
        });

        return Task.FromResult<IResult>(summary is null
            ? Results.Json(new SafeError("CASE_NOT_FOUND", NewCorrelationId()), statusCode: 404)
            : Results.Ok(summary));
    }

    public IResult GetReviewTasks(string caseId, HttpContext httpContext)
    {
        var correlationId = NewCorrelationId();
        var caller = CallerScopeParser.Parse(httpContext.Request.Headers.Authorization);
        if (caller is null)
        {
            return Results.Json(
                new SafeError("AUTHENTICATION_REQUIRED", correlationId), statusCode: 401);
        }

        var tasks = stateStore.Read(state =>
        {
            if (!state.Cases.TryGetValue(CaseKey(caller, caseId), out var persistedCase) ||
                !caller.Matches(persistedCase.Context))
            {
                return null;
            }

            return ReviewTasksForCase(state, persistedCase);
        });
        return tasks is null
            ? Results.Json(new SafeError("CASE_NOT_FOUND", correlationId), statusCode: 404)
            : Results.Ok(tasks);
    }

    public IResult GetReviewTask(
        string caseId,
        string taskId,
        HttpContext httpContext)
    {
        var correlationId = NewCorrelationId();
        var caller = CallerScopeParser.Parse(httpContext.Request.Headers.Authorization);
        if (caller is null)
        {
            return Results.Json(
                new SafeError("AUTHENTICATION_REQUIRED", correlationId), statusCode: 401);
        }

        var task = stateStore.Read(state =>
        {
            if (!state.Cases.TryGetValue(CaseKey(caller, caseId), out var persistedCase) ||
                !caller.Matches(persistedCase.Context) ||
                state.ReviewTasks is null ||
                !state.ReviewTasks.TryGetValue(taskId, out var candidate) ||
                !TaskBelongsToCase(state, candidate, persistedCase.Context))
            {
                return null;
            }

            return candidate;
        });
        return task is null
            ? Results.Json(new SafeError("CASE_NOT_FOUND", correlationId), statusCode: 404)
            : Results.Ok(task);
    }

    public IResult DeriveReviewTasks(string caseId, HttpContext httpContext)
    {
        var correlationId = NewCorrelationId();
        var caller = CallerScopeParser.Parse(httpContext.Request.Headers.Authorization);
        if (caller is null)
        {
            return Results.Json(
                new SafeError("AUTHENTICATION_REQUIRED", correlationId), statusCode: 401);
        }

        ReviewTask[]? tasks = null;
        SafeError? failure = null;
        stateStore.Update(state =>
        {
            if (!state.Cases.TryGetValue(CaseKey(caller, caseId), out var persistedCase) ||
                !caller.Matches(persistedCase.Context))
            {
                failure = new SafeError("CASE_NOT_FOUND", correlationId);
                return;
            }

            var basis = CurrentBasis(state, persistedCase.Context);
            if (basis is not null)
            {
                var findings = state.Findings.Values
                    .Where(finding => finding.BasisId == basis.BasisId)
                    .ToArray();
                PersistReviewTasks(state, basis, findings);
            }

            tasks = ReviewTasksForCase(state, persistedCase).ToArray();
        });

        return failure is null
            ? Results.Ok(tasks ?? [])
            : Results.Json(failure, statusCode: 404);
    }

    public IResult GetMockInbox(HttpContext httpContext)
    {
        var caller = CallerScopeParser.Parse(httpContext.Request.Headers.Authorization);
        if (caller is null)
        {
            return Results.Json(
                new SafeError("AUTHENTICATION_REQUIRED", NewCorrelationId()), statusCode: 401);
        }

        var items = stateStore.Read(state => state.MockInbox.Values
            .Where(item => caller.Matches(item.Context))
            .OrderBy(item => item.DeliveredAt)
            .ToArray());
        return Results.Ok(items);
    }

    public Task<IResult> DispatchAsync(
        DispatchCommand? command,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var correlationId = NewCorrelationId();
        var caller = CallerScopeParser.Parse(httpContext.Request.Headers.Authorization);
        if (caller is null)
        {
            return Task.FromResult<IResult>(Results.Json(
                new SafeError("AUTHENTICATION_REQUIRED", correlationId), statusCode: 401));
        }

        var commandProvided = command is not null;
        command ??= new DispatchCommand();
        if (command.RequestId is not null && !IsValidId(command.RequestId) ||
            command.Acknowledgement is not null &&
            command.Acknowledgement is not
                ("acknowledged" or "indeterminate" or "absent" or "none") ||
            command.LeaseDurationSeconds is < 1 or > 300)
        {
            return Task.FromResult<IResult>(Results.Json(
                new SafeError("INVALID_PAYLOAD", correlationId), statusCode: 400));
        }

        var now = DateTimeOffset.UtcNow;
        var leaseDuration = TimeSpan.FromSeconds(command.LeaseDurationSeconds ?? 60);
        string? intentId = null;
        string? requestId = null;
        string? attemptId = null;
        string? leaseId = null;
        SafeError? failure = null;
        var leaseExpiresAt = now.Add(leaseDuration);

        stateStore.Update(state =>
        {
            var candidate = state.DispatchOutbox.Values
                .Where(intent => command.RequestId is null || intent.RequestId == command.RequestId)
                .Where(intent =>
                    intent.Status is "pending" or "delivery_unknown" ||
                    intent.Status == "leased" &&
                    (intent.LeaseExpiresAt is null || intent.LeaseExpiresAt <= now))
                .Select(intent => state.EvidenceRequests.TryGetValue(intent.RequestId, out var request)
                    ? (Intent: intent, Request: request)
                    : ((DispatchOutboxIntent Intent, EvidenceRequest Request)?)null)
                .Where(candidate => candidate is not null)
                .Select(candidate => candidate!.Value)
                .Where(candidate =>
                    IsRequestInCallerScope(state, candidate.Request, caller))
                .OrderBy(candidate => candidate.Intent.CreatedAt)
                .FirstOrDefault();

            if (candidate == default)
            {
                return;
            }

            intentId = candidate.Intent.IntentId;
            requestId = candidate.Request.RequestId;
            leaseId = NewId("LEASE");
            attemptId = NewId("DELIVERY");
            state.DispatchOutbox[intentId] = candidate.Intent with
            {
                Status = "leased",
                LeaseId = leaseId,
                LeaseExpiresAt = leaseExpiresAt
            };
            state.DeliveryAttempts[attemptId] = new DeliveryAttempt(
                attemptId,
                intentId,
                requestId,
                leaseId,
                "leased",
                now,
                correlationId);
        });

        if (intentId is null || requestId is null || attemptId is null || leaseId is null)
        {
            return Task.FromResult<IResult>(Results.Ok(
                new DispatchResult("no_work", Reason: command.RequestId is null
                    ? "No eligible dispatch intent is available."
                    : "The requested dispatch intent is not eligible.")));
        }

        var acknowledgement = commandProvided
            ? command.Acknowledgement ?? "absent"
            : "acknowledged";
        DispatchResult? result = null;
        stateStore.Update(state =>
        {
            if (!state.DispatchOutbox.TryGetValue(intentId, out var intent) ||
                !state.EvidenceRequests.TryGetValue(requestId, out var request) ||
                intent.LeaseId != leaseId ||
                !IsRequestInCallerScope(state, request, caller))
            {
                failure = new SafeError("DISPATCH_LEASE_LOST", correlationId);
                return;
            }

            var existingItem = state.MockInbox.Values
                .SingleOrDefault(item => item.RequestId == request.RequestId);
            var isCurrentAndEligible = IsDispatchEligible(
                state, request, automaticRequestPolicy);
            if (!isCurrentAndEligible && existingItem is null)
            {
                var obsoleteReason = DispatchObsoleteReason(state, request);
                if (request.Status == "pending")
                {
                    state.EvidenceRequests[request.RequestId] = request with
                    {
                        Status = "cancelled",
                        ClosureReason = $"obsolete:{obsoleteReason}"
                    };
                    request = state.EvidenceRequests[request.RequestId];
                }
                state.DispatchOutbox[intent.IntentId] = intent with
                {
                    Status = "obsolete",
                    LeaseId = null,
                    LeaseExpiresAt = null
                };
                state.DeliveryAttempts[attemptId] = state.DeliveryAttempts[attemptId] with
                {
                    Status = "obsolete",
                    Reason = obsoleteReason
                };
                AddDispatchAudit(
                    state,
                    request,
                    "dispatch.obsolete",
                    [request.RequestId, intent.IntentId, attemptId],
                    correlationId,
                    obsoleteReason);
                result = new DispatchResult(
                    "obsolete",
                    request.RequestId,
                    intent.IntentId,
                    attemptId,
                    Reason: obsoleteReason);
                return;
            }

            var item = existingItem ?? new MockInboxItem(
                NewId("INBOX"),
                request.RequestId,
                request.RequestKey,
                RequestContext(state, request),
                request.RecipientRef,
                request.TemplateVersion,
                request.Message,
                now);
            if (existingItem is null)
            {
                state.MockInbox[item.ItemId] = item;
            }

            if (acknowledgement == "acknowledged")
            {
                state.EvidenceRequests[request.RequestId] = request with
                {
                    Status = "delivered",
                    ClosureReason = null
                };
                state.DispatchOutbox[intent.IntentId] = intent with
                {
                    Status = "delivered",
                    LeaseId = null,
                    LeaseExpiresAt = null
                };
                state.DeliveryAttempts[attemptId] = state.DeliveryAttempts[attemptId] with
                {
                    Status = "acknowledged"
                };
                AddDispatchAudit(
                    state,
                    request,
                    "dispatch.delivered",
                    [request.RequestId, intent.IntentId, item.ItemId],
                    correlationId);
                result = new DispatchResult(
                    "delivered",
                    request.RequestId,
                    intent.IntentId,
                    attemptId,
                    item.ItemId);
            }
            else
            {
                state.EvidenceRequests[request.RequestId] = request with
                {
                    Status = "delivery_unknown",
                    ClosureReason = "delivery_acknowledgement_unavailable"
                };
                state.DispatchOutbox[intent.IntentId] = intent with
                {
                    Status = "delivery_unknown",
                    LeaseId = null,
                    LeaseExpiresAt = null
                };
                state.DeliveryAttempts[attemptId] = state.DeliveryAttempts[attemptId] with
                {
                    Status = "delivery_unknown",
                    Reason = "delivery_acknowledgement_unavailable"
                };
                AddDispatchAudit(
                    state,
                    request,
                    "dispatch.delivery_unknown",
                    [request.RequestId, intent.IntentId, attemptId],
                    correlationId,
                    "delivery_acknowledgement_unavailable");
                result = new DispatchResult(
                    "delivery_unknown",
                    request.RequestId,
                    intent.IntentId,
                    attemptId,
                    item.ItemId,
                    "delivery_acknowledgement_unavailable");
            }
        });

        return Task.FromResult<IResult>(failure is null
            ? Results.Ok(result!)
            : Results.Json(failure, statusCode: 409));
    }

    public Task<IResult> SubmitReviewAsync(
        string caseId,
        ReviewCommand? command,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var correlationId = NewCorrelationId();
        var caller = CallerScopeParser.Parse(httpContext.Request.Headers.Authorization);
        if (caller is null)
        {
            return Task.FromResult<IResult>(Results.Json(
                new SafeError("AUTHENTICATION_REQUIRED", correlationId), statusCode: 401));
        }

        if (command is null ||
            !IsValidId(command.FindingId) ||
            !IsValidId(command.BasisId) ||
            command.Decision is not
                ("accept_evidence" or "dismiss_finding" or "needs_evidence") ||
            string.IsNullOrWhiteSpace(command.Reason) ||
            command.Reason.Length > 4000 ||
            !TryParseExpectedRevision(httpContext.Request.Headers.IfMatch, out var expectedRevision))
        {
            return Task.FromResult<IResult>(Results.Json(
                new SafeError("INVALID_PAYLOAD", correlationId), statusCode: 400));
        }

        ReviewDecision? review = null;
        SafeError? failure = null;
        var statusCode = 200;
        stateStore.Update(state =>
        {
            var persistedCase = state.Cases.Values.SingleOrDefault(candidate =>
                candidate.Context.CaseId == caseId &&
                caller.Matches(candidate.Context));
            if (persistedCase is null)
            {
                failure = new SafeError("CASE_NOT_FOUND", correlationId);
                statusCode = 404;
                return;
            }

            if (persistedCase.CaseRevision != expectedRevision)
            {
                failure = new SafeError("STALE_PRECONDITION", correlationId);
                statusCode = 412;
                return;
            }

            var basis = state.EvidenceBases.Values
                .Where(candidate =>
                    ScopeMatches(candidate.Context, persistedCase.Context))
                .OrderByDescending(candidate => candidate.CaseRevision)
                .ThenByDescending(candidate => candidate.CreatedAt)
                .FirstOrDefault();
            if (basis is null ||
                !StringComparer.Ordinal.Equals(basis.BasisId, command.BasisId))
            {
                failure = new SafeError("STALE_PRECONDITION", correlationId);
                statusCode = 412;
                return;
            }

            var finding = state.Findings.Values.SingleOrDefault(candidate =>
                StringComparer.Ordinal.Equals(candidate.FindingId, command.FindingId) &&
                StringComparer.Ordinal.Equals(candidate.BasisId, basis.BasisId));
            if (finding is null)
            {
                failure = new SafeError("CASE_NOT_FOUND", correlationId);
                statusCode = 404;
                return;
            }

            var reviewId = NewId("REVIEW");
            var decidedAt = DateTimeOffset.UtcNow;
            review = new ReviewDecision(
                reviewId,
                finding.FindingId,
                basis.BasisId,
                command.Decision,
                caller.Subject,
                command.Reason.Trim(),
                decidedAt);
            (state.ReviewDecisions ??= new(StringComparer.Ordinal)).Add(reviewId, review);
            (state.FindingDispositions ??= new(StringComparer.Ordinal))[$"{basis.BasisId}:{finding.FindingId}"] =
                new FindingDisposition(
                    finding.FindingId,
                    basis.BasisId,
                    command.Decision switch
                    {
                        "accept_evidence" => "accepted",
                        "dismiss_finding" => "dismissed",
                        _ => "needs_evidence"
                    },
                    reviewId);
            if (state.ReviewTasks is not null)
            {
                var linkedTask = state.ReviewTasks.Values
                    .Where(task => task.BasisId == basis.BasisId &&
                        task.FindingId == finding.FindingId)
                    .OrderByDescending(task => task.Status == "open")
                    .ThenBy(task => task.TaskId, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (linkedTask is not null)
                {
                    state.ReviewTasks[linkedTask.TaskId] = linkedTask with
                    {
                        Status = command.Decision == "needs_evidence" ? "open" : "completed"
                    };
                }
            }
            var auditId = NewId("AUDIT");
            (state.AuditEntries ??= new(StringComparer.Ordinal)).Add(
                auditId,
                new AuditEntry(
                    auditId,
                    persistedCase.Context,
                    "human_reviewer",
                    caller.Subject,
                    $"review.{command.Decision}",
                    [finding.FindingId],
                    basis.BasisId,
                    decidedAt,
                    correlationId));
            var nextCase = persistedCase with
            {
                CaseRevision = checked(persistedCase.CaseRevision + 1),
                Status = ReviewCaseStatus(state, persistedCase.Context)
            };
            state.Cases[CaseKey(persistedCase.Context)] = nextCase;
        });

        return Task.FromResult<IResult>(failure is null
            ? Results.Ok(review)
            : Results.Json(failure, statusCode: statusCode));
    }

    public Task<IResult> CompleteReviewTaskAsync(
        string caseId,
        string taskId,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var correlationId = NewCorrelationId();
        var caller = CallerScopeParser.Parse(httpContext.Request.Headers.Authorization);
        if (caller is null)
        {
            return Task.FromResult<IResult>(Results.Json(
                new SafeError("AUTHENTICATION_REQUIRED", correlationId), statusCode: 401));
        }

        if (!IsValidId(taskId) ||
            !TryParseExpectedRevision(httpContext.Request.Headers.IfMatch, out var expectedRevision))
        {
            return Task.FromResult<IResult>(Results.Json(
                new SafeError("INVALID_PAYLOAD", correlationId), statusCode: 400));
        }

        ReviewTask? completedTask = null;
        SafeError? failure = null;
        var statusCode = 200;
        stateStore.Update(state =>
        {
            var persistedCase = state.Cases.Values.SingleOrDefault(candidate =>
                candidate.Context.CaseId == caseId &&
                caller.Matches(candidate.Context));
            if (persistedCase is null)
            {
                failure = new SafeError("CASE_NOT_FOUND", correlationId);
                statusCode = 404;
                return;
            }

            if (persistedCase.CaseRevision != expectedRevision)
            {
                failure = new SafeError("STALE_PRECONDITION", correlationId);
                statusCode = 412;
                return;
            }

            if (state.ReviewTasks is null ||
                !state.ReviewTasks.TryGetValue(taskId, out var task))
            {
                failure = new SafeError("CASE_NOT_FOUND", correlationId);
                statusCode = 404;
                return;
            }

            var basis = state.EvidenceBases.Values
                .Where(candidate => ScopeMatches(candidate.Context, persistedCase.Context))
                .OrderByDescending(candidate => candidate.CaseRevision)
                .ThenByDescending(candidate => candidate.CreatedAt)
                .FirstOrDefault();
            if (basis is null ||
                basis.BasisId != task.BasisId ||
                !TaskBelongsToCase(state, task, persistedCase.Context))
            {
                failure = new SafeError("STALE_PRECONDITION", correlationId);
                statusCode = 412;
                return;
            }

            if (task.Status == "completed")
            {
                completedTask = task;
                return;
            }

            if (task.Status != "open")
            {
                failure = new SafeError("INVALID_PAYLOAD", correlationId);
                statusCode = 400;
                return;
            }

            completedTask = task with { Status = "completed" };
            state.ReviewTasks[taskId] = completedTask;
            state.Cases[CaseKey(persistedCase.Context)] = persistedCase with
            {
                CaseRevision = checked(persistedCase.CaseRevision + 1)
            };
        });

        return Task.FromResult<IResult>(failure is null
            ? Results.Ok(completedTask)
            : Results.Json(failure, statusCode: statusCode));
    }

    public Task<IResult> AssignReviewTaskAsync(
        string caseId,
        string taskId,
        ReviewTaskAssignment? assignment,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var correlationId = NewCorrelationId();
        var caller = CallerScopeParser.Parse(httpContext.Request.Headers.Authorization);
        if (caller is null)
        {
            return Task.FromResult<IResult>(Results.Json(
                new SafeError("AUTHENTICATION_REQUIRED", correlationId), statusCode: 401));
        }

        if (!IsValidId(taskId) ||
            assignment is null ||
            !IsValidId(assignment.ReviewerSubject) ||
            !TryParseExpectedRevision(httpContext.Request.Headers.IfMatch, out var expectedRevision))
        {
            return Task.FromResult<IResult>(Results.Json(
                new SafeError("INVALID_PAYLOAD", correlationId), statusCode: 400));
        }

        ReviewTask? assignedTask = null;
        SafeError? failure = null;
        var statusCode = 200;
        stateStore.Update(state =>
        {
            var persistedCase = state.Cases.Values.SingleOrDefault(candidate =>
                candidate.Context.CaseId == caseId &&
                caller.Matches(candidate.Context));
            if (persistedCase is null)
            {
                failure = new SafeError("CASE_NOT_FOUND", correlationId);
                statusCode = 404;
                return;
            }

            if (persistedCase.CaseRevision != expectedRevision)
            {
                failure = new SafeError("STALE_PRECONDITION", correlationId);
                statusCode = 412;
                return;
            }

            if (state.ReviewTasks is null ||
                !state.ReviewTasks.TryGetValue(taskId, out var task) ||
                !TaskBelongsToCase(state, task, persistedCase.Context))
            {
                failure = new SafeError("CASE_NOT_FOUND", correlationId);
                statusCode = 404;
                return;
            }

            if (task.Status != "open")
            {
                failure = new SafeError("INVALID_PAYLOAD", correlationId);
                statusCode = 400;
                return;
            }

            assignedTask = task with
            {
                AssignedReviewerSubject = assignment.ReviewerSubject
            };
            state.ReviewTasks[taskId] = assignedTask;
            state.Cases[CaseKey(persistedCase.Context)] = persistedCase with
            {
                CaseRevision = checked(persistedCase.CaseRevision + 1)
            };
            var auditId = NewId("AUDIT");
            (state.AuditEntries ??= new(StringComparer.Ordinal))[auditId] = new AuditEntry(
                auditId,
                persistedCase.Context,
                "human_reviewer",
                caller.Subject,
                "review-task.assigned",
                [taskId, assignment.ReviewerSubject],
                task.BasisId,
                DateTimeOffset.UtcNow,
                correlationId);
        });

        return Task.FromResult<IResult>(failure is null
            ? Results.Ok(assignedTask)
            : Results.Json(failure, statusCode: statusCode));
    }

    private async Task<InvestigationOutcome> InvestigateBasisAsync(
        EvidenceBasis basis,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var pageSources = stateStore.Read(state =>
            basis.DocumentInventory
                .Select(document =>
                {
                    var key = DocumentKey(
                        basis.Context, document.DocumentId, document.Version);
                    return state.ExtractionRecords.TryGetValue(key, out var extraction) &&
                        ScopeMatches(extraction.Context, basis.Context) &&
                        extraction.ProcessingState == "complete"
                        ? (Document: document, Pages: extraction.PageInventory)
                        : (Document: document, Pages: (IReadOnlyList<int>)[]);
                })
                .ToArray());
        var content = new List<InvestigationDocumentContent>();
        foreach (var source in pageSources)
        {
            foreach (var page in source.Pages)
            {
                var locator =
                    $"{basis.Context.RunId}/{basis.Context.CaseId}/" +
                    $"{source.Document.DocumentId}/v{source.Document.Version}";
                var text = await documentStorage.ReadPagePreviewAsync(
                    locator, page, cancellationToken);
                if (text is not null)
                {
                    content.Add(new InvestigationDocumentContent(
                        basis.Context,
                        source.Document.DocumentId,
                        source.Document.Version,
                        page,
                        text));
                }
            }
        }

        return await investigationGateway!.InvestigateAsync(
            basis,
            content,
            correlationId,
            cancellationToken);
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
            packageId.ValueKind != JsonValueKind.String ||
            packageId.GetString() != request.Package.PackageId ||
            request.Event.CaseId != request.Package.CaseId ||
            request.Event.RunId != request.Package.RunId ||
            request.Package.Manifest is null ||
            request.Package.Manifest.Count == 0 ||
            !IsValidId(request.Event.EventId) ||
            !IsValidId(request.Event.CorrelationId) ||
            !IsValidId(request.Event.RunId) ||
            !IsValidId(request.Event.CaseId) ||
            !IsValidId(request.Event.AirlineId) ||
            !IsValidId(request.Event.AircraftId) ||
            !IsValidId(request.Event.LeaseId) ||
            !IsValidId(request.Package.PackageId) ||
            request.Package.Manifest.Any(document =>
                document is null ||
                !IsValidId(document.DocumentId) ||
                document.Version < 1 ||
                !IsValidId(document.SourceRecordId) ||
                document.SourceSystem is null ||
                document.SourceSystem.Length is < 1 or > 128 ||
                document.MediaType is null ||
                document.MediaType.Length is < 1 or > 128 ||
                document.FileName is null ||
                document.FileName.Length is < 1 or > 255 ||
                document.FileName.Contains('/') ||
                document.FileName.Contains('\\') ||
                document.Sha256 is null ||
                document.Sha256.Length != 64 ||
                document.Sha256.Any(character => !Uri.IsHexDigit(character)) ||
                document.IssuedOn == default ||
                document.IssuedOn.Offset != TimeSpan.Zero) ||
            request.Package.Manifest
                .GroupBy(document => $"{document.DocumentId}:{document.Version}", StringComparer.Ordinal)
                .Any(group => group.Count() > 1) ||
            request.Event.OccurredAt == default ||
            request.Event.ScenarioEffectiveAt == default ||
            request.Package.SubmittedAt == default ||
            request.Package.ScenarioEffectiveAt == default ||
            request.Event.OccurredAt.Offset != TimeSpan.Zero ||
            request.Event.ScenarioEffectiveAt.Offset != TimeSpan.Zero ||
            request.Package.SubmittedAt.Offset != TimeSpan.Zero ||
            request.Package.ScenarioEffectiveAt.Offset != TimeSpan.Zero)
        {
            return "The package submission does not match the published contract.";
        }

        return null;
    }

    private static string? ValidatePartnerResponse(PackageSubmissionRequest request)
    {
        if (request.Event.Type != "partner.response.received" ||
            request.Event.Payload.ValueKind != JsonValueKind.Object ||
            !request.Event.Payload.TryGetProperty("requestId", out var requestId) ||
            requestId.ValueKind != JsonValueKind.String ||
            !IsValidId(requestId.GetString()) ||
            request.Event.Payload.EnumerateObject().Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(["packageId", "requestId"]) is false)
        {
            return "The partner response does not match the published contract.";
        }

        var packageEvent = request.Event with { Type = "package.submitted" };
        return ValidateSubmission(request with { Event = packageEvent });
    }

    private static bool IsValidId(string? value) =>
        value is not null &&
        value.Length is > 0 and <= 128 &&
        char.IsLetterOrDigit(value[0]) &&
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

    private static bool TryParseExpectedRevision(
        string? ifMatch,
        out long expectedRevision)
    {
        expectedRevision = 0;
        if (string.IsNullOrWhiteSpace(ifMatch) ||
            StringComparer.Ordinal.Equals(ifMatch.Trim(), "*"))
        {
            return false;
        }

        var value = ifMatch.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..].Trim();
        }

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        return long.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out expectedRevision) &&
            expectedRevision >= 1;
    }

    private static string ReviewCaseStatus(
        PersistedState state,
        CaseContext context)
    {
        var openTasks = ReviewTasksForCase(
            state,
            new PersistedCase(context, 0, string.Empty, [], []))
            .Where(task => task.Status == "open")
            .ToArray();
        var activeRequests = state.EvidenceRequests.Values
            .Where(request => request.Status is not ("closed" or "cancelled"))
            .Where(request => state.EvidenceBases.TryGetValue(request.BasisId, out var basis) &&
                ScopeMatches(basis.Context, context))
            .ToArray();
        var investigation = state.Investigations.Values
            .Where(candidate => ScopeMatches(candidate.Context, context))
            .OrderByDescending(candidate => candidate.BasisId, StringComparer.Ordinal)
            .FirstOrDefault();
        var basis = CurrentBasis(state, context);
        var findings = basis is null
            ? []
            : state.Findings.Values
                .Where(finding => finding.BasisId == basis.BasisId)
                .ToArray();
        if (findings.Length > 0 &&
            findings.All(finding =>
                state.FindingDispositions is not null &&
                state.FindingDispositions.TryGetValue(
                    $"{basis!.BasisId}:{finding.FindingId}",
                    out var disposition) &&
                disposition.Disposition == "accepted"))
        {
            return "accepted";
        }
        return CoordinationStatus(state, context, investigation, openTasks, activeRequests);
    }

    private static string CoordinationStatus(
        PersistedState state,
        PersistedCase persistedCase,
        InvestigationOutcome? investigation,
        IReadOnlyList<ReviewTask> openReviewTasks,
        IReadOnlyList<EvidenceRequest> activeEvidenceRequests) =>
        CoordinationStatus(
            state,
            persistedCase.Context,
            investigation,
            openReviewTasks,
            activeEvidenceRequests,
            persistedCase.Status);

    private static string CoordinationStatus(
        PersistedState state,
        CaseContext context,
        InvestigationOutcome? investigation,
        IReadOnlyList<ReviewTask> openReviewTasks,
        IReadOnlyList<EvidenceRequest> activeEvidenceRequests,
        string? currentStatus = null)
    {
        if (state.Packages.Values.Any(package =>
                ScopeMatches(package.Context, context) &&
                package.ProcessingStatus == "failed") ||
            investigation?.Status == "blocked")
        {
            return "blocked";
        }

        if (openReviewTasks.Count > 0)
        {
            return "awaiting_review";
        }

        if (activeEvidenceRequests.Count > 0)
        {
            return "awaiting_external";
        }

        if (currentStatus == "accepted")
        {
            return "accepted";
        }

        if (state.Packages.Values.Any(package =>
                ScopeMatches(package.Context, context) &&
                package.ProcessingStatus is "queued" or "processing"))
        {
            return "active";
        }

        return "ready_for_acceptance";
    }

    private static EvidenceBasis? CurrentBasis(
        PersistedState state,
        CaseContext context) =>
        state.EvidenceBases.Values
            .Where(candidate => ScopeMatches(candidate.Context, context))
            .OrderByDescending(candidate => candidate.CaseRevision)
            .ThenByDescending(candidate => candidate.CreatedAt)
            .FirstOrDefault();

    private static IReadOnlyList<ReviewTask> ReviewTasksForCase(
        PersistedState state,
        PersistedCase persistedCase) =>
        state.ReviewTasks?.Values
            .Where(task => TaskBelongsToCase(state, task, persistedCase.Context))
            .OrderBy(task => task.TaskId, StringComparer.Ordinal)
            .ToArray() ?? [];

    private static bool TaskBelongsToCase(
        PersistedState state,
        ReviewTask task,
        CaseContext context) =>
        state.EvidenceBases.TryGetValue(task.BasisId, out var basis) &&
        ScopeMatches(basis.Context, context) &&
        state.Findings.TryGetValue(FindingKeyForTask(task), out var finding) &&
        finding.BasisId == task.BasisId;

    private static string FindingKeyForTask(ReviewTask task) =>
        $"{task.BasisId}:{task.FindingId}";

    private static bool IsRequestInCallerScope(
        PersistedState state,
        EvidenceRequest request,
        CallerScope caller) =>
        state.EvidenceBases.TryGetValue(request.BasisId, out var basis) &&
        caller.Matches(basis.Context);

    private static CaseContext RequestContext(
        PersistedState state,
        EvidenceRequest request) =>
        state.EvidenceBases.TryGetValue(request.BasisId, out var basis)
            ? basis.Context
            : throw new InvalidDataException("The evidence request basis is missing.");

    private static bool IsDispatchEligible(
        PersistedState state,
        EvidenceRequest request,
        AutomaticRequestPolicyConfiguration policy)
    {
        if (request.Status is not ("pending" or "delivery_unknown") ||
            !state.EvidenceBases.TryGetValue(request.BasisId, out var basis) ||
            !state.Cases.TryGetValue(CaseKey(basis.Context), out var persistedCase) ||
            persistedCase.Status is "closed" or "cancelled" or "accepted" ||
            !state.Findings.TryGetValue(FindingKeyForRequest(request), out var finding) ||
            finding.BasisId != basis.BasisId ||
            finding.Assessment != "missing" ||
            finding.EvidenceRefs.Count != 0 ||
            !StringComparer.Ordinal.Equals(finding.RequirementId, request.RequirementId) ||
            !state.PolicyDecisions.Values.Any(decision =>
                decision.BasisId == request.BasisId &&
                decision.FindingId == request.FindingId &&
                decision.Outcome == "auto_request") ||
            !StringComparer.Ordinal.Equals(request.RecipientRef, policy.RecipientRef) ||
            !policy.ApprovedRecipientRefs.Contains(request.RecipientRef) ||
            !policy.ApprovedTemplateVersions.Contains(request.TemplateVersion) ||
            !IsCurrentBasis(state, basis) ||
            !IsCompleteBasis(state, basis))
        {
            return false;
        }

        return true;
    }

    private static bool IsCurrentBasis(PersistedState state, EvidenceBasis basis)
    {
        var current = state.EvidenceBases.Values
            .Where(candidate => ScopeMatches(candidate.Context, basis.Context))
            .OrderByDescending(candidate => candidate.CaseRevision)
            .ThenByDescending(candidate => candidate.CreatedAt)
            .FirstOrDefault();
        return current is not null &&
            StringComparer.Ordinal.Equals(current.BasisId, basis.BasisId);
    }

    private static bool IsCompleteBasis(PersistedState state, EvidenceBasis basis) =>
        state.Packages.Values.Any(package =>
            ScopeMatches(package.Context, basis.Context) &&
            package.ProcessingStatus == "complete") &&
        basis.DocumentInventory.All(document =>
        {
            var key = DocumentKey(basis.Context, document.DocumentId, document.Version);
            return state.ExtractionRecords.TryGetValue(key, out var extraction) &&
                ScopeMatches(extraction.Context, basis.Context) &&
                extraction.ProcessingState == "complete" &&
                extraction.PageInventory.Count > 0 &&
                StringComparer.OrdinalIgnoreCase.Equals(extraction.Sha256, document.Sha256);
        });

    private static string DispatchObsoleteReason(
        PersistedState state,
        EvidenceRequest request)
    {
        if (request.Status is not ("pending" or "delivery_unknown"))
        {
            return $"request_status_{request.Status}";
        }

        if (!state.EvidenceBases.TryGetValue(request.BasisId, out var basis))
        {
            return "basis_missing";
        }

        if (!IsCurrentBasis(state, basis))
        {
            return "basis_superseded";
        }

        if (!state.PolicyDecisions.Values.Any(decision =>
                decision.BasisId == request.BasisId &&
                decision.FindingId == request.FindingId &&
                decision.Outcome == "auto_request"))
        {
            return "policy_not_eligible";
        }

        if (!IsCompleteBasis(state, basis))
        {
            return "processing_incomplete";
        }

        return "request_not_eligible";
    }

    private static string FindingKeyForRequest(EvidenceRequest request) =>
        $"{request.BasisId}:{request.FindingId}";

    private static void AddDispatchAudit(
        PersistedState state,
        EvidenceRequest request,
        string action,
        IReadOnlyList<string> affectedIds,
        string correlationId,
        string? reason = null)
    {
        var context = RequestContext(state, request);
        var auditId = NewId("AUDIT");
        (state.AuditEntries ??= new(StringComparer.Ordinal)).Add(
            auditId,
            new AuditEntry(
                auditId,
                context,
                "dispatcher",
                "mock-inbox-dispatcher",
                action,
                affectedIds,
                request.BasisId,
                DateTimeOffset.UtcNow,
                correlationId));
    }

    private static string CaseKey(CaseContext context) =>
        CaseKey(new CallerScope(context.RunId, context.AirlineId, context.AircraftId, context.LeaseId),
            context.CaseId);

    private static string CaseKey(CallerScope caller, string caseId) =>
        $"{caller.RunId}:{caller.AirlineId}:{caller.AircraftId}:{caller.LeaseId}:{caseId}";

    private static string PackageKey(CaseContext context, string packageId) =>
        $"{context.RunId}:{context.AirlineId}:{context.AircraftId}:{context.LeaseId}:{context.CaseId}:{packageId}";

    private static string DocumentKey(CaseContext context, string documentId, int version) =>
        $"{context.RunId}:{context.AirlineId}:{context.AircraftId}:{context.LeaseId}:{context.CaseId}:{documentId}:v{version}";

    private static string FindingKey(Finding finding) =>
        $"{finding.BasisId}:{finding.FindingId}";

    private async Task<string> GetSelectedManifestSha256Async(
        CaseContext context,
        IReadOnlyList<DocumentMetadata> manifest,
        CancellationToken cancellationToken)
    {
        if (documentStorage is ISelectedManifestStorage selectedManifestStorage)
        {
            var selectedHash = await selectedManifestStorage.GetSelectedManifestSha256Async(
                context, cancellationToken);
            if (!string.IsNullOrWhiteSpace(selectedHash))
            {
                return selectedHash;
            }
        }

        return ManifestHash(manifest);
    }

    private static string ManifestHash(IReadOnlyList<DocumentMetadata> manifest)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = false
            });
        return Convert.ToHexString(SHA256.HashData(json));
    }

    private static string CanonicalHash(PackageSubmissionRequest request)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(
            request,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = false
            });
        using var document = JsonDocument.Parse(json);
        using var canonical = new MemoryStream();
        using (var writer = new Utf8JsonWriter(canonical))
        {
            WriteCanonicalJson(writer, document.RootElement);
        }

        return Convert.ToHexString(SHA256.HashData(canonical.ToArray()));
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
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
            ? CreateCallerScope(values, run, airline, aircraft, lease)
            : null;
    }

    private static CallerScope? CreateCallerScope(
        IReadOnlyDictionary<string, string> values,
        string run,
        string airline,
        string aircraft,
        string lease)
    {
        var subject = values.TryGetValue("subject", out var explicitSubject)
            ? explicitSubject
            : values.TryGetValue("sub", out var tokenSubject)
                ? tokenSubject
                : $"scope-{run}-{airline}-{aircraft}-{lease}";
        return IsSafeId(subject)
            ? new CallerScope(run, airline, aircraft, lease, subject)
            : null;
    }

    private static bool IsSafeId(string value) =>
        value.Length > 0 && value.All(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.');
}

internal static class CallerScopeExtensions
{
    public static bool IsMockPartner(this CallerScope caller) =>
        StringComparer.Ordinal.Equals(caller.Subject, "mock-partner");

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
