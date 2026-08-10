namespace TaskSpaces.Core.Domain;

// One process, as much of it as the launched-by rule needs (#94): its name, so the shell can be told
// apart from an app, and its parent, so the chain can be walked.
//
// Name WITHOUT the extension, matching WindowInfo.ProcessName ("chrome", not "chrome.exe"), so the two
// can be compared without either side remembering to trim.
public sealed record ProcessFacts(int ProcessId, string Name, int ParentProcessId);
