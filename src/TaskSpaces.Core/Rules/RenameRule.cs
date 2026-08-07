namespace TaskSpaces.Core.Rules;

// "Windows matching Pattern get taskbar name ShortName" -- the TaskBarRenamer feature.
public sealed record RenameRule(RuleMatchKind Kind, string Pattern, string ShortName);
