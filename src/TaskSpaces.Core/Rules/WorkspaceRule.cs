namespace TaskSpaces.Core.Rules;

// "Windows matching Pattern belong to workspace WorkspaceId."
public sealed record WorkspaceRule(Guid WorkspaceId, RuleMatchKind Kind, string Pattern);
