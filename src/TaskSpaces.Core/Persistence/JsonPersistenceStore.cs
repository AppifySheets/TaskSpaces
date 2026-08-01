using System.Text.Json;
using CSharpFunctionalExtensions;

namespace TaskSpaces.Core.Persistence;

public sealed class JsonPersistenceStore(string baseDirectory) : IPersistenceStore
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    string StatePath => Path.Combine(baseDirectory, "state.json");

    // Missing file is the normal first-run case -> Empty. Unreadable/corrupt file is a
    // real failure the caller must surface (we refuse to silently overwrite user data).
    public Result<AppState> Load() =>
        !File.Exists(StatePath)
            ? AppState.Empty
            : Result.Try(() => JsonSerializer.Deserialize<AppState>(File.ReadAllText(StatePath), Options)
                               ?? throw new JsonException("state.json deserialized to null"),
                         e => $"Could not read {StatePath}: {e.Message}");

    // Write-then-rename so a crash mid-write can never destroy the previous good state.
    public Result Save(AppState state) =>
        Result.Try(() =>
        {
            Directory.CreateDirectory(baseDirectory);
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
            File.Move(tmp, StatePath, overwrite: true);
        }, e => $"Could not write {StatePath}: {e.Message}");
}
