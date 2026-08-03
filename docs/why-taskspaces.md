# Why TaskSpaces

## The problem in one sentence

Switching between projects is expensive, and the expensive part is not the switch — it is
**rebuilding the context you had before**.

## What the research actually says

### Knowledge work is already fragmented into contexts

González and Mark followed analysts, developers and managers through their working days and
found that people naturally organise work into **"working spheres"** — thematically connected
units of work, each with its own documents, tools and people. Their observation was that
workers spend roughly **three minutes on a single event** before switching to another, and a
little over two minutes on any one document or tool.[^spheres]

This matters for a workspace tool because the unit people actually think in is not "a window"
— it is "the project". A taskbar shows you windows. Your head is organised by spheres.

Mark's twenty-year follow-up found the fragmentation getting finer: average time on a single
screen dropped from about **2.5 minutes in 2004 to roughly 47 seconds** in her later data.[^attentionspan]

### Resuming is the costly half, and it is mostly *searching*

The most directly relevant study is Parnin and Rugaber's analysis of **10,000 recorded
programming sessions from 86 developers**, plus a survey of 414 more. Two numbers stand out:

- only **10%** of sessions resume programming activity within a minute of an interruption;
- only **7%** of sessions involve *no* navigation to other locations before the developer
  starts editing again.[^resumption]

In other words: after a switch, the overwhelming majority of the time is spent re-finding
things, not doing the work. That is the cost a workspace tool can actually attack.

### People already use their windows as external memory

The same study named the coping strategy developers invent for themselves: **cue priming** —
deliberately leaving the last edited window open, or highlighting the relevant lines, so that
returning to the task triggers recall.[^resumption]

This is the crux. **Your window arrangement is not clutter; it is your externalised mental
state.** Every open window is a deliberate cue about where you were. Which means anything
that destroys the arrangement destroys the cue — and anything that preserves it preserves
the context for free.

### Which is exactly what the original virtual-desktop research was for

Henderson and Card built *Rooms* at Xerox PARC in 1986 to attack what they named **"window
thrashing"**: the state where the screen is too small for the work, so the user "must expend
considerable effort to keep desired windows visible". Their solution was multiple virtual
workspaces, exploiting the statistical fact that window access clusters by task.[^rooms]

Forty years later Windows ships virtual desktops that solve the *space* problem while barely
addressing the *context* problem: the desktops have no visible names, no memory of what
belongs where, and nothing survives a reboot.

### Attention residue: why unfinished work keeps costing you

Leroy's work on **attention residue** found that when people switch tasks, part of their
attention stays with the previous one — and that they perform measurably worse on the new
task as a result. The effect is strongest when the previous task was **unfinished**,
time-pressured, or emotionally engaging, and it does not simply fade after a moment's
adjustment.[^residue]

The plausible mechanism for a workspace tool is straightforward: unfinished work that remains
*visible* keeps advertising itself. Fifteen taskbar buttons from four projects are fifteen
reminders of things you have not finished. Note the honest limit here — Leroy studied
cognitive residue, not taskbars. That visual reminders sustain residue is a reasonable
inference, not a measured finding.

### One number to stop repeating

You will see everywhere that it takes **"23 minutes and 15 seconds"** to recover from an
interruption, usually credited to Mark's *The Cost of Interrupted Work*. That paper does not
contain the figure. It reports something closer to the opposite: participants spent *less*
time on the original task when interrupted (20.31 and 20.60 minutes) than when not (22.77
minutes) — they worked faster, but at the cost of significantly **higher stress, frustration,
time pressure and effort**.[^cost] The 23-minute figure appears to originate in interviews and
press coverage rather than any published result, and has become self-perpetuating folk
wisdom.[^debunk]

**Do not use it to market this app.** The real finding is more interesting anyway: interruption
does not necessarily make you slower, it makes you more stressed and more error-prone.

## What TaskSpaces does about it

| The research says | What the app does |
|---|---|
| Work is organised in *spheres*, not windows[^spheres] | Workspaces are named, first-class things you switch between — the unit matches the mental model |
| Resumption is mostly re-finding: only 10% resume in under a minute[^resumption] | Switching restores an entire context at once. Nothing to re-find, because nothing was scattered |
| People manually leave windows open as recall cues[^resumption] | The cue *is* the workspace, and it is preserved and persisted rather than depending on you not closing anything |
| Window thrashing wastes effort keeping the right windows visible[^rooms] | Windows the current context does not need are on another desktop, natively filtered out of the taskbar |
| Unfinished work stays costly while it is in view[^residue] | Other projects are genuinely out of sight, not merely minimised |
| Fragmentation is getting finer[^attentionspan] | A switch is one click or one hotkey, not a rebuild |

And the parts that exist because organisation decays if it needs constant maintenance:

- **Placement memory** — where you last put a window is where it goes next time, keyed to what
  the app *is* rather than to a window handle, so it survives closing and reopening the app.
- **Rosters and rehydration** — a workspace remembers the apps that belong to it even when
  they are closed, so a reboot does not erase the context.
- **Renaming** — a window called `RDP` is parsed faster than
  `Remote Desktop Manager [_Richard - fhd]`. Retrieval cues work better when they are legible.

## Who this is for

The benefit scales with **how many unrelated contexts you hold at once**, not with how much
you work. Concretely, people who:

- carry more than two projects with distinct toolchains (several IDE windows, several
  terminals, several browser profiles);
- switch on someone else's schedule — support rotations, meetings, code review;
- separate work and personal life on one machine;
- keep long-lived contexts open for days rather than closing everything nightly.

If you work on exactly one thing at a time and close it when you finish, this tool solves a
problem you do not have.

## Where the argument is weak

Stated plainly, because a "why" document that only argues one side is advertising:

1. **Cheaper switching is not the same as less switching.** Every study above suggests the
   *frequency* of switching is what drives stress. TaskSpaces lowers the cost per switch; it
   does not reduce how often you switch, and by making switching pleasant it might even
   encourage more of it.
2. **Nothing here measures TaskSpaces.** Every finding is from adjacent research on
   interruption, resumption and window management. The chain from "resumption is mostly
   re-finding" to "therefore this app helps" is mechanism-level reasoning, not evidence about
   this product.
3. **Windows' own virtual desktops already provide the mechanism.** The honest claim is not
   that this invents context isolation; it is that it makes it *nameable, persistent, and
   automatic* — which is the part Windows leaves undone.

## Sources

[^spheres]: Victor M. González and Gloria Mark, ["Constant, constant, multi-tasking craziness": Managing multiple working spheres](https://dl.acm.org/doi/10.1145/985692.985707), CHI 2004.
[^cost]: Gloria Mark, Daniela Gudith and Ulrich Klocke, [The Cost of Interrupted Work: More Speed and Stress](https://ics.uci.edu/~gmark/chi08-mark.pdf), CHI 2008.
[^attentionspan]: Gloria Mark, *Attention Span* (2023), reporting time-on-screen falling from ~2.5 minutes (2004) to ~47 seconds. See also [No Task Left Behind?](https://ics.uci.edu/~gmark/CHI2005.pdf), CHI 2005.
[^resumption]: Chris Parnin and Spencer Rugaber, [Resumption strategies for interrupted programming tasks](https://link.springer.com/article/10.1007/s11219-010-9104-9), Software Quality Journal 19(1), 2011 ([PDF](http://www.chrisparnin.me/pdf/parnin-sqj11.pdf)).
[^rooms]: D. Austin Henderson and Stuart K. Card, [Rooms: the use of multiple virtual workspaces to reduce space contention in a window-based graphical user interface](https://dl.acm.org/doi/10.1145/24054.24056), ACM Transactions on Graphics 5(3), 1986 ([PDF](http://rivcons.com/wp-content/uploads/1987/Rooms-TOG.pdf)).
[^residue]: Sophie Leroy, [Why is it so hard to do my work? The challenge of attention residue when switching between work tasks](https://ideas.repec.org/a/eee/jobhdp/v109y2009i2p168-181.html), Organizational Behavior and Human Decision Processes 109(2), 2009.
[^debunk]: oberien, [Interruptions cost 23 minutes 15 seconds, right?](https://blog.oberien.de/2023/11/05/23-minutes-15-seconds.html) — traces the figure's provenance and finds no published source.

*<sub>Collaboration by Claude</sub>*
