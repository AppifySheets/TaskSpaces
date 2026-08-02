using System.Diagnostics;
using TaskSpaces.Core.Domain;
using TaskSpaces.Windows.Activation;
using Xunit.Abstractions;

namespace TaskSpaces.Windows.Tests;

[Trait("Category", "Integration")]
public class WindowActivatorTests(ITestOutputHelper output)
{
    [Fact]
    public void Activates_a_real_window()
    {
        var winver = Process.Start("winver.exe");
        try
        {
            while (winver.MainWindowHandle == 0) { Thread.Sleep(100); winver.Refresh(); }
            var result = new WindowActivator().Activate(new WindowHandle(winver.MainWindowHandle));
            output.WriteLine($"activate: {result.IsSuccess}");
            Assert.True(result.IsSuccess);
        }
        finally { if (!winver.HasExited) winver.Kill(); }
    }
}
