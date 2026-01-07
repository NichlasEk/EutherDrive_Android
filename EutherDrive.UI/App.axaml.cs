using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;
using System.Linq;

namespace EutherDrive.UI;

public partial class App : Application
{
    public static string[] CommandLineArgs = Array.Empty<string>();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Check for ROM path in command line args
            var romArg = CommandLineArgs.FirstOrDefault(a => !a.StartsWith("-"));
            desktop.MainWindow = new MainWindow(romArg);
        }

        base.OnFrameworkInitializationCompleted();
    }
}