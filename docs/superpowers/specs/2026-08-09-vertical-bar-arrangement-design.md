# Vertical arrangement: rows as columns

Date: 2026-08-09. Issue: #38.

Petre: *"vertical arrangement, rows as columns, configurable in the settings."*

## What changes

Every group that is a ROW today becomes a COLUMN: icons run down it, and groups stack across
instead of down. The bar becomes tall and slim, which is what suits a screen edge.

Both axes swap, and that is the whole of the feature -- nothing about what a group CONTAINS
changes. Ordering, the hover freeze, membership, the rings, placement memory and the roster are
untouched, and must stay untouched: this is a layout decision, not a model one.

## Decisions taken before writing any of it

**Labels rotate 90°, they do not go flat or vanish.** Chosen from three drawn options. A flat name
under each column makes the column as wide as the name -- "Messaging" is about nine icons wide --
which turns a slim bar into a grid of blocks and gives up the reason to go vertical at all.
Dropping labels entirely is slimmer still, but an unnamed column is only recognisable once the
colours have been learned, and an EMPTY workspace would become invisible rather than clickable --
and an empty row's label being a legitimate click target is a decision already taken once (fix
round 6, "an empty workspace is just its label, which is a switch target rather than dead chrome").

**The setting lives on Manage and applies live.** An orientation you cannot see until you restart
is one you cannot judge, and this is a look-at-it decision. It costs the bar re-deriving its sizing
and growth anchor on the fly rather than only at construction, which is the real work in this
change.

**The dragged dimension follows the icons.** In horizontal mode you drag the WIDTH, because width
is what decides where a row wraps. In vertical mode you drag the HEIGHT for exactly the same
reason. Stored separately (`FloatingBarState.Height` beside `Width`), because a number chosen by
eye in one orientation is meaningless in the other.

## The layout, concretely

| | Horizontal (today) | Vertical |
|---|---|---|
| `Rows` panel | stacks down | stacks across |
| A group | Grid: icons column + label column | Grid: icons row + label row |
| Icons within a group | vertical stack of horizontal lines | horizontal stack of vertical lines |
| A wrap adds | another line below | another column beside |
| Separator between groups | 1px horizontal hairline | 1px vertical hairline |
| Group label | right gutter, flat | bottom gutter, rotated 90° |
| Monitor marker | vertical strokes | rotated with the group |
| `SizeToContent` | Height once a width is set | Width once a height is set |
| Resize grips | left and right edges | top and bottom edges |
| Growth anchor | right edge (`anchorRight`) | right edge AND bottom edge |

The bottom anchor is new and is not optional. Today only WIDTH growth is anchored, because in a
horizontal bar a new window usually widens a row and only occasionally adds one. A vertical bar
inverts that: every new window makes a column TALLER, and the bar lives in the bottom-right corner,
so unanchored growth walks straight off the bottom of the screen. `EdgeSnap` gains `GrowsUpwards`,
the exact twin of `GrowsLeftwards`, and `OnSizeChanged` handles the height axis the way it already
handles the width.

## Persistence

- `AppState.BarOrientation`, an enum. The store already installs `JsonStringEnumConverter`, so it
  is readable and hand-editable in state.json without a string-parsing layer of its own.
- `FloatingBarState.Height`, an init property beside `Width`. Null means "never dragged", which in
  vertical mode leaves the column wrapping at the fixed five icons, exactly as a null width does
  in horizontal mode.
- Both are init properties with no migration, the pattern every key in this file already follows.

## How live switching works, and why it needs no new plumbing

`ManageWindow` calls `manager.SetBarOrientation(...)`, which persists and pulses `stateChanged` --
the same channel every other state change already uses. The bar's existing subscription rebuilds
on that pulse, and `RebuildCore` reads the orientation at its top and re-applies the window-level
consequences (panel orientation, `SizeToContent`, which stored size to honour, which edges carry
the resize grips) before building anything.

So the switch rides the mechanism that is already there. The one thing that does NOT ride it is the
growth anchor, which is derived from the bar's position rather than from state, and is re-derived
on the same pass.

## Testing

Pure and therefore tested in Core: `EdgeSnap.GrowsUpwards` (the twin of the existing
`GrowsLeftwards` cases), the orientation's default and round-trip through `AppState`, and
`SetBarOrientation` persisting and pulsing exactly once.

Not testable honestly and verified by hand: that a rotated label reads correctly, that columns
wrap where they should, and that dragging a top or bottom edge resizes rather than moves. Same
boundary as the hover freeze and the resize grip before it -- a WPF layout pass driven by a real
mouse is not something a unit test can claim.
