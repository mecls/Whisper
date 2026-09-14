namespace Spit.Core;

/// Port of mac/Voice/Audio/RingBuffer.swift.
///
/// Fixed-capacity sample buffer written from the audio thread, drained from another thread. Capacity
/// is the 90 s cap: when full, writes are dropped and the recorder stops.
///
/// Thread-safe by one lock: every read and write of `storage` and `head` happens inside it, and
/// neither is handed out — readers copy into a fresh array before returning. `RingBufferTests`
/// exercises that claim from several threads at once.
public sealed class RingBuffer
{
    private readonly float[] storage;
    private readonly Lock gate = new();
    private int head;

    public RingBuffer(int capacity) => storage = new float[capacity];

    public int Count { get { lock (gate) return head; } }
    public bool IsFull { get { lock (gate) return head >= storage.Length; } }

    /// Called on the audio thread, which is why the body allocates nothing.
    public void Write(ReadOnlySpan<float> xs)
    {
        lock (gate)
        {
            var n = Math.Min(xs.Length, storage.Length - head);
            if (n <= 0) return;
            xs[..n].CopyTo(storage.AsSpan(head));
            head += n;
        }
    }

    /// Everything written so far, without consuming it. Streaming reads the buffer repeatedly while the
    /// dictation is still running, which `Drain` cannot do.
    public float[] Snapshot()
    {
        lock (gate) return storage[..head];
    }

    public float[] Drain()
    {
        lock (gate)
        {
            var output = storage[..head];
            head = 0;
            return output;
        }
    }
}
