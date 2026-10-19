using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace TeamScreen;

/// <summary>Draws the app's monitor glyph, tinted for the taskbar theme, with a status dot.</summary>
public static class IconFactory
{
    public static Color StatusColor(ScreenMode mode, bool frozen, bool blanked) =>
        blanked ? Color.FromArgb(120, 120, 120)
        : frozen ? Color.FromArgb(0, 120, 212)
        : mode switch
        {
            ScreenMode.Off => Color.Empty,
            ScreenMode.MirrorArea or ScreenMode.MirrorWindow => Color.FromArgb(232, 17, 35),   // live: red like a recording dot
            _ => Color.FromArgb(16, 137, 62),
        };

    static bool TaskbarIsLight()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    static Bitmap Draw(int size, Color dot, Color ink)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = size / 32f;
        using var pen = new Pen(ink, 2.4f * s) { LineJoin = LineJoin.Round };
        using var path = Rounded(new RectangleF(3 * s, 5 * s, 26 * s, 17 * s), 3 * s);
        g.DrawPath(pen, path);
        g.DrawLine(pen, 16 * s, 22 * s, 16 * s, 27 * s);
        g.DrawLine(pen, 10 * s, 27.5f * s, 22 * s, 27.5f * s);
        if (dot != Color.Empty)
        {
            using var b = new SolidBrush(dot);
            using var ring = new Pen(TaskbarIsLight() ? Color.White : Color.FromArgb(32, 32, 32), 2 * s);
            var r = new RectangleF(19 * s, 1 * s, 12 * s, 12 * s);
            g.FillEllipse(b, r);
            g.DrawEllipse(ring, r);
        }
        return bmp;
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, radius * 2, radius * 2, 180, 90);
        p.AddArc(r.Right - radius * 2, r.Y, radius * 2, radius * 2, 270, 90);
        p.AddArc(r.Right - radius * 2, r.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        p.AddArc(r.X, r.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Tray icon. Caller owns the returned Icon and must destroy its handle via <see cref="Destroy"/>.</summary>
    public static Icon Tray(ScreenMode mode, bool frozen, bool blanked)
    {
        using var bmp = Draw(32, StatusColor(mode, frozen, blanked), TaskbarIsLight() ? Color.FromArgb(28, 28, 28) : Color.White);
        return Icon.FromHandle(bmp.GetHicon());
    }

    public static void Destroy(Icon? icon)
    {
        if (icon == null) return;
        Native.DestroyIcon(icon.Handle);
        icon.Dispose();
    }

    /// <summary>Window title-bar icon.</summary>
    public static BitmapSource WindowIcon()
    {
        using var bmp = Draw(64, Color.FromArgb(16, 137, 62), Color.FromArgb(0, 120, 212));
        var h = bmp.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { Native.DeleteObject(h); }
    }
}
