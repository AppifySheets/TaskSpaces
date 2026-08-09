using System.Text.Json;
using CSharpFunctionalExtensions;

namespace TaskSpaces.Core.Time;

// time.json, beside state.json. Same write-then-rename discipline as JsonPersistenceStore, for the
// same reason: a crash mid-write must not destroy what was already tracked.
//
// Stored as a flat list of rows rather than as the nested dictionary the model uses. Guid and
// DateOnly keys serialise awkwardly and read badly, and this file is meant to be openable:
//
//     { "Rows": [ { "Workspace": "…guid…", "Day": "2026-08-09", "Seconds": 3600 } ] }
//
// Seconds rather than a TimeSpan, because System.Text.Json writes TimeSpan as "01:00:00" and the
// point of a number here is that a spreadsheet can add it up.
public sealed class JsonTimeStore(string baseDirectory) : ITimeStore
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public sealed record Row(Guid Workspace, DateOnly Day, double Seconds);

    sealed record File_(IReadOnlyList<Row> Rows);

    string TimePath => Path.Combine(baseDirectory, "time.json");

    // Missing file is the normal first-run case. A corrupt one is a real failure the caller
    // surfaces rather than something to overwrite silently -- tracked history is not
    // reconstructible from anywhere else.
    public Result<WorkspaceTime> Load() =>
        !File.Exists(TimePath)
            ? WorkspaceTime.Empty
            : Result.Try(() => Rebuild(JsonSerializer.Deserialize<File_>(File.ReadAllText(TimePath), Options)
                                       ?? throw new JsonException("time.json deserialized to null")),
                         e => $"Could not read {TimePath}: {e.Message}");

    static WorkspaceTime Rebuild(File_ file) =>
        file.Rows.Aggregate(WorkspaceTime.Empty, (time, row) =>
            time.Credit(row.Workspace, row.Day, TimeSpan.FromSeconds(row.Seconds)));

    public Result Save(WorkspaceTime time) =>
        Result.Try(() =>
        {
            Directory.CreateDirectory(baseDirectory);
            var rows = time.ByWorkspace
                .SelectMany(w => w.Value.Select(d => new Row(w.Key, d.Key, Math.Round(d.Value.TotalSeconds))))
                // Ordered so a diff of this file reads as "one day changed" rather than as a
                // reshuffle -- dictionary order is not stable across runs.
                .OrderBy(r => r.Workspace).ThenBy(r => r.Day)
                .ToList();

            var tmp = TimePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new File_(rows), Options));
            File.Move(tmp, TimePath, overwrite: true);
        }, e => $"Could not write {TimePath}: {e.Message}");
}
