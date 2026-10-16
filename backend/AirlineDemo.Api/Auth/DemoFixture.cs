using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AirlineDemo.Api.Auth;

public sealed record DemoUser(string Id, string DisplayName, string Role, IReadOnlyList<string> CaseScopes);

// Loads the versioned demo sign-in roster. Any invalid input throws; there is no compiled-roster fallback.
public static class DemoFixture
{
    public const int SupportedSchemaVersion = 1;
    public const string ResourceName = "AirlineDemo.Api.Auth.Fixtures.demo-users-v1.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static IReadOnlyList<DemoUser>? _users;

    public static IReadOnlyList<DemoUser> Users =>
        _users ?? throw new InvalidOperationException("The demo user fixture has not been loaded.");

    public static DemoUser? Find(string? id) => Users.FirstOrDefault(u => u.Id == id);

    public static void LoadEmbedded() =>
        Initialize(Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName));

    public static void Initialize(Stream? stream) => _users = Load(stream);

    public static IReadOnlyList<DemoUser> Load(Stream? stream)
    {
        if (stream is null) throw Invalid($"embedded resource '{ResourceName}' is missing.");

        FixtureFile? file;
        try
        {
            file = JsonSerializer.Deserialize<FixtureFile>(stream, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Invalid demo user fixture: malformed JSON.", ex);
        }

        if (file is null) throw Invalid("fixture is empty.");
        if (file.SchemaVersion != SupportedSchemaVersion)
            throw Invalid($"unsupported schema version '{file.SchemaVersion?.ToString() ?? "(missing)"}'.");
        if (file.Users is null || file.Users.Count == 0) throw Invalid("no users.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var users = new List<DemoUser>(file.Users.Count);
        foreach (var entry in file.Users)
        {
            if (entry is null) throw Invalid("null user entry.");
            var id = Required(entry.Id, "id");
            if (!ids.Add(id)) throw Invalid($"duplicate id '{id}'.");
            var displayName = Required(entry.DisplayName, $"displayName for user '{id}'");
            var role = Required(entry.Role, $"role for user '{id}'");
            if (entry.CaseScopes is null || entry.CaseScopes.Count == 0)
                throw Invalid($"user '{id}' has no caseScopes.");
            var scopes = new List<string>(entry.CaseScopes.Count);
            foreach (var scope in entry.CaseScopes)
                scopes.Add(Required(scope, $"caseScopes entry for user '{id}'"));
            users.Add(new DemoUser(id, displayName, role, scopes.AsReadOnly()));
        }

        return users.AsReadOnly();
    }

    private static string Required(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw Invalid($"blank {field}.") : value;

    private static InvalidDataException Invalid(string message) => new($"Invalid demo user fixture: {message}");

    private sealed class FixtureFile
    {
        public int? SchemaVersion { get; set; }
        public List<FixtureUser?>? Users { get; set; }
    }

    private sealed class FixtureUser
    {
        public string? Id { get; set; }
        public string? DisplayName { get; set; }
        public string? Role { get; set; }
        public List<string?>? CaseScopes { get; set; }
    }
}
