using System.IO;

namespace Spit.App.Tests;

/// A directory under the temp folder that the test deletes afterwards; it doesn't exist until written to.
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "spit-app-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
