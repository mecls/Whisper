namespace Spit.App.Tests;

/// Clipboard tests never run in parallel. The clipboard is one system-wide object, and each test owns it
/// from its own thread: when one thread's `EmptyClipboard` takes ownership away from another test's window,
/// Windows *sends* that window `WM_DESTROYCLIPBOARD` and waits for its thread to answer — a thread that is
/// busy in its own test body, not pumping messages. The first thread then sits inside `EmptyClipboard`
/// holding the clipboard open, and the second can't open it. A CI run failed exactly this way ("held by
/// Spit.App.Tests").
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ClipboardCollection
{
    public const string Name = "Clipboard";
}
