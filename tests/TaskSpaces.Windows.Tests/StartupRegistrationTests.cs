using Microsoft.Win32;
using TaskSpaces.App;

namespace TaskSpaces.Windows.Tests;

// Petre: "taskspaces didn't start with windows".
//
// The Run key pointed at tests\TaskSpaces.Windows.Tests\bin\...\testhost.exe. ManageWindow sets its
// "Start with Windows" box from the registry when it is built, that raises Checked, and the handler
// calls Enable(), which writes Environment.ProcessPath: under a test run, the test host. Three test
// classes build a ManageWindow, so every run of the suite repointed his real login entry. It went
// unnoticed since August because the next app start re-asserts the value; it bit when the machine
// rebooted after a test run and before the app was started again. Measured: running only
// ManageSettingsTabTests turned the value from TaskSpaces.App.exe into testhost.exe.
//
// So only the app itself may write or remove the entry. These tests run under exactly the host that
// broke it, which is what makes them the regression test.
public class StartupRegistrationTests
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static object? RunValue() => Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("TaskSpaces");

    [Theory]
    [InlineData("TaskSpaces.App", true)]
    [InlineData("testhost", false)]
    [InlineData("ReSharperTestRunner", false)]
    [InlineData(null, false)]
    public void Only_the_app_itself_owns_the_entry(string? entryAssembly, bool expected) =>
        Assert.Equal(expected, StartupRegistration.IsTheApp(entryAssembly));

    [Fact]
    public void Enabling_from_a_test_host_refuses_and_leaves_the_entry_alone()
    {
        var before = RunValue();

        Assert.True(StartupRegistration.Enable().IsFailure);

        Assert.Equal(before, RunValue());
    }

    // The other direction would be worse than the bug: a test that unticks the box would delete his
    // real registration outright.
    [Fact]
    public void Disabling_from_a_test_host_refuses_and_leaves_the_entry_alone()
    {
        var before = RunValue();

        Assert.True(StartupRegistration.Disable().IsFailure);

        Assert.Equal(before, RunValue());
    }
}
