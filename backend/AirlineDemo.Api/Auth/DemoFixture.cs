namespace AirlineDemo.Api.Auth;

public sealed record DemoUser(string Id, string DisplayName, string Role, IReadOnlyList<string> CaseScopes);

public static class DemoFixture
{
    public static readonly IReadOnlyList<DemoUser> Users =
    [
        new("presenter", "Demo presenter", "presenter", ["case-001", "case-002"]),
        new("reviewer", "Technical reviewer", "technical-reviewer", ["case-001"]),
    ];

    public static DemoUser? Find(string? id) => Users.FirstOrDefault(u => u.Id == id);
}
