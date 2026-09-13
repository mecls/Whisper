namespace Spit.Core.Tests;

/// Port of mac/VoiceTests/OutboxTests.swift.
public sealed class OutboxTests
{
    [Fact]
    public void testCapsAt200KeepingNewest()
    {
        var o = new Outbox();
        for (var i = 0; i < 250; i++)
            o.Add(new OutboxEntry(Guid.NewGuid(), $"{i}", Injected.Raw, FallbackReason.Offline, DateTimeOffset.UtcNow, "clean", "auto",
                null, null, 0, 0));
        Assert.Equal(200, o.Count);
        Assert.Equal("50", o.Drain().FirstOrDefault()?.Raw);
        Assert.Equal(0, o.Count);
    }
}
