using System.Security.Cryptography;
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

        return app;
    }
}

internal sealed class WorkflowService
{
    private const string ParserVersion = "deterministic-pdf-parser/1.0";
    private const string ExtractorVersion = "deterministic-text-extractor/1.0";

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
            affectedRecordCount =
                caseKeys.Length +
                packageKeys.Length +
                operationKeys.Length +
                documentKeys.Length +
                extractionKeys.Length +
                extractionAttemptKeys.Length +
                evidenceBasisKeys.Length;
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

    private static string CaseKey(CaseContext context) =>
        CaseKey(new CallerScope(context.RunId, context.AirlineId, context.AircraftId, context.LeaseId),
            context.CaseId);

    private static string CaseKey(CallerScope caller, string caseId) =>
        $"{caller.RunId}:{caller.AirlineId}:{caller.AircraftId}:{caller.LeaseId}:{caseId}";

    private static string PackageKey(CaseContext context, string packageId) =>
        $"{context.RunId}:{context.AirlineId}:{context.AircraftId}:{context.LeaseId}:{context.CaseId}:{packageId}";

    private static string DocumentKey(CaseContext context, string documentId, int version) =>
        $"{context.RunId}:{context.AirlineId}:{context.AircraftId}:{context.LeaseId}:{context.CaseId}:{documentId}:v{version}";

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
