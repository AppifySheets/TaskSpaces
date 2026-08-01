using CSharpFunctionalExtensions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Abstractions;

/// <summary>
/// Abstraction for reading/writing window titles. Isolates Win32 plumbing
/// from business logic so WorkspaceManager can be tested with fakes.
/// </summary>
public interface IWindowTitles
{
    /// <summary>
    /// Set a window's title, with timeout protection against hung windows.
    /// Returns failure if the window is closed or unresponsive.
    /// </summary>
    Result Set(WindowHandle window, string title);

    /// <summary>
    /// Get a window's current title.
    /// </summary>
    Result<string> Get(WindowHandle window);
}
