namespace TaskSpaces.Core.Rehydration;

// Petre, on being shown the "Restore workspaces?" prompt yet again: "this seems like an
// overkill".
//
// It was. The prompt appeared on EVERY launch, because its only condition was "some workspace
// has an app that is not running" -- which is true the moment you close anything. Restarting
// the app fifteen times in an afternoon meant fifteen prompts offering to relaunch one app.
//
// The feature's own name is post-REBOOT rehydration, and that is the missing condition. A
// reboot is the case the prompt exists for: desktops do not survive one, so the inventory in
// state.json is the only record of what was where. An app restart within the same session is
// not that case at all -- the windows are still on their desktops, exactly where you left them.
//
// Pure and in Core so the rule is testable without a clock, a registry or a reboot.
public static class RestoreOffer
{
    // True when this run is the FIRST since the machine booted.
    //
    // Both boundary cases resolve to "offer", deliberately: a state.json with no recorded run
    // (first ever launch, or one written by a build that predates this field) cannot prove we
    // already ran this session, and the cost of asking once when we did not need to is a
    // dialog, while the cost of staying silent when we did need to is a workspace the user has
    // to rebuild by hand.
    // Phrased as "offer unless we can PROVE we already ran since boot", which is why the
    // comparison is strict on the other side: a run recorded exactly at bootedAt proves
    // nothing, and bootedAt is itself an approximation (now minus uptime), so the boundary
    // belongs with the unknowns rather than with the proof.
    public static bool ShouldOffer(DateTimeOffset? previousRunAt, DateTimeOffset bootedAt) =>
        previousRunAt is not { } previous || previous <= bootedAt;
}
