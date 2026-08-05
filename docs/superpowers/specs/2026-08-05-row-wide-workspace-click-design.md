# Row-wide workspace click on the floating bar

**Date:** 2026-08-05
**Status:** approved, ready to implement

## The ask

> "i think i'd prefer to be able to click on the empty row as well and it takes me to the
> right place. let the text be highlighted as it is now when i am over a row and take me
> there when i click it"

Today only the ~10px workspace label at the right end of a row switches workspace. The rest
of the row — the blank space left of the icons, the gutter around the label, and the whole
width of a workspace that has no windows open — does nothing. That makes the smallest target
on the bar the one that performs the bar's second most common action.

## Behaviour

| Mouse is over | Hover shows | Left click does |
|---|---|---|
| Blank area of a row with a destination | The row's label brightens | Switches to that workspace/desktop |
| The row's label | The label brightens (as today) | Switches (as today) |
| A window icon | Info line names the window and its group (as today) | Jumps to that window (as today) |
| A row with no destination (📌 Pinned, Unplaced) | Nothing | Nothing |

Three decisions inside that table, and why:

- **Icons do not highlight the row.** A click on an icon jumps to a *window*, not to a
  workspace, so lighting the label there would advertise an action the click does not
  perform. The info line already reports which group an icon belongs to, which is the
  information the highlight would have carried.
- **Only the label text changes, not the row background.** The row background is already
  spoken for: `DropHighlight` means "a dragged window will land here". A second meaning on
  the same channel would make the bar ambiguous exactly when it needs to be readable.
- **Rows with no destination stay inert.** `switchTo` is null for Pinned (its windows are on
  every workspace, so there is no single destination) and for Unplaced (`Guid.Empty` is not a
  real desktop). Attaching a highlight with nowhere to go would be dead chrome. The highlight
  therefore always means "click and you go there".

## Why this does not break dragging the bar

The bar is dragged by pressing anywhere that is not an icon and moving past the system drag
threshold. That gesture and a row click cannot collide, because the existing mechanism
already separates them by distance rather than by location:

- `OnPreviewMouseLeftButtonDown` records the press point without handling the event, so
  controls still arm normally.
- `OnPreviewMouseMove` starts `DragMove()` only once the pointer passes
  `SystemParameters.MinimumHorizontal/VerticalDragDistance`.
- `DragMove()` runs a native move loop that **consumes the mouse-up**, so a press that became
  a drag never delivers a click to anything.

This is the same split that already lets you drag the bar by pressing on a row label. A
row-wide click target inherits it for free; no new mechanism, no new state.

## Implementation

Both changes are in `src/TaskSpaces.App/FloatingBar.xaml.cs`. No XAML change.

### `RowLabel` — expose the hovered look

It currently bakes `Opacity` and `FontWeight` into the `TextBlock` at construction and returns
a `UIElement`, so the row cannot reach the text afterwards. It gains a way to hand back a
setter for the hover state. The hovered value is the same near-full strength the current row
is drawn at (`0.95`); the resting value stays `isCurrent ? 0.95 : 0.5`. Weight does not move —
bold already means "current workspace", and hover must not impersonate it.

A row whose label is not clickable (`switchTo is null`) returns no setter, which is what makes
the inert rows inert without a second null check at the call site.

### `GroupRow` — wire the container

- `container.MouseLeftButtonUp` → `Report(switchTo())`, attached only when `switchTo` is
  not null. Nothing else is needed to keep icon and label clicks doing their own jobs:
  `ButtonBase` marks the event handled when it raises `Click`, so releases on those
  controls never reach the container.
- `container.MouseEnter` / `MouseLeave` → set / clear the highlight.
- Each icon button's `MouseEnter` / `MouseLeave` → clear / set the highlight, so the icons
  punch holes in the row's hover area. Attached in `GroupRow` rather than inside
  `IconButton`, which has no business knowing about the row it sits in.

The gaps between icons count as blank row, because the icons `StackPanel` has no background
of its own and hit tests fall through it to the container. That is the desired result as well
as the incidental one.

## Testing

- **Unit (`TaskSpaces.Windows.Tests`):** raising `MouseLeftButtonUp` on a row container
  invokes `switchTo`; a row built with `switchTo: null` does not throw and does not switch.
- **Not unit-tested:** the hover highlight. It depends on real hit-testing and a real pointer
  position, and any test that faked those would be asserting the fake. Verified by looking at
  the running bar instead, and recorded here as a deliberate gap rather than an oversight.

## Out of scope

- No row-level tooltip: it would surface while the bar is being dragged.
- No `Hand` cursor: nothing else on the bar uses one, and the label does not today.
- No keyboard focus for rows. Wrapping the row in a `Button` would give it for free and was
  rejected — it would put an always-on-top window into the tab order, nest the icon buttons
  inside a clickable parent, and wrap the drag-and-drop target, which is the most delicately
  tuned code in the file.
