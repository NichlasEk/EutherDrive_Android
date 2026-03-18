using Android.App;
using Android.Content.Res;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia.Android;
using Orientation = Android.Content.Res.Orientation;

namespace EutherDrive.Android;

[Activity(
    Label = "EutherDrive",
    Icon = "@mipmap/ic_launcher",
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
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ApplyFullscreenForOrientation(Resources?.Configuration?.Orientation ?? Orientation.Undefined);
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplyFullscreenForOrientation(newConfig.Orientation);
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);

        if (hasFocus)
        {
            ApplyFullscreenForOrientation(Resources?.Configuration?.Orientation ?? Orientation.Undefined);
        }
    }

    private void ApplyFullscreenForOrientation(Orientation orientation)
    {
        if (Window is null)
        {
            return;
        }

        bool immersiveLandscape = orientation == Orientation.Landscape;

        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            Window.SetDecorFitsSystemWindows(!immersiveLandscape);

            var controller = Window.InsetsController;
            if (controller is null)
            {
                return;
            }

            if (immersiveLandscape)
            {
                controller.Hide(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars());
                controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }
            else
            {
                controller.Show(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars());
            }

            return;
        }

#pragma warning disable CA1422
        var flags = (StatusBarVisibility)(
            SystemUiFlags.LayoutStable |
            SystemUiFlags.LayoutHideNavigation |
            SystemUiFlags.LayoutFullscreen |
            SystemUiFlags.HideNavigation |
            SystemUiFlags.Fullscreen |
            SystemUiFlags.ImmersiveSticky);

        Window.DecorView!.SystemUiVisibility = immersiveLandscape
            ? flags
            : StatusBarVisibility.Visible;
#pragma warning restore CA1422
    }
}
