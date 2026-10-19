using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace TeamScreen;

public partial class App : Application
{
    Mutex? single;
    Controller? controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        single = new Mutex(true, "TeamScreen.SingleInstance", out var first);
        if (!first)
        {
            // Already running in the tray: ask it to open Settings, then quit.
            try { EventWaitHandle.OpenExisting("TeamScreen.OpenSettings").Set(); } catch { }
            Shutdown();
            return;
        }

        var wake = new EventWaitHandle(false, EventResetMode.AutoReset, "TeamScreen.OpenSettings");
        new Thread(() => { while (wake.WaitOne()) Dispatcher.BeginInvoke(() => controller?.OpenSettings()); })
            { IsBackground = true, Name = "TeamScreen wake" }.Start();

        controller = new Controller(AppSettings.Load());
        controller.Start(controller.Settings.ShowOnStartup);
        if (!e.Args.Contains("--background")) controller.OpenSettings();   // launched by hand: show something
    }

    protected override void OnExit(ExitEventArgs e)
    {
        controller?.Dispose();
        single?.Dispose();
        base.OnExit(e);
    }
}
