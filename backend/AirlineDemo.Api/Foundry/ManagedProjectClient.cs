using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AirlineDemo.Api.Diagnostics;
using AirlineDemo.Api.Evaluation;
using Azure.Core;
using Azure.Identity;

namespace AirlineDemo.Api.Foundry;

public sealed record ManagedAgentTarget(string Name, string Version, string ConfigurationId);

public sealed class ManagedProjectException(string code, bool uncertainWrite = false, bool retryableRead = false) : Exception(code)
{
    public string Code { get; } = code;
    public bool UncertainWrite { get; } = uncertainWrite;
    public bool RetryableRead { get; } = retryableRead;
}

public interface IManagedProjectClient
{
    ManagedAgentTarget Agent(string workload);
    Task<JsonObject> CreateConversationAsync(string workload, string reservationId, string attemptId, GatewayCorrelation correlation, CancellationToken cancellationToken);
    Task<JsonObject> ReadConversationAsync(string workload, string id, GatewayCorrelation correlation, CancellationToken cancellationToken);
    Task<JsonObject> ListItemsAsync(string workload, string conversationId, string? after, GatewayCorrelation correlation, CancellationToken cancellationToken);
    Task<JsonObject> DeleteItemAsync(string workload, string conversationId, string itemId, GatewayCorrelation correlation, CancellationToken cancellationToken);
    Task<JsonObject> DeleteConversationAsync(string workload, string id, GatewayCorrelation correlation, CancellationToken cancellationToken);
    Task<JsonObject> CreateResponseAsync(string workload, string conversationId, JsonArray input, GatewayCorrelation correlation, CancellationToken cancellationToken);
    Task<JsonObject> ReadResponseAsync(string workload, string id, GatewayCorrelation correlation, CancellationToken cancellationToken);
    Task<JsonObject> DeleteResponseAsync(string workload, string id, GatewayCorrelation correlation, CancellationToken cancellationToken);
}

public sealed class ManagedProjectClient(IConfiguration configuration, IHttpClientFactory clients,
    Func<TokenCredential> credentialFactory) : IManagedProjectClient
{
    public const string HttpClientName = "managed-foundry";
    private static readonly Regex Identifier = new("^[A-Za-z0-9_-]{1,128}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Digest = new("^[a-f0-9]{64}\\z", RegexOptions.CultureInvariant);
    private readonly Lazy<TokenCredential> _credential = new(credentialFactory);

    public ManagedAgentTarget Agent(string workload)
    {
        if (workload is not ("copilot" or "investigator")) throw new ManagedProjectException("managed_agent_unconfigured");
        var section = configuration.GetSection($"Foundry:Agents:{workload}");
        var target = new ManagedAgentTarget(section["Name"] ?? "", section["Version"] ?? "", section["ConfigurationId"] ?? "");
        if (!Regex.IsMatch(target.Name, "^[A-Za-z][A-Za-z0-9_-]{0,62}\\z", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(target.Version, "^[1-9][0-9]{0,9}\\z", RegexOptions.CultureInvariant) ||
            !Digest.IsMatch(target.ConfigurationId))
            throw new ManagedProjectException("managed_agent_unconfigured");
        return target;
    }

    private Uri Endpoint()
    {
        var project = configuration["Foundry:ProjectName"] ?? "";
        var gateway = configuration["Foundry:GatewayEndpoint"];
        var root = configuration["ModelGateway:Endpoint"];
        if (!Identifier.IsMatch(project) || !Uri.TryCreate(gateway, UriKind.Absolute, out var endpoint) ||
            !Uri.TryCreate(root, UriKind.Absolute, out var gatewayRoot) || endpoint.Scheme != "https" ||
            endpoint.UserInfo != "" || endpoint.Query != "" || endpoint.Fragment != "" ||
            endpoint.AbsolutePath.TrimEnd('/') != $"/foundry/projects/{project}" ||
            endpoint.Authority != gatewayRoot.Authority || gatewayRoot.Scheme != "https" ||
            endpoint.Host is "services.ai.azure.com" or "openai.azure.com" or "cognitiveservices.azure.com" ||
            endpoint.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase) ||
            endpoint.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase) ||
            endpoint.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase))
            throw new ManagedProjectException("managed_agent_unconfigured");
        return endpoint;
    }

    private static string Id(string value)
    {
        if (!Identifier.IsMatch(value)) throw new ManagedProjectException("conversation_history_invalid");
        return value;
    }

    public Task<JsonObject> CreateConversationAsync(string workload, string reservationId, string attemptId,
        GatewayCorrelation correlation, CancellationToken cancellationToken) =>
        SendAsync(workload, HttpMethod.Post, "conversations", new JsonObject
        {
            ["metadata"] = new JsonObject { ["reservation_id"] = Id(reservationId), ["attempt_id"] = Id(attemptId) }
        }, correlation, cancellationToken, "create_conversation");

    public Task<JsonObject> ReadConversationAsync(string workload, string id, GatewayCorrelation correlation, CancellationToken cancellationToken) =>
        SendAsync(workload, HttpMethod.Get, $"conversations/{Id(id)}", null, correlation, cancellationToken, "get_conversation", conversationId: id);

    public Task<JsonObject> ListItemsAsync(string workload, string conversationId, string? after, GatewayCorrelation correlation, CancellationToken cancellationToken) =>
        SendAsync(workload, HttpMethod.Get, $"conversations/{Id(conversationId)}/items?limit=100&order=asc" +
            (after is null ? "" : $"&after={Id(after)}"), null, correlation, cancellationToken, "list_conversation_items", conversationId: conversationId);

    public Task<JsonObject> DeleteItemAsync(string workload, string conversationId, string itemId, GatewayCorrelation correlation, CancellationToken cancellationToken) =>
        SendAsync(workload, HttpMethod.Delete, $"conversations/{Id(conversationId)}/items/{Id(itemId)}", null, correlation, cancellationToken, "delete_conversation", conversationId: conversationId);

    public Task<JsonObject> DeleteConversationAsync(string workload, string id, GatewayCorrelation correlation, CancellationToken cancellationToken) =>
        SendAsync(workload, HttpMethod.Delete, $"conversations/{Id(id)}", null, correlation, cancellationToken, "delete_conversation", conversationId: id);

    public Task<JsonObject> CreateResponseAsync(string workload, string conversationId, JsonArray input,
        GatewayCorrelation correlation, CancellationToken cancellationToken)
    {
        var agent = Agent(workload);
        return SendAsync(workload, HttpMethod.Post, "responses", new JsonObject
        {
            ["agent_reference"] = new JsonObject { ["type"] = "agent_reference", ["name"] = agent.Name, ["version"] = agent.Version },
            ["conversation"] = Id(conversationId), ["input"] = input.DeepClone(),
            ["max_output_tokens"] = 2000, ["truncation"] = "disabled", ["background"] = false, ["stream"] = false,
            ["metadata"] = new JsonObject { ["submission_id"] = correlation.CallIdentity }
        }, correlation, cancellationToken, "invoke_agent", conversationId: conversationId);
    }

    public Task<JsonObject> ReadResponseAsync(string workload, string id, GatewayCorrelation correlation, CancellationToken cancellationToken) =>
        SendAsync(workload, HttpMethod.Get, $"responses/{Id(id)}", null, correlation, cancellationToken, "get_response", responseId: id);

    public Task<JsonObject> DeleteResponseAsync(string workload, string id, GatewayCorrelation correlation, CancellationToken cancellationToken) =>
        SendAsync(workload, HttpMethod.Delete, $"responses/{Id(id)}", null, correlation, cancellationToken, "delete_response", responseId: id);

    private async Task<JsonObject> SendAsync(string workload, HttpMethod method, string path, JsonObject? body,
        GatewayCorrelation correlation, CancellationToken cancellationToken, string operation,
        string? conversationId = null, string? responseId = null)
    {
        var agent = Agent(workload);
        var endpoint = Endpoint();
        ModelGatewayTokenomics.ValidateCorrelation(correlation);
        if (correlation.ModelConfiguration != agent.ConfigurationId) throw new ManagedProjectException("managed_agent_unconfigured");
        var started = Stopwatch.GetTimestamp();
        using var activity = TelemetryRedaction.StartManagedOperation(operation, agent, conversationId, responseId);
        activity?.SetTag("http.request.method", method.Method);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var write = method != HttpMethod.Get;
        var dispatched = false;
        try
        {
            var token = await _credential.Value.GetTokenAsync(
                new TokenRequestContext([ModelGatewayTokenomics.TokenScope(configuration["ModelGateway:Audience"])]), deadline.Token);
            using var request = new HttpRequestMessage(method, endpoint.AbsoluteUri.TrimEnd('/') + "/openai/v1/" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            string[] values = [correlation.Arm, correlation.Run, correlation.Package, correlation.Case,
                correlation.ModelConfiguration, correlation.SuiteVersion, correlation.CallIdentity];
            for (var i = 0; i < values.Length; i++) request.Headers.Add(GatewayCorrelationHeadersPolicy.HeaderNames[i], values[i]);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            dispatched = true;
            using var client = clients.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            activity?.SetTag("http.response.status_code", (int)response.StatusCode);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new ManagedProjectException("model_unavailable");
                if (!write && response.StatusCode == HttpStatusCode.NotFound && path.StartsWith("conversations/", StringComparison.Ordinal))
                    throw new ManagedProjectException("conversation_unavailable");
                var transient = (int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
                throw new ManagedProjectException("model_unavailable", write && transient, !write && transient);
            }
            var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(deadline.Token),
                documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }) as JsonObject
                ?? throw new JsonException();
            if (operation == "create_conversation" && ServiceId(result["id"]) is { } createdConversation)
            {
                activity?.SetTag("gen_ai.conversation.id", TelemetryRedaction.OpaqueReference(createdConversation));
            }
            if (operation == "invoke_agent")
            {
                if (ServiceId(result["id"]) is { } createdResponse)
                {
                    activity?.SetTag("gen_ai.response.id", TelemetryRedaction.OpaqueReference(createdResponse));
                }
                if (result["usage"] is JsonObject usage)
                {
                    RecordUsage(usage, "input_tokens", "input", activity);
                    RecordUsage(usage, "output_tokens", "output", activity);
                }
            }
            return result;
        }
        catch (ManagedProjectException exception)
        {
            TelemetryRedaction.MarkManagedOperationFailed(activity, exception.Code);
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or AuthenticationFailedException)
        {
            TelemetryRedaction.MarkManagedOperationFailed(activity, "model_unavailable");
            throw new ManagedProjectException("model_unavailable", write && dispatched,
                !write && !cancellationToken.IsCancellationRequested && (exception is HttpRequestException or OperationCanceledException));
        }
        finally
        {
            TelemetryRedaction.RecordManagedOperationDuration(operation, Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    private static string? ServiceId(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && Identifier.IsMatch(text) ? text : null;

    private static void RecordUsage(JsonObject usage, string property, string tokenType, Activity? activity)
    {
        if (usage[property] is JsonValue value && value.TryGetValue<long>(out var count) && count >= 0)
        {
            activity?.SetTag($"gen_ai.usage.{property}", count);
            TelemetryRedaction.RecordManagedTokenUsage(tokenType, count);
        }
    }
}

public static class ManagedFoundryRegistration
{
    public static IServiceCollection AddManagedFoundry(this IServiceCollection services)
    {
        services.AddHttpClient(ManagedProjectClient.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(sp => sp.GetService<HttpMessageHandler>() ??
                new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<IManagedProjectClient, ManagedProjectClient>();
        services.AddSingleton<ManagedConversationLifecycle>();
        services.AddSingleton<ManagedTurnCoordinator>();
        return services;
    }
}
