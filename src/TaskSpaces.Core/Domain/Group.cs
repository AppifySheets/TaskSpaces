namespace TaskSpaces.Core.Domain;

// A named group of workspaces, drawn together on the bar.
//
// Two kinds of grouping were asked for and this is one record covering both, which was Petre's
// call when the choice was put to him:
//
//   * ANCHORED (#42, "nested workspaces"). AnchorWorkspaceId names a member that is the parent.
//     Its windows are borrowed onto the other members' desktops while you are standing in one of
//     them, and the group wears the anchor's lane colour. The anchor is a member like any other,
//     which is why it is an id rather than a separate field outside the membership list.
//
//   * ANCHORLESS (#84, "visual groups"). AnchorWorkspaceId is null. The group is a name and
//     nothing else: no desktop to switch to, no windows to borrow, no parent icons. Membership is
//     organisational.
//
// Modelling them together rather than side by side is what makes #83's operations (move into a
// group, move out, ungroup) one implementation instead of two, and it is why nothing in the
// manager or the bar has to ask which kind it is except in the two places where the anchor
// genuinely changes behaviour: borrowing windows, and where the name comes from.
//
// Name is stored even for an anchored group. It starts as the anchor's name, and keeping its own
// copy means renaming the anchor workspace does not silently rename the group, and an anchored
// group can lose its anchor (the anchor gets deleted, or moved out) without becoming nameless.
public sealed record Group(Guid Id, string Name, Guid? AnchorWorkspaceId = null)
{
    public bool IsAnchored => AnchorWorkspaceId is not null;
}
