namespace Spit.Core.Tests;

/// `context.appBundleId` and `appName` fit the server's 200-character cap, so a long FileDescription can never
/// turn a dictation report into a 400 that the outbox replays forever. Not a Mac port.
public sealed class RefineContextTests
{
    [Fact]
    public void testContextFieldsAreCutToTheServersCap()
    {
        var context = RefineContext.For(new FrontmostApp(new string('b', 255), new string('n', 300)));

        Assert.Equal(RefineContext.MaxLength, context.AppBundleId!.Length);
        Assert.Equal(RefineContext.MaxLength, context.AppName!.Length);
        Assert.Equal(new RefineContext("chrome.exe", "Google Chrome"), RefineContext.For(new FrontmostApp("chrome.exe", "Google Chrome")));
        Assert.Equal(new RefineContext(null, null), RefineContext.For(null));
    }

    [Fact]
    public void testASurrogatePairIsNeverSplitAtTheCap()
    {
        var name = new string('x', RefineContext.MaxLength - 1) + "\U0001F600" + "tail";

        Assert.Equal(new string('x', RefineContext.MaxLength - 1), RefineContext.Clamp(name));
    }
}
