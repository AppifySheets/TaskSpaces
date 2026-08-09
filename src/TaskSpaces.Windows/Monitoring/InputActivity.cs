using System.Runtime.InteropServices;
using TaskSpaces.Core.Abstractions;

namespace TaskSpaces.Windows.Monitoring;

// GetLastInputInfo: the tick of the most recent keyboard or mouse input, system-wide (#53).
//
// A timestamp and nothing else -- no key, no position, no target window. That is the whole reason
// this was chosen over low-level hooks, which would see every keystroke on the machine and sit in
// the input path while doing it.
public sealed class InputActivity : IInputActivity
{
    [StructLayout(LayoutKind.Sequential)]
    struct LastInputInfo
    {
        public uint Size;
        public uint TickOfLastInput;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("kernel32.dll")]
    static extern uint GetTickCount();

    public TimeSpan SinceLastInput()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        // A failure here means "no idea", and the safe reading of no idea is IDLE -- crediting time
        // nobody can vouch for is the one outcome this feature must not produce.
        if (!GetLastInputInfo(ref info)) return TimeSpan.MaxValue;

        // BOTH values are 32-bit tick counts that wrap every ~49.7 days of uptime, and unsigned
        // subtraction is what makes the wrap harmless: 0x00000005 - 0xFFFFFFF0 is 21 in uint
        // arithmetic, which is the right answer, where a signed or widened subtraction would give
        // a wildly negative one. GetTickCount is used rather than Environment.TickCount64 for
        // exactly this reason -- it is the same clock, in the same width, as the value being
        // compared.
        return TimeSpan.FromMilliseconds(GetTickCount() - info.TickOfLastInput);
    }
}
