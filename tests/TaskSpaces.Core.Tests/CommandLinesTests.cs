using TaskSpaces.Core.Rehydration;

namespace TaskSpaces.Core.Tests;

public class CommandLinesTests
{
    [Theory]
    [InlineData("\"C:\\Tools\\app.exe\" --flag value", @"C:\Tools\app.exe", "--flag value")]
    [InlineData(@"C:\Tools\app.exe --flag", @"C:\Tools\app.exe", "--flag")]
    [InlineData("\"C:\\Tools\\app.exe\"", @"C:\Tools\app.exe", "")]
    [InlineData(null, @"C:\Tools\app.exe", "")]
    [InlineData("", @"C:\Tools\app.exe", "")]
    [InlineData(@"D:\other\thing.exe --x", @"C:\Tools\app.exe", "")] // unknown exe prefix -> bare
    public void Extracts_arguments(string? commandLine, string path, string expected) =>
        Assert.Equal(expected, CommandLines.ArgumentsOf(commandLine, path));
}
