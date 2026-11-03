using System;
using System.IO;
using System.Windows;

namespace G300sController;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
            File.WriteAllText(logPath, e.Exception.ToString());
        };
    }
}
