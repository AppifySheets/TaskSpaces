namespace TaskSpaces.Core.Persistence;

// A MANUAL window rename, made durable. Identity across app restarts is heuristic --
// hwnds die with the session, so the best stable key is "same app, same title it had
// when Petre renamed it". A window whose natural title has since changed (browser
// navigated elsewhere) will not match, and deliberately stays untouched.
public sealed record PersistedRename(string ProcessName, string OriginalTitle, string ShortName);
