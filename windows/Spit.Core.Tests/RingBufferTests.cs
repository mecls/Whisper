namespace Spit.Core.Tests;

// Port of mac/VoiceTests/RingBufferTests.swift.
public sealed class RingBufferTests
{
    [Fact]
    public void testWriteDrainPreservesOrder()
    {
        var rb = new RingBuffer(8);
        rb.Write([1, 2, 3]);
        rb.Write([4, 5]);
        Assert.Equal(5, rb.Count);
        Assert.Equal([1f, 2f, 3f, 4f, 5f], rb.Drain());
        Assert.Equal(0, rb.Count);
    }

    [Fact]
    public void testStopsAtCapacityInsteadOfOverwriting()
    {
        var rb = new RingBuffer(4);
        rb.Write([1, 2, 3, 4, 5, 6]);
        Assert.True(rb.IsFull);
        Assert.Equal([1f, 2f, 3f, 4f], rb.Drain());
    }

    /// Exercises the thread-safety claim instead of taking its word for it.
    ///
    /// Storage starts as zeroes and every writer writes 1. `Snapshot()` returns `storage[..head]`, and
    /// `head` only advances after the copy completes under the lock, so every sample a reader sees must
    /// already be 1. A zero means a reader observed `head` ahead of the data it points at.
    [Fact]
    public void testConcurrentWritesAreNeverVisibleBeforeTheyLand()
    {
        var frame = Enumerable.Repeat(1f, 160).ToArray();   // 10 ms at 16 kHz
        const int writes = 400;
        var rb = new RingBuffer(frame.Length * writes);

        // Every fifth iteration reads while the rest are mid-write.
        Parallel.For(0, writes + writes / 4, i =>
        {
            if (i % 5 == 0)
            {
                var seen = rb.Snapshot();
                Assert.True(Array.TrueForAll(seen, x => x == 1),
                    "Snapshot() exposed a sample the writer had not finished storing — head moved ahead of the data");
                Assert.True(seen.Length <= frame.Length * writes);
            }
            else
            {
                rb.Write(frame);
            }
        });

        // The writers are sized to fill it exactly, so a lost or double-counted update shows up here.
        Assert.True(rb.IsFull);
        Assert.Equal(frame.Length * writes, rb.Count);
        Assert.True(Array.TrueForAll(rb.Drain(), x => x == 1));
    }
}
