using System.IO;
using System.IO.Pipes;
using System.Text;
using TaskSpaces.Core.Control;
using TaskSpaces.Windows.Control;

namespace TaskSpaces.Windows.Tests;

// The transport under the remote control, over a REAL named pipe: a unique name per test so nothing
// here can reach the TaskSpaces Petre is running, and nothing touches a desktop or a window.
public class ControlPipeTests
{
    static string UniquePipe() => $"TaskSpaces.Control.Test.{Guid.NewGuid():N}";

    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_command_line_reaches_the_server_and_its_reply_comes_back()
    {
        var pipe = UniquePipe();
        IReadOnlyList<string>? received = null;
        using var server = new ControlPipeServer(pipe, args =>
        {
            received = args;
            return Task.FromResult(new ControlReply(ControlReply.Ok, "{\"done\":true}"));
        });

        var reply = await ControlClient.SendAsync(pipe, ["move", "wt login", "--title", "wt-login"], Patience);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal("{\"done\":true}", reply.Output);
        Assert.Equal(["move", "wt login", "--title", "wt-login"], received);
    }

    // Exit code 2 is the client's own conclusion: there is nobody to ask.
    [Fact]
    public async Task With_no_server_the_client_says_TaskSpaces_is_not_running()
    {
        var reply = await ControlClient.SendAsync(UniquePipe(), ["windows"], TimeSpan.FromSeconds(1));

        Assert.Equal(ControlReply.NotRunning, reply.ExitCode);
    }

    // One server answers one request after another: the pipe is re-created after every reply.
    [Fact]
    public async Task The_server_answers_more_than_one_request()
    {
        var pipe = UniquePipe();
        var count = 0;
        using var server = new ControlPipeServer(pipe, _ =>
            Task.FromResult(new ControlReply(ControlReply.Ok, $"{Interlocked.Increment(ref count)}")));

        var first = await ControlClient.SendAsync(pipe, ["windows"], Patience);
        var second = await ControlClient.SendAsync(pipe, ["windows"], Patience);

        Assert.Equal(["1", "2"], [first.Output, second.Output]);
    }

    // --wait: "nothing matched" is retried until it matches, which is the window an agent just asked
    // an editor to open turning up a moment later. --wait itself never reaches the server.
    [Fact]
    public async Task Wait_retries_nothing_matched_until_the_server_finds_the_window()
    {
        var pipe = UniquePipe();
        var attempts = new List<IReadOnlyList<string>>();
        using var server = new ControlPipeServer(pipe, args =>
        {
            attempts.Add(args);
            return Task.FromResult(attempts.Count < 3
                ? new ControlReply(ControlReply.NothingMatched, "not yet")
                : new ControlReply(ControlReply.Ok, "moved"));
        });

        var reply = await ControlClient.RunAsync(pipe, ["move", "x", "--title", "y", "--wait", "10"]);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(3, attempts.Count);
        Assert.All(attempts, args => Assert.DoesNotContain("--wait", args));
    }

    [Fact]
    public async Task Without_wait_nothing_matched_is_answered_at_once()
    {
        var pipe = UniquePipe();
        var attempts = 0;
        using var server = new ControlPipeServer(pipe, _ =>
        {
            attempts++;
            return Task.FromResult(new ControlReply(ControlReply.NothingMatched, "not yet"));
        });

        var reply = await ControlClient.RunAsync(pipe, ["move", "x", "--title", "y"]);

        Assert.Equal(ControlReply.NothingMatched, reply.ExitCode);
        Assert.Equal(1, attempts);
    }

    // A handler that throws must cost one reply, not the server: the next request still gets answered.
    [Fact]
    public async Task A_handler_that_throws_is_reported_and_the_server_keeps_serving()
    {
        var pipe = UniquePipe();
        var calls = 0;
        using var server = new ControlPipeServer(pipe, _ => ++calls == 1
            ? throw new InvalidOperationException("boom")
            : Task.FromResult(new ControlReply(ControlReply.Ok, "fine")));

        var broken = await ControlClient.SendAsync(pipe, ["windows"], Patience);
        var next = await ControlClient.SendAsync(pipe, ["windows"], Patience);

        Assert.Equal(ControlReply.Failed, broken.ExitCode);
        Assert.Contains("boom", broken.Output);
        Assert.Equal("fine", next.Output);
    }

    // A stray client writing garbage gets a refusal rather than taking the loop down.
    [Fact]
    public async Task A_malformed_request_is_refused_and_the_server_keeps_serving()
    {
        var pipe = UniquePipe();
        using var server = new ControlPipeServer(pipe, _ => Task.FromResult(new ControlReply(ControlReply.Ok, "fine")));

        await using (var raw = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await raw.ConnectAsync(5000);
            var bytes = Encoding.UTF8.GetBytes("this is not json\n");
            await raw.WriteAsync(bytes);
            await raw.FlushAsync();
            using var reader = new StreamReader(raw);
            var refusal = ControlWire.DecodeReply(await reader.ReadLineAsync());
            Assert.Equal(ControlReply.Failed, refusal.Value.ExitCode);
        }

        var next = await ControlClient.SendAsync(pipe, ["windows"], Patience);
        Assert.Equal("fine", next.Output);
    }
}
