using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Controls;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace TeamScreen;

/// <summary>Full-screen surface on the team monitor. Never takes focus.</summary>
public partial class DisplayWindow : Window
{
    public const string Host = "team.local";

    enum Layer { Web, Picture, Tiles }

    Layer content = Layer.Web;
    bool frozen, blanked;
    bool userMuted, muteWhenBlank = true;
    Stretch liveStretch = Stretch.Uniform;

    public DisplayWindow()
    {
        InitializeComponent();
        Icon = IconFactory.WindowIcon();
        SourceInitialized += (_, _) =>
            Native.AddExStyle(new WindowInteropHelper(this).Handle, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
    }

    public async Task InitAsync()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeamScreen", "WebView2");
        var env = await CoreWebView2Environment.CreateAsync(null, data, new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
        await Web.EnsureCoreWebView2Async(env);
        var core = Web.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(Host, Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Allow);
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.NewWindowRequested += (_, e) => { e.Handled = true; core.Navigate(e.Uri); };   // keep pop-ups on the team screen
    }

    public void Apply(AppSettings s)
    {
        Topmost = s.KeepOnTop;
        userMuted = s.MuteScreenAudio;
        var stretch = s.Scaling switch { FrameScaling.Fill => Stretch.UniformToFill, FrameScaling.Stretch => Stretch.Fill, _ => Stretch.Uniform };
        Live.Stretch = Still.Stretch = liveStretch = stretch;
        foreach (Image tile in Tiles.Children) tile.Stretch = stretch;
        BlankText.Text = s.BlankMessage;
        muteWhenBlank = s.MuteWhenBlank;
        Refresh();
    }

    void UpdateMute()
    {
        if (Web.CoreWebView2 != null) Web.CoreWebView2.IsMuted = userMuted || (blanked && muteWhenBlank);
    }

    public async void ShowWeb(string url)
    {
        content = Layer.Web;
        frozen = blanked = false;
        Refresh();
        if (Web.CoreWebView2 == null) return;
        Web.CoreWebView2.Navigate(url);
        if (url.Contains("youtube.com/") || url.Contains("youtu.be/"))
        {
            // YouTube refuses top-level embeds (Error 152/153); play the normal page and press "f" for fullscreen.
            await Task.Delay(4000);
            foreach (var type in new[] { "keyDown", "keyUp" })
                await Web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                    $"{{\"type\":\"{type}\",\"key\":\"f\",\"code\":\"KeyF\",\"text\":\"f\",\"windowsVirtualKeyCode\":70}}");
        }
    }

    /// <summary>Live mirrors: one tile per shared area, laid out side by side then in a grid.</summary>
    public void ShowTiles(IReadOnlyList<ImageSource> sources)
    {
        StopWeb();
        content = Layer.Tiles;
        frozen = blanked = false;
        SetTiles(sources);
        Refresh();
    }

    public void SetTiles(IReadOnlyList<ImageSource> sources)
    {
        var n = sources.Count;
        Tiles.Columns = n <= 2 ? Math.Max(1, n) : (int)Math.Ceiling(Math.Sqrt(n));
        Tiles.Rows = Math.Max(1, (int)Math.Ceiling(n / (double)Tiles.Columns));
        while (Tiles.Children.Count > n) Tiles.Children.RemoveAt(Tiles.Children.Count - 1);
        while (Tiles.Children.Count < n)
        {
            var tile = new Image { Stretch = liveStretch };
            RenderOptions.SetBitmapScalingMode(tile, BitmapScalingMode.HighQuality);
            Tiles.Children.Add(tile);
        }
        for (var i = 0; i < n; i++)
        {
            var tile = (Image)Tiles.Children[i];
            tile.Source = sources[i];
            tile.Margin = new Thickness(n > 1 ? 4 : 0);
        }
    }

    public void ShowPicture(BitmapSource picture)
    {
        StopWeb();
        Live.Source = picture;
        content = Layer.Picture;
        frozen = blanked = false;
        Refresh();
    }

    /// <summary>Freeze holds a still of whatever is showing right now, whatever the mode.</summary>
    public void SetFrozen(BitmapSource? still)
    {
        frozen = still != null;
        Still.Source = still;
        Refresh();
    }

    public void SetBlank(bool on)
    {
        blanked = on;
        Refresh();
    }

    void StopWeb()
    {
        if (Web.CoreWebView2 != null && Web.Source?.AbsoluteUri != "about:blank") Web.CoreWebView2.Navigate("about:blank");   // stops any audio
    }

    void Refresh()
    {
        BlankLayer.Visibility = blanked ? Visibility.Visible : Visibility.Collapsed;
        Still.Visibility = frozen && !blanked ? Visibility.Visible : Visibility.Collapsed;
        var showContent = !frozen && !blanked;
        Live.Visibility = showContent && content == Layer.Picture ? Visibility.Visible : Visibility.Collapsed;
        Tiles.Visibility = showContent && content == Layer.Tiles ? Visibility.Visible : Visibility.Collapsed;
        Web.Visibility = showContent && content == Layer.Web ? Visibility.Visible : Visibility.Collapsed;
        UpdateMute();
    }
}
