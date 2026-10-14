using AirlineDemo.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public sealed class PersistenceWiringTests
{
    private static WebApplicationFactory<Program> CreateFactory(string caseStatePath, params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var values = new Dictionary<string, string?> { ["CaseState:Path"] = caseStatePath };
                foreach (var (key, value) in settings)
                {
                    values[key] = value;
                }
                config.AddInMemoryCollection(values);
            }));

    private static string NewCaseStatePath() =>
        Path.Combine(Path.GetTempPath(), $"persistence-wiring-{Guid.NewGuid():N}", "cases.json");

    [Fact]
    public void Case_state_survives_host_restart_with_the_same_file_store()
    {
        var path = NewCaseStatePath();
        using (var first = CreateFactory(path))
        {
            first.Services.GetRequiredService<ICaseStateStore>().CreateCase("case-restart", "presenter");
        }

        using var second = CreateFactory(path);
        Assert.Equal("presenter", second.Services.GetRequiredService<ICaseStateStore>().OwnerOf("case-restart"));
    }

    [Fact]
    public void Defaults_are_file_for_case_state_and_memory_for_evidence()
    {
        using var factory = CreateFactory(NewCaseStatePath());

        Assert.IsType<CaseStateStore>(factory.Services.GetRequiredService<ICaseStateStore>());
        Assert.IsType<InMemoryEvidenceBlobStore>(factory.Services.GetRequiredService<IEvidenceBlobStore>());
    }

    [Fact]
    public void Unknown_case_provider_fails_startup_with_a_clear_message()
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            using var factory = CreateFactory(NewCaseStatePath(), ("Persistence:Provider", "Bogus"));
            _ = factory.Services.GetRequiredService<ICaseStateStore>();
        });

        Assert.Contains("Persistence:Provider 'Bogus' is not supported", ex.ToString());
    }

    [Fact]
    public void Unknown_evidence_provider_fails_startup_with_a_clear_message()
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            using var factory = CreateFactory(NewCaseStatePath(), ("Persistence:EvidenceProvider", "Bogus"));
            _ = factory.Services.GetRequiredService<IEvidenceBlobStore>();
        });

        Assert.Contains("Persistence:EvidenceProvider 'Bogus' is not supported", ex.ToString());
    }
}
