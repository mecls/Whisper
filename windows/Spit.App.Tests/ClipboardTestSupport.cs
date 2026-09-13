using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Spit.App.Tests;

internal static class ClipboardTestSupport
{
    private static readonly nint MessageOnlyParent = new(-3);   // HWND_MESSAGE

    /// Runs `body` on a fresh STA thread that owns a message-only window, passing its handle: every
    /// clipboard open needs a real owner (rule 33), and the thread that opens the clipboard should own it.
    public static void WithOwnerWindow(Action<nint> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var source = new HwndSource(new HwndSourceParameters("SpitTestClipboardOwner") { ParentWindow = MessageOnlyParent, WindowStyle = 0 });
                body(source.Handle);
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
    }

    public static byte[] Utf16(string text)
    {
        var bytes = new byte[(text.Length + 1) * sizeof(char)];
        MemoryMarshal.AsBytes(text.AsSpan()).CopyTo(bytes);
        return bytes;
    }

    /// `CF_UNICODETEXT` bytes up to the terminating NUL.
    public static string TextOf(byte[] bytes)
    {
        var chars = MemoryMarshal.Cast<byte, char>(bytes.AsSpan(0, bytes.Length / sizeof(char) * sizeof(char)));
        var end = chars.IndexOf('\0');
        return new string(end < 0 ? chars : chars[..end]);
    }

    /// A 1×1 32-bit `CF_DIB`: a BITMAPINFOHEADER followed by one BGRA pixel.
    public static byte[] OnePixelDib()
    {
        var dib = new byte[44];
        BitConverter.TryWriteBytes(dib.AsSpan(0), 40);        // biSize
        BitConverter.TryWriteBytes(dib.AsSpan(4), 1);         // biWidth
        BitConverter.TryWriteBytes(dib.AsSpan(8), 1);         // biHeight
        BitConverter.TryWriteBytes(dib.AsSpan(12), (short)1); // biPlanes
        BitConverter.TryWriteBytes(dib.AsSpan(14), (short)32); // biBitCount
        BitConverter.TryWriteBytes(dib.AsSpan(20), 4);        // biSizeImage
        dib[40] = 0x20; dib[41] = 0x40; dib[42] = 0x80; dib[43] = 0xFF;
        return dib;
    }
}
