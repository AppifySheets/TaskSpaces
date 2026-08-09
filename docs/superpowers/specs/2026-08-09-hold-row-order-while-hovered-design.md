# Hold a row's icon order while the pointer is in it

Date: 2026-08-09

## The problem

Icons in a workspace row sort by z-order, front-most first (`OverviewBuilder.OnDesktop`).
Clicking an icon activates its window, which puts that window in front, which moves its icon
to the head of its monitor group -- under the pointer that just clicked it.

Petre: *"when an app becomes the top app, if i press on it in the workspace, it moves to the
first position, which is good, but i want that position changing to happen after i've left the
floating window with a mouse ... so that i can minimize it back if i didn't want to use it and
am testing what it is."*

The move is wanted; its timing is not. Probing a row -- click, look, minimise back -- means the
row rearranges itself twice while the hand is still on it, and the icon you were probing from is
no longer where you left it.

## Behaviour

While the pointer is inside a row, that row's icon order is frozen exactly as it was at the
moment the pointer entered.

Everything else about the row stays live. The clicked icon takes the active highlight and the
"on top" mark in place, dimming updates, a window that opens or closes still appears or vanishes.
Only the ORDER holds. That split is the point: freezing the whole row would leave a click with no
visible effect at all, which reads as a click that did not land.

The freeze is released the moment the pointer leaves that row -- off the bar, onto a neighbouring
row, onto a separator -- and the row re-sorts to live z-order immediately. Per ROW, not per bar:
Petre asked for it on exiting the row rather than the window.

Arming is on ENTER, not on click. The rule is then simply "the row under the pointer holds
still", with no hidden dependence on whether you happened to click. It also covers the case not
reported: hovering to read a label while focus changes elsewhere no longer shuffles icons out
from under the pointer.

Only rows built from a real desktop can reorder at all. 📌 Pinned and Unplaced are not built by
`OnDesktop` and never sort by z-order, and every desktop but the current one is made of cloaked
windows and so has no z-order to sort by. In practice this affects exactly one row: the current
workspace's.

## Mechanism

### `TaskSpaces.Core/Overview/RowOrderFreeze.cs` (new, pure)

- `Capture(rows)` -> the row handles in displayed order.
- `Apply(rows, frozen)` -> `rows.OrderBy(MonitorRank).ThenBy(index in frozen, unknown last)`.

`OrderBy` is stable, and that carries the two cases the snapshot cannot describe:

- A window that appeared while frozen is not in the snapshot, so it sorts after the known ones
  **within its own monitor group** and keeps its live relative order. Sorting newcomers to the
  very end of the row instead would break monitor grouping, and the hairline markers are drawn
  from exactly that grouping (`GroupRow` emits a mark wherever the monitor number changes), so a
  stray icon would draw a stray boundary.
- A window that closed is simply absent from `rows`; its snapshot entry is inert.

Keeping `MonitorRank` as the primary key means the freeze can only ever reorder icons WITHIN a
monitor group -- the structure of the row is not something a hover is allowed to change.

### `FloatingBar`

- One field, `(string GroupKey, IReadOnlyList<WindowHandle> Order)?`. One row is hovered at a
  time, so one slot is the whole store.
- `GroupRow` applies the snapshot when `groupKey` matches, tags the row container with its
  `groupKey`, and wires `MouseEnter` (capture what is displayed) / `MouseLeave` (release).
  `groupKey` rather than `rowKey` because `rowKey` is null for unbound-desktop rows, which do
  sort by z-order when they are the current desktop.
- Releasing rebuilds, so the re-sort is visible the instant the pointer leaves.

The trap this has to survive: a rebuild destroys the hovered container and WPF raises `MouseLeave`
on removal, which would unfreeze a row the pointer never left -- and rebuilds are frequent
precisely while the user is working in the bar. So `MouseLeave` does not clear directly. It
queues a check on the next dispatcher turn that hit-tests the live cursor position against `Rows`
and clears only if the pointer is no longer inside a row carrying that key. A rebuilt row is
found by key, so the freeze survives its own container being replaced.

The 1s heartbeat that already re-asserts topmost (`FlushIfIdle`) runs the same check, bounding a
missed `MouseLeave` at one second. Same reasoning as the deferred-rebuild flush in that method:
the release must not depend on an event that can fail to arrive.

Composes with the existing button-down deferral rather than replacing it. A press still postpones
the whole rebuild so the pressed `Button` survives to raise `Click`; the freeze governs order
after that.

## Testing

`RowOrderFreeze` is pure and carries the whole ordering rule, so it is covered in
`TaskSpaces.Core.Tests`:

- the frozen order wins over live z-order;
- a closed window drops out without disturbing the others;
- a window that appeared while frozen lands last **within its own monitor group**, not at the end
  of the row;
- an empty snapshot is the identity, so an unfrozen row is bit-for-bit what it is today.

The hover wiring is verified by hand. Synthesising genuine enter/leave over a visual tree that is
being rebuilt underneath the cursor is the one part of this that a test cannot claim honestly, and
the failure it would be guarding (unfreezing on rebuild) is exactly the part the hit-test check
exists for. Manual check: hover a row, click an icon, confirm nothing moves and the highlight
follows the click; minimise it back; leave the row and confirm it re-sorts.

## Not in this change

Aligning monitor groups to opposite edges of the row -- first monitor's icons left, second
monitor's right, hairline centred between them, the bar keeping the width it computes now. Asked
for in the same conversation and explicitly deferred ("after you're done with the current
feature"). It is a layout change to `GroupRow`'s icon column and gets its own spec.
