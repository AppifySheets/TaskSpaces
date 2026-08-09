namespace TaskSpaces.Core.Domain;

// Petre: "vertical arrangement, rows as columns, configurable in the settings."
//
// Which way the bar lays its groups out. Horizontal is what the bar has always been -- a group is
// a ROW, icons run across it, groups stack downwards. Vertical swaps both axes: a group is a
// COLUMN, icons run down it, groups stack across, and the whole bar becomes tall and slim, which
// is what suits a screen edge.
//
// It is a layout choice and nothing more. Membership, ordering, the hover freeze, the rings and
// placement memory are all indifferent to it, and must stay that way -- the moment an orientation
// starts changing what a group CONTAINS, the same feature has to be maintained twice.
//
// An enum rather than the text SwitcherShortcut is stored as: the persistence store already
// installs JsonStringEnumConverter (deliberately, against the renumbering hazard of writing enums
// as integers), so this is readable and hand-editable in state.json without a parsing layer of
// its own -- which the shortcut needed only because a chord is user-typed prose.
public enum BarArrangement
{
    Horizontal,
    Vertical,
}
