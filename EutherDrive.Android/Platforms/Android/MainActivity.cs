using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace EutherDrive.Android;

[Activity(
    Label = "EutherDrive",
    Theme = "@style/MainTheme",
    MainLauncher = true,
    Exported = true,
    ConfigurationChanges =
        ConfigChanges.Orientation |
        ConfigChanges.ScreenSize |
        ConfigChanges.UiMode |
        ConfigChanges.ScreenLayout |
        ConfigChanges.SmallestScreenSize |
        ConfigChanges.Density)]
public class MainActivity : AvaloniaMainActivity<App>
{
}
