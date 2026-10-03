using CSharpFunctionalExtensions;
using System.IO.Pipes;
using System.Text;
using TaskSpaces.Core.Control;

namespace TaskSpaces.Windows.Control;

// The asking end of the remote control: what `TaskSpaces.exe ctl ...` does instead of starting the app.
//
// It forwards the command line verbatim and prints what comes back (see ControlWire for why the client
// does not parse commands itself). The two things it does decide on its own are the two things only it
// can know: that nobody answered (exit 2), and how long to keep re-asking while the answer is "nothing
// matched yet" (--wait).
public static class ControlClient
{
    // How long to wait for the running app to accept the connection. Short, because the usual reason for
    // no answer is that TaskSpaces is not running, and an agent should hear that quickly.
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    // How long to wait for the reply once connected. Generous, because a move runs on the app's UI thread
    // and can sit behind a bar rebuild or a COM call to the shell.
    static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);

    // Between --wait attempts. A window that is opening takes about a second to appear; asking twice a
    // second costs the app nothing it would notice.
    static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(500);

    // The whole `ctl` command: strip --wait, then ask, re-asking on "nothing matched" until the wait runs
    // out. The last answer is the one returned, so a wait that expires reports what still did not match.
    public static async Task<ControlReply> RunAsync(string pipeName, IReadOnlyList<string> args)
    {
        var split = ControlArguments.SplitWait(args);
        if (split.IsFailure) return ControlReply.Fail(split.Error);

        var (forwarded, wait) = split.Value;
        var deadline = DateTime.UtcNow + wait;
        var reply = await SendAsync(pipeName, forwarded, ConnectTimeout).ConfigureAwait(false);
        while (reply.ExitCode == ControlReply.NothingMatched && DateTime.UtcNow + RetryInterval < deadline)
        {
            await Task.Delay(RetryInterval).ConfigureAwait(false);
            reply = await SendAsync(pipeName, forwarded, ConnectTimeout).ConfigureAwait(false);
        }
        return reply;
    }

    // One request, one reply. Every way of not getting an answer becomes a ControlReply rather than an
    // exception, so the caller has exactly one thing to print and one exit code to return.
    public static async Task<ControlReply> SendAsync(string pipeName, IReadOnlyList<string> args, TimeSpan connectTimeout)
    {
        // CurrentUserOnly on the client too: it refuses a pipe of this name owned by someone else, so a
        // process squatting on the name cannot answer in TaskSpaces' place.
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync((int)connectTimeout.TotalMilliseconds).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new ControlReply(ControlReply.NotRunning,
                "TaskSpaces is not running (nothing answered on its control pipe). Start it and try again.");
        }

        try
        {
            using var timeout = new CancellationTokenSource(ReplyTimeout);
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            await using var writer = new StreamWriter(pipe, utf8, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

            await writer.WriteLineAsync(ControlWire.EncodeRequest(args).AsMemory(), timeout.Token).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            return ControlWire.DecodeReply(line).Match(
                reply => reply,
                error => ControlReply.Fail($"TaskSpaces sent a reply that could not be read: {error}"));
        }
        catch (OperationCanceledException)
        {
            return ControlReply.Fail($"TaskSpaces accepted the request but did not answer within {ReplyTimeout.TotalSeconds:0} seconds.");
        }
        catch (IOException e)
        {
            return ControlReply.Fail($"The connection to TaskSpaces broke: {e.Message}");
        }
    }
}
