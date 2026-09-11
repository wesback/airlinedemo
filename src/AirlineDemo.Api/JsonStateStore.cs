using System.Text.Json;

namespace AirlineDemo.Api;

internal sealed class JsonStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly object Gate = new();
    private readonly string statePath;
    private PersistedState state;

    public JsonStateStore(string directory)
    {
        Directory.CreateDirectory(directory);
        statePath = Path.Combine(Path.GetFullPath(directory), "workflow-state.json");
        state = Load();
    }

    public T Read<T>(Func<PersistedState, T> reader)
    {
        lock (Gate)
        {
            return reader(state);
        }
    }

    public void Update(Action<PersistedState> update)
    {
        lock (Gate)
        {
            update(state);
            var temporaryPath = statePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, SerializerOptions));
            File.Move(temporaryPath, statePath, true);
        }
    }

    private PersistedState Load()
    {
        if (!File.Exists(statePath))
        {
            return new PersistedState();
        }

        var json = File.ReadAllText(statePath);
        return JsonSerializer.Deserialize<PersistedState>(json, SerializerOptions)
            ?? throw new InvalidDataException("The workflow state file is invalid.");
    }
}
