using System.Text.Json.Nodes;
using AirlineDemo.Deployment;

namespace AirlineDemo.Api.Tests;

public sealed class ManagedCreateRecoveryTests
{
    [Fact]
    public async Task ManagedCreateRecovery_UsesExactReservationAndAttemptAcrossAllPages()
    {
        var cursors = new List<string?>();
        var admitted = 0;
        var result = await ManagedCreateRecovery.ResolveConversationAsync("reservation-1", "attempt-1", (after, _) =>
        {
            cursors.Add(after);
            return Task.FromResult(after is null
                ? Page(true, Item("unrelated", "reservation-1", "other"), Item("candidate", "reservation-1", "attempt-1"))
                : Page(false, Item("other", "different", "attempt-1")));
        }, () => ++admitted <= 3, TestContext.Current.CancellationToken);
        Assert.Equal("candidate", result);
        Assert.Equal([null, "candidate"], cursors);
        Assert.Equal(2, admitted);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("multiple")]
    [InlineData("unsupported")]
    [InlineData("incomplete")]
    [InlineData("repeated")]
    [InlineData("malformed-metadata")]
    public async Task ManagedCreateRecovery_AmbiguousOrUnsupportedEnumerationStaysUnresolved(string scenario)
    {
        var calls = 0;
        var result = await ManagedCreateRecovery.ResolveConversationAsync("reservation-1", "attempt-1", (_, _) =>
        {
            calls++;
            var candidate = Item("candidate", "reservation-1", "attempt-1");
            return Task.FromResult(scenario switch
            {
                "none" => Page(false, Item("other", "reservation-1", "different")),
                "multiple" => Page(false, candidate, Item("second", "reservation-1", "attempt-1")),
                "unsupported" => new JsonObject { ["data"] = new JsonArray(candidate) },
                "incomplete" => new JsonObject { ["data"] = new JsonArray(candidate), ["has_more"] = true },
                "repeated" => Page(true, candidate),
                _ => Page(false, new JsonObject { ["id"] = "candidate", ["metadata"] = new JsonObject { ["reservation_id"] = 1 } })
            });
        }, () => true, TestContext.Current.CancellationToken);
        Assert.Null(result);
        Assert.Equal(scenario == "repeated" ? 2 : 1, calls);
    }

    [Fact]
    public async Task ManagedCreateRecovery_RequestBudgetCannotBecomeSuccessfulPartialEnumeration()
    {
        var calls = 0;
        var admissions = 0;
        var result = await ManagedCreateRecovery.ResolveConversationAsync("reservation-1", "attempt-1", (_, _) =>
        {
            calls++;
            return Task.FromResult(Page(true, Item("candidate", "reservation-1", "attempt-1")));
        }, () => ++admissions <= 1, TestContext.Current.CancellationToken);
        Assert.Null(result);
        Assert.Equal((1, 2), (calls, admissions));
    }

    private static JsonObject Item(string id, string reservation, string attempt) => new()
    {
        ["id"] = id, ["metadata"] = new JsonObject { ["reservation_id"] = reservation, ["attempt_id"] = attempt }
    };

    private static JsonObject Page(bool more, params JsonObject[] items) => new()
    {
        ["data"] = new JsonArray(items.Select(item => (JsonNode)item).ToArray()),
        ["has_more"] = more, ["last_id"] = items.LastOrDefault()?["id"]?.DeepClone()
    };
}
