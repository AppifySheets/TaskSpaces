# A back button on the floating bar

**Date:** 2026-08-05
**Status:** approved, ready to implement

## The ask

> "on the floating window i want a go back to previous button. Usecase => i often click on a
> specific window in a different workspace and want to go to where i was before i arrived here"
>
> "basically the same as ctrl+win+tab tap once, without the kb"

The second message is the specification: this is a **mouse equivalent of one tap** of the
switcher chord, not a new navigation model. It exists because the use case starts with the
mouse — you clicked an icon on the bar to get here — and reaching for a chord to undo a click
is a mode switch.

## Why nothing new has to be remembered

The destination already exists. `JumpTo` switches desktops through `desktops.Switch`, and the
`CurrentChanged` subscription's `RememberVisit` touches the MRU for switches the app did not
initiate. So by the time you have landed in another workspace by clicking an icon, the MRU
already holds the workspace you came from in second place — which is exactly what one tap of
the chord resolves to.

The button therefore adds no state, no history stack and nothing persisted. It reads the same
MRU the chord reads.

It also self-toggles for free: clicking it switches, switching touches the MRU, and the button
then points back at where you just came from. Press it twice and you are where you started,
exactly as tapping the chord twice behaves.

## Behaviour

- Click → switch to the workspace one tap forward in most-recently-used order.
- Dim and unclickable when there is nowhere to go: no workspaces at all, or one workspace and
  you are already on it.
- **Not** dim when you are on an unbound desktop such as "Main". `CurrentIndex` is −1 there, so
  one step forward lands on your most recent workspace, which is a real move and is what the
  chord already does.
- Present-but-dimmed rather than hidden, following the ruling the icon context menu already
  follows for a greyed "Restore title": a surface whose shape shifts is harder to learn than
  one with a disabled control.

### The consequence worth naming

This is workspace-granular, because one tap of the chord is. If you jump *within* a workspace,
or the window you were using is not the one Windows focuses when you return, the button brings
you back to the **workspace** and not to the specific window. The stated use case is
cross-workspace, so this is correct — it is recorded here so that the first time it lands on
the right workspace with the wrong window focused, it reads as the design rather than a defect.

## Implementation

### Core — `RecentWorkspaces` owns the destination

`WorkspaceSwitchGesture.Step` currently computes the wrapping index inline. That arithmetic
moves onto the record both surfaces read, so the button and the chord cannot drift apart:

- `IndexAfter(from, direction)` — the wrapping index, moved verbatim, including the comment
  about `%` keeping the sign of its left operand.
- `Back` → `Maybe<Workspace>`: `Ordered[IndexAfter(CurrentIndex, +1)]`, and `None` when
  `Ordered` is empty or the step lands on where you already are.

`Back` returning `Maybe` rather than a workspace-or-null is what makes the button's enabled
state a single expression at the call site, with no arithmetic repeated in the UI.

The gesture holds the whole `RecentWorkspaces` record instead of just its `Ordered` list, so it
can call `IndexAfter`. That is the only reason it changes; its behaviour must not.

**Rejected:** recomputing the index in the bar. Two copies of the same wrapping arithmetic, and
one of them eventually stops matching the chord.

### App — a fixed-width button on the info line

- `↩`, ~18px, **fixed width**, sitting left of `Info` in a horizontal panel in
  `FloatingBar.xaml`. Fixed because this is a `SizeToContent` window: a control whose width
  tracked a workspace name would resize and therefore reposition the whole bar every time the
  MRU changed. The bar widens by that ~18px once and never again.
- Declared in XAML rather than built in `RebuildCore`, because it is not part of the `Rows`
  panel that gets cleared. `RebuildCore` refreshes its enabled state and tooltip instead. That
  refresh happens before the overview query, so a transient enumeration failure cannot leave
  the button stale.
- Tooltip names the destination — "Back to Sparrow" — or says there is nowhere to go when dim.
  The glyph alone cannot convey which workspace it means, and the info line's own text is
  overwritten on icon hover, so the tooltip is the only stable place for it.
- Untagged, so `StartedOnIcon` does not match it and the window-level threshold split treats it
  exactly like a row label: press-and-drag moves the bar, press-and-release clicks.

## Testing

**Core**, where the logic is pure:

- After visiting A then B, `Back` is A.
- `Back` is `None` with a single workspace you are already on, and with no workspaces.
- `Back` is the most recent workspace when `CurrentIndex` is −1 (an unbound desktop).
- Two steps return to the start — the self-toggle.
- `IndexAfter` wraps in both directions, including the negative-direction case that motivated
  writing the modulo the long way.

**WPF**: the button exists, is disabled when `Back` is `None`, and switches when clicked.

## Out of scope

- No history stack. "Go back to previous" is one step; a stack on a surface that cannot show
  its own history would be guesswork after the first press.
- No window-level back. See "The consequence worth naming" above.
- No change to the info line's idle hint text.
