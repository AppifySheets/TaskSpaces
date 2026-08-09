# Floating bar backlog, as asked for on 2026-08-09

Captured in one sitting, in the order they were asked for, then RESEQUENCED so nothing gets
built twice. Sequence reasoning is the point of this file; the asks themselves are short.

## Done

1. **A row holds its icon order while the pointer is in it.** Spec:
   `2026-08-09-hold-row-order-while-hovered-design.md`. Shipped, plus the two minimise bugs it
   surfaced (a minimised window could not be brought back; icons did not reliably dim).

## What actually happened to the sequence

Kept as written below, with the outcomes marked, because the reasoning was sound and the result
still went sideways -- which is the useful part to remember.

- **2 (#36) and 3 (#37) shipped** as designed.
- **4 (#38) was built and scrapped.** Petre, having lived with it: "okay, it's pretty bad :) but
  it is what it is, let's scrap it". Columns worked and the live switch worked; the arrangement
  was simply worse to use than rows. The workspace NAMES are why -- rotated, stacked and flat were
  all tried, and the first two are unreadable at a glance while the third makes every column as
  wide as its name, which removes the reason to go vertical at all. All three roads end somewhere
  worse than rows.
- **5 (#39) therefore lost its dependency on 4** and was built straight after, against rows.
- **6 (#40) is what remains.**

The sequencing call itself was right for the wrong reason: 5 was held back so it would not be
built twice, and what actually saved that work was 4 being abandoned.

## Sequenced

2. **Resizable width, persisted.** Drag either side edge; the width is stored in
   `FloatingBarState` and a set width makes rows wrap at as many icons as fit rather than at
   today's fixed five. Never fewer than **three icons per line** ("i want no less than 3 icons
   per row width").

3. **Fade when the pointer leaves.** "when i leave the floating window i want it to fade away,
   still be visible, but much dimmer, so i can see what's behind it better." Orientation- and
   layout-independent, so it can land at any point.

4. **Vertical arrangement, configurable.** "rows as columns", a setting. The structural one:
   icons run down a column and groups stack across, which swaps both axes.

5. **Monitor groups aligned to opposite ends.** "if apps are separated by monitor, let them be
   aligned to the left and right, not all left... hairline in the middle between them", with the
   bar keeping the width it has.

6. **Workspace management from the bar's right-click.** Rename an existing workspace; add a new
   one *before* or *after* the row you clicked (insert-before/insert-after); move a workspace up
   or down.

### Why this order and not the order they were asked in

- **5 depends on 2.** Aligning groups to opposite ends means nothing while the bar is exactly as
  wide as its widest row -- `SizeToContent` leaves no slack to push a group into. A user-set
  width is what creates the space the alignment spends.
- **5 also depends on 4, and is the one thing here that would otherwise be built twice.**
  "Left and right, hairline between" becomes "top and bottom" once icons run down a column. Same
  idea, different axis. Built after the orientation exists, it is written once.
- **2 survives 4 nearly free.** The dimension you drag is whichever one icons run along -- width
  today, height once columns land -- so the gesture, the clamp and the stored value carry over
  under a different name.
- **3 is independent of all of it** and can jump the queue whenever it is wanted.
- **6 touches none of the layout work** and is sequenced last only because it is the largest.

### Known consequence of 6, decided already, not a surprise to raise later

Lane colours come from `WorkspacePalette` **by position** (deliberately: renaming must not
recolour a workspace, and reordering should carry its colour with it). So moving a workspace up
or down recolours it and its neighbour. That is the intended behaviour of the existing rule, not
a defect of the reorder feature.
