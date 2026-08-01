using System.Diagnostics;
using WindowsDesktop;

// SPIKE — is Slions.VirtualDesktop usable on THIS machine (Win11 build 26200 / 25H2)?
// The library runtime-compiles COM interop matched to the OS build; 6.9.2 documents
// builds up to 26100. Each numbered check prints OK/FAIL so a partial run still tells
// us exactly which capability broke. Findings go to
// docs/superpowers/notes/2026-08-01-virtualdesktop-spike.md.
//
// This spike creates a real virtual desktop and switches the active desktop — expected
// and authorized. Everything from "Create" onward runs inside try/finally so the created
// desktop and the guinea-pig process are torn down even if a check throws partway through.
//
// CORRECTION vs brief's draft: this can't be a top-level-statements Program.cs.
// VirtualDesktop.Configure() internally creates a WPF HwndSource (to listen for explorer.exe
// restarts) and that throws InvalidOperationException("The calling thread must be STA")
// unless the entry thread is STA. Top-level statements cannot carry a [STAThread] attribute,
// so this had to become an explicit class with a [STAThread] Main.
//
// SECOND CORRECTION, non-obvious: [STAThread] is silently ignored by the CLR when Main is
// declared `async Task`/`async Task<int>` — verified independently with a throwaway repro
// (Thread.CurrentThread.GetApartmentState() reports MTA even with the attribute present).
// Main must stay synchronous; the async body is moved to RunAsync() and blocked on here.
internal static class Program
{
    [STAThread]
    static int Main() => RunAsync().GetAwaiter().GetResult();

    static async Task<int> RunAsync()
    {
        Console.WriteLine($"OS build: {Environment.OSVersion.Version}");
        Console.WriteLine($"Thread apartment state: {Thread.CurrentThread.GetApartmentState()}");

        // CORRECTION vs brief's draft: the package XML doc states Configure() "should always
        // be called first". The brief's original snippet skipped this call entirely.
        try
        {
            VirtualDesktop.Configure();
            Console.WriteLine("0. Configure(): OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"0. Configure(): FAIL - {ex}");
            return 1;
        }

        try
        {
            Console.WriteLine($"1. IsSupported: {VirtualDesktop.IsSupported}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"1. IsSupported: FAIL - {ex}");
            return 1;
        }

        VirtualDesktop[] desktops;
        try
        {
            desktops = VirtualDesktop.GetDesktops();
            Console.WriteLine($"2. Enumerate: OK - {desktops.Length} desktop(s): {string.Join(", ", desktops.Select(d => $"'{d.Name}'"))}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"2. Enumerate: FAIL - {ex}");
            return 1;
        }

        var original = VirtualDesktop.Current;
        VirtualDesktop? created = null;
        Process? winver = null;

        try
        {
            try
            {
                created = VirtualDesktop.Create();
                created.Name = "TaskSpaces spike";
                Console.WriteLine($"3. Create+rename: OK - {created.Id} '{created.Name}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"3. Create+rename: FAIL - {ex}");
                return 1;
            }

            try
            {
                // Switch away and back — visually confirms the taskbar swap that the whole product rides on.
                created.Switch();
                await Task.Delay(1500);
                var switched = VirtualDesktop.Current.Id == created.Id;
                Console.WriteLine($"4. Switch: {(switched ? "OK" : "FAIL")} - current == created ? {switched}");
                original.Switch();
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"4. Switch: FAIL - {ex}");
            }

            try
            {
                // Guinea-pig window: winver is a classic same-process dialog, so MainWindowHandle is
                // reliable (Win11 notepad hands off to a packaged process and would lie to us here).
                winver = Process.Start("winver.exe");
                var waited = 0;
                while (winver.MainWindowHandle == 0 && waited < 5000)
                {
                    await Task.Delay(100);
                    winver.Refresh();
                    waited += 100;
                }

                var hwnd = winver.MainWindowHandle;
                if (hwnd == 0)
                {
                    Console.WriteLine("5. Move window: FAIL - winver never produced a MainWindowHandle");
                }
                else
                {
                    VirtualDesktop.MoveToDesktop(hwnd, created);
                    var found = VirtualDesktop.FromHwnd(hwnd);
                    var moved = found?.Id == created.Id;
                    Console.WriteLine($"5. Move window: {(moved ? "OK" : "FAIL")} - on created desktop ? {moved}");
                }

                var roundtrip = VirtualDesktop.FromId(created.Id)?.Id == created.Id;
                Console.WriteLine($"6. FromId roundtrip: {(roundtrip ? "OK" : "FAIL")} - {roundtrip}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"5/6. Move/FromId: FAIL - {ex}");
            }

            try
            {
                // Event check — Task 4 exposes this as an RX observable.
                VirtualDesktop.CurrentChanged += (_, e) => Console.WriteLine($"7. CurrentChanged fired: OK - -> {e.NewDesktop.Id}");
                created.Switch();
                await Task.Delay(1500);
                original.Switch();
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"7. CurrentChanged: FAIL - {ex}");
            }

            Console.WriteLine("SPIKE RUN COMPLETE - see numbered OK/FAIL lines above for the verdict.");
            return 0;
        }
        finally
        {
            // Cleanup must happen even if a check above threw or returned early.
            if (winver is { HasExited: false })
            {
                try
                {
                    winver.Kill();
                    Console.WriteLine("Cleanup: killed winver.exe");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Cleanup: FAILED to kill winver.exe - {ex.Message}");
                }
            }

            if (created is not null)
            {
                try
                {
                    // Explicit fallback desktop makes this deterministic regardless of which
                    // desktop happens to be current when cleanup runs.
                    created.Remove(original);
                    Console.WriteLine("8. Removed spike desktop - check Task View that no stray desktop remains.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"8. FAILED to remove spike desktop '{created.Id}' - {ex}");
                    Console.WriteLine("ACTION REQUIRED: remove this virtual desktop manually via Task View.");
                }
            }
        }
    }
}
