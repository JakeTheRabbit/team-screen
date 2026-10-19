using System;
using System.IO;
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

    enum Layer { Web, Live, Picture }

    Layer content = Layer.Web;
    bool frozen, blanked;
    bool userMuted, muteWhenBlank = true;

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
        Live.Stretch = Still.Stretch = stretch;
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

    public void ShowLive(ImageSource surface)
    {
        StopWeb();
        Live.Source = surface;
        content = Layer.Live;
        frozen = blanked = false;
        Refresh();
    }

    public void ShowPicture(BitmapSource picture)
    {
        StopWeb();
        Live.Source = picture;
        content = Layer.Picture;
        frozen = blanked = false;
        Refresh();
    }

    public void SetLiveSurface(ImageSource surface) => Live.Source = surface;

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
        Live.Visibility = showContent && content != Layer.Web ? Visibility.Visible : Visibility.Collapsed;
        Web.Visibility = showContent && content == Layer.Web ? Visibility.Visible : Visibility.Collapsed;
        UpdateMute();
    }
}
