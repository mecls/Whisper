using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Spit.Core;

namespace Spit.App;

/// Draws the four tray icons at runtime (rule 37): mic, filled mic, red record, mic with a cross.
///
/// Drawn as vector geometry rather than font glyphs: Segoe Fluent Icons exists only on Windows 11 and
/// the MDL2 set has no filled or crossed microphone, while a geometry renders identically everywhere.
/// Rendered at several pixel sizes into one PNG-framed .ico, so Windows picks a crisp frame for the
/// taskbar's DPI instead of scaling one.
internal static class TrayIconRenderer
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48];

    public static System.Drawing.Icon Render(MenuIconState state, bool lightTaskbar)
    {
        var ink = lightTaskbar ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White;
        var frames = Sizes.Select(size => (size, png: RenderPng(state, ink, size))).ToArray();
        var ico = BuildIco(frames);
        var wanted = Math.Clamp(UiNative.GetSystemMetrics(UiNative.SM_CXSMICON), 16, 48);
        using var stream = new MemoryStream(ico);
        return new System.Drawing.Icon(stream, wanted, wanted);
    }

    /// The fallback when rendering fails: the app icon, from the assembly's resources.
    public static System.Drawing.Icon? AppIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Spit;component/Assets/Spit.ico", UriKind.Absolute));
            if (info is null) return null;
            using var s = info.Stream;
            return new System.Drawing.Icon(s);
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException)
        {
            Log.Failure("tray", "load Spit.ico", e);
            return null;
        }
    }

    private static byte[] RenderPng(MenuIconState state, Color ink, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Design grid: 24 × 24 units.
            dc.PushTransform(new ScaleTransform(size / 24.0, size / 24.0));
            Draw(dc, state, ink);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void Draw(DrawingContext dc, MenuIconState state, Color ink)
    {
        var inkBrush = new SolidColorBrush(ink);
        var pen = new Pen(inkBrush, 1.9) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        if (state == MenuIconState.Recording)
        {
            var red = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));
            dc.DrawEllipse(null, new Pen(red, 2), new Point(12, 12), 9.5, 9.5);
            dc.DrawEllipse(red, null, new Point(12, 12), 6, 6);
            return;
        }

        var capsule = new RectangleGeometry(new Rect(8.5, 2, 7, 12.5), 3.5, 3.5);
        if (state == MenuIconState.MicFilled) dc.DrawGeometry(inkBrush, pen, capsule);
        else dc.DrawGeometry(null, pen, capsule);

        // The stand: an arc under the capsule, a stem and a foot.
        var stand = Geometry.Parse("M 5.5,10.5 A 6.5,6.5 0 0 0 18.5,10.5");
        dc.DrawGeometry(null, pen, stand);
        dc.DrawLine(pen, new Point(12, 17), new Point(12, 21));
        dc.DrawLine(pen, new Point(8.5, 21), new Point(15.5, 21));

        if (state == MenuIconState.MicCrossed)
        {
            dc.DrawLine(new Pen(inkBrush, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                new Point(3.5, 3), new Point(20.5, 21.5));
        }
    }

    /// An .ico whose frames are PNG streams (supported since Windows Vista).
    private static byte[] BuildIco((int size, byte[] png)[] frames)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write((ushort)0);             // reserved
        w.Write((ushort)1);             // type: icon
        w.Write((ushort)frames.Length);
        var offset = 6 + 16 * frames.Length;
        foreach (var (size, png) in frames)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);           // palette colours
            w.Write((byte)0);           // reserved
            w.Write((ushort)1);         // planes
            w.Write((ushort)32);        // bits per pixel
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in frames) w.Write(png);
        w.Flush();
        return stream.ToArray();
    }
}
