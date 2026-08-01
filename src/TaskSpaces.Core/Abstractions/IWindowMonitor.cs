using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Abstractions;

// Source of truth for "what windows exist". Start() MUST be called on a thread that
// pumps messages (the WPF dispatcher thread) — WinEvent callbacks arrive there.
public interface IWindowMonitor
{
    Result Start();
    IObservable<WindowEvent> Events { get; }
    IReadOnlyList<WindowInfo> Snapshot();
}
