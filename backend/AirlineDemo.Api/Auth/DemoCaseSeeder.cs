using AirlineDemo.Api.Persistence;
using Microsoft.Extensions.Configuration;

namespace AirlineDemo.Api.Auth;

public static class DemoCaseSeeder
{
    private static readonly string[] CaseIds =
    [
        "case-001",
        "case-002",
        "cp-case-a",
        "cp-case-b",
        "cp-case-c",
    ];

    public static void Seed(IConfiguration configuration, ICaseStateStore store)
    {
        if (!bool.TryParse(configuration["Demo:SeedCases"], out var enabled) || !enabled)
        {
            return;
        }

        foreach (var caseId in CaseIds)
        {
            if (store.OwnerOf(caseId) is not null)
            {
                continue;
            }

            try
            {
                store.CreateCase(caseId, "presenter");
            }
            catch (InvalidOperationException)
            {
                // Another startup or process may have created the case between the owner check and create.
            }
        }
    }
}
