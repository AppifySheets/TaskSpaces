namespace TaskSpaces.Core.Abstractions;

// "How long since anybody touched the keyboard or mouse?" (#53)
//
// One number, system-wide, and deliberately nothing more. The Windows implementation is
// GetLastInputInfo, which returns the TICK of the last input and no content whatsoever -- not
// which key, not where the mouse went, not which application received it.
//
// Low-level hooks (WH_KEYBOARD_LL / WH_MOUSE_LL) were rejected before this interface existed and
// the reasons are worth keeping next to it: they see every keystroke in the system, they sit in
// the input path so a slow callback adds latency to the whole machine, and Windows silently
// unhooks one that takes too long. All of that cost, to answer a question a timestamp already
// answers.
//
// Abstracted for the usual reason: the accrual rules are testable with a number, and a test that
// needed real typing would not be a test.
public interface IInputActivity
{
    TimeSpan SinceLastInput();
}
