using System.IO;
using TaskSpaces.App;

namespace TaskSpaces.Windows.Tests;

// The batch file the running app leaves at %APPDATA%\TaskSpaces\taskspaces.cmd so other programs have
// one fixed path to call, whatever the exe is named this version. Measured before it was written:
// PowerShell does not wait for a GUI exe called directly (nothing captured, $LASTEXITCODE empty), and a
// batch file does, with output and exit code intact. See ControlCommandLine.WriteShim.
public class ControlShimTests
{
    [Fact]
    public void The_shim_runs_the_exe_with_every_argument_and_hands_back_its_exit_code()
    {
        var content = ControlCommandLine.ShimContent(@"C:\Tools\TaskSpaces-1.17.0-win-x64.exe");

        Assert.Contains("\"C:\\Tools\\TaskSpaces-1.17.0-win-x64.exe\" ctl %*", content);
        Assert.Contains("exit /b %ERRORLEVEL%", content);
    }

    // cmd expands %NAME% even inside quotes, so a literal percent in the path has to be doubled.
    [Fact]
    public void A_percent_sign_in_the_exe_path_survives_cmd()
    {
        var content = ControlCommandLine.ShimContent(@"C:\100% real\TaskSpaces.exe");

        Assert.Contains("\"C:\\100%% real\\TaskSpaces.exe\" ctl %*", content);
    }

    [Fact]
    public void Writing_the_shim_creates_it_and_rewrites_it_for_a_new_exe()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"ts-shim-{Guid.NewGuid():N}");
        try
        {
            ControlCommandLine.WriteShim(folder, @"C:\old\TaskSpaces.exe");
            ControlCommandLine.WriteShim(folder, @"C:\new\TaskSpaces.exe");

            var written = File.ReadAllText(Path.Combine(folder, "taskspaces.cmd"));
            Assert.Contains(@"C:\new\TaskSpaces.exe", written);
            Assert.DoesNotContain(@"C:\old\TaskSpaces.exe", written);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
