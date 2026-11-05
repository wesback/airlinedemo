using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.SourceSystems;
using Microsoft.Extensions.AI;

namespace AirlineDemo.Api.Foundry;

internal sealed class ManagedInvestigatorTurn
{
    private sealed record CommittedRecord(string CaseId, string SourceId, string RecordId, Action<SourceReadTools> Restore);
    private static readonly Lazy<IReadOnlyDictionary<string, CommittedRecord>> CommittedSources = new(BuildSourceRegistry);
    private readonly ApprovedModelManifest _manifest;
    private readonly ModelRequest _request;
    private readonly SyntheticContentRegistry _registry;
    private readonly string _caseId;
    private readonly string _role;
    private readonly ICaseReadSource _source;
    private readonly SourceReadTools _sourceTools;
    private readonly IReadOnlyDictionary<string, AIFunction> _functions;
    private readonly string _prompt;
    private readonly InvestigationTraceRecorder? _traceRecorder;

    internal ManagedInvestigatorTurn(string caseId, string role, ICaseReadSource source,
        InvestigationSourceSystems sources, ApprovedModelManifest manifest, ModelRequest request, SyntheticContentRegistry registry,
        InvestigationTraceRecorder? traceRecorder = null)
    {
        _caseId = caseId;
        _role = role;
        _source = source;
        _manifest = manifest;
        _request = request with { Evidence = request.Evidence.ToArray() };
        _registry = registry;
        _traceRecorder = traceRecorder;
        Input = ManagedHistoryContract.InvestigatorDocuments(manifest, _request, registry);
        _prompt = BoundedInvestigator.BuildPrompt(_request.Evidence);
        var caseTools = new CaseReadTools(caseId, source, NoInvestigationLimits.Instance);
        _sourceTools = new SourceReadTools(caseId, role, sources, NoInvestigationLimits.Instance);
        _functions = caseTools.Functions.Concat(_sourceTools.Functions).ToDictionary(function => function.Name, StringComparer.Ordinal);
        Functions = _functions.Values.ToArray();
        EvidenceHashesDigest = SyntheticContentRegistry.Sha256Hex(string.Join('\n',
            _request.Evidence.Select(SyntheticContentRegistry.Sha256Hex).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).Select(hash => "document:" + hash).Concat(CommittedSources.Value
                .Where(record => record.Value.CaseId == caseId && SourceReadTools.Allowed(role, record.Value.SourceId))
                .Select(record => "source:" + record.Key)).Order(StringComparer.Ordinal)));
        Policy = new(
            text =>
            {
                Reset();
                _ = ModelCallGate.Authorize(_manifest, _request, _registry);
                return text == _prompt;
            },
            text =>
            {
                Claims = ManagedHistoryContract.InvestigatorAnswer(text, _manifest, _request, _registry, _caseId,
                    _source, _sourceTools, _traceRecorder);
                return true;
            },
            ValidArguments,
            ValidOutput)
        {
            Reset = Reset,
            Workload = "investigator",
            Context = tuple => !tuple.IsListing && tuple.CaseId == _caseId && tuple.Role == _role &&
                tuple.EvidenceVersion == _request.EvidenceVersion && tuple.EvidenceHashesDigest == EvidenceHashesDigest
        };
    }

    internal JsonArray Input { get; }
    internal ManagedHistoryPolicy Policy { get; }
    internal IReadOnlyList<AIFunction> Functions { get; }
    internal IReadOnlyList<InvestigationClaim>? Claims { get; private set; }
    internal string EvidenceHashesDigest { get; }

    private void Reset()
    {
        _sourceTools.ResetManagedWitnesses();
        Claims = null;
    }

    internal async Task<JsonObject> DispatchAsync(ManagedToolSelection selection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidArguments(selection.Name, selection.Arguments))
            throw new ManagedProjectException("conversation_history_invalid");
        _traceRecorder?.RecordToolCall(selection.Name);
        var arguments = new AIFunctionArguments();
        foreach (var (name, value) in selection.Arguments)
        {
            var scalar = (JsonValue)value!;
            arguments[name] = scalar.TryGetValue<string>(out var text) ? text : scalar.GetValue<int>();
        }
        var returned = await _functions[selection.Name].InvokeAsync(arguments, cancellationToken);
        var output = returned switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()!,
            _ => throw new ManagedProjectException("copilot_context_invalid")
        };
        var result = new JsonObject { ["text"] = output };
        if (!ValidOutput(selection.Name, selection.Arguments, result))
            throw new ModelGateException("investigator tool output is not in the committed synthetic registry");
        return result;
    }

    private bool ValidArguments(string name, JsonObject arguments)
    {
        if (!_functions.TryGetValue(name, out var function)) return false;
        var schema = function.JsonSchema;
        var properties = schema.GetProperty("properties");
        if (schema.TryGetProperty("required", out var required) &&
            required.EnumerateArray().Any(property => !arguments.ContainsKey(property.GetString()!))) return false;
        foreach (var (key, node) in arguments)
        {
            if (!properties.TryGetProperty(key, out var property) || node is not JsonValue value) return false;
            if (property.GetProperty("type").GetString() switch
            {
                "string" => !value.TryGetValue<string>(out _),
                "integer" => !value.TryGetValue<int>(out _),
                _ => true
            }) return false;
        }
        return true;
    }

    private bool ValidOutput(string name, JsonObject arguments, JsonObject output)
    {
        if (output.Count != 1 || output["text"] is not JsonValue value || !value.TryGetValue<string>(out var text)) return false;
        if (name is SourceReadTools.ListTool or SourceReadTools.GetTool) return SourceOutput(name, arguments, text);
        if (name == CaseReadTools.InventoryTool)
        {
            var inventory = _source.GetInventory(_caseId);
            return text == (inventory.Count == 0 ? "no documents" :
                $"{BoundedInvestigator.UntrustedBegin}\n{string.Join('\n', inventory)}\n{BoundedInvestigator.UntrustedEnd}");
        }
        if (FixedCaseResult(name, text)) return true;
        if (!Unwrap(text, out var document)) return false;
        AuthorizeDocument(document);
        return true;
    }

    private static bool FixedCaseResult(string name, string text) => name switch
    {
        CaseReadTools.InventoryTool => text == "no documents",
        CaseReadTools.RequirementsTool => text == "no approved requirements",
        CaseReadTools.SourcePageTool => text == "denied: document page is not part of this case",
        CaseReadTools.SearchTool => text is "no matches" or "denied: query must be 1 to 200 characters",
        CaseReadTools.StatusTool => text == "unknown",
        _ => false
    } || text == "denied: document text contains an untrusted-data delimiter";

    private void AuthorizeDocument(string text)
    {
        BoundedInvestigator.ValidateDocuments([text]);
        _ = ModelCallGate.Authorize(_manifest, _request with { Evidence = [text] }, _registry);
    }

    private bool SourceOutput(string name, JsonObject arguments, string text)
    {
        try
        {
            using var document = JsonDocument.Parse(Unwrap(text, out var inner) ? inner : text,
                new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
                return !Unwrap(text, out _) && root.EnumerateObject().Count() == 1 && error.ValueKind == JsonValueKind.String &&
                    error.GetString() is "unknown_source" or "forbidden" or "invalid_source_response" or "source_record_limit" or
                        "source_case_mismatch" or "source_unavailable" or "record_not_found" or "untrusted_source_text";
            if (!Unwrap(text, out _) || !SourceReadTools.Allowed(_role, (string)arguments["sourceId"]!)) return false;
            var records = name == SourceReadTools.GetTool
                ? root.ValueKind == JsonValueKind.Object ? new[] { root } : null
                : root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : null;
            if (records is null || records.Length > 100) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in records)
            {
                var canonical = JsonNode.Parse(record.GetRawText())!.ToJsonString();
                if (!CommittedSources.Value.TryGetValue(SyntheticContentRegistry.Sha256Hex(canonical), out var committed) ||
                    committed.CaseId != _caseId || committed.SourceId != (string?)arguments["sourceId"] ||
                    name == SourceReadTools.GetTool && committed.RecordId != (string?)arguments["recordId"] ||
                    !seen.Add(committed.RecordId)) return false;
                committed.Restore(_sourceTools);
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool Unwrap(string text, out string inner)
    {
        var begin = BoundedInvestigator.UntrustedBegin + "\n";
        var end = "\n" + BoundedInvestigator.UntrustedEnd;
        if (text.StartsWith(begin, StringComparison.Ordinal) && text.EndsWith(end, StringComparison.Ordinal))
        {
            inner = text[begin.Length..^end.Length];
            return !inner.Contains(BoundedInvestigator.UntrustedBegin, StringComparison.Ordinal) &&
                !inner.Contains(BoundedInvestigator.UntrustedEnd, StringComparison.Ordinal);
        }
        inner = "";
        return false;
    }

    private static IReadOnlyDictionary<string, CommittedRecord> BuildSourceRegistry()
    {
        var records = new Dictionary<string, CommittedRecord>(StringComparer.Ordinal);
        Add(new TechnicalSource().CommittedRecords);
        Add(new MaintenanceSource().CommittedRecords);
        Add(new FinanceSource().CommittedRecords);
        Add(new RecordsSource().CommittedRecords);
        Add(new ContractsSource().CommittedRecords);
        return records;

        void Add<TPayload>(IReadOnlyList<SourceRecord<TPayload>> values) where TPayload : class
        {
            foreach (var record in values)
            {
                var json = SourceReadTools.SerializeRecord(record);
                records.Add(SyntheticContentRegistry.Sha256Hex(json),
                    new(record.CaseId, record.SourceId, record.RecordId, tools => tools.RestoreCommittedWitness(record)));
            }
        }
    }
}
