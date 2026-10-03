using CSharpFunctionalExtensions;
using System.IO.Pipes;
using System.Text;
using TaskSpaces.Core.Control;

namespace TaskSpaces.Windows.Control;

// The running app's end of the remote control: a named pipe that takes one command line per
// connection, hands it to `handle`, and writes the reply back.
//
// Why a pipe, and why in this process. The running instance owns state.json (last writer wins, see the
// single-instance comment in App.OnStartup), the virtual-desktop COM objects and the window list. A
// second process that edited any of those itself would race the first, so the only safe way for an
// outside caller to create a workspace or move a window is to ASK the running instance to do it.
//
// One connection at a time, served on a background task, the pipe re-created after every reply. The
// requests are a person or an agent typing commands, so there is nothing to gain from concurrency, and
// serialising them means the handler never sees two at once.
//
// CurrentUserOnly sets an ACL that admits only this user, so another account on the same machine
// cannot drive this one's desktops. The session id in the name (ControlWire.PipeName) is what keeps two
// signed-in users from colliding on the name itself.
//
// Never throws out of the loop. A malformed request, a handler that throws and a client that hangs up
// mid-way each cost that one request and nothing more, because a remote control that dies quietly
// leaves the caller with "not running" while the app is plainly on screen.
public sealed class ControlPipeServer : IDisposable
{
    // Long enough for any honest client to send one line; short enough that a client which connects and
    // then says nothing does not hold the only pipe instance for long.
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    readonly CancellationTokenSource stop = new();

    public ControlPipeServer(string pipeName, Func<IReadOnlyList<string>, Task<ControlReply>> handle, Action<string>? trace = null) =>
        _ = Task.Run(() => Serve(pipeName, handle, trace, stop.Token));

    static async Task Serve(string pipeName, Func<IReadOnlyList<string>, Task<ControlReply>> handle, Action<string>? trace, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // maxNumberOfServerInstances: 1, so a second TaskSpaces can never answer alongside this
                // one. During an update restart the new instance meets the old one's pipe for a moment;
                // creating it throws, the catch below waits a second, and it gets the name once the old
                // process has gone.
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                await Answer(pipe, handle, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                trace?.Invoke($"control pipe: {e.GetType().Name}: {e.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    static async Task Answer(NamedPipeServerStream pipe, Func<IReadOnlyList<string>, Task<ControlReply>> handle, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(RequestTimeout);

        // UTF-8 without a BOM both ways: a BOM at the front of a line would make it not JSON.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var reader = new StreamReader(pipe, utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, utf8, leaveOpen: true) { AutoFlush = true };

        var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        var reply = await ControlWire.DecodeRequest(line).Match(
            args => Invoke(handle, args),
            error => Task.FromResult(ControlReply.Fail($"TaskSpaces could not read the request: {error}"))).ConfigureAwait(false);

        await writer.WriteLineAsync(ControlWire.EncodeReply(reply).AsMemory(), timeout.Token).ConfigureAwait(false);
        // Without this the reply can still be in the pipe's buffer when the stream is disposed, and the
        // client reads end-of-stream instead of an answer.
        pipe.WaitForPipeDrain();
    }

    // The handler is the app's code, so whatever it throws (synchronously or from its task) becomes a
    // refusal with the message in it rather than an exception in the loop.
    static async Task<ControlReply> Invoke(Func<IReadOnlyList<string>, Task<ControlReply>> handle, IReadOnlyList<string> args)
    {
        try
        {
            return await handle(args).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return ControlReply.Fail($"TaskSpaces failed while running the command: {e.Message}");
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        stop.Dispose();
    }
}
