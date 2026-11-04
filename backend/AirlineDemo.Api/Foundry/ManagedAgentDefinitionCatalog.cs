using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Copilot;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.SourceSystems;
using Microsoft.Extensions.AI;

namespace AirlineDemo.Api.Foundry;

public static class ManagedAgentDefinitionCatalog
{
    public static string CopilotInstructions => CopilotInputContract.SystemInstructions;
    public static string InvestigatorInstructions => BoundedInvestigator.Instructions;

    public static JsonArray CopilotTools() =>
        FunctionTools(CopilotQuestions.ToolDeclarations.OfType<AIFunctionDeclaration>()
            .Select(function => (function.Name, function.Description, function.JsonSchema)));

    public static JsonArray InvestigatorTools()
    {
        var caseTools = new CaseReadTools("definition", EmptyCaseSource.Instance, NoInvestigationLimits.Instance);
        var sources = new InvestigationSourceSystems(
            new EmptySource<TechnicalPayload>(),
            new EmptySource<MaintenancePayload>(),
            new EmptySource<FinancePayload>(),
            new EmptySource<RecordsPayload>(),
            new EmptySource<ContractsPayload>());
        var sourceTools = new SourceReadTools("definition", "presenter", sources, NoInvestigationLimits.Instance);
        return FunctionTools(caseTools.Functions.Concat(sourceTools.Functions)
            .Select(function => (function.Name, function.Description, function.JsonSchema)));
    }

    private static JsonArray FunctionTools(IEnumerable<(string Name, string Description, JsonElement JsonSchema)> functions)
    {
        var tools = new JsonArray();
        foreach (var function in functions)
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = function.Name,
                ["description"] = function.Description,
                ["parameters"] = JsonNode.Parse(function.JsonSchema.GetRawText()),
            });
        }

        return tools;
    }

    private sealed class EmptyCaseSource : ICaseReadSource
    {
        public static EmptyCaseSource Instance { get; } = new();

        public IReadOnlyList<string> GetInventory(string caseId) => [];
        public string? GetApprovedRequirements(string caseId) => null;
        public string? GetSourcePage(string caseId, string documentId, int page) => null;
        public IReadOnlyList<string> SearchWithinBasis(string caseId, string query) => [];
        public string? GetProcessingStatus(string caseId) => null;
    }

    private sealed class EmptySource<TPayload> : ISourceSystem<TPayload> where TPayload : class
    {
        public Task<SourceReadResult<IReadOnlyList<SourceRecord<TPayload>>>> ListAsync(
            string caseId, CancellationToken cancellationToken = default, int? maxRecords = null) =>
            Task.FromResult<SourceReadResult<IReadOnlyList<SourceRecord<TPayload>>>>(
                new SourceUnavailable<IReadOnlyList<SourceRecord<TPayload>>>("definition"));

        public Task<SourceReadResult<SourceRecord<TPayload>>> GetAsync(
            string caseId, string recordId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SourceReadResult<SourceRecord<TPayload>>>(new SourceUnavailable<SourceRecord<TPayload>>("definition"));
    }
}
