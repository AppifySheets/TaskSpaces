namespace TaskSpaces.Core.Rehydration;

// A recorded command line is the ORIGINAL full line ("exe" args...). The exe part is
// noise for both relaunching (ProcessStartInfo takes args separately) and identity
// (quoting differs between captures) — this strips it, leaving only the arguments.
public static class CommandLines
{
    public static string ArgumentsOf(string? commandLine, string processPath)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return "";
        var trimmed = commandLine.TrimStart();
        // Quoted form: "C:\path\app.exe" args   Unquoted form: C:\path\app.exe args
        if (trimmed.StartsWith('"'))
        {
            var close = trimmed.IndexOf('"', 1);
            return close < 0 ? "" : trimmed[(close + 1)..].TrimStart();
        }
        return trimmed.StartsWith(processPath, StringComparison.OrdinalIgnoreCase)
            ? trimmed[processPath.Length..].TrimStart()
            : ""; // command line doesn't start with the known exe — safer to treat as bare
    }
}
