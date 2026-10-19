using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace TeamScreen;

/// <summary>Notification-area icon. Left click toggles the preview; right click opens a themed WPF menu.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly Controller c;
    readonly Forms.NotifyIcon icon;
    System.Drawing.Icon? current;

    public TrayIcon(Controller controller)
    {
        c = controller;
        icon = new Forms.NotifyIcon { Visible = true };
        icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) c.TogglePreview();
            else if (e.Button == Forms.MouseButtons.Right) ShowMenu();
        };
        c.Changed += Refresh;
        Refresh();
    }

    void Refresh()
    {
        var old = current;
        current = IconFactory.Tray(c.Mode, c.Frozen, c.Blanked);
        icon.Icon = current;
        IconFactory.Destroy(old);
        var text = $"Team Screen — {c.StatusText}";
        icon.Text = text.Length > 127 ? text[..127] : text;
    }

    public void Notify(string title, string text) => icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);

    void ShowMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        string Keys(HotkeyAction a) => c.Settings.Hotkeys.TryGetValue(a, out var k) ? k.ToString() : "";
        MenuItem Item(string header, string glyph, Action click, HotkeyAction? key = null, bool isChecked = false, bool enabled = true)
        {
            var mi = new MenuItem
            {
                Header = header, IsChecked = isChecked, IsEnabled = enabled,
                InputGestureText = key is { } k ? Keys(k) : "",
                Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14 },
            };
            mi.Click += (_, _) => click();
            return mi;
        }

        var on = c.Mode != ScreenMode.Off;
        menu.Items.Add(new MenuItem
        {
            Header = c.Monitor is { } m ? $"{c.StatusText} on {m.Name}" : $"Team screen: {c.StatusText}",
            IsEnabled = false, FontWeight = FontWeights.SemiBold,
        });
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(on ? "Turn team screen off" : "Turn team screen on", "", c.ToggleScreen, HotkeyAction.ToggleScreen));
        menu.Items.Add(Item("Show dashboard", "", () => _ = c.ShowDashboard(), HotkeyAction.Dashboard, c.Mode == ScreenMode.Dashboard));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Mirror an area…", "", c.PickArea, HotkeyAction.MirrorArea, c.Mode == ScreenMode.MirrorArea));
        menu.Items.Add(Item("Mirror the last area", "", c.MirrorLastArea, HotkeyAction.MirrorLastArea, enabled: c.Settings.LastArea != null));

        var windows = new MenuItem
        {
            Header = "Mirror a window", IsChecked = c.Mode == ScreenMode.MirrorWindow,
            Icon = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14 },
        };
        foreach (var (handle, title) in Controller.OpenWindows().Take(25))
        {
            var h = handle;
            var label = title.Length > 60 ? title[..57] + "…" : title;
            var mi = new MenuItem { Header = label.Replace("_", "__") };   // underscores would become access keys
            mi.Click += (_, _) => _ = c.MirrorWindow(h);
            windows.Items.Add(mi);
        }
        menu.Items.Add(windows);
        menu.Items.Add(Item("Show what's on my clipboard", "", () => _ = c.ShowClipboard(), HotkeyAction.ShowClipboard));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Freeze", "", c.ToggleFreeze, HotkeyAction.Freeze, c.Frozen, on));
        menu.Items.Add(Item("Blank", "", c.ToggleBlank, HotkeyAction.Blank, c.Blanked, on));
        menu.Items.Add(Item("Mute team screen sound", "", () =>
        {
            var s = c.Settings.Clone();
            s.MuteScreenAudio = !s.MuteScreenAudio;
            c.ApplySettings(s);
        }, isChecked: c.Settings.MuteScreenAudio));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Preview window", "", c.TogglePreview, HotkeyAction.TogglePreview, c.PreviewOpen));
        menu.Items.Add(Item("Settings…", "", c.OpenSettings, HotkeyAction.OpenSettings));
        menu.Items.Add(Item("Exit", "", () => Application.Current.Shutdown()));

        // Without foreground the menu would never close on an outside click.
        menu.Opened += (_, _) => { if (PresentationSource.FromVisual(menu) is HwndSource src) Native.SetForegroundWindow(src.Handle); };
        menu.IsOpen = true;
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        IconFactory.Destroy(current);
    }
}
