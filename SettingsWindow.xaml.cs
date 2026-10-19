using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace TeamScreen;

/// <summary>Edits a copy of the settings; Apply/Save hands it to the controller.</summary>
public partial class SettingsWindow : Window
{
    readonly Controller c;
    AppSettings draft;
    readonly Dictionary<string, Func<UIElement>> pages = new();
    readonly Dictionary<HotkeyAction, TextBox> hotkeyBoxes = new();
    readonly Dictionary<HotkeyAction, TextBlock> hotkeyWarnings = new();

    public SettingsWindow(Controller controller)
    {
        InitializeComponent();
        c = controller;
        draft = c.Settings.Clone();
        Icon = IconFactory.WindowIcon();
        AppIcon.Source = Icon;

        pages["General"] = GeneralPage;
        pages["Dashboard"] = DashboardPage;
        pages["Mirroring"] = MirroringPage;
        pages["Freeze & blank"] = FreezePage;
        pages["Sound"] = SoundPage;
        pages["Hotkeys"] = HotkeysPage;
        pages["Preview window"] = PreviewPage;
        pages["About"] = AboutPage;
        var glyphs = new[] { "", "", "", "", "", "", "", "" };
        foreach (var (name, glyph) in pages.Keys.Zip(glyphs))
            Nav.Items.Add(new ListBoxItem
            {
                Tag = name, Padding = new Thickness(10, 8, 10, 8),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            });
        Nav.SelectedIndex = 0;
        c.Changed += SyncStatus;
        Closed += (_, _) => { c.Changed -= SyncStatus; c.SuspendHotkeys(false); };
        SyncStatus();
    }

    void SyncStatus()
    {
        StatusLine.Text = c.StatusText;
        Footer.Text = c.Monitor is { } m ? $"Showing on {m.Name}" : "";
    }

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is ListBoxItem { Tag: string name }) Page.Content = pages[name]();
    }

    // ---- Building blocks -------------------------------------------------------------------

    static StackPanel PageOf(string title, params UIElement[] children)
    {
        var p = new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Stretch };
        p.Children.Add(new TextBlock { Text = title, FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 20) });
        foreach (var child in children) p.Children.Add(child);
        return p;
    }

    static TextBlock Heading(string text) =>
        new() { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 16, 0, 8) };

    /// <summary>A settings card: title and description on the left, the control on the right.</summary>
    static Border Card(string title, string? description, FrameworkElement? control)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (description != null)
            text.Children.Add(new TextBlock { Text = description, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        grid.Children.Add(text);
        if (control != null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            control.Margin = new Thickness(24, 0, 0, 0);
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }
        var card = new Border
        {
            Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1), Child = grid,
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return card;
    }

    Binding Bind(string property) => new(property) { Source = draft, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };

    CheckBox Switch(string property, string label = "")
    {
        var cb = new CheckBox { Content = label, MinWidth = 0 };
        cb.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, Bind(property));
        return cb;
    }

    TextBox Text(string property, double width = 300, string? placeholder = null)
    {
        var tb = new TextBox { Width = width };
        tb.SetBinding(TextBox.TextProperty, Bind(property));
        if (placeholder != null) tb.ToolTip = placeholder;
        return tb;
    }

    FrameworkElement SliderFor(string property, int min, int max, string unit, int tick = 1)
    {
        var s = new Slider { Minimum = min, Maximum = max, Width = 220, IsSnapToTickEnabled = true, TickFrequency = tick, VerticalAlignment = VerticalAlignment.Center };
        s.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty, Bind(property));
        var value = new TextBlock { Width = 72, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        value.SetBinding(TextBlock.TextProperty, new Binding(property) { Source = draft, StringFormat = "{0} " + unit });
        s.ValueChanged += (_, _) => value.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { s, value } };
    }

    ComboBox EnumCombo<T>(string property, params (T Value, string Label)[] items) where T : struct, Enum
    {
        var cb = new ComboBox { Width = 220, DisplayMemberPath = "Label", SelectedValuePath = "Value" };
        cb.ItemsSource = items.Select(i => new { i.Value, i.Label }).ToList();
        cb.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, Bind(property));
        return cb;
    }

    static Button ButtonOf(string text, Action click)
    {
        var b = new Button { Content = text, MinWidth = 96 };
        b.Click += (_, _) => click();
        return b;
    }

    // ---- Pages -----------------------------------------------------------------------------

    UIElement GeneralPage()
    {
        var monitors = Monitors.All();
        var combo = new ComboBox { Width = 320, DisplayMemberPath = "Label" };
        combo.ItemsSource = monitors;
        combo.SelectedItem = draft.MonitorId.Length == 0
            ? monitors.FirstOrDefault(m => !m.Primary && !m.Mirrored)
            : monitors.FirstOrDefault(m => m.Id.Equals(draft.MonitorId, StringComparison.OrdinalIgnoreCase));
        if (combo.SelectedItem == null)
        {
            combo.ItemsSource = monitors.Prepend(new MonitorInfo("", draft.MonitorId, $"{draft.MonitorId} (not connected)", default, false, false)).ToList();
            combo.SelectedIndex = 0;
        }
        var warning = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(196, 43, 28)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 4, 0, 0) };
        void Check()
        {
            var m = combo.SelectedItem as MonitorInfo;
            warning.Text = m == null || m.DeviceName == "" ? "That screen isn't connected right now."
                : m.Mirrored ? "This screen is mirroring another one. Press Win+P and choose Extend so it can show its own content."
                : m.Primary ? "This is your main screen. The team screen will cover it." : "";
            warning.Visibility = warning.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is MonitorInfo m) draft.MonitorId = m.Id; Check(); };
        Check();
        var identify = ButtonOf("Identify", () => { if (combo.SelectedItem is MonitorInfo { DeviceName.Length: > 0 } m) IdentifyFlash.Show(m); });
        var pick = new StackPanel { Orientation = Orientation.Horizontal, Children = { combo, identify } };
        identify.Margin = new Thickness(8, 0, 0, 0);

        return PageOf("General",
            Card("Team screen", "The monitor your team sees.", pick),
            warning,
            Heading("Startup"),
            Card("Start with Windows", "Runs quietly in the system tray when you sign in.", Switch(nameof(AppSettings.StartWithWindows))),
            Card("Show the dashboard when Team Screen starts", null, Switch(nameof(AppSettings.ShowOnStartup))),
            Heading("Behaviour"),
            Card("Keep the team screen on top", "Stops other windows covering it if they get dragged onto that monitor.", Switch(nameof(AppSettings.KeepOnTop))));
    }

    UIElement DashboardPage()
    {
        var builtIn = new RadioButton { Content = "Clock and weather", GroupName = "home", Margin = new Thickness(0, 0, 16, 0) };
        var custom = new RadioButton { Content = "A web page", GroupName = "home" };
        builtIn.IsChecked = !draft.UseCustomHome;
        custom.IsChecked = draft.UseCustomHome;
        var url = Text(nameof(AppSettings.HomeUrl), 420);
        url.IsEnabled = draft.UseCustomHome;
        custom.Checked += (_, _) => { draft.UseCustomHome = true; url.IsEnabled = true; };
        builtIn.Checked += (_, _) => { draft.UseCustomHome = false; url.IsEnabled = false; };

        return PageOf("Dashboard",
            Card("What the team screen shows when nothing else is up", null, new StackPanel { Orientation = Orientation.Horizontal, Children = { builtIn, custom } }),
            Card("Web page address", "Any site, e.g. a Home Assistant dashboard. Sign-ins are remembered on the team screen.", url),
            Heading("Clock and weather"),
            Card("Message", "Shown under the clock, e.g. \"Pizza Friday 🍕\". Leave empty for none.", Text(nameof(AppSettings.Note), 300)),
            Card("24-hour clock", null, Switch(nameof(AppSettings.Clock24h))),
            Card("Show seconds", null, Switch(nameof(AppSettings.ShowSeconds))),
            Card("Show weather", null, Switch(nameof(AppSettings.ShowWeather))),
            Card("Weather location", "Latitude, longitude (e.g. -36.85, 174.76). Leave empty to work it out from your internet connection.", Text(nameof(AppSettings.WeatherLocation), 220)),
            Card("Try it", "Saves your changes and puts the dashboard up now.", ButtonOf("Show dashboard", () => { Commit(); _ = c.ShowDashboard(); })));
    }

    UIElement MirroringPage() => PageOf("Mirroring",
        Card("Match the team screen's shape", "The box you drag keeps the team screen's proportions, so the mirror fills it edge to edge with no black bars. Hold Shift while dragging for any shape.", Switch(nameof(AppSettings.LockAreaShape))),
        Card("Frame rate", "Higher is smoother but uses more CPU. 15 is plenty for pages and documents.", SliderFor(nameof(AppSettings.Fps), 5, 60, "fps", 5)),
        Card("Show my mouse pointer", "Draws your pointer into the mirror so people can follow along.", Switch(nameof(AppSettings.ShowCursor))),
        Card("Outline the mirrored area on my screen", "A red frame around what's being shown (blue while frozen). It never appears on the team screen.", Switch(nameof(AppSettings.HighlightArea))),
        Card("Scaling", "How the mirror fits the team screen.", EnumCombo<FrameScaling>(nameof(AppSettings.Scaling),
            (FrameScaling.Fit, "Fit (black bars)"), (FrameScaling.Fill, "Fill (crop edges)"), (FrameScaling.Stretch, "Stretch"))),
        Card("Go back to the dashboard after", "Stops a mirror you forgot about. 0 means never.", SliderFor(nameof(AppSettings.AutoReturnMinutes), 0, 120, "min", 5)),
        Card("Forget the last area", draft.LastArea is [_, _, var w, var h] ? $"Remembered: {w} × {h}" : "Nothing remembered yet.",
            ButtonOf("Forget", () => draft.LastArea = null)));

    UIElement FreezePage() => PageOf("Freeze & blank",
        Card("Freeze", "Holds whatever's on the team screen while you carry on working: switch tabs, log in, look things up. Unfreeze when you're ready.", null),
        Card("Blank", "Covers the team screen straight away, e.g. if something private comes up.", null),
        Heading("Blank screen"),
        Card("Message while blank", "Leave empty for plain black.", Text(nameof(AppSettings.BlankMessage), 280)),
        Card("Mute sound while blank", null, Switch(nameof(AppSettings.MuteWhenBlank))));

    UIElement SoundPage()
    {
        var media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
        var files = Directory.Exists(media) ? Directory.GetFiles(media, "*.wav").Select(Path.GetFileName).OrderBy(f => f).ToList() : [];
        var combo = new ComboBox { Width = 300, ItemsSource = files };
        combo.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, Bind(nameof(AppSettings.SoundFile)));
        var test = ButtonOf("Play", () => c.PlaySound(Path.Combine(media, draft.SoundFile), draft.SoundVolume));
        test.Margin = new Thickness(8, 0, 0, 0);

        return PageOf("Sound",
            Heading("Action sounds"),
            Card("Play a sound when the team screen changes", "Confirms a hotkey worked without looking over.", Switch(nameof(AppSettings.ActionSounds))),
            Card("Sound", null, new StackPanel { Orientation = Orientation.Horizontal, Children = { combo, test } }),
            Card("Volume", null, SliderFor(nameof(AppSettings.SoundVolume), 0, 100, "%", 5)),
            Heading("Team screen audio"),
            Card("Mute the team screen", "Silences videos and pages playing on it.", Switch(nameof(AppSettings.MuteScreenAudio))));
    }

    UIElement HotkeysPage()
    {
        hotkeyBoxes.Clear();
        hotkeyWarnings.Clear();
        var rows = new List<UIElement>
        {
            Card("How to change one", "Click a box and press the new keys. Backspace clears it. Hotkeys work from any app.", ButtonOf("Reset all", () =>
            {
                draft.Hotkeys = AppSettings.DefaultHotkeys();
                foreach (var (a, box) in hotkeyBoxes) box.Text = draft.Hotkeys[a].ToString();
                CheckDuplicates();
            })),
        };
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var box = new TextBox { Width = 200, IsReadOnly = true, IsReadOnlyCaretVisible = false, Cursor = Cursors.Hand, Text = draft.Hotkeys.GetValueOrDefault(action, new()).ToString() };
            box.GotKeyboardFocus += (_, _) => { c.SuspendHotkeys(true); box.SelectAll(); };
            box.LostKeyboardFocus += (_, _) => c.SuspendHotkeys(false);
            box.PreviewKeyDown += (_, e) => Record(action, box, e);
            var warn = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(196, 43, 28)), FontSize = 12, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
            hotkeyBoxes[action] = box;
            hotkeyWarnings[action] = warn;
            rows.Add(Card(Controller.Describe(action), null, new StackPanel { Children = { box, warn } }));
        }
        CheckDuplicates();
        return PageOf("Hotkeys", rows.ToArray());
    }

    void Record(HotkeyAction action, TextBox box, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            box.Text = mods != ModifierKeys.None ? ModsText(mods) + " + …" : "";
            return;
        }
        if (key is Key.Tab) { e.Handled = false; return; }
        if (mods == ModifierKeys.None && key is Key.Back or Key.Delete or Key.Escape)
        {
            draft.Hotkeys[action] = new();
            box.Text = "";
        }
        else if (mods == ModifierKeys.None && key is not (>= Key.F1 and <= Key.F24))
        {
            box.Text = "Add Ctrl, Alt, Shift or Win";
            return;
        }
        else
        {
            draft.Hotkeys[action] = new Hotkey(mods, key);
            box.Text = draft.Hotkeys[action].ToString();
        }
        CheckDuplicates();
    }

    static string ModsText(ModifierKeys m) => new Hotkey(m, Key.A).ToString()[..^4];

    void CheckDuplicates()
    {
        var failed = c.FailedHotkeys.ToHashSet();
        foreach (var (action, warn) in hotkeyWarnings)
        {
            var key = draft.Hotkeys.GetValueOrDefault(action, new());
            var dupe = !key.IsEmpty && draft.Hotkeys.Any(p => p.Key != action && p.Value.Modifiers == key.Modifiers && p.Value.Key == key.Key);
            var taken = failed.Contains(action) && key.ToString() == c.Settings.Hotkeys.GetValueOrDefault(action, new()).ToString();
            warn.Text = dupe ? "Also used by another action here" : taken ? "Another app already uses this" : "";
            warn.Visibility = warn.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    UIElement PreviewPage() => PageOf("Preview window",
        Card("What it is", "A small window on your desk showing exactly what the team screen shows. Left-click the tray icon to open it. Drop files or links onto it to put them up.", null),
        Card("Keep the preview on top", null, Switch(nameof(AppSettings.PreviewOnTop))),
        Card("Preview frame rate", "How often the preview refreshes.", SliderFor(nameof(AppSettings.PreviewFps), 1, 30, "fps")));

    UIElement AboutPage()
    {
        var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "";
        return PageOf("About",
            Card("Team Screen", $"Version {version}. Puts a dashboard, pictures, pages or a live piece of your screen on a second monitor without touching your own screens.", null),
            Card("Settings file", AppSettings.Folder, ButtonOf("Open folder", c.OpenSettingsFolder)),
            Card("Reset everything", "Puts every option and hotkey back to how it started.", ButtonOf("Reset", () =>
            {
                draft = new AppSettings { MonitorId = draft.MonitorId };
                Page.Content = AboutPage();
                Footer.Text = "Defaults restored — press Save to keep them.";
            })));
    }

    // ---- Buttons ---------------------------------------------------------------------------

    void Commit()
    {
        FocusManager.SetFocusedElement(this, this);   // push any pending text-box edit into the draft
        c.ApplySettings(draft);
        draft = c.Settings.Clone();
        if (Nav.SelectedItem is ListBoxItem { Tag: string name }) Page.Content = pages[name]();
        Footer.Text = c.FailedHotkeys.Count > 0 ? "Saved — some hotkeys are taken by another app (see Hotkeys)." : "Saved.";
    }

    void Apply_Click(object sender, RoutedEventArgs e) => Commit();
    void Save_Click(object sender, RoutedEventArgs e) { Commit(); Close(); }
    void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
