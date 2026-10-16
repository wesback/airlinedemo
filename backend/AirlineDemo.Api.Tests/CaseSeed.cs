using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Tests;

/// <summary>Builds a temporary file-backed case store with the demo cases the auth tests exercise.</summary>
internal static class CaseSeed
{
    public static string CreateFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"case-state-{Guid.NewGuid():N}.json");
        var store = new CaseStateStore(path);
        Seed(store, "case-001", "presenter");
        Seed(store, "case-002", "presenter");
        Seed(store, "case-003", "other-owner");
        return path;
    }

    private static void Seed(ICaseStateStore store, string caseId, string owner)
    {
        var scope = store.CreateCase(caseId, owner);
        var basis = CaseStateStore.CreateBasisVersion(1, $"{caseId} basis 1", string.Empty);
        store.Commit(scope, 0, new CaseCommit(basis, null, "presenter", "submit-evidence", "basis 1"));
    }
}
