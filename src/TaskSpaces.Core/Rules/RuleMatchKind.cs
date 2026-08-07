namespace TaskSpaces.Core.Rules;

// How a rule inspects a window. Matched in the user's list order -- first hit wins --
// so specific rules (a title regex) can sit above broad ones (a process name).
public enum RuleMatchKind { ProcessName, TitleRegex, BrowserProfile }
