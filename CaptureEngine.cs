using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Rect32 = System.Drawing.Rectangle;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace TeamScreen;

/// <summary>
/// Streams an area of the desktop or one window into a WriteableBitmap on a background thread.
/// Area mode copies pixels straight off screen, so anything you're logged into shows as-is.
/// </summary>
public sealed class CaptureEngine
{
    public event Action<WriteableBitmap>? NewSurface;   // raised when the frame size changes
    public event Action? SourceClosed;

    public volatile bool Paused;
    public int Fps { get; set; } = 15;
    public bool DrawCursor { get; set; } = true;
    public bool Running => thread != null;

    readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
    Thread? thread;
    int generation;   // bumped on every start/stop; stale frames from an old loop are dropped
    Rect32 area;
    IntPtr window;
    WriteableBitmap? surface;

    public void StartArea(Rect32 r) { area = r; window = IntPtr.Zero; Begin(); }
    public void StartWindow(IntPtr hwnd) { window = hwnd; Begin(); }

    public void Stop()
    {
        Interlocked.Increment(ref generation);   // no Join: the loop may be waiting on the UI thread we're on
        thread = null;
        Paused = false;
        surface = null;
    }

    void Begin()
    {
        Stop();
        var gen = generation;
        thread = new Thread(() => Loop(gen)) { IsBackground = true, Name = "TeamScreen capture" };
        thread.Start();
    }

    void Loop(int gen)
    {
        var clock = new Stopwatch();
        while (gen == generation)
        {
            clock.Restart();
            if (!Paused)
            {
                try
                {
                    using var frame = window != IntPtr.Zero ? GrabWindow() : GrabArea();
                    if (frame != null) Push(frame, gen);
                    else if (window != IntPtr.Zero && !Native.IsWindow(window))
                    {
                        ui.BeginInvoke(() => { if (gen == generation) SourceClosed?.Invoke(); });
                        return;
                    }
                }
                catch { /* transient GDI failure (e.g. secure desktop): try again next tick */ }
            }
            var wait = 1000 / Math.Clamp(Fps, 1, 60) - (int)clock.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep(wait);
        }
    }

    Bitmap GrabArea()
    {
        var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(area.X, area.Y, 0, 0, area.Size);
        if (DrawCursor) PaintCursor(g, area.Location);
        return bmp;
    }

    Bitmap? GrabWindow()
    {
        if (!Native.IsWindow(window) || Native.IsIconic(window)) return null;   // minimised: hold the last frame
        Native.GetClientRect(window, out var rc);
        if (rc.Right < 2 || rc.Bottom < 2) return null;
        var bmp = new Bitmap(rc.Right, rc.Bottom, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bmp);
        var hdc = g.GetHdc();
        Native.PrintWindow(window, hdc, 3);   // PW_CLIENTONLY | PW_RENDERFULLCONTENT: works for GPU apps and covered windows
        g.ReleaseHdc(hdc);
        if (DrawCursor)
        {
            var origin = new Native.POINT();
            Native.ClientToScreen(window, ref origin);
            PaintCursor(g, new System.Drawing.Point(origin.X, origin.Y));
        }
        return bmp;
    }

    static void PaintCursor(Graphics g, System.Drawing.Point origin)
    {
        var ci = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
        if (!Native.GetCursorInfo(ref ci) || (ci.flags & 1) == 0) return;
        if (!Native.GetIconInfo(ci.hCursor, out var ii)) return;
        Native.DeleteObject(ii.hbmMask);
        Native.DeleteObject(ii.hbmColor);
        var hdc = g.GetHdc();
        Native.DrawIconEx(hdc, ci.pt.X - origin.X - ii.xHotspot, ci.pt.Y - origin.Y - ii.yHotspot, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3);
        g.ReleaseHdc(hdc);
    }

    void Push(Bitmap frame, int gen)
    {
        var data = frame.LockBits(new Rect32(0, 0, frame.Width, frame.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            ui.Invoke(() =>
            {
                if (gen != generation) return;
                if (surface == null || surface.PixelWidth != frame.Width || surface.PixelHeight != frame.Height)
                {
                    surface = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
                    NewSurface?.Invoke(surface);
                }
                surface.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), data.Scan0, data.Stride * frame.Height, data.Stride);
            });
        }
        finally { frame.UnlockBits(data); }
    }

    /// <summary>One-off still of a screen rectangle (freeze, preview).</summary>
    public static BitmapSource GrabScreen(Rect32 r)
    {
        using var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.X, r.Y, 0, 0, r.Size);
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
