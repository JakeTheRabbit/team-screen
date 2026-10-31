using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace TeamScreen;

public partial class App : Application
{
    Mutex? single;
    Controller? controller;

    static string CommandFile => Path.Combine(AppSettings.Folder, "command.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var isCommand = e.Args.Any(a => a is "--run" or "--share-area");
        single = new Mutex(true, "TeamScreen.SingleInstance", out var first);
        if (!first)
        {
            // Already running in the tray: hand it the command (or ask it to open Settings), then quit.
            try
            {
                if (isCommand)
                {
                    Directory.CreateDirectory(AppSettings.Folder);
                    File.WriteAllLines(CommandFile, e.Args);
                    EventWaitHandle.OpenExisting("TeamScreen.Command").Set();
                }
                else if (!e.Args.Contains("--background")) EventWaitHandle.OpenExisting("TeamScreen.OpenSettings").Set();
            }
            catch { }
            Shutdown();
            return;
        }

        var wake = new EventWaitHandle(false, EventResetMode.AutoReset, "TeamScreen.OpenSettings");
        new Thread(() => { while (wake.WaitOne()) Dispatcher.BeginInvoke(() => controller?.OpenSettings()); })
            { IsBackground = true, Name = "TeamScreen wake" }.Start();
        var command = new EventWaitHandle(false, EventResetMode.AutoReset, "TeamScreen.Command");
        new Thread(() =>
        {
            while (command.WaitOne())
            {
                string[] args;
                try { args = File.ReadAllLines(CommandFile); File.Delete(CommandFile); } catch { continue; }
                Dispatcher.BeginInvoke(() => controller?.RunCommand(args));
            }
        }) { IsBackground = true, Name = "TeamScreen commands" }.Start();

        controller = new Controller(AppSettings.Load());
        controller.Start(controller.Settings.ShowOnStartup);
        if (isCommand) controller.RunCommand(e.Args);
        else if (!e.Args.Contains("--background")) controller.OpenSettings();   // launched by hand: show something
    }

    protected override void OnExit(ExitEventArgs e)
    {
        controller?.Dispose();
        single?.Dispose();
        base.OnExit(e);
    }
}
