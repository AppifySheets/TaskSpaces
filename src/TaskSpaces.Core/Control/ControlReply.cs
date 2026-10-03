namespace TaskSpaces.Core.Control;

// What one remote-control command answers: an exit code the calling process hands back to its shell,
// and the text it prints (JSON on success, a sentence on failure).
//
// The exit codes are part of the contract with whoever scripts this, an agent most of all, so they are
// named here once and never renumbered:
//   0  done
//   1  refused: a bad command line, a missing workspace, a move Windows would not make
//   2  no running TaskSpaces answered (decided by the client, which is the only side that can know)
//   3  a selector matched no window. Nothing was changed. The client's --wait retries on exactly this,
//      because the usual cause is a window that has been asked for and has not opened yet.
public sealed record ControlReply(int ExitCode, string Output)
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int NotRunning = 2;
    public const int NothingMatched = 3;

    public static ControlReply Fail(string message) => new(Failed, message);
}
