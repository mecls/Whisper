using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Spit.App;

/// Opening, reading and writing the Win32 clipboard. `OpenClipboard` fails while another window holds
/// it, so every open retries every 10 ms for up to 200 ms, and always with Spit's own window: a NULL
/// owner makes `SetClipboardData` fail after `EmptyClipboard` (rule 33).
///
/// The work runs synchronously between open and close. An open clipboard belongs to the thread that
/// opened it, and an `await` in between could close it from another thread.
internal static class ClipboardSession
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(10);
    public static readonly TimeSpan OpenTimeout = TimeSpan.FromMilliseconds(200);

    /// Content carrying this format stays out of Clipboard History and cloud clipboard: the Windows form
    /// of the Mac's `.currentHostOnly` plus `TransientType` / `ConcealedType` (rule 25).
    public const string ExcludeFromMonitorFormatName = "ExcludeClipboardContentFromMonitorProcessing";

    /// Opens the clipboard, runs `work`, closes it. False when it stayed busy past the timeout (logged)
    /// or when `work` reports failure.
    public static bool Run(nint owner, string purpose, Func<bool> work)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            if (TryOpen(owner, out var error)) return RunOpen(work);
            if (Stopwatch.GetElapsedTime(started) >= OpenTimeout) return Busy(purpose, error);
            Thread.Sleep(RetryInterval);
        }
    }

    /// `Run` without blocking the caller's thread between attempts.
    public static async Task<bool> RunAsync(nint owner, string purpose, Func<bool> work)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            if (TryOpen(owner, out var error)) return RunOpen(work);
            if (Stopwatch.GetElapsedTime(started) >= OpenTimeout) return Busy(purpose, error);
            await Task.Delay(RetryInterval);
        }
    }

    public static bool Empty()
    {
        if (Native.EmptyClipboard()) return true;
        Log.Win32Failure("clipboard", "EmptyClipboard", Marshal.GetLastPInvokeError());
        return false;
    }

    /// 0 on failure (logged).
    public static uint Register(string name)
    {
        var format = Native.RegisterClipboardFormat(name);
        if (format == 0) Log.Win32Failure("clipboard", "RegisterClipboardFormat", Marshal.GetLastPInvokeError());
        return format;
    }

    /// The exclusion format needs a non-empty payload to be honoured; a DWORD 0 is Microsoft's own.
    public static bool MarkExcludedFromHistory()
    {
        var format = Register(ExcludeFromMonitorFormatName);
        return format != 0 && SetData(format, [0, 0, 0, 0]);
    }

    /// Empties the open clipboard and puts `text` on it as `CF_UNICODETEXT`, excluded from history. Placed
    /// directly rather than delay-rendered (rule 32).
    public static bool PlaceText(string text)
    {
        if (!Empty() || !MarkExcludedFromHistory()) return false;
        var bytes = new byte[(text.Length + 1) * sizeof(char)];
        MemoryMarshal.AsBytes(text.AsSpan()).CopyTo(bytes);
        return SetData(Native.CF_UNICODETEXT, bytes);
    }

    /// Copies `bytes` into a movable global block and hands it to the open clipboard, which owns it from
    /// then on. On failure the block is freed here.
    public static unsafe bool SetData(uint format, ReadOnlySpan<byte> bytes)
    {
        var size = Math.Max(bytes.Length, 1);
        var block = Native.GlobalAlloc(Native.GMEM_MOVEABLE | Native.GMEM_ZEROINIT, (nuint)size);
        if (block == 0)
        {
            Log.Win32Failure("clipboard", "GlobalAlloc", Marshal.GetLastPInvokeError());
            return false;
        }

        var target = Native.GlobalLock(block);
        if (target == 0)
        {
            Log.Win32Failure("clipboard", "GlobalLock", Marshal.GetLastPInvokeError());
            Free(block);
            return false;
        }
        bytes.CopyTo(new Span<byte>((void*)target, size));
        Unlock(block);

        if (Native.SetClipboardData(format, block) != 0) return true;
        Log.Win32Failure("clipboard", $"SetClipboardData({format})", Marshal.GetLastPInvokeError());
        Free(block);
        return false;
    }

    /// The bytes of `format` on the open clipboard; null when absent, unreadable or over `maxBytes`
    /// (logged with the format id and size only).
    public static unsafe byte[]? ReadData(uint format, int maxBytes)
    {
        var block = Native.GetClipboardData(format);
        if (block == 0)
        {
            Log.Win32Failure("clipboard", $"GetClipboardData({format})", Marshal.GetLastPInvokeError());
            return null;
        }

        var size = Native.GlobalSize(block);
        if (size == 0)
        {
            Log.Win32Failure("clipboard", $"GlobalSize({format})", Marshal.GetLastPInvokeError());
            return null;
        }
        if (size > (nuint)maxBytes)
        {
            Log.Info("clipboard", $"format {format} left out of the snapshot: {size} bytes");
            return null;
        }

        var source = Native.GlobalLock(block);
        if (source == 0)
        {
            Log.Win32Failure("clipboard", $"GlobalLock({format})", Marshal.GetLastPInvokeError());
            return null;
        }
        try
        {
            return new ReadOnlySpan<byte>((void*)source, (int)size).ToArray();
        }
        finally
        {
            Unlock(block);
        }
    }

    private static bool TryOpen(nint owner, out int error)
    {
        if (Native.OpenClipboard(owner))
        {
            error = 0;
            return true;
        }
        error = Marshal.GetLastPInvokeError();
        return false;
    }

    private static bool RunOpen(Func<bool> work)
    {
        try
        {
            return work();
        }
        finally
        {
            if (!Native.CloseClipboard()) Log.Win32Failure("clipboard", "CloseClipboard", Marshal.GetLastPInvokeError());
        }
    }

    private static bool Busy(string purpose, int error)
    {
        Log.Win32Failure("clipboard", $"OpenClipboard for {purpose} (busy for {OpenTimeout.TotalMilliseconds} ms)", error);
        return false;
    }

    private static void Unlock(nint block)
    {
        if (Native.GlobalUnlock(block)) return;
        var error = Marshal.GetLastPInvokeError();
        if (error != 0) Log.Win32Failure("clipboard", "GlobalUnlock", error);
    }

    private static void Free(nint block)
    {
        if (Native.GlobalFree(block) != 0) Log.Win32Failure("clipboard", "GlobalFree", Marshal.GetLastPInvokeError());
    }
}
