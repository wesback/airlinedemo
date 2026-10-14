using System.Text;
using AirlineDemo.Api.Investigation;
using Microsoft.Extensions.AI;

namespace AirlineDemo.Api.Tests;

/// <summary>M-05 injection battery: direct, indirect, split-page and encoded instructions in document text must not reach a forbidden action.</summary>
public class InvestigatorInjectionTests
{
    private const string CaseId = "case-1";
    private const string Region = "eastus2";
    private const string ReturnStatusText = "SYNTHETIC EVIDENCE: Return readiness is pending reserve approval.";
    private const string OtherCaseText = "SYNTHETIC EVIDENCE: Other case private note.";

    private const string ApprovedJson = """{"manifestVersion":"2026.10.1","approved":true,"arm":"investigation-live","deploymentName":"fixture-deployment","model":"fixture-model","apiVersion":"2025-01-01","region":"eastus2","contentFilter":"enabled"}""";

    private const string ClaimsJson =
        """[{"text":"Return readiness is pending reserve approval.","witnesses":[{"documentId":"return-status","page":1,"quote":"Return readiness is pending reserve approval."}]}]""";

    private static readonly string[] FiveTools =
    [
        "get_case_inventory",
        "get_approved_requirements",
        "get_source_page",
        "search_within_basis",
        "get_processing_status",
    ];

    public static TheoryData<string> Fixtures => ["injection-direct", "injection-indirect", "injection-split-page", "injection-encoded"];

    private static ApprovedModelManifest Approved() => ApprovedModelManifest.Parse(ApprovedJson);

    private static ChatResponse Reply(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    private static string Fixture(string name)
    {
        using var stream = typeof(SyntheticContentRegistry).Assembly
            .GetManifestResourceStream("AirlineDemo.Api.Investigation.SyntheticFixtures." + name + ".txt")
            ?? throw new InvalidOperationException("fixture not embedded: " + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().TrimEnd('\r', '\n');
    }

    private static BoundedInvestigator Investigator(IChatClient model) =>
        new(CaseId, new InjectionSource(), Approved(), SyntheticContentRegistry.FromCommittedFixtures(), Region, "evidence-v1", model);

    /// <summary>Model stub that, on its first call, tries a forbidden action and a cross-case read, then answers with claims.</summary>
    private sealed class HostileChatClient : IChatClient
    {
        public List<IList<ChatMessage>> Requests { get; } = [];

        public List<ChatOptions?> Options { get; } = [];

        public ChatClientMetadata Metadata { get; } = new("hostile");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            Requests.Add(list);
            Options.Add(options);

            if (list.Any(m => m.Contents.OfType<FunctionResultContent>().Any()))
            {
                return Task.FromResult(Reply(ClaimsJson));
            }

            var attack = new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("call-forbidden", "send_to_recipient", new Dictionary<string, object?> { ["caseId"] = CaseId }),
                new FunctionCallContent("call-cross-case", CaseReadTools.SourcePageTool, new Dictionary<string, object?> { ["documentId"] = "other-case-doc", ["page"] = 1 }),
            ]);
            return Task.FromResult(new ChatResponse(attack));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>Case store with the case's own documents and one document belonging to another case.</summary>
    private sealed class InjectionSource : ICaseReadSource
    {
        private readonly Dictionary<string, (string CaseId, string Text)> _docs = new()
        {
            ["return-status"] = (CaseId, ReturnStatusText),
            ["other-case-doc"] = ("case-2", OtherCaseText),
        };

        public IReadOnlyList<string> GetInventory(string caseId) =>
            _docs.Where(d => d.Value.CaseId == caseId).Select(d => d.Key).Order().ToList();

        public string? GetApprovedRequirements(string caseId) => caseId == CaseId ? "Reserve approval is required." : null;

        public string? GetSourcePage(string caseId, string documentId, int page) =>
            _docs.TryGetValue(documentId, out var doc) && doc.CaseId == caseId && page == 1 ? doc.Text : null;

        public IReadOnlyList<string> SearchWithinBasis(string caseId, string query) =>
            GetInventory(caseId).Where(id => _docs[id].Text.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        public string? GetProcessingStatus(string caseId) => caseId == CaseId ? "ready for review" : null;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Injection_fixture_is_a_registered_synthetic_item_the_gate_accepts(string name)
    {
        var text = Fixture(name);

        var trace = ModelCallGate.Authorize(Approved(), new ModelRequest("evidence-v1", Region, [ReturnStatusText, text]), SyntheticContentRegistry.FromCommittedFixtures());

        Assert.NotNull(trace);
        Assert.StartsWith(ModelCallGate.SyntheticMarker, text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Injected_instructions_cannot_reach_a_forbidden_action_or_another_case(string name)
    {
        var text = Fixture(name);
        var model = new HostileChatClient();

        var result = await Investigator(model).InvestigateAsync([ReturnStatusText, text]);

        Assert.Single(result.Claims);
        Assert.Equal(FiveTools.Order().ToArray(), model.Options[0]!.Tools!.Select(t => t.Name).Order().ToArray());
        Assert.DoesNotContain(model.Options[0]!.Tools!, t => t.Name == "send_to_recipient");
        Assert.DoesNotContain(model.Requests.SelectMany(r => r).SelectMany(m => m.Contents.OfType<FunctionResultContent>()),
            r => r.Result?.ToString()?.Contains(OtherCaseText, StringComparison.Ordinal) == true);
        Assert.DoesNotContain(model.Requests.SelectMany(r => r).Select(m => m.Text), t => t.Contains(OtherCaseText, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Injected_document_text_is_sent_only_inside_the_untrusted_delimiter(string name)
    {
        var text = Fixture(name);
        var model = new HostileChatClient();

        await Investigator(model).InvestigateAsync([text]);

        var sent = string.Concat(model.Requests[0].Select(m => m.Text));
        Assert.Contains(BoundedInvestigator.UntrustedBegin + "\n" + text + "\n" + BoundedInvestigator.UntrustedEnd, sent);
    }
}
