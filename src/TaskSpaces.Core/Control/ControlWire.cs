using System.Text.Json;
using CSharpFunctionalExtensions;

namespace TaskSpaces.Core.Control;

// What travels over the control pipe: one line of JSON each way, then the connection closes.
//
// The client sends its command line VERBATIM and the running app parses it. That split is deliberate:
// the exe that asks may be a different build from the one running (a release exe in Downloads asking
// the dev build, or the other way round), and if only the running side understands the commands then
// a newer client can never send a request an older app half-understands. The client's whole job is to
// forward, wait and print.
public static class ControlWire
{
    // Pipe names are machine-wide, unlike the Local\ single-instance mutex, so the login session is in
    // the name: two users on one PC each reach their own TaskSpaces. The pipe is also created
    // CurrentUserOnly, which is what actually stops one user driving another's (see ControlPipeServer).
    public static string PipeName(int sessionId) => $"TaskSpaces.Control.{sessionId}";

    sealed record Request(IReadOnlyList<string> Args);
    sealed record Reply(int ExitCode, string Output);

    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string EncodeRequest(IReadOnlyList<string> args) => JsonSerializer.Serialize(new Request(args), Options);

    public static Result<IReadOnlyList<string>> DecodeRequest(string? line) =>
        Decode<Request>(line).Map(request => request.Args ?? []);

    public static string EncodeReply(ControlReply reply) => JsonSerializer.Serialize(new Reply(reply.ExitCode, reply.Output), Options);

    public static Result<ControlReply> DecodeReply(string? line) =>
        Decode<Reply>(line).Map(reply => new ControlReply(reply.ExitCode, reply.Output ?? ""));

    // A malformed line is an expected situation (a stray client, a truncated write), so it is a Result
    // rather than an exception escaping into the pipe loop.
    static Result<T> Decode<T>(string? line) where T : class
    {
        if (string.IsNullOrWhiteSpace(line)) return Result.Failure<T>("empty message");
        try
        {
            return JsonSerializer.Deserialize<T>(line, Options) is { } value
                ? value
                : Result.Failure<T>("empty message");
        }
        catch (JsonException e)
        {
            return Result.Failure<T>($"malformed message: {e.Message}");
        }
    }
}
