using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rect32 = System.Drawing.Rectangle;
using Screen = System.Windows.Forms.Screen;

namespace TeamScreen;

public enum InkTool { Pen, Highlighter, Arrow, Box, Eraser }

/// <summary>
/// One area of your screen being shared. Three windows sit over it:
/// a resize frame and a toolbar (both hidden from capture, so the team never sees them)
/// and a drawing layer (captured, so annotations show on the team screen).
/// </summary>
public sealed class SharedArea : IDisposable
{
    public const int MinSize = 40;
    public static readonly Color LiveColor = Color.FromRgb(232, 17, 35);
    public static readonly Color FrozenColor = Color.FromRgb(0, 120, 212);

    public int Number { get; }
    public Rect32 Area { get; private set; }
    public CaptureEngine Engine { get; } = new();
    public ImageSource Surface { get; private set; } = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgr32, null);
    public bool Drawing { get; private set; }
    public InkTool Tool => ink.Tool;
    public Color InkColor => ink.InkColor;

    public event Action<SharedArea>? SurfaceChanged, StopRequested, Moved, DrawingChanged;

    readonly AreaFrame frame;
    readonly AreaToolbar bar;
    readonly InkLayer ink = new();
    bool controls;

    public SharedArea(int number, Rect32 area, bool showControls, int fps, bool drawCursor)
    {
        Number = number;
        Area = Normalize(area);
        Engine.Fps = fps;
        Engine.DrawCursor = drawCursor;
        Engine.NewSurface += s => { Surface = s; SurfaceChanged?.Invoke(this); };
        frame = new AreaFrame(this);
        bar = new AreaToolbar(this);

        Placement.SetBounds(ink, Area, topmost: true);
        ink.Show();
        Placement.SetBounds(ink, Area, topmost: true);
        ShowControls(showControls);
        Engine.StartArea(Area);
    }

    static Rect32 Normalize(Rect32 r) => new(r.X, r.Y, Math.Max(MinSize, r.Width), Math.Max(MinSize, r.Height));

    /// <summary>Move or resize the shared area; the capture follows immediately.</summary>
    public void SetArea(Rect32 r)
    {
        Area = Normalize(r);
        Engine.MoveArea(Area);
        Placement.SetBounds(ink, Area, topmost: true);
        if (controls) { frame.Place(Area); bar.Place(Area); }
        Moved?.Invoke(this);
    }

    public void ShowControls(bool on)
    {
        controls = on;
        if (on)
        {
            frame.Place(Area);
            if (!frame.IsVisible) frame.Show();
            frame.Place(Area);
            if (!bar.IsVisible) bar.Show();
            bar.Place(Area);
        }
        else { frame.Hide(); bar.Hide(); }
    }

    public void SetDrawing(bool on)
    {
        Drawing = on;
        ink.SetDrawing(on);
        bar.SyncDrawing();
        DrawingChanged?.Invoke(this);
    }

    public void SetTool(InkTool tool) { ink.Tool = tool; ink.ApplyTool(); bar.SyncDrawing(); }
    public void SetInkColor(Color c) { ink.InkColor = c; if (ink.Tool == InkTool.Eraser) ink.Tool = InkTool.Pen; ink.ApplyTool(); bar.SyncDrawing(); }
    public void Undo() => ink.Undo();
    public void ClearDrawing() => ink.Clear();
    public void SetFrozen(bool frozen) => frame.SetColor(frozen ? FrozenColor : LiveColor);
    public void RequestStop() => StopRequested?.Invoke(this);

    public void Dispose()
    {
        Engine.Stop();
        frame.Close();
        bar.Close();
        ink.Close();
    }

    internal static void ExcludeFromCapture(Window w) => w.SourceInitialized += (_, _) =>
    {
        var h = new WindowInteropHelper(w).Handle;
        Native.AddExStyle(h, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        Native.SetWindowDisplayAffinity(h, Native.WDA_EXCLUDEFROMCAPTURE);
    };

    internal static double Scale(Window w)
    {
        var dpi = Native.GetDpiForWindow(new WindowInteropHelper(w).EnsureHandle());
        return dpi == 0 ? 1 : dpi / 96.0;
    }
}

/// <summary>Red border round a shared area. Drag any edge or corner to resize. Its inside is click-through.</summary>
sealed class AreaFrame : Window
{
    const double Grip = 8;   // DIP band you can grab
    readonly SharedArea owner;
    readonly Border line = new() { BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(3), Margin = new Thickness(Grip - 3), IsHitTestVisible = false, BorderBrush = new SolidColorBrush(SharedArea.LiveColor) };
    int edges;   // 1 left, 2 top, 4 right, 8 bottom
    Native.POINT start;
    Rect32 startArea;

    public AreaFrame(SharedArea owner)
    {
        this.owner = owner;
        ThemeMode = ThemeMode.None;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "Team Screen shared area";
        // Near-invisible band so the edges catch the mouse; the transparent middle passes clicks through.
        var band = new Border { BorderThickness = new Thickness(Grip), BorderBrush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
        Content = new Grid { Children = { band, line } };
        SharedArea.ExcludeFromCapture(this);

        MouseLeftButtonDown += (_, e) =>
        {
            edges = EdgesAt(e.GetPosition(this));
            if (edges == 0) return;
            Native.GetCursorPos(out start);
            startArea = owner.Area;
            CaptureMouse();
        };
        MouseMove += (_, e) =>
        {
            if (!IsMouseCaptured) { Cursor = CursorFor(EdgesAt(e.GetPosition(this))); return; }
            Native.GetCursorPos(out var p);
            int dx = p.X - start.X, dy = p.Y - start.Y, min = SharedArea.MinSize;
            int l = startArea.Left, t = startArea.Top, r = startArea.Right, b = startArea.Bottom;
            if ((edges & 1) != 0) l = Math.Min(l + dx, r - min);
            if ((edges & 4) != 0) r = Math.Max(r + dx, l + min);
            if ((edges & 2) != 0) t = Math.Min(t + dy, b - min);
            if ((edges & 8) != 0) b = Math.Max(b + dy, t + min);
            owner.SetArea(Rect32.FromLTRB(l, t, r, b));
        };
        MouseLeftButtonUp += (_, _) => { ReleaseMouseCapture(); edges = 0; };
    }

    int EdgesAt(Point p) =>
        (p.X < Grip ? 1 : 0) | (p.Y < Grip ? 2 : 0) | (p.X > ActualWidth - Grip ? 4 : 0) | (p.Y > ActualHeight - Grip ? 8 : 0);

    static Cursor CursorFor(int e) => e switch
    {
        1 or 4 => Cursors.SizeWE,
        2 or 8 => Cursors.SizeNS,
        3 or 12 => Cursors.SizeNWSE,
        6 or 9 => Cursors.SizeNESW,
        _ => Cursors.Arrow,
    };

    public void Place(Rect32 area)
    {
        var g = (int)Math.Round(Grip * SharedArea.Scale(this));
        Placement.SetBounds(this, Rect32.Inflate(area, g, g), topmost: true);
    }

    public void SetColor(Color c) => line.BorderBrush = new SolidColorBrush(c);
}

/// <summary>Controls for one shared area: move handle, Draw tools, Stop sharing. Hidden from capture.</summary>
sealed class AreaToolbar : Window
{
    readonly SharedArea owner;
    readonly ToggleButton draw;
    readonly StackPanel tools = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    readonly Dictionary<InkTool, ToggleButton> toolButtons = new();
    readonly Dictionary<Color, Border> swatches = new();
    Native.POINT dragStart;
    Rect32 dragArea;

    static readonly (Color Color, string Name)[] Palette =
    [
        (Color.FromRgb(232, 17, 35), "Red"), (Color.FromRgb(255, 212, 0), "Yellow"), (Color.FromRgb(22, 198, 12), "Green"),
        (Color.FromRgb(0, 120, 212), "Blue"), (Colors.White, "White"), (Colors.Black, "Black"),
    ];

    public AreaToolbar(SharedArea owner)
    {
        this.owner = owner;
        ThemeMode = ThemeMode.Dark;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Left = Top = -10000;
        Title = "Team Screen area controls";
        SharedArea.ExcludeFromCapture(this);

        var grip = new TextBlock
        {
            Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 6, 4, 6), Cursor = Cursors.SizeAll,
            ToolTip = "Drag to move this area", Background = Brushes.Transparent,
        };
        grip.MouseLeftButtonDown += (_, e) => { Native.GetCursorPos(out dragStart); dragArea = owner.Area; grip.CaptureMouse(); e.Handled = true; };
        grip.MouseMove += (_, _) =>
        {
            if (!grip.IsMouseCaptured) return;
            Native.GetCursorPos(out var p);
            owner.SetArea(new Rect32(dragArea.X + p.X - dragStart.X, dragArea.Y + p.Y - dragStart.Y, dragArea.Width, dragArea.Height));
        };
        grip.MouseLeftButtonUp += (_, _) => grip.ReleaseMouseCapture();

        var label = new TextBlock { Text = $"Sharing area {owner.Number}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0), FontWeight = FontWeights.SemiBold };

        draw = new ToggleButton { Content = Labelled("", "Draw"), Margin = new Thickness(2), ToolTip = "Draw on this area. Your team sees the drawing." };
        draw.Click += (_, _) => owner.SetDrawing(draw.IsChecked == true);

        foreach (var (tool, glyph, name) in new[]
                 {
                     (InkTool.Pen, "", "Pen"), (InkTool.Highlighter, "", "Highlighter"), (InkTool.Arrow, "", "Arrow"),
                     (InkTool.Box, "", "Box"), (InkTool.Eraser, "", "Eraser"),
                 })
        {
            var b = new ToggleButton { Content = Labelled(glyph, name), Margin = new Thickness(2), ToolTip = name };
            b.Click += (_, _) => owner.SetTool(tool);
            toolButtons[tool] = b;
            tools.Children.Add(b);
        }
        tools.Children.Add(new Border { Width = 8 });
        foreach (var (color, name) in Palette)
        {
            var s = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(2), Background = new SolidColorBrush(color),
                BorderBrush = Brushes.White, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = name, VerticalAlignment = VerticalAlignment.Center,
            };
            s.MouseLeftButtonUp += (_, _) => owner.SetInkColor(color);
            swatches[color] = s;
            tools.Children.Add(s);
        }
        tools.Children.Add(new Border { Width = 8 });
        tools.Children.Add(Button("", "Undo", owner.Undo, "Undo the last drawing"));
        tools.Children.Add(Button("", "Clear", owner.ClearDrawing, "Remove all drawings on this area"));

        var stop = Button("", "Stop sharing this area", owner.RequestStop, "Take this area off the team screen");
        stop.Margin = new Thickness(10, 2, 2, 2);

        Content = new Border
        {
            Padding = new Thickness(4), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 232, 17, 35)), Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { grip, label, draw, tools, stop } },
        };
        SyncDrawing();
    }

    static StackPanel Labelled(string glyph, string text) => new()
    {
        Orientation = Orientation.Horizontal,
        Children =
        {
            new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) },
            new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
        },
    };

    static Button Button(string glyph, string text, Action click, string tip)
    {
        var b = new Button { Content = Labelled(glyph, text), Margin = new Thickness(2), ToolTip = tip };
        b.Click += (_, _) => click();
        return b;
    }

    public void SyncDrawing()
    {
        draw.IsChecked = owner.Drawing;
        tools.Visibility = owner.Drawing ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (tool, b) in toolButtons) b.IsChecked = tool == owner.Tool;
        foreach (var (color, s) in swatches) s.BorderThickness = new Thickness(color == owner.InkColor && owner.Tool != InkTool.Eraser ? 2 : 0);
        if (IsVisible) Dispatcher.BeginInvoke(() => Place(owner.Area));   // width changed: keep it on screen
    }

    /// <summary>Sits just above the area; below it if there's no room above; inside the top edge as a last resort.</summary>
    public void Place(Rect32 area)
    {
        UpdateLayout();
        var h = new WindowInteropHelper(this).EnsureHandle();
        Native.GetWindowRect(h, out var r);
        int w = r.Right - r.Left, ht = r.Bottom - r.Top, gap = (int)Math.Round(12 * SharedArea.Scale(this));
        var mon = Screen.FromRectangle(area).WorkingArea;
        var y = area.Top - ht - gap;
        if (y < mon.Top) y = area.Bottom + gap;
        if (y + ht > mon.Bottom) y = area.Top + gap;
        var x = Math.Clamp(area.Left, mon.Left, Math.Max(mon.Left, mon.Right - w));
        Native.SetWindowPos(h, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
}

/// <summary>Transparent drawing layer over a shared area. Captured with the area, so the team sees what you draw.</summary>
sealed class InkLayer : Window
{
    readonly InkCanvas canvas = new() { Background = Brushes.Transparent, EditingMode = InkCanvasEditingMode.None };
    readonly Stack<Stroke> history = new();
    Stroke? shape;
    Point shapeStart;
    bool drawing;

    public InkTool Tool { get; set; } = InkTool.Pen;
    public Color InkColor { get; set; } = SharedArea.LiveColor;

    public InkLayer()
    {
        ThemeMode = ThemeMode.None;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "Team Screen drawing layer";
        Content = canvas;
        SourceInitialized += (_, _) =>
            Native.AddExStyle(new WindowInteropHelper(this).Handle, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT);

        canvas.StrokeCollected += (_, e) => history.Push(e.Stroke);
        canvas.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!drawing || Tool is not (InkTool.Arrow or InkTool.Box)) return;
            shapeStart = e.GetPosition(canvas);
            shape = null;
            canvas.CaptureMouse();
            e.Handled = true;
        };
        canvas.PreviewMouseMove += (_, e) =>
        {
            if (!canvas.IsMouseCaptured || Tool is not (InkTool.Arrow or InkTool.Box)) return;
            var points = new StylusPointCollection(Tool == InkTool.Arrow ? ArrowPoints(shapeStart, e.GetPosition(canvas)) : BoxPoints(shapeStart, e.GetPosition(canvas)));
            if (shape == null)
            {
                var da = canvas.DefaultDrawingAttributes.Clone();
                da.FitToCurve = false;
                shape = new Stroke(points, da);
                canvas.Strokes.Add(shape);
            }
            else shape.StylusPoints = points;
        };
        canvas.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (!canvas.IsMouseCaptured) return;
            canvas.ReleaseMouseCapture();
            if (shape != null) history.Push(shape);
            shape = null;
        };
    }

    /// <summary>Drawing on: the layer catches the mouse. Off: clicks go straight through to your apps, drawings stay.</summary>
    public void SetDrawing(bool on)
    {
        drawing = on;
        var h = new WindowInteropHelper(this).EnsureHandle();
        if (on) Native.RemoveExStyle(h, Native.WS_EX_TRANSPARENT);
        else Native.AddExStyle(h, Native.WS_EX_TRANSPARENT);
        // Alpha 1 is invisible but makes the whole layer hit-testable while drawing.
        canvas.Background = on ? new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)) : Brushes.Transparent;
        ApplyTool();
    }

    public void ApplyTool()
    {
        var da = new DrawingAttributes { Color = InkColor, Width = 4, Height = 4, FitToCurve = true, IgnorePressure = true };
        if (Tool == InkTool.Highlighter)
        {
            da.IsHighlighter = true;
            da.Width = 6;
            da.Height = 22;
            da.StylusTip = StylusTip.Rectangle;
        }
        canvas.DefaultDrawingAttributes = da;
        canvas.EditingMode = !drawing ? InkCanvasEditingMode.None
            : Tool switch
            {
                InkTool.Pen or InkTool.Highlighter => InkCanvasEditingMode.Ink,
                InkTool.Eraser => InkCanvasEditingMode.EraseByStroke,
                _ => InkCanvasEditingMode.None,
            };
        canvas.Cursor = drawing && Tool is InkTool.Arrow or InkTool.Box ? Cursors.Cross : null;
    }

    public void Undo()
    {
        while (history.Count > 0)
        {
            var s = history.Pop();
            if (canvas.Strokes.Contains(s)) { canvas.Strokes.Remove(s); return; }   // skip strokes already erased
        }
    }

    public void Clear()
    {
        canvas.Strokes.Clear();
        history.Clear();
    }

    static IEnumerable<Point> BoxPoints(Point a, Point b) =>
        [a, new Point(b.X, a.Y), b, new Point(a.X, b.Y), a];

    static IEnumerable<Point> ArrowPoints(Point from, Point to)
    {
        var angle = Math.Atan2(from.Y - to.Y, from.X - to.X);
        const double head = 22, spread = Math.PI / 7;
        Point Barb(double a) => new(to.X + head * Math.Cos(a), to.Y + head * Math.Sin(a));
        return [from, to, Barb(angle + spread), to, Barb(angle - spread)];
    }
}
