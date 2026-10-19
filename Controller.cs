using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Rect32 = System.Drawing.Rectangle;

namespace TeamScreen;

public enum ScreenMode { Off, Dashboard, WebPage, Picture, MirrorArea, MirrorWindow }

/// <summary>Owns the team screen and every action on it. All methods run on the UI thread.</summary>
public sealed class Controller : IDisposable
{
    public AppSettings Settings { get; private set; }
    public ScreenMode Mode { get; private set; } = ScreenMode.Off;
    public bool Frozen { get; private set; }
    public bool Blanked { get; private set; }
    public string Detail { get; private set; } = "";
    public MonitorInfo? Monitor { get; private set; }
    public IReadOnlyList<HotkeyAction> FailedHotkeys { get; private set; } = [];
    public event Action? Changed;

    readonly CaptureEngine capture = new();
    readonly AreaOutline outline = new();
    readonly HotkeyManager hotkeys = new();
    readonly DispatcherTimer autoReturn = new();
    readonly MediaPlayer sound = new();
    readonly TrayIcon tray;
    DisplayWindow? display;
    Task? displayReady;
    PreviewWindow? preview;
    SettingsWindow? settingsWindow;

    public Controller(AppSettings settings)
    {
        Settings = settings;
        tray = new TrayIcon(this);
        hotkeys.Pressed += Run;
        capture.NewSurface += s => display?.SetLiveSurface(s);
        capture.SourceClosed += () => { Notify("Mirrored window closed", "Back to the dashboard."); _ = ShowDashboard(); };
        autoReturn.Tick += (_, _) => { autoReturn.Stop(); _ = ShowDashboard(); };
        SystemEvents.DisplaySettingsChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(OnDisplaysChanged);
    }

    public async void Start(bool showScreen)
    {
        ApplySettings(Settings, save: false);
        if (showScreen) await ShowDashboard(quietIfMissing: true);
    }

    public void Run(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleScreen: ToggleScreen(); break;
            case HotkeyAction.Dashboard: _ = ShowDashboard(); break;
            case HotkeyAction.MirrorArea: PickArea(); break;
            case HotkeyAction.MirrorLastArea: MirrorLastArea(); break;
            case HotkeyAction.MirrorActiveWindow: MirrorActiveWindow(); break;
            case HotkeyAction.ShowClipboard: _ = ShowClipboard(); break;
            case HotkeyAction.Freeze: ToggleFreeze(); break;
            case HotkeyAction.Blank: ToggleBlank(); break;
            case HotkeyAction.TogglePreview: TogglePreview(); break;
            case HotkeyAction.OpenSettings: OpenSettings(); break;
        }
    }

    // ---- Screen lifecycle ------------------------------------------------------------------

    async Task<bool> EnsureScreen(bool quiet = false)
    {
        var m = ResolveMonitor();
        if (m == null)
        {
            if (!quiet) Notify("Team screen not found", "That monitor isn't connected or is switched off. Choose another one in Settings.");
            return false;
        }
        if (m.Mirrored)
        {
            if (!quiet) Notify("Team screen is mirroring another screen", "Press Win+P and choose Extend, then try again.");
            return false;
        }
        if (display == null)
        {
            display = new DisplayWindow();
            display.Apply(Settings);
            Placement.FillMonitor(display, m.Bounds);
            displayReady = display.InitAsync();
        }
        else if (!display.IsVisible || Monitor?.Bounds != m.Bounds)
        {
            Placement.FillMonitor(display, m.Bounds);
        }
        Monitor = m;
        await displayReady!;
        return true;
    }

    /// <summary>The chosen screen, or on first run the first screen that isn't the main one (remembered once found).</summary>
    MonitorInfo? ResolveMonitor()
    {
        if (Settings.MonitorId.Length > 0) return Monitors.Find(Settings.MonitorId);
        var m = Monitors.All().FirstOrDefault(x => !x.Primary && !x.Mirrored);
        if (m != null) { Settings.MonitorId = m.Id; Settings.Save(); }
        return m;
    }

    void OnDisplaysChanged()
    {
        if (Mode == ScreenMode.Off || display == null) return;
        var m = ResolveMonitor();
        if (m == null || m.Mirrored) { TurnOff(); Notify("Team screen disconnected", "Turned it off. It'll come back when you switch it on again."); return; }
        if (m.Bounds != Monitor?.Bounds) { Monitor = m; Placement.FillMonitor(display, m.Bounds); }
    }

    void SetMode(ScreenMode mode, string detail)
    {
        Mode = mode;
        Detail = detail;
        Frozen = Blanked = false;
        autoReturn.Stop();
        if (mode is ScreenMode.MirrorArea or ScreenMode.MirrorWindow && Settings.AutoReturnMinutes > 0)
        {
            autoReturn.Interval = TimeSpan.FromMinutes(Settings.AutoReturnMinutes);
            autoReturn.Start();
        }
        Changed?.Invoke();
    }

    void StopMirror()
    {
        capture.Stop();
        outline.Hide();
    }

    public void ToggleScreen()
    {
        if (Mode == ScreenMode.Off) _ = ShowDashboard();
        else TurnOff();
    }

    public void TurnOff()
    {
        StopMirror();
        if (display != null)
        {
            display.ShowWeb("about:blank");
            display.Hide();
        }
        SetMode(ScreenMode.Off, "");
        Play();
    }

    // ---- What goes on the screen ----------------------------------------------------------

    public async Task ShowDashboard(bool quietIfMissing = false)
    {
        if (!await EnsureScreen(quietIfMissing)) return;
        StopMirror();
        var custom = Settings.UseCustomHome && Settings.HomeUrl.Trim().Length > 0;
        display!.ShowWeb(custom ? Settings.HomeUrl.Trim() : DashboardUrl());
        SetMode(ScreenMode.Dashboard, custom ? Settings.HomeUrl.Trim() : "Clock and weather");
        Play();
    }

    string DashboardUrl() =>
        $"https://{DisplayWindow.Host}/dashboard.html?note={Uri.EscapeDataString(Settings.Note)}" +
        $"&h24={(Settings.Clock24h ? 1 : 0)}&sec={(Settings.ShowSeconds ? 1 : 0)}&wx={(Settings.ShowWeather ? 1 : 0)}" +
        $"&loc={Uri.EscapeDataString(Settings.WeatherLocation.Replace(" ", ""))}";

    public async Task ShowUrl(string url)
    {
        if (!await EnsureScreen()) return;
        StopMirror();
        display!.ShowWeb(url);
        SetMode(ScreenMode.WebPage, url);
        Play();
    }

    public async Task ShowPicture(BitmapSource picture, string what)
    {
        if (!await EnsureScreen()) return;
        StopMirror();
        display!.ShowPicture(picture);
        SetMode(ScreenMode.Picture, what);
        Play();
    }

    /// <summary>Drop or paste target: images show as pictures, anything else opens as a page.</summary>
    public async Task ShowFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".tif" or ".tiff")
        {
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(path);
                img.EndInit();
                img.Freeze();
                await ShowPicture(img, Path.GetFileName(path));
                return;
            }
            catch { /* WIC can't decode it (e.g. animated webp): let the browser try */ }
        }
        await ShowUrl(new Uri(path).AbsoluteUri);
    }

    public async Task ShowClipboard()
    {
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img) { await ShowPicture(img, "Pasted picture"); return; }
            if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList() is { Count: > 0 } files) { await ShowFile(files[0]!); return; }
            var text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") { await ShowUrl(uri.AbsoluteUri); return; }
        }
        catch { /* clipboard locked by another app */ }
        Notify("Nothing to show", "Copy a picture (Win+Shift+S), a file or a web link first.");
    }

    // ---- Mirroring -----------------------------------------------------------------------

    public void PickArea()
    {
        if (AreaPicker.IsOpen) return;
        // Leave the team screen itself out: your team shouldn't see the picker, and mirroring it onto itself is pointless.
        var sources = Monitors.All().Where(m => !m.Id.Equals(Settings.MonitorId, StringComparison.OrdinalIgnoreCase) || m.Mirrored).ToList();
        var team = ResolveMonitor();
        double? aspect = Settings.LockAreaShape && team != null ? (double)team.Bounds.Width / team.Bounds.Height : null;
        AreaPicker.Pick(sources, aspect, r => _ = MirrorArea(r));
    }

    public void MirrorLastArea()
    {
        if (Settings.LastArea is [var x, var y, var w, var h]) _ = MirrorArea(new Rect32(x, y, w, h));
        else PickArea();
    }

    public async Task MirrorArea(Rect32 area)
    {
        if (!await EnsureScreen()) return;
        StopMirror();
        ConfigureCapture();
        capture.StartArea(area);
        display!.ShowLive(new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgr32, null));
        if (Settings.HighlightArea) outline.Show(area, LiveColor);
        Settings.LastArea = [area.X, area.Y, area.Width, area.Height];
        Settings.Save();
        SetMode(ScreenMode.MirrorArea, $"Area {area.Width} × {area.Height}");
        Play();
    }

    public void MirrorActiveWindow()
    {
        var h = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(h, out var pid);
        if (h == IntPtr.Zero || pid == Environment.ProcessId || Native.WindowTitle(h).Length == 0)
        {
            Notify("No window to mirror", "Click into the window you want to show, then press the hotkey.");
            return;
        }
        _ = MirrorWindow(h);
    }

    public async Task MirrorWindow(IntPtr hwnd)
    {
        if (!await EnsureScreen()) return;
        StopMirror();
        ConfigureCapture();
        capture.StartWindow(hwnd);
        display!.ShowLive(new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgr32, null));
        SetMode(ScreenMode.MirrorWindow, Native.WindowTitle(hwnd));
        Play();
    }

    void ConfigureCapture()
    {
        capture.Fps = Settings.Fps;
        capture.DrawCursor = Settings.ShowCursor;
    }

    static readonly Color LiveColor = Color.FromRgb(232, 17, 35);
    static readonly Color FrozenColor = Color.FromRgb(0, 120, 212);

    /// <summary>Top-level app windows worth offering in the "Mirror a window" menu.</summary>
    public static List<(IntPtr Handle, string Title)> OpenWindows()
    {
        var list = new List<(IntPtr, string)>();
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.GetWindow(h, 4) != IntPtr.Zero) return true;   // GW_OWNER
            if ((Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (Native.DwmGetWindowAttribute(h, 14, out var cloaked, 4) == 0 && cloaked != 0) return true;   // hidden store apps
            Native.GetWindowThreadProcessId(h, out var pid);
            var title = Native.WindowTitle(h);
            if (pid != Environment.ProcessId && title.Length > 0 && title != "Program Manager") list.Add((h, title));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // ---- Freeze / blank ------------------------------------------------------------------

    public void ToggleFreeze()
    {
        if (Mode == ScreenMode.Off || display == null || Monitor == null) return;
        Frozen = !Frozen;
        // A still of what's on the team screen right now, so freeze works the same in every mode.
        display.SetFrozen(Frozen ? CaptureEngine.GrabScreen(Monitor.Bounds) : null);
        capture.Paused = Frozen || Blanked;
        if (Mode == ScreenMode.MirrorArea) outline.SetColor(Frozen ? FrozenColor : LiveColor);
        Changed?.Invoke();
        Play();
    }

    public void ToggleBlank()
    {
        if (Mode == ScreenMode.Off || display == null) return;
        Blanked = !Blanked;
        display.SetBlank(Blanked);
        capture.Paused = Frozen || Blanked;
        Changed?.Invoke();
        Play();
    }

    // ---- Windows ---------------------------------------------------------------------------

    public void TogglePreview()
    {
        if (preview != null) { preview.Close(); return; }
        preview = new PreviewWindow(this);
        preview.Closed += (_, _) => { preview = null; Changed?.Invoke(); };
        preview.Show();
        Changed?.Invoke();
    }

    public bool PreviewOpen => preview != null;

    public void OpenSettings()
    {
        if (settingsWindow != null) { settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow(this);
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
        settingsWindow.Activate();
    }

    /// <summary>Used by the hotkey recorder so pressing a combo records it instead of firing it.</summary>
    public void SuspendHotkeys(bool suspend)
    {
        if (suspend) hotkeys.UnregisterAll();
        else FailedHotkeys = hotkeys.Register(Settings.Hotkeys);
    }

    public void ApplySettings(AppSettings s, bool save = true)
    {
        var monitorChanged = s.MonitorId != Settings.MonitorId;
        Settings = s;
        if (save) s.Save();
        try { AppSettings.SetStartWithWindows(s.StartWithWindows); } catch { /* policy-locked Run key */ }
        FailedHotkeys = hotkeys.Register(s.Hotkeys);
        if (FailedHotkeys.Count > 0 && save)
            Notify("Some hotkeys are taken", string.Join(", ", FailedHotkeys.Select(Describe)) + " — another app already uses those keys.");
        display?.Apply(s);
        ConfigureCapture();
        if (!s.HighlightArea) outline.Hide();
        preview?.Apply();
        if (Mode == ScreenMode.Dashboard || (monitorChanged && Mode != ScreenMode.Off)) _ = ShowDashboard();
        Changed?.Invoke();
    }

    // ---- Feedback --------------------------------------------------------------------------

    void Play()
    {
        if (!Settings.ActionSounds) return;
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", Settings.SoundFile);
        PlaySound(file, Settings.SoundVolume);
    }

    public void PlaySound(string file, int volume)
    {
        if (!File.Exists(file)) return;
        sound.Open(new Uri(file));
        sound.Volume = volume / 100.0;
        sound.Play();
    }

    public void Notify(string title, string text) => tray.Notify(title, text);

    public string StatusText => Mode switch
    {
        ScreenMode.Off => "Off",
        ScreenMode.Dashboard => "Dashboard",
        ScreenMode.WebPage => "Web page",
        ScreenMode.Picture => "Picture",
        ScreenMode.MirrorArea => "Mirroring an area",
        ScreenMode.MirrorWindow => "Mirroring a window",
        _ => "",
    } + (Blanked ? " · blanked" : Frozen ? " · frozen" : "");

    public static string Describe(HotkeyAction a) => a switch
    {
        HotkeyAction.ToggleScreen => "Turn team screen on/off",
        HotkeyAction.Dashboard => "Show dashboard",
        HotkeyAction.MirrorArea => "Mirror an area",
        HotkeyAction.MirrorLastArea => "Mirror the last area again",
        HotkeyAction.MirrorActiveWindow => "Mirror the window I'm in",
        HotkeyAction.ShowClipboard => "Show what's on my clipboard",
        HotkeyAction.Freeze => "Freeze / unfreeze",
        HotkeyAction.Blank => "Blank / unblank",
        HotkeyAction.TogglePreview => "Show / hide preview window",
        HotkeyAction.OpenSettings => "Open settings",
        _ => a.ToString(),
    };

    public void OpenSettingsFolder() => Process.Start("explorer.exe", AppSettings.Folder);

    public void Dispose()
    {
        capture.Stop();
        hotkeys.Dispose();
        tray.Dispose();
    }
}
