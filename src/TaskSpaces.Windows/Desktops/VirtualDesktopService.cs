using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using WindowsDesktop;

namespace TaskSpaces.Windows.Desktops;

// Adapter over Slions.VirtualDesktop (runtime-compiled COM interop, undocumented API).
// See docs/superpowers/notes/2026-08-01-virtualdesktop-spike.md for what was verified
// on Win11 build 26200. Every member call is wrapped: if Windows Update changes the
// COM shape, callers get Result.Failure, not a crash.
//
// PRECONDITION (spike finding): the thread that calls Initialize() — and, in practice,
// every other member on this instance — must be STA. VirtualDesktop.Configure() builds a
// WPF HwndSource internally (to listen for explorer.exe restarts), and WPF's InputManager
// throws InvalidOperationException on an MTA thread. A real host (WPF/WinForms tray app)
// already runs its UI thread as STA, so this falls out naturally there. If the caller is
// NOT on an STA thread, Configure() throws and Initialize() turns that into a Result
// failure — the app degrades to compatibility mode instead of crashing, per spec.
public sealed class VirtualDesktopService : IVirtualDesktopService
{
    // DEVIATION from the brief's draft (Initialize body): the draft only touched
    // VirtualDesktop.Current to force the interop compile. The spike found that
    // VirtualDesktop.Configure() — documented only in the package's XML doc comments,
    // not the README — "should always be called first"; skipping it doesn't fail
    // immediately, it fails later from inside Configure()'s own implicit call chain.
    // Configure() is therefore called explicitly, first, here.
    public Result Initialize() =>
        Result.Try(() =>
        {
            VirtualDesktop.Configure();
            if (!VirtualDesktop.IsSupported)
                throw new NotSupportedException("Virtual desktop API not recognized on this Windows build.");
        }, e => $"Virtual desktops unavailable: {e.Message}");

    public Result<IReadOnlyList<DesktopInfo>> GetDesktops() =>
        Result.Try<IReadOnlyList<DesktopInfo>>(
            () => VirtualDesktop.GetDesktops().Select(d => new DesktopInfo(d.Id, d.Name)).ToList(),
            e => $"Could not enumerate desktops: {e.Message}");

    public Result<DesktopInfo> Create(string name) =>
        Result.Try(() =>
        {
            var desktop = VirtualDesktop.Create();
            desktop.Name = name;
            return new DesktopInfo(desktop.Id, name);
        }, e => $"Could not create desktop '{name}': {e.Message}");

    public Result Rename(Guid desktopId, string name) =>
        Find(desktopId).Tap(d => d.Name = name);

    public Result Switch(Guid desktopId) => Find(desktopId).Tap(d => d.Switch());

    public Result Remove(Guid desktopId) => Find(desktopId).Tap(d => d.Remove());

    public Result MoveWindow(WindowHandle window, Guid desktopId) =>
        Find(desktopId).Bind(d => Result.Try(
            () => VirtualDesktop.MoveToDesktop(window.Value, d),
            e => $"Could not move window {window.Value} (it may have closed): {e.Message}"));

    public Result<Guid> DesktopOf(WindowHandle window) =>
        Result.Try(() => VirtualDesktop.FromHwnd(window.Value), e => e.Message)
            .Ensure(d => d is not null, "Window is not on any desktop (closed or pinned).")
            .Map(d => d!.Id);

    // DEVIATION from the brief's draft: the draft subscribed to the static
    // VirtualDesktop.CurrentChanged event eagerly, in a property initializer that runs
    // in the constructor — i.e. before any caller has a chance to call Initialize().
    // That touches the undocumented COM type before Configure() has run and outside any
    // Result.Try, which could throw straight out of the constructor (violating "Initialize()
    // failure = compatibility mode, never a crash" — a constructor throw isn't a Result
    // failure, it's an unhandled exception). Observable.Defer delays the event subscription
    // (VirtualDesktop.CurrentChanged +=) until a consumer actually subscribes, by which
    // point Initialize() is expected to have already run.
    public IObservable<Guid> CurrentChanged { get; } =
        Observable.Defer(() =>
            Observable.FromEventPattern<VirtualDesktopChangedEventArgs>(
                    h => VirtualDesktop.CurrentChanged += h,
                    h => VirtualDesktop.CurrentChanged -= h)
                .Select(e => e.EventArgs.NewDesktop.Id));

    // Shared lookup: every mutating operation needs the live VirtualDesktop instance for
    // a Guid, and "desktop no longer exists" is an expected, everyday Result failure —
    // not an exception — since desktops can vanish between UI render and user click.
    static Result<VirtualDesktop> Find(Guid desktopId) =>
        Result.Try(() => VirtualDesktop.FromId(desktopId), e => e.Message)
            .Ensure(d => d is not null, $"Desktop {desktopId} no longer exists.")
            .Map(d => d!);
}
