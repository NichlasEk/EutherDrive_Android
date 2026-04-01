using System;
using Android.App;
using Android.Content.Res;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
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
    public static MainActivity? Current { get; private set; }
    private FrameLayout? _nativeVideoContainer;
    private float _lastRequestedContentFps;
    private float _lastNormalizedContentFps;
    private float _lastPreferredDisplayRefresh;
    private int _lastPreferredDisplayModeId;
    private float _lastObservedDisplayRefresh;
    private int _lastObservedDisplayModeId;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Current = this;
        EnsureNativeVideoContainer();
        ApplyFullscreenForOrientation(Resources?.Configuration?.Orientation ?? Orientation.Undefined);
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Current, this))
            Current = null;
        base.OnDestroy();
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
        var flags =
            SystemUiFlags.LayoutStable |
            SystemUiFlags.LayoutHideNavigation |
            SystemUiFlags.LayoutFullscreen |
            SystemUiFlags.HideNavigation |
            SystemUiFlags.Fullscreen |
            SystemUiFlags.ImmersiveSticky;

        Window.DecorView!.SystemUiFlags = immersiveLandscape
            ? flags
            : SystemUiFlags.Visible;
#pragma warning restore CA1422
    }

    public void AttachNativeVideoView(View view)
    {
        var container = EnsureNativeVideoContainer();
        if (view.Parent is ViewGroup existingParent && !ReferenceEquals(existingParent, container))
            existingParent.RemoveView(view);

        if (!ReferenceEquals(view.Parent, container))
        {
            var layout = new FrameLayout.LayoutParams(1, 1)
            {
                LeftMargin = 0,
                TopMargin = 0
            };
            container.AddView(view, layout);
        }
    }

    public void DetachNativeVideoView(View view)
    {
        if (view.Parent is ViewGroup parent)
            parent.RemoveView(view);
    }

    public void UpdateNativeVideoViewLayout(View view, int left, int top, int width, int height, bool visible)
    {
        AttachNativeVideoView(view);

        int clampedWidth = Math.Max(1, width);
        int clampedHeight = Math.Max(1, height);
        if (view.LayoutParameters is not FrameLayout.LayoutParams layout)
        {
            layout = new FrameLayout.LayoutParams(clampedWidth, clampedHeight);
        }

        bool changed = layout.Width != clampedWidth
            || layout.Height != clampedHeight
            || layout.LeftMargin != left
            || layout.TopMargin != top;

        if (changed)
        {
            layout.Width = clampedWidth;
            layout.Height = clampedHeight;
            layout.LeftMargin = left;
            layout.TopMargin = top;
            view.LayoutParameters = layout;
        }

        var desiredVisibility = visible ? ViewStates.Visible : ViewStates.Invisible;
        if (view.Visibility != desiredVisibility)
            view.Visibility = desiredVisibility;
    }

    public void SetPreferredRefreshRate(float fps)
    {
        if (Window is null)
            return;

        try
        {
            var attributes = Window.Attributes;
            float preferredContentFps = fps > 1.0f ? fps : 0f;
            float normalizedContentFps = NormalizePreferredContentFps(preferredContentFps);
            int preferredModeId = 0;
            float preferredDisplayRefresh = 0f;
            Display? display = Window.DecorView?.Display;

            if (normalizedContentFps > 1.0f && display is not null)
                SelectPreferredDisplayMode(display, normalizedContentFps, out preferredModeId, out preferredDisplayRefresh);

            if (preferredDisplayRefresh <= 1.0f)
                preferredDisplayRefresh = normalizedContentFps;

            _lastRequestedContentFps = preferredContentFps;
            _lastNormalizedContentFps = normalizedContentFps;
            _lastPreferredDisplayRefresh = preferredDisplayRefresh;
            _lastPreferredDisplayModeId = preferredModeId;
            UpdateObservedDisplayMode(display);

            if (Math.Abs(attributes.PreferredRefreshRate - preferredDisplayRefresh) <= 0.05f
                && attributes.PreferredDisplayModeId == preferredModeId)
                return;

            attributes.PreferredRefreshRate = preferredDisplayRefresh;
            attributes.PreferredDisplayModeId = preferredModeId;
            Window.Attributes = attributes;
        }
        catch
        {
        }
    }

    public string GetRefreshDebugSummary()
    {
        try
        {
            UpdateObservedDisplayMode(Window?.DecorView?.Display);
            if (_lastObservedDisplayRefresh <= 1.0f
                && _lastPreferredDisplayRefresh <= 1.0f
                && _lastRequestedContentFps <= 1.0f)
            {
                return string.Empty;
            }

            string requested = _lastRequestedContentFps > 1.0f ? _lastRequestedContentFps.ToString("0.00") : "off";
            string normalized = _lastNormalizedContentFps > 1.0f ? _lastNormalizedContentFps.ToString("0.00") : "off";
            string preferred = _lastPreferredDisplayRefresh > 1.0f ? _lastPreferredDisplayRefresh.ToString("0.00") : "auto";
            string observed = _lastObservedDisplayRefresh > 1.0f ? _lastObservedDisplayRefresh.ToString("0.00") : "n/a";
            return $"Disp req:{requested}->{normalized} pref:{preferred} mode:{_lastPreferredDisplayModeId} now:{observed} mode:{_lastObservedDisplayModeId}";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static float NormalizePreferredContentFps(float fps)
    {
        if (fps <= 1.0f)
            return 0f;

        if (Math.Abs(fps - 60f) <= 1.25f || Math.Abs(fps - 59.94f) <= 1.25f)
            return 60f;

        if (Math.Abs(fps - 50f) <= 1.0f)
            return 50f;

        if (Math.Abs(fps - 30f) <= 0.75f || Math.Abs(fps - 29.97f) <= 0.75f)
            return 30f;

        if (Math.Abs(fps - 24f) <= 0.5f || Math.Abs(fps - 23.976f) <= 0.5f)
            return 24f;

        return fps;
    }

    private static void SelectPreferredDisplayMode(Display display, float contentFps, out int modeId, out float refreshRate)
    {
        modeId = 0;
        refreshRate = 0f;

        if (contentFps <= 1.0f)
            return;

        if (Build.VERSION.SdkInt < BuildVersionCodes.M)
        {
            refreshRate = display.RefreshRate;
            return;
        }

        Display.Mode[]? modes = display.GetSupportedModes();
        if (modes == null || modes.Length == 0)
        {
            refreshRate = display.RefreshRate;
            return;
        }

        float bestScore = float.MaxValue;
        foreach (Display.Mode mode in modes)
        {
            float modeRefresh = mode.RefreshRate;
            if (modeRefresh <= 1.0f)
                continue;

            int multiple = Math.Max(1, Math.Min(4, (int)MathF.Round(modeRefresh / contentFps)));
            float cadenceError = MathF.Abs(modeRefresh - (contentFps * multiple));
            float score = cadenceError + ((multiple - 1) * 0.35f);

            if (score >= bestScore)
                continue;

            bestScore = score;
            modeId = mode.ModeId;
            refreshRate = modeRefresh;
        }

        if (refreshRate <= 1.0f)
            refreshRate = display.RefreshRate;
    }

    private void UpdateObservedDisplayMode(Display? display)
    {
        if (display == null)
        {
            _lastObservedDisplayRefresh = 0f;
            _lastObservedDisplayModeId = 0;
            return;
        }

        _lastObservedDisplayRefresh = display.RefreshRate;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
        {
            Display.Mode? mode = display.GetMode();
            _lastObservedDisplayModeId = mode?.ModeId ?? 0;
            if (mode?.RefreshRate > 1.0f)
                _lastObservedDisplayRefresh = mode.RefreshRate;
        }
        else
        {
            _lastObservedDisplayModeId = 0;
        }
    }

    private FrameLayout EnsureNativeVideoContainer()
    {
        if (_nativeVideoContainer != null)
            return _nativeVideoContainer;

        var content = FindViewById(global::Android.Resource.Id.Content) as ViewGroup
            ?? throw new InvalidOperationException("Android content root is unavailable.");

        var container = new FrameLayout(this)
        {
            Clickable = false,
            Focusable = false,
            FocusableInTouchMode = false
        };
        container.SetClipChildren(false);
        container.SetClipToPadding(false);
        container.LayoutParameters = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent);

        content.AddView(container);
        _nativeVideoContainer = container;
        return container;
    }
}
