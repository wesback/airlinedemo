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
        var seedCases = bool.TryParse(configuration["Demo:SeedCases"], out var enabled) && enabled;
        var arrivalJourney = bool.TryParse(configuration["Demo:ArrivalJourney"], out var arrivalEnabled) && arrivalEnabled;
        if (!seedCases && !arrivalJourney)
        {
            return;
        }

        foreach (var caseId in seedCases ? CaseIds : ["case-001"])
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
