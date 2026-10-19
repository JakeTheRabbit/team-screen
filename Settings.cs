using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using Microsoft.Win32;

namespace TeamScreen;

public enum HotkeyAction
{
    ToggleScreen, Dashboard, MirrorArea, MirrorLastArea, MirrorActiveWindow,
    ShowClipboard, Freeze, Blank, TogglePreview, OpenSettings,
}

public enum FrameScaling { Fit, Fill, Stretch }

public sealed class Hotkey
{
    public ModifierKeys Modifiers { get; set; }
    public Key Key { get; set; }

    public Hotkey() { }
    public Hotkey(ModifierKeys modifiers, Key key) { Modifiers = modifiers; Key = key; }

    [JsonIgnore] public bool IsEmpty => Key == Key.None;

    public override string ToString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(Key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(Key - Key.D0)).ToString(),
            Key.OemComma => ",", Key.OemPeriod => ".", Key.OemMinus => "-", Key.OemPlus => "=",
            Key.Oem2 => "/", Key.Oem1 => ";", Key.Oem3 => "`", Key.Oem4 => "[", Key.Oem6 => "]",
            _ => Key.ToString(),
        });
        return string.Join(" + ", parts);
    }
}

public sealed class AppSettings
{
    // General
    public string MonitorId { get; set; } = "";   // empty: first screen that isn't the main one
    public bool StartWithWindows { get; set; } = true;
    public bool ShowOnStartup { get; set; } = true;
    public bool KeepOnTop { get; set; }

    // Dashboard
    public bool UseCustomHome { get; set; }
    public string HomeUrl { get; set; } = "";
    public string Note { get; set; } = "";
    public bool Clock24h { get; set; }
    public bool ShowSeconds { get; set; }
    public bool ShowWeather { get; set; } = true;
    public string WeatherLocation { get; set; } = "";

    // Mirroring
    public int Fps { get; set; } = 15;
    public bool ShowCursor { get; set; } = true;
    public bool HighlightArea { get; set; } = true;
    public bool LockAreaShape { get; set; } = true;
    public FrameScaling Scaling { get; set; } = FrameScaling.Fit;
    public int AutoReturnMinutes { get; set; }
    public int[]? LastArea { get; set; }

    // Freeze & blank
    public string BlankMessage { get; set; } = "Back soon";
    public bool MuteWhenBlank { get; set; } = true;

    // Sound
    public bool ActionSounds { get; set; } = true;
    public string SoundFile { get; set; } = "Windows Notify System Generic.wav";
    public int SoundVolume { get; set; } = 60;
    public bool MuteScreenAudio { get; set; }

    // Preview
    public bool PreviewOnTop { get; set; } = true;
    public int PreviewFps { get; set; } = 4;

    public Dictionary<HotkeyAction, Hotkey> Hotkeys { get; set; } = DefaultHotkeys();

    public static Dictionary<HotkeyAction, Hotkey> DefaultHotkeys()
    {
        const ModifierKeys CA = ModifierKeys.Control | ModifierKeys.Alt;
        return new()
        {
            [HotkeyAction.ToggleScreen] = new(CA, Key.L),
            [HotkeyAction.Dashboard] = new(CA, Key.H),
            [HotkeyAction.MirrorArea] = new(CA, Key.R),
            [HotkeyAction.MirrorLastArea] = new(CA | ModifierKeys.Shift, Key.R),
            [HotkeyAction.MirrorActiveWindow] = new(CA, Key.N),   // Ctrl+Alt+M is NVIDIA overlay's mic toggle
            [HotkeyAction.ShowClipboard] = new(CA, Key.P),
            [HotkeyAction.Freeze] = new(CA, Key.S),   // F = Everything, Z = NVIDIA overlay: both use low-level hooks RegisterHotKey can't see
            [HotkeyAction.Blank] = new(CA, Key.B),
            [HotkeyAction.TogglePreview] = new(CA, Key.W),
            [HotkeyAction.OpenSettings] = new(),
        };
    }

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TeamScreen");
    static string FilePath => Path.Combine(Folder, "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static AppSettings Load()
    {
        AppSettings s;
        try { s = File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new() : new(); }
        catch { s = new(); }
        foreach (var (action, key) in DefaultHotkeys())   // new actions added in later versions get their defaults
            s.Hotkeys.TryAdd(action, key);
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json), Json)!;

    public static void SetStartWithWindows(bool on)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (key == null) return;
        if (on) key.SetValue("TeamScreen", $"\"{Environment.ProcessPath}\" --background");
        else key.DeleteValue("TeamScreen", false);
    }
}
