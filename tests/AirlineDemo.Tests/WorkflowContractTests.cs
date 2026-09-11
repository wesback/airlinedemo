using Json.Schema;
using System.Text.Json;

namespace AirlineDemo.Tests;

public sealed class WorkflowContractTests
{
    private static readonly string[] RequiredDefinitions =
    [
        "CaseContext",
        "Asset",
        "Requirement",
        "SubmissionPackage",
        "Document",
        "EvidenceRef",
        "EventEnvelope",
        "PackageSubmissionRequest",
        "OperationAccepted",
        "OperationStatus",
        "CaseSummary",
        "EvidencePreview",
        "SafeError",
        "EvidenceBasis",
        "Finding",
        "ReviewCommand",
        "ReviewDecision",
        "ReviewTask",
        "AuditEntry",
        "InvestigationResult",
        "InvestigationOutcome"
    ];

    [Fact]
    public void PublishedSchema_ContainsVersionedWorkflowObjectsAndRejectsUnsupportedSchemaVersions()
    {
        using var schema = LoadJson("contracts", "1.0", "workflow.schema.json");
        var root = schema.RootElement;

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", root.GetProperty("$schema").GetString());
        var schemaVersions = root.GetProperty("$defs").GetProperty("SchemaVersion")
            .GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("1.0", schemaVersions);
        Assert.DoesNotContain("0.9", schemaVersions);
        Assert.DoesNotContain("2.0", schemaVersions);

        foreach (var definition in RequiredDefinitions)
        {
            Assert.True(root.GetProperty("$defs").TryGetProperty(definition, out _),
                $"The published schema is missing {definition}.");
        }

        var submissionVersion = root.GetProperty("$defs")
            .GetProperty("SubmissionPackage")
            .GetProperty("properties")
            .GetProperty("schemaVersion");
        Assert.Equal("#/$defs/SchemaVersion", submissionVersion.GetProperty("$ref").GetString());

        Assert.DoesNotContain("0.9", schemaVersions);
        Assert.DoesNotContain("2.0", schemaVersions);
        Assert.Contains("1.0", schemaVersions);
    }

    [Fact]
    public void PublishedSchema_UsesClosedEnumsAndRejectsUnknownValues()
    {
        using var schema = LoadJson("contracts", "1.0", "workflow.schema.json");
        var definitions = schema.RootElement.GetProperty("$defs");

        AssertClosedEnum(definitions, "EventEnvelope", "type",
            "package.submitted", "partner.response.received");
        AssertClosedEnum(definitions, "OperationStatus", "status",
            "queued", "processing", "complete", "failed");
        AssertClosedEnum(definitions, "CaseSummary", "status",
            "active", "awaiting_external", "awaiting_review", "ready_for_acceptance",
            "accepted", "blocked");
        AssertClosedEnum(definitions, "SafeError", "safeCode",
            "AUTHENTICATION_REQUIRED", "INVALID_AUTHENTICATION", "CASE_NOT_FOUND",
            "EVIDENCE_NOT_FOUND", "OPERATION_NOT_FOUND", "ACTION_FORBIDDEN",
            "INVALID_PAYLOAD", "EVENT_PAYLOAD_CONFLICT", "STALE_PRECONDITION",
            "INVESTIGATION_BASIS_NOT_FOUND", "INVESTIGATION_CALL_LIMIT",
            "INVESTIGATION_CONTEXT_LIMIT", "INVESTIGATION_OUTPUT_INVALID",
            "INVESTIGATION_PAGE_LIMIT", "INVESTIGATION_RETRY_LIMIT",
            "INVESTIGATION_TIMEOUT");

        Assert.DoesNotContain("package.received", EnumValues(definitions, "EventEnvelope", "type"));
        Assert.DoesNotContain("running", EnumValues(definitions, "OperationStatus", "status"));
        Assert.DoesNotContain("hidden", EnumValues(definitions, "CaseSummary", "status"));
        Assert.DoesNotContain("document-content-leaked",
            EnumValues(definitions, "SafeError", "safeCode"));
    }

    [Fact]
    public void PublishedSchema_ValidatesRepresentativeWorkflowInstances()
    {
        using var schema = LoadJson("contracts", "1.0", "workflow.schema.json");

        var validInstances = new (string Definition, string Instance)[]
        {
            ("CaseContext", """
                {
                  "runId": "RUN-0001",
                  "caseId": "CASE-0001",
                  "airlineId": "AIRLINE-0001",
                  "aircraftId": "MOCK-AC-001",
                  "leaseId": "LEASE-0001"
                }
                """),
            ("Asset", """
                {
                  "aircraftId": "MOCK-AC-001",
                  "engineId": "MOCK-ENG-001",
                  "components": [{
                    "componentId": "COMP-0001",
                    "serialNumber": "SERIAL-0001"
                  }],
                  "referenceSource": {
                    "identity": "asset-register",
                    "version": 1
                  }
                }
                """),
            ("Requirement", """
                {
                  "requirementId": "REQ-0001",
                  "version": 1,
                  "componentId": "COMP-0001",
                  "evidenceKind": "maintenance-record",
                  "description": "Installation record",
                  "applicability": "fixture-setup",
                  "approvedBy": "mock-setup",
                  "approvedAt": "2026-09-10T08:00:00Z",
                  "sourceRef": "CHECKLIST-0001"
                }
                """),
            ("Document", """
                {
                  "documentId": "DOC-0001",
                  "version": 1,
                  "sourceSystem": "fixture-generator",
                  "sourceRecordId": "SOURCE-0001",
                  "fileName": "maintenance-record.pdf",
                  "mediaType": "application/pdf",
                  "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "issuedOn": "2026-09-10T12:00:00Z"
                }
                """),
            ("EvidenceRef", """
                {
                  "documentId": "DOC-0001",
                  "version": 1,
                  "page": 1,
                  "bounds": { "x": 1, "y": 2, "width": 10, "height": 20 },
                  "quote": "Inspection completed"
                }
                """),
            ("SubmissionPackage", """
                {
                  "schemaVersion": "1.0",
                  "packageId": "PKG-0001",
                  "runId": "RUN-0001",
                  "caseId": "CASE-0001",
                  "airlineId": "AIRLINE-0001",
                  "aircraftId": "MOCK-AC-001",
                  "leaseId": "LEASE-0001",
                  "submittedAt": "2026-09-10T13:00:00Z",
                  "scenarioEffectiveAt": "2026-09-10T09:00:00Z",
                  "manifest": [{
                    "documentId": "DOC-0001",
                    "version": 1,
                    "sourceSystem": "fixture-generator",
                    "sourceRecordId": "SOURCE-0001",
                    "fileName": "maintenance-record.pdf",
                    "mediaType": "application/pdf",
                    "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "issuedOn": "2026-09-10T12:00:00Z"
                  }]
                }
                """),
            ("EventEnvelope", """
                {
                  "schemaVersion": "1.0",
                  "eventId": "EVT-0001",
                  "type": "package.submitted",
                  "runId": "RUN-0001",
                  "caseId": "CASE-0001",
                  "airlineId": "AIRLINE-0001",
                  "aircraftId": "MOCK-AC-001",
                  "leaseId": "LEASE-0001",
                  "occurredAt": "2026-09-10T13:00:00Z",
                  "scenarioEffectiveAt": "2026-09-10T09:00:00Z",
                  "correlationId": "CORR-0001",
                  "payload": { "packageId": "PKG-0001" }
                }
                """),
            ("PackageSubmissionRequest", """
                {
                  "event": {
                    "schemaVersion": "1.0",
                    "eventId": "EVT-0001",
                    "type": "package.submitted",
                    "runId": "RUN-0001",
                    "caseId": "CASE-0001",
                    "airlineId": "AIRLINE-0001",
                    "aircraftId": "MOCK-AC-001",
                    "leaseId": "LEASE-0001",
                    "occurredAt": "2026-09-10T13:00:00Z",
                    "scenarioEffectiveAt": "2026-09-10T09:00:00Z",
                    "correlationId": "CORR-0001",
                    "payload": { "packageId": "PKG-0001" }
                  },
                  "package": {
                    "schemaVersion": "1.0",
                    "packageId": "PKG-0001",
                    "runId": "RUN-0001",
                    "caseId": "CASE-0001",
                    "airlineId": "AIRLINE-0001",
                    "aircraftId": "MOCK-AC-001",
                    "leaseId": "LEASE-0001",
                    "submittedAt": "2026-09-10T13:00:00Z",
                    "scenarioEffectiveAt": "2026-09-10T09:00:00Z",
                    "manifest": [{
                      "documentId": "DOC-0001",
                      "version": 1,
                      "sourceSystem": "fixture-generator",
                      "sourceRecordId": "SOURCE-0001",
                      "fileName": "maintenance-record.pdf",
                      "mediaType": "application/pdf",
                      "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                      "issuedOn": "2026-09-10T12:00:00Z"
                    }]
                  }
                }
                """),
            ("OperationAccepted", """
                {
                  "operationId": "OP-0001",
                  "caseId": "CASE-0001",
                  "receiptId": "RECEIPT-0001"
                }
                """),
            ("OperationStatus", """
                {
                  "operationId": "OP-0001",
                  "caseId": "CASE-0001",
                  "runId": "RUN-0001",
                  "airlineId": "AIRLINE-0001",
                  "aircraftId": "MOCK-AC-001",
                  "leaseId": "LEASE-0001",
                  "status": "processing"
                }
                """),
            ("CaseSummary", """
                {
                  "caseId": "CASE-0001",
                  "runId": "RUN-0001",
                  "airlineId": "AIRLINE-0001",
                  "aircraftId": "MOCK-AC-001",
                  "leaseId": "LEASE-0001",
                  "caseRevision": 1,
                  "status": "awaiting_review",
                  "packageProcessing": []
                }
                """),
            ("EvidencePreview", """
                {
                  "documentId": "DOC-0001",
                  "version": 1,
                  "page": 1,
                  "mediaType": "text/plain",
                  "textExcerpt": "Inspection completed"
                }
                """)
        };

        foreach (var (definition, instance) in validInstances)
        {
            Assert.True(ValidateDefinition(schema.RootElement, definition, instance),
                $"{definition} representative instance failed schema validation.");
        }
    }

    [Theory]
    [InlineData("0.9")]
    [InlineData("2.0")]
    public void PublishedSchema_RejectsUnsupportedSchemaVersions(string schemaVersion)
    {
        using var schema = LoadJson("contracts", "1.0", "workflow.schema.json");
        var invalidPackage = $$"""
            {
              "schemaVersion": "{{schemaVersion}}",
              "packageId": "PKG-0001",
              "runId": "RUN-0001",
              "caseId": "CASE-0001",
              "airlineId": "AIRLINE-0001",
              "aircraftId": "MOCK-AC-001",
              "leaseId": "LEASE-0001",
              "submittedAt": "2026-09-10T13:00:00Z",
              "scenarioEffectiveAt": "2026-09-10T09:00:00Z",
              "manifest": [{
                "documentId": "DOC-0001",
                "version": 1,
                "sourceSystem": "fixture-generator",
                "sourceRecordId": "SOURCE-0001",
                "fileName": "maintenance-record.pdf",
                "mediaType": "application/pdf",
                "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "issuedOn": "2026-09-10T12:00:00Z"
              }]
            }
            """;

        Assert.False(ValidateDefinition(schema.RootElement, "SubmissionPackage", invalidPackage),
            $"Unsupported schema version {schemaVersion} was accepted.");
    }

    public static IEnumerable<object[]> UnknownClosedEnumValues()
    {
        yield return ["EventEnvelope", "type", "package.unknown"];
        yield return ["OperationStatus", "status", "running"];
        yield return ["CaseSummary", "status", "hidden"];
        yield return ["SafeError", "safeCode", "DOCUMENT_CONTENT_LEAKED"];
    }

    [Theory]
    [MemberData(nameof(UnknownClosedEnumValues))]
    public void PublishedSchema_RejectsUnknownClosedEnumValues(
        string definition,
        string property,
        string unknownValue)
    {
        using var schema = LoadJson("contracts", "1.0", "workflow.schema.json");
        var instance = definition switch
        {
            "EventEnvelope" => $$"""
                {
                  "schemaVersion": "1.0",
                  "eventId": "EVT-0001",
                  "type": "{{unknownValue}}",
                  "runId": "RUN-0001",
                  "caseId": "CASE-0001",
                  "airlineId": "AIRLINE-0001",
                  "aircraftId": "MOCK-AC-001",
                  "leaseId": "LEASE-0001",
                  "occurredAt": "2026-09-10T13:00:00Z",
                  "scenarioEffectiveAt": "2026-09-10T09:00:00Z",
                  "correlationId": "CORR-0001",
                  "payload": { "packageId": "PKG-0001" }
                }
                """,
            "OperationStatus" => $$"""
                {
                  "operationId": "OP-0001",
                  "caseId": "CASE-0001",
                  "status": "{{unknownValue}}"
                }
                """,
            "CaseSummary" => $$"""
                {
                  "caseId": "CASE-0001",
                  "runId": "RUN-0001",
                  "airlineId": "AIRLINE-0001",
                  "aircraftId": "MOCK-AC-001",
                  "leaseId": "LEASE-0001",
                  "caseRevision": 1,
                  "status": "{{unknownValue}}"
                }
                """,
            "SafeError" => $$"""
                {
                  "safeCode": "{{unknownValue}}",
                  "correlationId": "CORR-0001"
                }
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(definition), definition, null)
        };

        Assert.False(ValidateDefinition(schema.RootElement, definition, instance),
            $"Unknown {definition}.{property} value {unknownValue} was accepted.");
    }

    [Fact]
    public void OpenApi_DeclaresPackageSubmissionAuthenticationAcceptedIdentifiersAndConflict()
    {
        using var contract = LoadJson("contracts", "1.0", "openapi.json");
        using var schema = LoadJson("contracts", "1.0", "workflow.schema.json");
        var post = contract.RootElement.GetProperty("paths").GetProperty("/packages")
            .GetProperty("post");

        Assert.Equal("submitPackage", post.GetProperty("operationId").GetString());
        Assert.Equal("workflow.write", post.GetProperty("x-required-scopes")[0].GetString());
        AssertBearerSecurity(post);

        var accepted = post.GetProperty("responses").GetProperty("202");
        Assert.Contains("accepted", accepted.GetProperty("description").GetString()!,
            StringComparison.OrdinalIgnoreCase);
        var acceptedSchema = accepted.GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("$ref").GetString();
        Assert.Equal("#/components/schemas/OperationAccepted", acceptedSchema);
        var acceptedFields = schema.RootElement.GetProperty("$defs")
            .GetProperty("OperationAccepted").GetProperty("required")
            .EnumerateArray().Select(value => value.GetString());
        Assert.Contains("operationId", acceptedFields);
        Assert.Contains("caseId", acceptedFields);
        Assert.Contains("receiptId", acceptedFields);

        var conflict = post.GetProperty("responses").GetProperty("409");
        var conflictResponse = contract.RootElement.GetProperty("components")
            .GetProperty("responses").GetProperty("EventPayloadConflict");
        Assert.Contains("different canonical payload",
            conflictResponse.GetProperty("description").GetString()!,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("#/components/responses/EventPayloadConflict",
            conflict.GetProperty("$ref").GetString());
    }

    [Fact]
    public void OpenApi_DeclaresOperationCaseAndEvidenceLookupContracts()
    {
        using var contract = LoadJson("contracts", "1.0", "openapi.json");
        using var schema = LoadJson("contracts", "1.0", "workflow.schema.json");
        var paths = contract.RootElement.GetProperty("paths");

        AssertOperation(paths, "/operations/{id}", "getOperation", "workflow.read", "200", "404");
        AssertOperation(paths, "/cases/{id}", "getCase", "case.read", "200", "404");
        AssertOperation(paths, "/cases/{id}/evidence/{documentId}", "previewEvidence",
            "evidence.read", "200", "404");

        var evidenceParameters = paths.GetProperty("/cases/{id}/evidence/{documentId}")
            .GetProperty("get").GetProperty("parameters").EnumerateArray();
        Assert.Contains(evidenceParameters, parameter =>
            parameter.TryGetProperty("name", out var name) &&
            name.GetString() == "version" &&
            parameter.GetProperty("required").GetBoolean());
        Assert.True(schema.RootElement.GetProperty("$defs")
            .GetProperty("CaseSummary").GetProperty("properties")
            .TryGetProperty("packageProcessing", out _));
        Assert.True(schema.RootElement.GetProperty("$defs")
            .TryGetProperty("PackageProcessingStatus", out _));
    }

    [Fact]
    public void OpenApi_MapsSafeErrorsAndExcludesDocumentContentAndStackTraces()
    {
        using var contract = LoadJson("contracts", "1.0", "openapi.json");
        using var schema = LoadJson("contracts", "1.0", "workflow.schema.json");
        var paths = contract.RootElement.GetProperty("paths");
        var safeErrorRef = contract.RootElement.GetProperty("components")
            .GetProperty("schemas").GetProperty("SafeError")
            .GetProperty("$ref").GetString();

        Assert.Equal("workflow.schema.json#/$defs/SafeError", safeErrorRef);
        AssertMapping(paths.GetProperty("/packages").GetProperty("post"),
            ("400", "InvalidPayload"), ("401", "AuthenticationRequired"),
            ("403", "ActionForbidden"), ("409", "EventPayloadConflict"));
        AssertMapping(paths.GetProperty("/cases/{id}").GetProperty("get"),
            ("401", "AuthenticationRequired"), ("403", "ActionForbidden"),
            ("404", "CaseNotFound"));
        AssertMapping(paths.GetProperty("/cases/{id}/evidence/{documentId}").GetProperty("get"),
            ("400", "InvalidPayload"), ("401", "AuthenticationRequired"),
            ("403", "ActionForbidden"), ("404", "EvidenceNotFound"));

        var responseDefinitions = contract.RootElement.GetProperty("components")
            .GetProperty("responses");
        foreach (var response in responseDefinitions.EnumerateObject())
        {
            var schemaRef = response.Value.GetProperty("content")
                .GetProperty("application/json").GetProperty("schema")
                .GetProperty("$ref").GetString();
            Assert.Equal("#/components/schemas/SafeError", schemaRef);
        }

        var error = schema.RootElement.GetProperty("$defs").GetProperty("SafeError");
        var properties = error.GetProperty("properties");
        Assert.Contains("safeCode", error.GetProperty("required").EnumerateArray()
            .Select(value => value.GetString()));
        Assert.Contains("correlationId", error.GetProperty("required").EnumerateArray()
            .Select(value => value.GetString()));
        Assert.False(properties.TryGetProperty("documentContent", out _));
        Assert.False(properties.TryGetProperty("stackTrace", out _));
        Assert.False(properties.TryGetProperty("exception", out _));
        Assert.True(error.GetProperty("additionalProperties").GetBoolean() == false);
    }

    private static void AssertClosedEnum(
        JsonElement definitions,
        string definition,
        string property,
        params string[] expectedValues)
    {
        var values = EnumValues(definitions, definition, property);
        Assert.Equal(expectedValues, values);
    }

    private static string?[] EnumValues(JsonElement definitions, string definition, string property) =>
        definitions.GetProperty(definition).GetProperty("properties")
            .GetProperty(property).GetProperty("enum").EnumerateArray()
            .Select(value => value.GetString()).ToArray();

    private static void AssertBearerSecurity(JsonElement operation)
    {
        var security = operation.GetProperty("security").EnumerateArray().Single();
        Assert.True(security.TryGetProperty("WorkflowBearer", out _));
        var scheme = operation
            .GetProperty("security")
            .EnumerateArray()
            .Single()
            .GetProperty("WorkflowBearer");
        Assert.Empty(scheme.EnumerateArray());
    }

    private static void AssertOperation(
        JsonElement paths,
        string path,
        string operationId,
        string requiredScope,
        params string[] responseCodes)
    {
        var operation = paths.GetProperty(path).GetProperty("get");
        Assert.Equal(operationId, operation.GetProperty("operationId").GetString());
        Assert.Equal(requiredScope, operation.GetProperty("x-required-scopes")[0].GetString());
        AssertBearerSecurity(operation);
        foreach (var responseCode in responseCodes)
        {
            Assert.True(operation.GetProperty("responses").TryGetProperty(responseCode, out _),
                $"{path} is missing {responseCode}.");
        }
    }

    private static void AssertMapping(
        JsonElement operation,
        params (string Code, string ResponseName)[] mappings)
    {
        foreach (var (code, responseName) in mappings)
        {
            var response = operation.GetProperty("responses").GetProperty(code);
            Assert.Equal("#/components/responses/" + responseName,
                response.GetProperty("$ref").GetString());
        }
    }

    private static JsonDocument LoadJson(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate))
            {
                return JsonDocument.Parse(File.ReadAllText(candidate));
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find published contract: {Path.Join(relativePath)}");
    }

    private static bool ValidateDefinition(JsonElement schemaRoot, string definition, string instanceJson)
    {
        var definitionSchema = $$"""
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$ref": "#/$defs/{{definition}}",
              "$defs": {{schemaRoot.GetProperty("$defs").GetRawText()}}
            }
            """;
        var validator = JsonSchema.FromText(definitionSchema);
        using var instance = JsonDocument.Parse(instanceJson);
        return validator.Evaluate(instance.RootElement).IsValid;
    }
}
