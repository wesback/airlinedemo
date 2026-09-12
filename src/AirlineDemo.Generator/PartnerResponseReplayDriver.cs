using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AirlineDemo.Generator;

public sealed record PartnerResponseReplayRequest(
    CaseContext Case,
    string StagedPackageManifestPath,
    string? EventId = null,
    string? CorrelationId = null);

public sealed record PartnerResponseReplayResult(
    string RequestId,
    string PackageId,
    string EventId,
    string EventType,
    HttpStatusCode StatusCode,
    string ResponseBody);

public sealed class PartnerResponseReplayDriver
{
    private const string MockPartnerSubject = "mock-partner";
    private const string MockInboxPath = "api/mock-inbox";
    private const string PartnerResponsePath = "api/partner-responses";
    private readonly HttpClient client;

    public PartnerResponseReplayDriver(HttpClient client)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<PartnerResponseReplayResult> ReplayAsync(
        PartnerResponseReplayRequest replay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(replay.Case);
        if (client.BaseAddress is null)
        {
            throw new InvalidOperationException(
                "The replay driver HTTP client must have a base address.");
        }

        var package = LoadStagedPackage(replay.StagedPackageManifestPath, replay.Case);
        ValidateId(package.PackageId, nameof(package.PackageId));
        var eventId = replay.EventId ?? $"REPLAY-{package.PackageId}";
        var correlationId = replay.CorrelationId ?? $"CORR-REPLAY-{package.PackageId}";
        ValidateId(eventId, nameof(replay.EventId));
        ValidateId(correlationId, nameof(replay.CorrelationId));

        using var inboxRequest = CreateAuthenticatedRequest(
            HttpMethod.Get,
            MockInboxPath,
            replay.Case);
        using var inboxResponse = await client.SendAsync(inboxRequest, cancellationToken);
        var inboxBody = await inboxResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!inboxResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The authoritative mock inbox request failed with {(int)inboxResponse.StatusCode}.",
                null,
                inboxResponse.StatusCode);
        }

        var inboxItems = JsonSerializer.Deserialize<IReadOnlyList<MockPartnerInboxItem>>(
            inboxBody,
            GeneratorContractJson.Options)
            ?? throw new JsonException("The authoritative mock inbox response was empty.");
        var matchingItems = inboxItems
            .Where(item =>
                item.Context is not null &&
                ScopeMatches(item.Context, replay.Case) &&
                StringComparer.Ordinal.Equals(item.RecipientRef, "mock-partner-inbox") &&
                !string.IsNullOrWhiteSpace(item.RequestId))
            .OrderBy(item => item.DeliveredAt)
            .ThenBy(item => item.RequestId, StringComparer.Ordinal)
            .ToArray();
        if (matchingItems.Length != 1)
        {
            throw new InvalidOperationException(
                matchingItems.Length == 0
                    ? "The authoritative application returned no delivered request for the selected case."
                    : "The authoritative application returned multiple delivered requests for the selected case.");
        }

        var requestId = matchingItems[0].RequestId;
        ValidateId(requestId, nameof(requestId));
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(
            new
            {
                packageId = package.PackageId,
                requestId
            },
            GeneratorContractJson.Options));
        var responseEvent = new EventEnvelope(
            WorkflowContract.Version,
            eventId,
            "partner.response.received",
            package.RunId,
            package.CaseId,
            package.AirlineId,
            package.AircraftId,
            package.LeaseId,
            package.SubmittedAt,
            package.ScenarioEffectiveAt,
            correlationId,
            payload.RootElement.Clone());
        var submission = new
        {
            Event = responseEvent,
            Package = package
        };

        using var responseRequest = CreateAuthenticatedRequest(
            HttpMethod.Post,
            PartnerResponsePath,
            replay.Case);
        responseRequest.Content = new StringContent(
            JsonSerializer.Serialize(submission, GeneratorContractJson.Options),
            Encoding.UTF8,
            "application/json");
        using var response = await client.SendAsync(responseRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        return new PartnerResponseReplayResult(
            requestId,
            package.PackageId,
            eventId,
            responseEvent.Type,
            response.StatusCode,
            responseBody);
    }

    private static SubmissionPackage LoadStagedPackage(
        string manifestPath,
        CaseContext selectedCase)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new ArgumentException(
                "The staged response manifest path is required.",
                nameof(manifestPath));
        }

        var fullPath = Path.GetFullPath(manifestPath);
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetFileName(fullPath),
                "manifest.json") ||
            !IsUnderNamedDirectory(fullPath, "staged-responses") ||
            IsUnderNamedDirectory(fullPath, "evaluator-only") ||
            IsUnderNamedDirectory(fullPath, "application-inputs") ||
            IsUnderNamedDirectory(fullPath, "replay-only"))
        {
            throw new InvalidDataException(
                "The replay package must be a manifest below the staged-responses boundary.");
        }

        var json = File.ReadAllText(fullPath);
        if (json.Contains("requestId", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The staged response package must not contain an authoritative request ID.");
        }

        var package = JsonSerializer.Deserialize<SubmissionPackage>(
            json,
            GeneratorContractJson.Options)
            ?? throw new JsonException("The staged response package manifest was empty.");
        if (!ScopeMatches(package, selectedCase))
        {
            throw new InvalidDataException(
                "The staged response package does not match the selected case scope.");
        }

        return package;
    }

    private HttpRequestMessage CreateAuthenticatedRequest(
        HttpMethod method,
        string path,
        CaseContext context)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            $"run={context.RunId};airline={context.AirlineId};" +
            $"aircraft={context.AircraftId};lease={context.LeaseId};" +
            $"subject={MockPartnerSubject}");
        return request;
    }

    private static bool ScopeMatches(SubmissionPackage package, CaseContext context) =>
        package.RunId == context.RunId &&
        package.CaseId == context.CaseId &&
        package.AirlineId == context.AirlineId &&
        package.AircraftId == context.AircraftId &&
        package.LeaseId == context.LeaseId;

    private static bool ScopeMatches(MockPartnerInboxContext actual, CaseContext expected) =>
        actual.RunId == expected.RunId &&
        actual.CaseId == expected.CaseId &&
        actual.AirlineId == expected.AirlineId &&
        actual.AircraftId == expected.AircraftId &&
        actual.LeaseId == expected.LeaseId;

    private static bool IsUnderNamedDirectory(string path, string directoryName) =>
        path.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => StringComparer.OrdinalIgnoreCase.Equals(segment, directoryName));

    private static void ValidateId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 128 ||
            !char.IsLetterOrDigit(value[0]) ||
            value.Any(character =>
                !char.IsLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new ArgumentException("The event identifier must be a safe workflow ID.", parameterName);
        }
    }
}

public sealed record MockPartnerInboxItem(
    string ItemId,
    string RequestId,
    string RequestKey,
    MockPartnerInboxContext Context,
    string RecipientRef,
    string TemplateVersion,
    string Message,
    DateTimeOffset DeliveredAt);

public sealed record MockPartnerInboxContext(
    string RunId,
    string CaseId,
    string AirlineId,
    string AircraftId,
    string LeaseId);
