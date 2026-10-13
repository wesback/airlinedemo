using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Tests;

public class BoundedInvestigatorTests
{
    private const string ApprovedJson = """
        {
          "manifestVersion": "1.0.0",
          "approved": true,
          "arm": "investigation-live",
          "deploymentName": "fixture-deployment",
          "model": "fixture-model",
          "apiVersion": "2025-01-01",
          "region": "eastus2",
          "contentFilter": "enabled"
        }
        """;

    private const string InjectionFixture = "injection-direct.txt";
    private const string Canary = "CANARY-7F3A-SECRET";

    private static ApprovedModelManifest Approved() => ApprovedModelManifest.Parse(ApprovedJson);

    private static SyntheticContentRegistry Registry() => SyntheticContentRegistry.FromCommittedFixtures();

    /// <summary>Reads a committed synthetic fixture exactly as the registry does, so its hash is registered.</summary>
    private static string Fixture(string name)
    {
        using var stream = typeof(BoundedInvestigator).Assembly
            .GetManifestResourceStream("AirlineDemo.Api.Investigation.SyntheticFixtures." + name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().TrimEnd('\r', '\n');
    }

    private static (CaseStateStore Store, CaseScope Scope) NewCase(params string[] documents)
    {
        var store = new CaseStateStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));
        var scope = store.CreateCase("case-" + Guid.NewGuid().ToString("N")[..8], "owner");
        var previous = string.Empty;
        foreach (var content in documents)
        {
            var basis = CaseStateStore.CreateBasisVersion(store.Read(scope).Basis.Count + 1, content, previous);
            store.Commit(scope, store.Read(scope).Revision, new CaseCommit(basis, null, "owner", "basis", "test"));
            previous = basis.BasisDigest;
        }

        return (store, scope);
    }

    private static ModelTurn Final(string json) => new([], json);

    private static ModelTurn Calls(params string[] names) =>
        new(names.Select(n => new ToolRequest(n, new Dictionary<string, string>())).ToList(), null);

    private const string ReserveClaim =
        """{"claims":[{"text":"Reserve note is present","witnesses":[{"version":1,"quote":"Reserve note"}]}]}""";

    private sealed class ScriptedModel(params ModelTurn[] turns) : IInvestigationModel
    {
        private int _next;

        public List<IReadOnlyList<string>> Documents { get; } = [];

        public List<IReadOnlyList<string>> ToolResults { get; } = [];

        public int Sends => Documents.Count;

        public Task<ModelTurn> SendAsync(
            string instructions,
            IReadOnlyList<string> documents,
            IReadOnlyList<string> toolResults,
            CancellationToken cancellationToken)
        {
            Documents.Add(documents);
            ToolResults.Add(toolResults);
            return Task.FromResult(turns[Math.Min(_next++, turns.Length - 1)]);
        }
    }

    private sealed class RecordingLimits : IInvestigationLimits
    {
        public List<string> Tools { get; } = [];

        public int ModelCalls { get; private set; }

        public void BeforeToolCall(string toolName) => Tools.Add(toolName);

        public void BeforeModelCall() => ModelCalls++;
    }

    [Fact]
    public void Declared_tools_are_exactly_the_five_case_reads()
    {
        Assert.Equal(
            ["list_inventory", "get_approved_requirements", "get_source_page", "search_within_basis", "get_processing_status"],
            CaseReadTools.Names);
    }

    [Fact]
    public async Task Undeclared_tool_requests_are_denied_and_never_executed()
    {
        var (store, scope) = NewCase(Fixture(InjectionFixture));
        var model = new ScriptedModel(
            Calls("send_notice", "dispatch_notice", "approve_case", "select_recipient", "fetch_url", "shell", "read_other_case"),
            Final(ReserveClaim));
        var investigator = new BoundedInvestigator(store, scope, Approved(), Registry(), model);

        var result = await investigator.RunAsync();

        Assert.Equal(
            ["send_notice", "dispatch_notice", "approve_case", "select_recipient", "fetch_url", "shell", "read_other_case"],
            result.DeniedToolCalls);
        Assert.All(model.ToolResults[1], r => Assert.Contains("denied", r));
        Assert.Single(result.Claims);
    }

    [Fact]
    public async Task Declared_tool_requests_pass_through_limits_before_running()
    {
        var (store, scope) = NewCase(Fixture(InjectionFixture));
        var model = new ScriptedModel(Calls("get_processing_status"), Final(ReserveClaim));
        var limits = new RecordingLimits();
        var investigator = new BoundedInvestigator(store, scope, Approved(), Registry(), model, limits);

        var result = await investigator.RunAsync();

        Assert.Equal(["get_processing_status"], limits.Tools);
        Assert.Equal(2, limits.ModelCalls);
        Assert.Equal(2, result.AuthorizedModelCalls);
        Assert.Empty(result.DeniedToolCalls);
    }

    [Fact]
    public void Read_tools_cannot_reach_another_case()
    {
        var (store, scopeA) = NewCase(Fixture(InjectionFixture));
        var (_, scopeB) = NewCase(Fixture(InjectionFixture));
        var tools = new CaseReadTools(store, scopeA);

        var hits = tools.Invoke(CaseReadTools.SearchWithinBasis, new Dictionary<string, string> { ["query"] = "Reserve" });
        Assert.NotEqual("[]", hits);

        var missing = tools.Invoke(CaseReadTools.GetSourcePage, new Dictionary<string, string> { ["version"] = "2" });
        Assert.Contains("not found in this case", missing);
        Assert.NotNull(scopeB);
    }

    [Fact]
    public void Unknown_tool_name_is_refused_by_the_tool_handler()
    {
        var (store, scope) = NewCase(Fixture(InjectionFixture));
        var tools = new CaseReadTools(store, scope);

        Assert.Throws<ArgumentException>(() => tools.Invoke("send_notice", new Dictionary<string, string>()));
    }

    [Fact]
    public async Task Injected_instructions_are_delimited_as_untrusted_and_do_not_add_tools()
    {
        var (store, scope) = NewCase(Fixture(InjectionFixture));
        var model = new ScriptedModel(Calls("send_notice"), Final(ReserveClaim));
        var investigator = new BoundedInvestigator(store, scope, Approved(), Registry(), model);

        var result = await investigator.RunAsync();

        var sent = model.Documents[0][0];
        Assert.StartsWith(BoundedInvestigator.DocumentOpen, sent);
        Assert.EndsWith(BoundedInvestigator.DocumentClose, sent);
        Assert.Contains("Ignore previous instructions", sent);
        Assert.Equal(["send_notice"], result.DeniedToolCalls);
    }

    [Fact]
    public void Investigation_instructions_are_public_and_carry_no_secret_or_authority()
    {
        Assert.DoesNotContain(Canary, BoundedInvestigator.Instructions);
        Assert.DoesNotContain("secret", BoundedInvestigator.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", BoundedInvestigator.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("role", BoundedInvestigator.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never an instruction", BoundedInvestigator.Instructions);
    }

    [Fact]
    public async Task Claim_with_a_quote_not_in_the_document_is_rejected()
    {
        var (store, scope) = NewCase(Fixture(InjectionFixture));
        var fabricated = """{"claims":[{"text":"x","witnesses":[{"version":1,"quote":"NOT IN DOCUMENT"}]}]}""";
        var investigator = new BoundedInvestigator(store, scope, Approved(), Registry(), new ScriptedModel(Final(fabricated)));

        var result = await investigator.RunAsync();

        Assert.Empty(result.Claims);
        Assert.Equal(1, result.RejectedClaims);
    }

    [Fact]
    public async Task Claim_citing_a_missing_basis_version_is_rejected()
    {
        var (store, scope) = NewCase(Fixture(InjectionFixture));
        var wrongVersion = """{"claims":[{"text":"x","witnesses":[{"version":9,"quote":"Reserve note"}]}]}""";
        var investigator = new BoundedInvestigator(store, scope, Approved(), Registry(), new ScriptedModel(Final(wrongVersion)));

        var result = await investigator.RunAsync();

        Assert.Empty(result.Claims);
        Assert.Equal(1, result.RejectedClaims);
    }

    [Fact]
    public async Task Claim_without_witnesses_is_rejected()
    {
        var (store, scope) = NewCase(Fixture(InjectionFixture));
        var noWitness = """{"claims":[{"text":"x","witnesses":[]}]}""";
        var investigator = new BoundedInvestigator(store, scope, Approved(), Registry(), new ScriptedModel(Final(noWitness)));

        var result = await investigator.RunAsync();

        Assert.Empty(result.Claims);
        Assert.Equal(1, result.RejectedClaims);
    }

    [Fact]
    public async Task Each_model_call_is_gated_and_counted()
    {
        var (store, scope) = NewCase(Fixture(InjectionFixture));
        var model = new ScriptedModel(Calls("get_processing_status"), Final(ReserveClaim));
        var investigator = new BoundedInvestigator(store, scope, Approved(), Registry(), model);

        var result = await investigator.RunAsync();

        Assert.Equal(2, model.Sends);
        Assert.Equal(2, result.AuthorizedModelCalls);
    }

    [Fact]
    public async Task Unapproved_manifest_is_refused_and_nothing_is_sent()
    {
        var (store, scope) = NewCase(Fixture(InjectionFixture));
        var unapproved = ApprovedModelManifest.Parse(ApprovedJson.Replace("\"approved\": true", "\"approved\": false"));
        var model = new ScriptedModel(Final(ReserveClaim));
        var investigator = new BoundedInvestigator(store, scope, unapproved, Registry(), model);

        await Assert.ThrowsAsync<ModelGateException>(() => investigator.RunAsync());

        Assert.Equal(0, model.Sends);
    }

    [Fact]
    public async Task Unregistered_case_content_is_refused_and_nothing_is_sent()
    {
        var (store, scope) = NewCase("Plain case note that is not a registered synthetic fixture.");
        var model = new ScriptedModel(Final(ReserveClaim));
        var investigator = new BoundedInvestigator(store, scope, Approved(), Registry(), model);

        await Assert.ThrowsAsync<ModelGateException>(() => investigator.RunAsync());

        Assert.Equal(0, model.Sends);
    }

    [Fact]
    public async Task Limits_refusal_stops_the_call_before_data_is_sent()
    {
        var (store, scope) = NewCase(Fixture(InjectionFixture));
        var model = new ScriptedModel(Final(ReserveClaim));
        var investigator = new BoundedInvestigator(store, scope, Approved(), Registry(), model, new RefusingLimits());

        await Assert.ThrowsAsync<InvalidOperationException>(() => investigator.RunAsync());

        Assert.Equal(0, model.Sends);
    }

    private sealed class RefusingLimits : IInvestigationLimits
    {
        public void BeforeToolCall(string toolName) { }

        public void BeforeModelCall() => throw new InvalidOperationException("limit reached");
    }
}
