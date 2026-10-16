using System.Reflection;
using AirlineDemo.Api.SourceSystems;

namespace AirlineDemo.Api.Tests;

public sealed class SourcePortContractTests
{
    private sealed record TestPayload(string Value);

    private static readonly string[] MutatingPrefixes =
    [
        "Write", "Update", "Delete", "Remove", "Save", "Create", "Put", "Set", "Add", "Insert", "Patch", "Upsert"
    ];

    private static SourceRecord<TestPayload> ValidRecord() => new()
    {
        SourceId = "technical",
        RecordId = "tech-001",
        RecordVersion = 1,
        AsOf = new DateOnly(2026, 10, 1),
        CaseId = "case-a",
        Kind = "airworthiness",
        Stale = false,
        Payload = new TestPayload("ok"),
        Synthetic = true,
    };

    [Fact]
    public void PortExposesCaseScopedReadOperationsOnly()
    {
        var port = typeof(ISourceSystem<>);
        var methods = port.GetMethods(BindingFlags.Public | BindingFlags.Instance);

        Assert.Equal(new[] { "GetAsync", "ListAsync" }, methods.Select(m => m.Name).OrderBy(n => n).ToArray());
        Assert.Empty(port.GetProperties());
        Assert.Empty(port.GetEvents());

        foreach (var method in methods)
        {
            Assert.DoesNotContain(MutatingPrefixes, prefix => method.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        var list = port.GetMethod("ListAsync", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(list);
        var listParams = list!.GetParameters();
        Assert.NotEmpty(listParams);
        Assert.Equal("caseId", listParams[0].Name);
        Assert.Equal(typeof(string), listParams[0].ParameterType);

        var get = port.GetMethod("GetAsync", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(get);
        var getParams = get!.GetParameters();
        Assert.True(getParams.Length >= 2);
        Assert.Equal("caseId", getParams[0].Name);
        Assert.Equal(typeof(string), getParams[0].ParameterType);
        Assert.Equal("recordId", getParams[1].Name);
        Assert.Equal(typeof(string), getParams[1].ParameterType);
    }

    [Fact]
    public void RecordRequiresCompleteProvenanceAndSyntheticFlag()
    {
        Assert.Empty(ValidRecord().Validate("case-a"));

        var invalid = new (string Name, SourceRecord<TestPayload> Record, string requestedCaseId)[]
        {
            ("empty sourceId", ValidRecord() with { SourceId = "" }, "case-a"),
            ("blank recordId", ValidRecord() with { RecordId = " " }, "case-a"),
            ("zero recordVersion", ValidRecord() with { RecordVersion = 0 }, "case-a"),
            ("negative recordVersion", ValidRecord() with { RecordVersion = -1 }, "case-a"),
            ("missing asOf", ValidRecord() with { AsOf = default }, "case-a"),
            ("empty caseId", ValidRecord() with { CaseId = "" }, "case-a"),
            ("empty kind", ValidRecord() with { Kind = "" }, "case-a"),
            ("null payload", ValidRecord() with { Payload = null! }, "case-a"),
            ("synthetic false", ValidRecord() with { Synthetic = false }, "case-a"),
            ("case mismatch", ValidRecord(), "case-b"),
        };

        foreach (var (name, record, requestedCaseId) in invalid)
        {
            Assert.True(record.Validate(requestedCaseId).Count > 0, $"Expected rejection for: {name}");
        }

        var mismatch = ValidRecord().Validate("case-b");
        Assert.Contains(mismatch, problem => problem.Contains("does not match the requested case", StringComparison.Ordinal));
    }

    [Fact]
    public void UnavailableAndUnknownSourcesHaveDistinctTypedResults()
    {
        SourceReadResult<IReadOnlyList<SourceRecord<TestPayload>>> unavailable =
            new SourceUnavailable<IReadOnlyList<SourceRecord<TestPayload>>>("technical");
        SourceReadResult<IReadOnlyList<SourceRecord<TestPayload>>> unknown =
            new UnknownSource<IReadOnlyList<SourceRecord<TestPayload>>>("technical");
        SourceReadResult<IReadOnlyList<SourceRecord<TestPayload>>> empty =
            new SourceReadSucceeded<IReadOnlyList<SourceRecord<TestPayload>>>(Array.Empty<SourceRecord<TestPayload>>());

        Assert.IsType<SourceUnavailable<IReadOnlyList<SourceRecord<TestPayload>>>>(unavailable);
        Assert.IsType<UnknownSource<IReadOnlyList<SourceRecord<TestPayload>>>>(unknown);
        Assert.IsType<SourceReadSucceeded<IReadOnlyList<SourceRecord<TestPayload>>>>(empty);

        Assert.False(unavailable is SourceReadSucceeded<IReadOnlyList<SourceRecord<TestPayload>>>);
        Assert.False(unknown is SourceReadSucceeded<IReadOnlyList<SourceRecord<TestPayload>>>);
        Assert.NotEqual(unavailable.GetType(), unknown.GetType());
        Assert.Equal("source_unavailable", SourceUnavailable<IReadOnlyList<SourceRecord<TestPayload>>>.ErrorCode);
    }
}
