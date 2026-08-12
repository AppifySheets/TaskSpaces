using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using CSharpFunctionalExtensions;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using TaskSpaces.Core;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using TaskSpaces.Core.Time;
using TaskSpaces.Core.Updates;
using TaskSpaces.Windows.Activation;
using TaskSpaces.Windows.Desktops;
using TaskSpaces.Windows.Monitoring;
using TaskSpaces.Windows.Renaming;

namespace TaskSpaces.App;

// Composition root. Explicit wiring instead of a DI container -- five objects don't
// justify one, and the construction ORDER documents the architecture.
public partial class App : Application
{
    TaskbarIcon? trayIcon;
    WorkspaceManager? manager;
    WindowMonitor? monitor;
    // Flashing taskbar buttons. Held for the process lifetime like `monitor`: it owns a real
    // hwnd registered with the shell, and letting it go would silently stop the notification
    // badges.
    ShellHookAttentionMonitor? attentionMonitor;
    bool compatibilityMode;
    ManageWindow? manageWindow; // single instance: a left-click on the tray opens this
    HotkeyService? hotkeys;
    WorkspaceSwitchGesture? switcher; // Alt+Tab-style workspace picker (Win+Ctrl+Tab by default)
    Chord boundSwitcher;              // the chord the picker and the hotkey are currently registered on
    FloatingBar? floatingBar; // Task 11: created lazily on first show
    TimeTracker? timeTracker; // #53: active time per workspace, in its own file beside state.json

    // Held for the whole process lifetime, in a FIELD so the GC cannot collect it and quietly
    // release the lock while we are still running. See OnStartup for why it exists.
    System.Threading.Mutex? singleInstance;
    IVirtualDesktopService? desktops; // Task 11 fix round 4: promoted from a local so PinOwnWindow (below) can reach it from the tray/hover callbacks, not just OnStartup
    bool floatingBarPinned; // Task 11 fix round 4: pin the bar's real hwnd to all desktops exactly once (see PinFloatingBar)

    // --- check for updates (#71) ------------------------------------------------------
    //
    // Petre: "tell the user a new version exists and offer a link to the new file", with ground
    // rules that decide the whole shape of this: check on startup and daily, fail silently
    // offline, never block the UI, and an opt-out because it is the app's only phone-home.
    //
    // The announcement is deliberately quiet -- one balloon, and an item on the tray menu that
    // stays until the user is on the new version. Nothing is downloaded and nothing is replaced;
    // the app is portable on purpose and the link hands the decision back to the user.

    // Held so the timer cannot be collected, and so the tray menu can be rebuilt with it.
    System.Windows.Threading.DispatcherTimer? updateTimer;
    ReleaseInfo? availableUpdate;

    void StartUpdateChecks()
    {
        // The only gate. With it off nothing below ever runs, so no request leaves the machine.
        if (manager?.State.CheckForUpdates != true)
        {
            ClickTrace.Write("update checks off (State.CheckForUpdates is not true)");
            return;
        }

        // Daily, and the first one is on a delay rather than immediate. Startup is already the
        // busiest moment this process has -- desktop enumeration, the placement sweep, the rename
        // sweep -- and an update is never so urgent that it cannot wait half a minute for them.
        updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        updateTimer.Tick += (_, _) =>
        {
            // After the first tick this becomes the daily timer. Re-setting the interval on the
            // running timer beats a second timer that would have to be kept alive too.
            updateTimer!.Interval = TimeSpan.FromHours(24);
            CheckForUpdate();
        };
        updateTimer.Start();
    }

    // Fire-and-forget on purpose: nothing waits for the answer and nothing reports its absence.
    // `async void` is the honest signature for that -- there is no caller to hand a Task to -- and
    // it is safe here because the awaited method catches every failure it can produce and returns
    // it as a Result rather than throwing.
    async void CheckForUpdate()
    {
        var latest = await UpdateService.NewerThanRunningAsync().ConfigureAwait(true);

        // TRACED, every outcome, and this line is why: the whole path is silent on purpose, so
        // "i didn't get a notification" is indistinguishable from four different things -- the check
        // never ran, the request failed, there is no newer release, or the balloon was raised and
        // Windows did not draw it. Petre hit exactly that ambiguity testing #71 against a build
        // stamped one version behind, and there was nothing to read.
        ClickTrace.Write(
            latest.IsFailure ? $"update check failed: {latest.Error}"
            : latest.Value.HasNoValue ? $"update check: {UpdateService.RunningVersion} is current"
            : $"update check: {latest.Value.Value.Version} available, running {UpdateService.RunningVersion}");

        // A failure is today's answer, not a problem: no network, a proxy, GitHub rate-limiting a
        // shared IP. Tomorrow's tick asks again, and the user is never told any of it.
        if (latest.IsFailure || latest.Value.HasNoValue) return;

        var release = latest.Value.Value;

        // Already announced this exact version. The daily tick would otherwise re-balloon the same
        // release every day for as long as the user chooses not to update, which is nagging.
        if (availableUpdate?.Version == release.Version)
        {
            ClickTrace.Write($"update {release.Version} already announced, staying quiet");
            return;
        }
        availableUpdate = release;

        Announce(release);

        // ...and then ASK, with a real dialog, rather than trusting the balloon (#123).
        //
        // The original design was deliberately quiet: one balloon, plus a tray menu item, and no
        // interruption. That assumed the balloon appears. Measured on Petre's machine it never does:
        // there is no per-app notification key, the process has no AppUserModelID and the app is a
        // portable exe with no Start-menu shortcut, so Windows has no identity to attribute a toast to
        // and drops it. Petre, after a full end-to-end test: "i didn't see the update", then "popup".
        //
        // So the quiet channel is the one that was never seen, and the announcement now costs an
        // interruption ONCE PER RELEASE -- the availableUpdate guard above is what keeps it to once,
        // including across the daily tick. Answering No leaves the tray menu item exactly as it was, so
        // the decision remains reversible without the app asking twice.
        OfferUpdate(release);
    }

    // The menu item and the balloon, which the manual check (#110) shares: it has to leave the tray
    // in the same state a background check would, or asking by hand and then dismissing the dialog
    // would lose the news the check just found.
    //
    // ConfigureAwait(true) on both callers' awaits put us back on the dispatcher, which these need.
    void Announce(ReleaseInfo release)
    {
        trayIcon!.ContextMenu = TrayMenu.Build(compatibilityMode, OpenManage, ExitApp, CheckForUpdateNow,
            ($"Update to {release.Version}…", () => OfferUpdate(release)));

        // The balloon is BEST EFFORT and always has been, which the trace now says out loud. It is a
        // Shell_NotifyIcon balloon, so whether anything appears is Windows' decision: notifications
        // off, Focus Assist, a full action centre, or simply a shell that drops it. The tray MENU item
        // set above is the channel that does not depend on any of that, and it stays until the user is
        // on the new version -- so a missing balloon is a missed glance, not a missed update.
        try
        {
            trayIcon.ShowNotification(
                title: $"TaskSpaces {release.Version} is available",
                message: $"You are running {UpdateService.RunningVersion}. Click here to update.");
            ClickTrace.Write($"update {release.Version} announced: menu item set, balloon requested");
        }
        catch (Exception e)
        {
            // Never worth losing the app over, and never worth losing the menu item over either: it is
            // already set by the time we get here.
            ClickTrace.Write($"update {release.Version} announced: menu item set, balloon threw {e.GetType().Name}: {e.Message}");
        }
    }

    // #110: "check for new version as a right-click menu option."
    //
    // The same check the timer runs, with the opposite manners at every step. The background check
    // is silent about everything -- no network, a proxy, GitHub rate-limiting a shared IP are all
    // just today's answer -- because nobody asked it anything. This one was asked, and somebody is
    // waiting on the reply, so all three outcomes get a dialog: a newer version, no newer version,
    // or a check that could not be made.
    //
    // "No newer version" is the important one and the reason the item was left out until now: a
    // button that usually reports nothing looks broken every time it works correctly. Saying which
    // version is running turns that into an answer.
    //
    // NOT gated on State.CheckForUpdates, deliberately. That setting exists because the automatic
    // check is the app's only phone-home, so what it governs is the UNPROMPTED request. Clicking
    // this IS the prompt, and refusing to answer a direct question because of an opt-out from being
    // asked without prompting would be the wrong reading of it.
    async void CheckForUpdateNow()
    {
        // The tooltip is the only progress there is room for, and the same one DownloadAndRestart
        // uses. A menu item that closes and then says nothing for two seconds looks like a click
        // that missed.
        var wasTip = trayIcon!.ToolTipText;
        trayIcon.ToolTipText = "TaskSpaces — checking for updates…";

        var latest = await UpdateService.NewerThanRunningAsync().ConfigureAwait(true);

        trayIcon.ToolTipText = wasTip;

        if (latest.IsFailure)
        {
            if (MessageBox.Show(
                    $"Could not check for updates:\n{latest.Error}\n\nOpen the releases page instead?",
                    "TaskSpaces", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                OpenUrl(UpdateService.ReleasesPage);
            return;
        }

        if (latest.Value.HasNoValue)
        {
            MessageBox.Show(
                $"You are running {UpdateService.DisplayVersion}, which is the latest version.",
                "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var release = latest.Value.Value;

        // Announced as well as offered, so declining the dialog leaves the news on the tray menu
        // rather than throwing it away. The version guard the background check uses is deliberately
        // not here: asking by hand should always answer, even for a release already announced.
        availableUpdate = release;
        Announce(release);
        OfferUpdate(release);
    }

    // Petre: "i clicked it, it disappeared."
    //
    // He clicked the balloon and nothing happened, because nothing was listening: only the tray
    // MENU item did anything, and a notification that says "click here" and then does not is worse
    // than one that says nothing. Wired once, at startup, rather than per notification -- WPF
    // routed-event handlers accumulate, and re-adding one on every check would fire the flow twice
    // after the second check, three times after the third.
    //
    // `availableUpdate` rather than a captured release: the click arrives long after the check, and
    // the field is the one that is still current.
    void OnUpdateNotificationClicked()
    {
        if (availableUpdate is { } release) OfferUpdate(release);
    }

    // Petre: "it should just tell me -- new version available, do you want to update? if i do, then
    // it should download the new one and restart to the new one."
    //
    // ONE question, not three. An earlier shape asked to download, then asked again whether to
    // restart; that is two dialogs for a decision the user already made by saying yes.
    void OfferUpdate(ReleaseInfo release)
    {
        // A release with no exe attached can still be looked at, which is what the link is for.
        // Reached when a build was published without its artifact, or when the asset failed the
        // github.com/https checks in UpdateCheck.
        if (release.AssetName is null)
        {
            if (MessageBox.Show(
                    $"TaskSpaces {release.Version} is available, but this release has no downloadable executable.\n\nOpen the release page?",
                    "TaskSpaces", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                OpenReleasePage(release);
            return;
        }

        var answer = MessageBox.Show(
            $"TaskSpaces {release.Version} is available. You are running {UpdateService.RunningVersion}.\n\n" +
            $"Download it next to the current program and restart into it?\n\n" +
            $"The version you are running now is kept, not replaced.",
            "TaskSpaces", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes) DownloadAndRestart(release);
    }

    // Downloads, then starts the new exe and stands down.
    //
    // `async void` for the same reason as the check: this is the end of an event, and there is no
    // caller to hand a Task to.
    async void DownloadAndRestart(ReleaseInfo release)
    {
        // The only progress there is room for. A ~75 MB download takes long enough that a tray icon
        // saying nothing looks like a click that did nothing -- which is the exact complaint that
        // started this. A real progress window would need cancellation, a percentage and a place to
        // live, and this is a once-a-release wait.
        var wasTip = trayIcon!.ToolTipText;
        trayIcon.ToolTipText = $"TaskSpaces — downloading {release.Version}…";

        var downloaded = await UpdateService.DownloadAsync(release).ConfigureAwait(true);

        trayIcon.ToolTipText = wasTip;

        if (downloaded.IsFailure)
        {
            // Told, not swallowed: unlike the background check, the user asked for this and is
            // waiting on it. The page is the way through -- the commonest cause is a folder this
            // process cannot write to, and downloading by hand works fine.
            if (MessageBox.Show(
                    $"Could not download {release.Version}:\n{downloaded.Error}\n\nOpen the release page instead?",
                    "TaskSpaces", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                OpenReleasePage(release);
            return;
        }

        // RELEASED BEFORE the new process starts, and this is the part that makes the handover work
        // at all. Both instances exist for a moment, and the single-instance guard is the first
        // thing the new one runs -- so a mutex still held here means the new version says
        // "TaskSpaces is already running" and quits, leaving the user on the old one with no clue
        // why the update did nothing.
        //
        // Letting go here rather than relying on the switch below is deliberate: the switch only
        // helps if the version being STARTED understands it, and versions already published do not.
        // Releasing first works whatever we are starting.
        //
        // The gap where nobody holds it is a few milliseconds during which a third copy could
        // start. That needs someone to double-click the exe inside that window, and the cost is the
        // "already running" dialog they would have got anyway.
        if (singleInstance is { } held)
        {
            held.ReleaseMutex();
            held.Dispose();
            singleInstance = null;
        }

        try
        {
            // Belt and braces to the release above: a build that understands this switch will also
            // WAIT for the previous instance rather than trusting the timing (see OnStartup). Both
            // together mean the handover survives a slow shutdown and an old binary alike.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(downloaded.Value)
            {
                UseShellExecute = true,
                Arguments = AwaitPreviousSwitch,
            });
        }
        catch (Exception e)
        {
            MessageBox.Show(
                $"Downloaded to:\n{downloaded.Value}\n\nbut could not start it:\n{e.Message}\n\nYou can run it yourself.",
                "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Stand down so the new instance can take the mutex. Shutdown rather than Environment.Exit:
        // the ordinary exit path restores every renamed window title and disposes the tray icon,
        // and skipping it would leave the user's windows wearing names we gave them.
        Shutdown();
    }

    // Passed by an updating instance to the version it starts. A switch rather than a file or an
    // environment variable because it has to survive exactly one process boundary and nothing else.
    const string AwaitPreviousSwitch = "--await-previous";

    // UseShellExecute is what makes this open a BROWSER rather than trying to execute the string.
    // The url is already known to be http(s) -- UpdateCheck refuses a release whose html_url is
    // anything else -- which matters because it arrived from the network.
    static void OpenReleasePage(ReleaseInfo release) => OpenUrl(release.PageUrl);

    // Split out for #110's failure path, which has no ReleaseInfo to name a page with: a check that
    // could not be made knows nothing about any release, and the releases page is still the way
    // through.
    static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            // No browser registered, or the shell refused. Worth saying, because unlike the background
            // check this one happened because the user just clicked something.
            MessageBox.Show($"Could not open the download page:\n{e.Message}\n\n{url}",
                "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // The app's icon, loaded once from the Resource the csproj also stamps into the exe.
    // Public so every window can bind its own Icon to it (Manage, switcher, prompts) without
    // each one re-decoding the file or hardcoding its own path.
    // Assembly-qualified pack URI, matching the window XAML: the short "/Assets/..." form
    // resolves against Application.ResourceAssembly, which only a WPF exe's generated Main
    // sets, so it breaks anywhere the app is loaded as a library (notably under test).
    public static readonly System.Windows.Media.ImageSource AppIcon =
        new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/TaskSpaces.App;component/Assets/taskspaces.ico"));

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // SINGLE INSTANCE, and it must be the very first thing that happens.
        //
        // Petre hit the visible symptom: "TaskSpaces could not register these keyboard
        // shortcuts (another app already owns them)" listing ALL of them. RegisterHotKey is
        // exclusive per chord, and the only thing that would own exactly OUR set is another
        // copy of TaskSpaces. The failed hotkeys were the least of it though. A second
        // instance also means two tray icons, two rename sweeps fighting over the same
        // windows, two startup placement sweeps, and two processes writing state.json with
        // last-writer-wins -- i.e. silent loss of workspaces or renames.
        //
        // This matters more now than it would have a week ago: the app ships as a portable
        // exe with no installer, so double-clicking it twice is the ordinary mistake rather
        // than an unusual one.
        //
        // "Local\" scopes the mutex to the login SESSION, not the machine: two users on one
        // PC should each get their own instance, since every piece of state this app touches
        // (state.json under %APPDATA%, the HKCU Run key, the user's own desktops) is per-user.
        // Before anything else that could be worth tracing, and it writes only when tracing is on
        // -- so the log's first line always says WHICH switch turned it on, and an empty log means
        // "not recording" rather than "nothing happened". Losing a reproduction to that ambiguity
        // is what put this line here (see ClickTrace).
        ClickTrace.Announce();

        singleInstance = new System.Threading.Mutex(initiallyOwned: true, @"Local\TaskSpaces.SingleInstance", out var isOnlyInstance);

        // The other half of "restart into the new version" (#71). The old instance starts this one
        // and then shuts down, so for a moment BOTH exist and the mutex is still held by the
        // process on its way out -- which the guard below would report as "TaskSpaces is already
        // running", leaving the user with the old version and a confusing dialog.
        //
        // So a build started by an update waits for the previous one to let go, instead of giving
        // up on the first try. Only ever with this switch: an ordinary double-click still gets the
        // immediate answer, because there the other instance is not going anywhere.
        //
        // Ten seconds is a shutdown, not a download -- the old instance has already finished
        // fetching by the time it starts this one, and all it has left to do is dispose a tray icon
        // and restore window titles.
        if (!isOnlyInstance && e.Args.Contains(AwaitPreviousSwitch))
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!isOnlyInstance && DateTime.UtcNow < deadline)
            {
                // Disposed and re-created rather than waited on: this process never owned the
                // mutex, and WaitOne on a mutex owned by a process that dies would hand back an
                // abandoned-mutex exception rather than a clean acquisition.
                singleInstance.Dispose();
                System.Threading.Thread.Sleep(250);
                singleInstance = new System.Threading.Mutex(initiallyOwned: true, @"Local\TaskSpaces.SingleInstance", out isOnlyInstance);
            }
        }

        if (!isOnlyInstance)
        {
            // Told, not silently exited: a portable exe that appears to do nothing when
            // double-clicked reads as broken, and the icon is easy to miss in a full tray.
            MessageBox.Show(
                "TaskSpaces is already running.\n\nLook for the tiled icon in the notification area, and click it to open Manage.",
                "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Information);
            singleInstance = null; // not ours to release
            Shutdown();
            return;
        }

        // AFTER the single-instance guard, deliberately: a second copy that is about to tell the
        // user it is already running has no business rewriting where startup points. Only the
        // instance that actually takes ownership gets to claim it.
        StartupRegistration.ReassertIfEnabled();

        // Reviewer (fix round 1, Critical, last-ditch backstop): an unhandled exception on
        // the dispatcher thread -- e.g. the ArgumentException a duplicate-name dictionary
        // build used to throw -- otherwise takes the whole process down immediately, with
        // every window still wearing its renamed title. WPF's default behavior for an
        // unhandled dispatcher exception is to terminate the process once this handler
        // returns with e.Handled left false, so this is NOT a crash suppressor: it is the
        // last opportunity to run RestoreAllTitles() -- "leave every window as we found
        // it" -- before that termination happens, plus a MessageBox so the failure isn't
        // silent. e.Handled is deliberately left false: we still want the crash (and its
        // real stack trace/telemetry), just not a window stuck with the wrong title.
        DispatcherUnhandledException += (_, args) =>
        {
            manager?.RestoreAllTitles();
            MessageBox.Show($"TaskSpaces hit an unexpected error and must close:\n{args.Exception.Message}",
                "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = false; // let it die -- titles are already restored
        };

        var stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskSpaces");
        var statePath = Path.Combine(stateDir, "state.json");
        // The trace explains the "Unplaced" row: why a window's desktop could not be resolved.
        desktops = new VirtualDesktopService(ClickTrace.On ? ClickTrace.Write : null);
        monitor = new WindowMonitor(ClickTrace.On ? ClickTrace.Write : null);
        // The activator is what lets a desktop switch put focus back on the window that had it
        // last time you were there (WorkspaceManager.RestoreLastActive). Same WindowActivator
        // the bar's icons use -- one definition of "bring it to me", so a restored window and a
        // clicked one behave identically.
        // Flashing taskbar buttons, so the bar can say which app wants Petre. Its own monitor
        // rather than part of WindowMonitor: a flash is invisible to WinEvents (measured -- a
        // probe hooked every event in both ranges and saw nothing), so this listens to the shell
        // hook instead, which is a different subscription with a different lifetime.
        attentionMonitor = new ShellHookAttentionMonitor();
        manager = new WorkspaceManager(desktops, monitor, new Win32WindowTitles(), new JsonPersistenceStore(stateDir),
            activator: new WindowActivator(), screenLayout: new ScreenLayout(), attention: attentionMonitor,
            // #94: which app started this one, so a window opened by another app joins it. Stateless
            // and cheap, asked only when a new window appears.
            processes: new ProcessTree(),
            // ...and a line per decision when tracing is on, because a window that stays put looks the
            // same whether no launcher was found, two workspaces were, or the walk never ran.
            trace: ClickTrace.On ? ClickTrace.Write : null,
            // #105: the ordinal colour band groups windows by the picture they are drawn with, and this
            // is the only layer that knows what that is. A pure read of the icon cache -- no probing, no
            // bitmap creation -- so it is safe to call while an overview is being built, and it answers
            // None for a window whose icon has not arrived yet, which the builder falls back for.
            artworkOf: w => IconCache.ArtworkKeyOf(w.Handle, w.ProcessPath) is { } key
                ? key
                : Maybe<string>.None);

        // Time tracking (#53). Its own file beside state.json, because this is the one thing the
        // app stores that grows without bound -- one row per workspace per day, forever -- and
        // mixing it into state.json would mean rewriting all of that on every workspace rename.
        timeTracker = new TimeTracker(new JsonTimeStore(stateDir), new InputActivity(), () => DateTime.Now);

        // Spec §Error handling: if the COM API is unrecognized (post-Windows-Update),
        // degrade to listing workspaces with a banner -- never crash, never move windows.
        compatibilityMode = desktops.Initialize().IsFailure;
        if (compatibilityMode)
        {
            // Finding 2 (reviewer, Important): compatibility mode still lists workspaces
            // per spec ("switcher still lists workspaces but shows a compatibility
            // banner") -- it just can't reconcile desktops (there are none to reconcile
            // onto) or start the window monitor (no desktop moves/renames will ever
            // happen, so there's nothing for it to drive). LoadState() alone gives the
            // tray menu and Manage window a read-only view of what's on disk.
            manager.LoadState()
                .TapError(err => MessageBox.Show(
                    $"TaskSpaces could not load your saved workspaces:\n{err}\n\nStarting with an empty list.",
                    "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
        else
        {
            // Finding 1 (reviewer, Critical): manager.Start()'s Result used to be discarded.
            // JsonPersistenceStore.Load() deliberately FAILS (rather than degrading to
            // AppState.Empty) when state.json is corrupt, precisely so this call site can
            // tell the difference and refuse to silently overwrite the user's data. If we
            // ignored the failure here, State would stay empty and the very next action
            // that persists (adding a workspace, a window appearing, ...) would happily
            // write that empty state straight over the corrupt file -- destroying whatever
            // was recoverable in it. Instead: back the corrupt file up (rename, never
            // delete), tell the user, and only THEN retry -- the retry succeeds because
            // Load() now sees a missing file, which is the normal "first run" case.
            var started = manager.Start();
            if (started.IsFailure)
            {
                var loadError = started.Error;
                BackupCorruptState(statePath, loadError);
                started = manager.Start();
                if (started.IsFailure)
                    MessageBox.Show(
                        $"TaskSpaces failed to start even after backing up state.json:\n{started.Error}",
                        "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            // Finding 1(b): monitor.Start() was also unchecked. A failure here means
            // WinEvent hooks never registered -- the app would run silently believing it
            // sees every window when it in fact sees none. Never fatal (v1 can limp along
            // with manual "Refresh" in Manage), but never silent either.
            monitor.Start().TapError(err => MessageBox.Show(
                $"TaskSpaces: window monitoring is unavailable:\n{err}\n\nRules, auto-renaming and the window list will not update automatically.",
                "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Warning));

            // Deliberately quieter than the failure above: losing the shell hook costs one
            // decoration on the icons, while losing WinEvents costs the window list itself.
            // Worth a line in the debugger, not a dialog interrupting Petre at startup.
            attentionMonitor.Start().TapError(err =>
                System.Diagnostics.Debug.WriteLine($"TaskSpaces: notification badges unavailable: {err}"));
        }

        trayIcon = new TaskbarIcon
        {
            // The real app icon, replacing the generic SystemIcons.Application placeholder.
            // IconSource (an ImageSource) rather than Icon (a System.Drawing.Icon): the ico's
            // frames are PNG-compressed, which WPF's icon decoder handles cleanly while
            // GDI+ historically does not. Same file the exe is stamped with, so tray,
            // taskbar and window icons cannot drift apart.
            IconSource = AppIcon,
            ToolTipText = compatibilityMode ? "TaskSpaces (compatibility mode)" : "TaskSpaces",
            ContextMenu = TrayMenu.Build(compatibilityMode, OpenManage, ExitApp, CheckForUpdateNow),
            // Petre: "left click gives us the main window, right click gives exit and
            // manage". RightClick only, so a left-click is free to open Manage (wired
            // below) instead of raising the same menu twice.
            MenuActivation = PopupActivationMode.RightClick,
        };
        // Left-click IS the main window now. Manage was previously reachable only through a
        // menu item, which made the app's one real window the least accessible thing in it.
        trayIcon.TrayLeftMouseUp += (_, _) => OpenManage();
        // H.NotifyIcon 2.x creates the shell icon lazily: with no window/XAML tree, a
        // code-built TaskbarIcon never registers with the tray until ForceCreate() is
        // called (see the library's own Wpf.Windowless sample -- found the hard way when
        // the app ran headless with no icon at all). Efficiency mode stays OFF: it puts
        // the process under EcoQoS throttling, and we need WinEvent callbacks handled
        // promptly to re-apply renames and route new windows without visible lag.
        trayIcon.ForceCreate(enablesEfficiencyMode: false);

        // Clicking the notification is what a notification saying "click here to update" has to do.
        // Subscribed ONCE, here, rather than alongside each ShowNotification: routed-event handlers
        // accumulate, so re-adding it per check would run the flow twice after the second check.
        trayIcon.TrayBalloonTipClicked += (_, _) => OnUpdateNotificationClicked();

        // AFTER ForceCreate, necessarily: a balloon needs a registered shell icon to come out of,
        // and announcing an update into a tray icon that does not exist yet is a silent no-op.
        StartUpdateChecks();
        // NOTE: no StateChanged subscription rebuilding this menu any more. It used to be
        // rebuilt on every pulse so the workspace list and the "Show floating bar" checkmark
        // stayed accurate; the menu now holds neither, so it is built once and never needs
        // to change. One fewer thing reacting to every window event.

        // Task 11 (spec §Floating icon bar): restore the bar's own on/off state across
        // restarts. Gated on !compatibilityMode for the same reason the hotkey is
        // are below -- every icon click calls JumpTo, which needs a real desktop to
        // switch to, and compatibility mode has none.
        // Always shown, no longer conditional on the persisted Visible flag. Petre: "show
        // floating bar doesn't make sense anymore, it's crucial for the app's design." The bar
        // is the only surface that lists windows and jumps to them now, so it starts with the
        // app. AppState.FloatingBar is still read for its POSITION; Visible is retained in the
        // record only so older state.json files keep deserialising, and is ignored.
        // Still gated on compatibility mode: every icon click calls JumpTo, which needs real
        // desktops to switch between.
        if (!compatibilityMode)
        {
            floatingBar = new FloatingBar(manager);
            // The bar's hwnd is created HERE, before it is ever shown, for two reasons that
            // both hang off the same handle:
            //
            //   1. monitor.Ignore -- WindowMonitor no longer hooks with
            //      WINEVENT_SKIPOWNPROCESS (Petre: "why isn't the taskspaces window in the
            //      floating window?"), so without this the bar would list ITSELF, and since
            //      we pin it below, it would list itself in the pinned row forever. Must
            //      happen before the first Show(), or that first EVENT_OBJECT_SHOW slips
            //      through and the bar acquires a permanent row for itself.
            //   2. PinFloatingBar -- which needed a real handle anyway, and previously had
            //      to guard against being called too early.
            //
            // EnsureHandle() is safe this early: AllowsTransparency/WindowStyle are set in
            // XAML and applied by InitializeComponent, which the constructor above ran.
            var barHwnd = new WindowInteropHelper(floatingBar).EnsureHandle();
            monitor.Ignore(barHwnd);
            floatingBar.ShowBar();
            PinFloatingBar(barHwnd);

            // Petre: "if i activate the taskbar, it hides the floating window". Topmost is a
            // shared band, not a rank, so the taskbar (and StartAllBack's menu) climbs over
            // the bar the moment it is activated. Reclaiming the top of the band on every
            // foreground change is the fix -- see FloatingBar.ReclaimTopmost. Subscribed here
            // rather than inside the bar because the monitor is the composition root's to hand
            // out, and the bar has no business knowing what a WinEvent hook is.
            monitor.ForegroundChanged.Subscribe(_ => floatingBar.ReclaimTopmost());

            // ...and a 1s timer, because the event alone is not enough. Petre: "taskbar makes
            // its way over the floating window if i click the taskbar twice, so maybe you could
            // be resetting the topmost position of the float every second or so". Exactly right:
            // the SECOND click changes no foreground window, so no event fires, while the shell
            // still re-raises the taskbar within the band.
            //
            // Its own timer rather than a job on the 5s sweep: the sweep also enumerates every
            // window and re-asserts every drifted title, and none of that wants to run five
            // times more often just to keep one z-order claim fresh.
            var topmost = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            topmost.Tick += (_, _) =>
            {
                floatingBar.ReclaimTopmost();
                // Petre: "i want to always refresh what's the active window, i can't afford to
                // think that one window is active and another being highlighted."
                //
                // Rides THIS timer rather than the 5s sweep, even though it is a drift repair
                // and the sweep is where drift repairs live. Two reasons. It is O(1) -- a
                // GetForegroundWindow and a dictionary lookup, pulsing only when the answer
                // actually changed -- so the argument above for keeping the sweep's heavy jobs
                // at 5s simply does not apply to it. And the cost of being wrong is paid by
                // eyes: a highlight pointing at the wrong window is misinformation, not a
                // missing feature, so up to five seconds of it is far worse than up to one.
                manager.ResyncActiveWindow();
                // ...and serve any rebuild the bar postponed while a mouse button was down. It
                // flushes those on mouse-up, but a drag has no mouse-up to flush from -- the OLE
                // drag loop eats it -- so without a heartbeat a dropped window stayed drawn in
                // its old row until something unrelated happened to pulse.
                floatingBar.FlushIfIdle();
            };
            topmost.Start();
        }

        // The hover-to-peek switcher panel USED to be summoned from here, with a 400ms
        // DispatcherTimer, a drift radius to reject drive-by cursor passes, and a proximity
        // keep-alive poll inside the panel. All of it is gone, along with the panel itself.
        //
        // Petre: "no switcher panel required on hover either", "we already have a nice way to
        // move windows across workspaces". Every job the panel did is now done by a surface
        // that is permanently on screen or one left-click away:
        //   see every window across workspaces -> the floating bar
        //   jump to a window                   -> bar icons
        //   drag windows between workspaces    -> bar rows, and Manage's Windows tab
        //   switch workspace                   -> bar row labels, and the Win+Ctrl+Tab switcher
        //   rename / pin / restore             -> bar icon right-click, Manage's Windows tab
        //
        // The deletion was cheap for one specific reason: the panel and Manage's Windows tab
        // already shared ONE control (WindowGroupsView), built that way in Task 10 so they
        // could not drift apart. Removing the panel left that control untouched in Manage, so
        // grouped drag-and-drop window management survived intact.

        // The app's ONE global chord: the Alt+Tab-style workspace switcher, Win+Ctrl+Tab by
        // default. Petre: "i don't think we need ctrl+alt and those, ctrl+tab is good enough" --
        // Ctrl+Alt+arrows and Ctrl+Alt+1..9 are gone, and HotkeyService's header records why.
        //
        // Gated on !compatibilityMode: the switcher ends in manager.Switch, which needs a real
        // desktop to switch to, and compatibility mode has none.
        //
        // ...and gated on the BAR existing, which is a real dependency rather than a null check
        // for the compiler's benefit: the gesture draws its candidate ring on the bar's rows and
        // anchors its picker against the bar, so without one there is nothing for it to drive.
        // Both are created under the same !compatibilityMode condition above, so this never
        // actually excludes anything -- but writing it as a pattern rather than a `!` says why
        // the pair travel together, and a suppression would only have said "trust me".
        if (!compatibilityMode && floatingBar is { } bar)
        {
            // Win+Ctrl+Tab (the configured chord) walks workspaces in most-recently-used order
            // while the modifiers stay held, and switches on release -- Alt+Tab's gesture,
            // applied to workspaces rather than windows.
            // Ignored by the monitor for the same reason the floating bar is: it is our own
            // chrome, and now that the hooks see our process it would otherwise appear in
            // the bar as a window every time it flashed up.
            // WorkspaceManager.SwitcherShortcut has already fallen back to the default for
            // anything unusable, so this parse cannot realistically fail -- but Parse returns
            // a Result, and inventing a value on failure here would hide a real bug behind a
            // silently different shortcut. Taking .Value is the honest reading.
            boundSwitcher = Chord.Parse(manager.SwitcherShortcut).Value;
            // Handed the bar as well as the manager: the gesture lights the bar's rows AND
            // anchors its picker against it (Petre: "show the previous list but ONLY next to the
            // floating window"). Ignored by the monitor for the same reason the bar is -- it is
            // our own chrome, and the hooks see our process.
            switcher = new WorkspaceSwitchGesture(manager, boundSwitcher, bar);
            monitor.Ignore(switcher.EnsureHandle());

            hotkeys = new HotkeyService(direction => switcher.Step(direction), boundSwitcher);

            // Petre: "i want it configurable". Rebinding is driven off StateChanged rather
            // than off a callback from the Shortcuts tab, so ANY route that changes the
            // shortcut takes effect immediately -- the editor today, and anything else that
            // ends up writing it later. Comparing against what is currently bound makes this
            // a no-op on the many pulses that have nothing to do with shortcuts.
            manager.StateChanged.Subscribe(_ => RebindSwitcherIfChanged());
            // One chord now, so at most one failure -- and it names the chord, since the whole
            // point is that the reader can go and change it on Manage -> Shortcuts.
            if (hotkeys.Failures.Count > 0)
                MessageBox.Show(
                    string.Join("\n", hotkeys.Failures)
                    + "\n\nTaskSpaces will keep running. Pick a different chord on Manage → Shortcuts.",
                    "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Safety-net sweep (spec §5): event-driven handling is the fast path, this is the
        // truth. Every 5s it re-asserts drifted titles, adopts persisted renames, and --
        // added after Petre found two windows missing from his Personal row -- reconciles the
        // window list itself against what the OS actually lists.
        //
        // That second job matters because WinEvents are lossy in two different ways: an
        // OUTOFCONTEXT event can be dropped when the message queue is busy, and a HIDE that
        // did not mean "gone" leaves a window flagged hidden until a SHOW that a window on
        // another virtual desktop never fires. Either way the bar silently loses a window
        // forever. See WindowMonitor.Resync for the full account. Costs one EnumWindows per
        // tick in the steady state.
        if (!compatibilityMode)
        {
            var sweep = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            var sweeps = 0;
            sweep.Tick += (_, _) =>
            {
                // ISOLATED, and traced, as a GUARD rather than as a fix for anything observed. Worth
                // being clear about that, because it was written while chasing a bug that turned out to
                // be elsewhere (a tray-minimised window has no resolvable desktop; see
                // WorkspaceManager.lastKnownDesktop) and a comment claiming otherwise would be a false
                // record.
                //
                // The reasoning stands on its own. This sweep is the app's only repair mechanism for a
                // window list that WinEvents can only ever lose entries from. An exception in a
                // DispatcherTimer tick goes to the dispatcher's unhandled handler, which this app
                // deliberately swallows so a stray fault cannot take the process down with renamed
                // titles still applied. The cost of that mercy would be a sweep that throws once and
                // then throws forever, silently, freezing the window list at that moment -- and the
                // reads inside it (a process path, a command line) are exactly the ones that throw for
                // an elevated or exiting process.
                //
                // So each job runs on its own and each failure is written down once. If the trace ever
                // shows one of these lines, that is a real bug with a name, found before it could look
                // like something else.
                try { monitor.Resync(); }
                catch (Exception e) { ClickTrace.Write($"sweep: Resync threw {e.GetType().Name}: {e.Message}"); }

                try { manager.ReapplyRenames(); }
                catch (Exception e) { ClickTrace.Write($"sweep: ReapplyRenames threw {e.GetType().Name}: {e.Message}"); }

                // Where each open folder is sitting (#132). Rides this timer rather than owning one
                // because it asks the same question the sweep already exists to ask -- where windows
                // ARE -- and because a position has to survive two ticks to be believed, which makes
                // the sweep's own interval the unit of patience. Writes only when an answer changes,
                // so the steady state is one dictionary lookup per window with a folder open.
                try { manager.SnapshotContainerHomes(); }
                catch (Exception e) { ClickTrace.Write($"sweep: SnapshotContainerHomes threw {e.GetType().Name}: {e.Message}"); }

                // A heartbeat, rare enough to be free: one line on the first tick and one every five
                // minutes after. A stalled timer is otherwise indistinguishable from a timer whose
                // work does nothing, and telling those apart is what cost this afternoon.
                if (++sweeps == 1 || sweeps % 60 == 0) ClickTrace.Write($"sweep tick {sweeps}");
            };
            sweep.Start();
        }

        // Time tracking's own clock (#53). A THIRD timer rather than a job on the 5s sweep, and
        // the interval is the reason: accrual credits whatever the interval is, so riding the
        // sweep would mean crediting five seconds at a time, twelve times more writes to the
        // ledger for no more accuracy. Fifteen seconds is the granularity Petre's own proposal
        // asked for, and it is also the error bar on a switch mid-tick -- which is not worth more
        // precision than that.
        //
        // Started even in compatibility mode: there are still workspaces to attribute time to,
        // and the only thing missing there is the ability to MOVE windows between them.
        timeTracker.Start();
        // Two years of history is generous for a personal tool and still trivially small; the
        // point is only that "forever" is not a plan. Pruned once, at startup, because a
        // background prune is machinery for a problem measured in kilobytes per year.
        timeTracker.Forget(DateOnly.FromDateTime(DateTime.Now).AddYears(-2));

        var tracking = new System.Windows.Threading.DispatcherTimer { Interval = ActivityAccrual.TickInterval };
        tracking.Tick += (_, _) => timeTracker.Tick(manager.CurrentWorkspaceId, ActivityAccrual.TickInterval);
        tracking.Start();
        // Whatever is still unwritten when the app closes. Exiting is the one moment a lost five
        // minutes is guaranteed rather than merely possible.
        Exit += (_, _) => timeTracker.Flush();

        // The "Restore workspaces?" prompt USED to appear here. Petre: "this seems like an
        // overkill", then "no, bad, don't want this". Gating it to first-run-after-reboot was
        // my first answer and it was the wrong one -- he did not want a better-timed prompt, he
        // did not want the prompt.
        //
        // Removing it left the whole LAUNCH path unreachable, because Manage's Windows tab (the
        // other ▶ Start surface) had already gone: StartWorkspace, StartRosterEntry,
        // RegisterPendingLaunch, PendingPlacements, IAppLauncher and AppLauncher are all deleted
        // with it, and placement precedence drops from three tiers to two.
        //
        // The ROSTER itself stays and is untouched. It is the workspace half of placement
        // memory (identity -> workspace, written on every Place), which is what puts a window
        // back where you last had it. Only the ability to relaunch a closed app is gone.

        // OS shutdown/logoff: every window is about to close, and each close would fire
        // Disappeared and ERASE the inventory placement memory needs. Unhook the monitor
        // FIRST so state.json keeps its last-known contents, then put titles back.
        //
        // Fix round 1 (reviewer, minor): deliberately does NOT call hotkeys?.Dispose()
        // here, unlike ExitApp() below. SessionEnding means Windows is tearing the whole
        // process down for logoff/shutdown regardless of what we do -- RegisterHotKey's
        // registrations are per-process and vanish with it, so unregistering first would
        // be pure ceremony with nothing left to observe the result. ExitApp is the
        // orderly, still-running-normally exit path (tray menu -> Exit), where disposing
        // first is the tidy, deterministic thing to do. The asymmetry is intentional.
        SessionEnding += (_, _) =>
        {
            monitor.Dispose();
            manager.RestoreAllTitles();
        };
    }

    // Finding 1 (reviewer, Critical): renames (never deletes) a corrupt state.json so the
    // user's data stays recoverable, and tells them about it. Called once, right before
    // the retried manager.Start() -- by the time this returns, nothing has had a chance to
    // persist an empty state over the original file.
    static void BackupCorruptState(string statePath, string loadError)
    {
        try
        {
            if (File.Exists(statePath))
            {
                var backupPath = statePath + ".bak";
                // Don't clobber a previous backup's forensic value -- an already-backed-up
                // corruption episode gets its own timestamped name instead.
                if (File.Exists(backupPath))
                    backupPath = $"{statePath}.{DateTime.Now:yyyyMMddHHmmss}.bak";
                File.Move(statePath, backupPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: even if the rename itself fails (e.g. file locked by another
            // process), the MessageBox below still fires -- the user is never left thinking
            // everything is fine when it isn't.
        }

        MessageBox.Show(
            $"TaskSpaces found a corrupted state file and could not load your saved workspaces:\n{loadError}\n\n" +
            "The corrupted file was backed up (state.json.bak) rather than deleted. Starting fresh.",
            "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // SINGLE INSTANCE, which matters now that a left-click opens this: the old version built
    // a new ManageWindow on every call, which was tolerable behind a menu item and would
    // stack up a pile of identical windows behind an easily-mis-clicked tray icon. A second
    // click now surfaces the window that is already open instead.
    void OpenManage()
    {
        if (manageWindow is { IsVisible: true })
        {
            if (manageWindow.WindowState == WindowState.Minimized) manageWindow.WindowState = WindowState.Normal;
            manageWindow.Activate();
            return;
        }

        manageWindow = new ManageWindow(manager!, compatibilityMode, timeTracker);
        manageWindow.Closed += (_, _) => manageWindow = null;
        manageWindow.Show();
    }


    // Task 11 fix round 4 (Petre: the bar stayed behind on workspace switch): dogfooding
    // our own Pin support. The FloatingBar is an ordinary top-level window --
    // without pinning, each belongs to whichever desktop it happened to be showing on
    // and vanishes the instant Petre switches away, defeating the entire point of an
    // "always visible" bar / "peek from anywhere" panel. Pinning makes them omnipresent.
    //
    // The caller passes the handle it already created with EnsureHandle(), so there is no
    // "is it shown yet" question left to guard. Guarded by the pinned flag so the native
    // pin call (and, on failure, the MessageBox) happens at most ONCE per window lifetime:
    // Windows' pin state lives on the hwnd and survives Hide()/Show() cycles, and this
    // window is never closed or recreated while the app runs.
    //
    // Pinning our own window used to be invisible to the rest of the app because
    // WindowMonitor hooked with WINEVENT_SKIPOWNPROCESS. That flag is gone (Petre wanted to
    // see the Manage window in the bar), so the bar would now be a perfectly ordinary
    // pinned window as far as the overview is concerned -- which is exactly why the caller
    // registers this handle with monitor.Ignore first.
    void PinFloatingBar(nint hwnd)
    {
        if (floatingBarPinned) return;
        floatingBarPinned = true;
        desktops!.Pin(new WindowHandle(hwnd))
            .TapError(err => MessageBox.Show(
                $"TaskSpaces could not pin the floating bar to every workspace:\n{err}\n\nIt will only stay visible on the desktop it was shown on.",
                "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    // Re-registers the Alt+Tab-style switcher when its configured chord changes, and moves
    // the picker's hold-detection onto the new modifiers at the same time. Both halves must
    // move together: a chord registered on Win+Tab whose release poll still watched Ctrl+Alt
    // would open the picker and never close it.
    void RebindSwitcherIfChanged()
    {
        var configured = Chord.Parse(manager!.SwitcherShortcut);
        if (configured.IsFailure || configured.Value == boundSwitcher) return;
        boundSwitcher = configured.Value;
        switcher!.Rebind(boundSwitcher);
        // A chord another app already owns is worth saying out loud: this is a change Petre
        // just made by hand, so silence would read as "applied" when nothing was.
        hotkeys!.BindSwitcher(boundSwitcher)
            .TapError(err => MessageBox.Show(err, "TaskSpaces", MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    void ExitApp()
    {
        manager?.RestoreAllTitles();  // leave every window as we found it
        monitor?.Dispose();
        hotkeys?.Dispose(); // unregisters RegisterHotKey chords before the process exits
        switcher?.Dispose(); // stops the release poll and closes the picker window
        trayIcon?.Dispose();
        // Released explicitly on the orderly exit path so a relaunch a moment later is never
        // refused. Windows would release it at process death anyway; being deterministic here
        // costs one line and removes any doubt about ordering.
        if (singleInstance is { } held)
        {
            held.ReleaseMutex();
            held.Dispose();
            singleInstance = null;
        }
        Shutdown();
    }
}
