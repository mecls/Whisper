using System.Runtime.CompilerServices;

namespace Spit.App.Tests;

/// A fact that runs only on Windows. xunit v3 has no class-level skip, so every test in this project
/// uses this attribute instead of `[Fact]`: on the Mac they compile and report as skipped.
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Needs Windows: drives the real clipboard, Credential Manager, registry or process tokens";
        SkipType = typeof(WindowsFactAttribute);
        SkipUnless = nameof(IsWindows);
    }

    public static bool IsWindows => OperatingSystem.IsWindows();
}
