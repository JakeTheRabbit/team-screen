using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Rect32 = System.Drawing.Rectangle;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace TeamScreen;

public static class Placement
{
    /// <summary>
    /// Put a borderless window exactly over a monitor (physical px). Moving the handle there before
    /// showing lets WPF settle that monitor's DPI; maximising then fills it with no rounding gaps.
    /// </summary>
    public static void FillMonitor(Window w, Rect32 bounds)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        if (w.WindowState == WindowState.Maximized) w.WindowState = WindowState.Normal;
        var h = new WindowInteropHelper(w).EnsureHandle();
        Native.SetWindowPos(h, IntPtr.Zero, bounds.X + 50, bounds.Y + 50, 400, 300, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        if (!w.IsVisible) w.Show();
        w.WindowState = WindowState.Maximized;
    }

    /// <summary>Exact physical-pixel rectangle; applied twice because the first move can trigger a DPI rescale.</summary>
    public static void SetBounds(Window w, Rect32 r, bool topmost)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        for (var i = 0; i < 2; i++)
            Native.SetWindowPos(h, topmost ? Native.HWND_TOPMOST : IntPtr.Zero, r.X, r.Y, r.Width, r.Height,
                Native.SWP_NOACTIVATE | (topmost ? 0 : Native.SWP_NOZORDER));
    }
}

/// <summary>Snipping-style picker: dims every screen, drag a box, or click a screen to take all of it.</summary>
public static class AreaPicker
{
    static List<PickerWindow>? open;

    public static bool IsOpen => open != null;

    public static void Pick(IEnumerable<MonitorInfo> monitors, Action<Rect32> picked)
    {
        if (open != null) return;
        Native.POINT? start = null;
        var selection = Rect32.Empty;

        void Close()
        {
            foreach (var w in open!) w.Close();
            open = null;
        }

        open = monitors.GroupBy(m => m.Bounds).Select(g => new PickerWindow(g.Key)).ToList();
        foreach (var w in open)
        {
            w.MouseLeftButtonDown += (_, _) =>
            {
                Native.GetCursorPos(out var p);
                start = p;
                w.CaptureMouse();
            };
            w.MouseMove += (_, _) =>
            {
                if (start is not { } s) return;
                Native.GetCursorPos(out var p);
                selection = Rect32.FromLTRB(Math.Min(s.X, p.X), Math.Min(s.Y, p.Y), Math.Max(s.X, p.X), Math.Max(s.Y, p.Y));
                foreach (var o in open!) o.ShowSelection(selection);
            };
            w.MouseLeftButtonUp += (_, _) =>
            {
                if (start == null) return;
                w.ReleaseMouseCapture();
                var result = selection.Width > 8 && selection.Height > 8 ? selection : w.Monitor;   // plain click = whole screen
                Close();
                picked(result);
            };
            w.MouseRightButtonUp += (_, _) => Close();
            w.KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        }
        foreach (var w in open) Placement.FillMonitor(w, w.Monitor);

        Native.GetCursorPos(out var cur);
        var under = open.FirstOrDefault(w => w.Monitor.Contains(cur.X, cur.Y)) ?? open[0];
        under.Activate();
        under.Focus();
    }

    sealed class PickerWindow : Window
    {
        public Rect32 Monitor { get; }
        readonly Canvas canvas = new();
        readonly WpfRectangle box = new() { Stroke = new SolidColorBrush(Color.FromRgb(232, 17, 35)), StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), Visibility = Visibility.Collapsed };
        readonly TextBlock size = new() { Foreground = Brushes.White, FontSize = 13, Padding = new Thickness(6, 2, 6, 2), Background = new SolidColorBrush(Color.FromArgb(200, 32, 32, 32)), Visibility = Visibility.Collapsed };

        public PickerWindow(Rect32 monitor)
        {
            Monitor = monitor;
            ThemeMode = ThemeMode.None;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0));
            Topmost = true;
            ShowInTaskbar = false;
            Cursor = Cursors.Cross;
            Title = "Choose what to mirror";

            var hint = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(230, 32, 32, 32)), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 10, 16, 10), HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 40, 0, 0), IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Foreground = Brushes.White, FontSize = 15, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                    Text = "Drag to choose what the team screen shows   ·   Click to show this whole screen   ·   Esc to cancel",
                },
            };
            canvas.Children.Add(box);
            canvas.Children.Add(size);
            Content = new Grid { Children = { canvas, hint } };
        }

        public void ShowSelection(Rect32 sel)
        {
            var part = Rect32.Intersect(sel, Monitor);
            if (part.IsEmpty) { box.Visibility = size.Visibility = Visibility.Collapsed; return; }
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Canvas.SetLeft(box, (part.X - Monitor.X) / scale);
            Canvas.SetTop(box, (part.Y - Monitor.Y) / scale);
            box.Width = part.Width / scale;
            box.Height = part.Height / scale;
            size.Text = $"{sel.Width} × {sel.Height}";
            Canvas.SetLeft(size, (part.X - Monitor.X) / scale);
            Canvas.SetTop(size, Math.Max(0, (part.Y - Monitor.Y) / scale - 26));
            box.Visibility = size.Visibility = Visibility.Visible;
        }
    }
}

/// <summary>Click-through outline around the area being mirrored, hidden from screen capture so it never shows up on the team screen.</summary>
public sealed class AreaOutline
{
    Window? window;
    Border? border;

    public void Show(Rect32 area, Color color)
    {
        if (window == null)
        {
            border = new Border { BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(4) };
            window = new Window
            {
                ThemeMode = ThemeMode.None, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true, Content = border,
                Title = "Team Screen mirrored area",
            };
            window.SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(window).Handle;
                Native.AddExStyle(h, Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
                Native.SetWindowDisplayAffinity(h, Native.WDA_EXCLUDEFROMCAPTURE);
            };
        }
        border!.BorderBrush = new SolidColorBrush(color);
        var outer = Rect32.Inflate(area, 5, 5);
        Placement.SetBounds(window, outer, topmost: true);
        window.Show();
        Placement.SetBounds(window, outer, topmost: true);
    }

    public void SetColor(Color color) { if (border != null) border.BorderBrush = new SolidColorBrush(color); }

    public void Hide() => window?.Hide();
}

/// <summary>Flashes a big label on a monitor so you can tell which one it is.</summary>
public static class IdentifyFlash
{
    public static void Show(MonitorInfo m)
    {
        var w = new Window
        {
            ThemeMode = ThemeMode.None, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            Topmost = true, ShowActivated = false, Background = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
            Content = new TextBlock
            {
                Text = $"This is {m.Name}", Foreground = Brushes.White, FontSize = 72, FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Placement.FillMonitor(w, m.Bounds);
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { t.Stop(); w.Close(); };
        t.Start();
    }
}
