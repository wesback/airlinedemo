using System.Text;
using AirlineDemo.Api.Diagnostics;

namespace AirlineDemo.Api.Tests;

public sealed class TelemetryPrivacyTests
{
    private static readonly byte[] TestKey = Encoding.UTF8.GetBytes("synthetic-test-key-not-a-secret");

    private static readonly string[] Canaries =
    [
        "CANARY-PROMPT-7f3a", "CANARY-DOC-9b21", "CANARY-PASSCODE-4d10",
        "CANARY-COOKIE-2e88", "CANARY-TOKEN-c6f0", "canary.person@example.invalid",
    ];

    [Theory]
    [InlineData("prompt")]
    [InlineData("gen_ai.prompt")]
    [InlineData("document.text")]
    [InlineData("passcode")]
    [InlineData("cookie")]
    [InlineData("authorization")]
    [InlineData("token")]
    [InlineData("user.email")]
    [InlineData("PASSCODE")]
    public void Denylisted_keys_are_dropped_regardless_of_case(string key)
    {
        var redacted = TelemetryRedaction.Redact(new[] { new KeyValuePair<string, object?>(key, "value") });

        Assert.DoesNotContain(key, redacted.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Canaries_never_appear_in_any_output()
    {
        var redacted = TelemetryRedaction.Redact(new[]
        {
            new KeyValuePair<string, object?>("prompt", Canaries[0]),
            new KeyValuePair<string, object?>("document.text", Canaries[1]),
            new KeyValuePair<string, object?>("passcode", Canaries[2]),
            new KeyValuePair<string, object?>("cookie", Canaries[3]),
            new KeyValuePair<string, object?>("token", Canaries[4]),
            new KeyValuePair<string, object?>("user.email", Canaries[5]),
            new KeyValuePair<string, object?>("detail", $"cookie: {Canaries[3]} token={Canaries[4]} owner {Canaries[5]}"),
        });

        var output = string.Join("|", redacted.Values);
        Assert.Single(redacted);
        foreach (var canary in Canaries)
        {
            Assert.DoesNotContain(canary, output);
        }

        Assert.Contains(TelemetryRedaction.Redacted, output);
    }

    [Fact]
    public void Injected_control_characters_cannot_forge_lines_or_fields()
    {
        var sanitized = TelemetryRedaction.Sanitize("ok\r\nsecurity.auth_scope_failure=forged\u202Ehidden\u0000");

        foreach (var control in new[] { '\r', '\n', '\u202E', '\u0000' })
        {
            Assert.DoesNotContain(control, sanitized);
        }
    }

    [Fact]
    public void Opaque_actor_is_stable_keyed_and_hides_identity()
    {
        var first = TelemetryRedaction.OpaqueActor("synthetic-analyst-01", TestKey);

        Assert.Equal(first, TelemetryRedaction.OpaqueActor("synthetic-analyst-01", TestKey));
        Assert.NotEqual(first, TelemetryRedaction.OpaqueActor("synthetic-analyst-02", TestKey));
        Assert.NotEqual(first, TelemetryRedaction.OpaqueActor("synthetic-analyst-01", Encoding.UTF8.GetBytes("other-key")));
        Assert.StartsWith("actor-", first);
        Assert.DoesNotContain("analyst", first);
    }

    [Fact]
    public void Resource_attributes_include_required_service_fields()
    {
        var resource = TelemetryRedaction.ResourceAttributes("1.2.3", "demo");

        Assert.Equal(TelemetryRedaction.ServiceName, resource["service.name"]);
        Assert.Equal("1.2.3", resource["service.version"]);
        Assert.Equal("demo", resource["deployment.environment"]);
    }
}
