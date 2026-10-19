using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace TeamScreen;

/// <summary>Live look at the team screen from your desk. Drop files or links on it to put them up.</summary>
public partial class PreviewWindow : Window
{
    readonly Controller c;
    readonly DispatcherTimer timer = new();

    public PreviewWindow(Controller controller)
    {
        InitializeComponent();
        c = controller;
        Icon = IconFactory.WindowIcon();
        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - 24;
        Top = work.Bottom - Height - 24;

        timer.Tick += (_, _) => Grab();
        c.Changed += Sync;
        Closed += (_, _) => { timer.Stop(); c.Changed -= Sync; };
        DragEnter += (_, e) => { DropHint.Visibility = Visibility.Visible; e.Effects = DragDropEffects.Copy; };
        DragLeave += (_, _) => DropHint.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
        Apply();
        Sync();
    }

    public void Apply()
    {
        Topmost = c.Settings.PreviewOnTop;
        timer.Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(c.Settings.PreviewFps, 1, 30));
        timer.Start();
    }

    void Grab()
    {
        if (c.Mode == ScreenMode.Off || c.Monitor == null || WindowState == WindowState.Minimized) return;
        try { Frame.Source = CaptureEngine.GrabScreen(c.Monitor.Bounds); } catch { /* screen locked */ }
    }

    void Sync()
    {
        var on = c.Mode != ScreenMode.Off;
        OffText.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (!on) Frame.Source = null;
        Status.Text = c.Mode == ScreenMode.Off ? "Off" : $"{c.StatusText} — {c.Detail}";
        Status.ToolTip = Status.Text;

        var live = c.Mode is ScreenMode.MirrorArea or ScreenMode.MirrorWindow;
        Badge.Visibility = on && (live || c.Frozen || c.Blanked) ? Visibility.Visible : Visibility.Collapsed;
        BadgeText.Text = c.Blanked ? "BLANK" : c.Frozen ? "FROZEN" : "LIVE";
        Badge.Background = new SolidColorBrush(c.Blanked ? Color.FromRgb(96, 96, 96) : c.Frozen ? Color.FromRgb(0, 120, 212) : Color.FromRgb(232, 17, 35));

        FreezeBtn.IsChecked = c.Frozen;
        BlankBtn.IsChecked = c.Blanked;
        FreezeBtn.IsEnabled = BlankBtn.IsEnabled = on;
        PowerBtn.Content = on ? "Turn off" : "Turn on";
        Grab();
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) _ = c.ShowFile(files[0]);
        else if (e.Data.GetData(DataFormats.UnicodeText) is string text && Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)) _ = c.ShowUrl(uri.AbsoluteUri);
    }

    void Dashboard_Click(object sender, RoutedEventArgs e) => _ = c.ShowDashboard();
    void Area_Click(object sender, RoutedEventArgs e) => c.PickArea();
    void Freeze_Click(object sender, RoutedEventArgs e) => c.ToggleFreeze();
    void Blank_Click(object sender, RoutedEventArgs e) => c.ToggleBlank();
    void Power_Click(object sender, RoutedEventArgs e) => c.ToggleScreen();
}
