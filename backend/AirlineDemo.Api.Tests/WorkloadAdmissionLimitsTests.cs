using AirlineDemo.Api.Admission;

namespace AirlineDemo.Api.Tests;

public sealed class WorkloadAdmissionLimitsTests
{
    [Fact]
    public void Package_rate_limit_uses_authenticated_session_and_rolling_window()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-16T12:00:00Z"));
        var limits = new WorkloadAdmissionLimits(clock);

        for (var i = 0; i < WorkloadAdmissionLimits.PackageAttemptsPerWindow; i++)
        {
            Assert.True(limits.TryAdmitPackage("presenter-a"));
        }

        Assert.False(limits.TryAdmitPackage("presenter-a"));
        Assert.True(limits.TryAdmitPackage("presenter-b"));

        var rejected = Assert.Single(limits.Events);
        Assert.Equal(WorkloadAdmissionLimits.PackageKind, rejected.Kind);
        Assert.Equal("presenter-a", rejected.Subject);
        Assert.Equal("package submission limit exceeded", rejected.Error);

        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.True(limits.TryAdmitPackage("presenter-a"));
    }

    [Fact]
    public void Concurrent_investigations_are_bounded_and_release_slots()
    {
        var limits = new WorkloadAdmissionLimits(TimeProvider.System);

        using var first = limits.TryAdmitInvestigation();
        using var second = limits.TryAdmitInvestigation();
        Assert.NotNull(first);
        Assert.NotNull(second);

        Assert.Null(limits.TryAdmitInvestigation());
        var rejected = Assert.Single(limits.Events);
        Assert.Equal("investigation concurrency limit exceeded", rejected.Error);

        first!.Dispose();
        first.Dispose();

        using var third = limits.TryAdmitInvestigation();
        Assert.NotNull(third);
        Assert.Null(limits.TryAdmitInvestigation());
    }

    [Fact]
    public void Investigation_slot_is_released_when_the_holder_throws()
    {
        var limits = new WorkloadAdmissionLimits(TimeProvider.System);

        Assert.Throws<InvalidOperationException>(() => HoldAndThrow(limits));

        using var a = limits.TryAdmitInvestigation();
        using var b = limits.TryAdmitInvestigation();
        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    private static void HoldAndThrow(WorkloadAdmissionLimits limits)
    {
        using var slot = limits.TryAdmitInvestigation();
        throw new InvalidOperationException("boom");
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
