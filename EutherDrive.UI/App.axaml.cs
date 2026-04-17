using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using System;
using System.Linq;
using System.Net.Http;
using EutherDrive.UI.Offworld;
using EutherDrive.UI.Skins;

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
        // Initialize skin manager before creating windows
        SkinManager.Instance.Initialize(this);
        
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Check for ROM path in command line args
            var romArg = CommandLineArgs.FirstOrDefault(a => !a.StartsWith("-"));
            var marsTelemetryHttpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            IMarsTelemetryProvider marsTelemetryProvider = new MarsWeatherService(marsTelemetryHttpClient);
            var offworldMonitorViewModel = new OffworldMonitorViewModel(marsTelemetryProvider);
            var mainWindow = new MainWindow(romArg, offworldMonitorViewModel);
            using var iconStream = AssetLoader.Open(new Uri("avares://EutherDrive.UI/Assets/eutherdrive.ico"));
            mainWindow.Icon = new WindowIcon(iconStream);
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
