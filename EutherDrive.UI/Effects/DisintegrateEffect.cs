using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace EutherDrive.UI.Effects;

public sealed class DisintegrateEffect : IUiEffect
{
    private readonly Random _random = new();

    public double Intensity { get; set; } = 1.0;
    public double FragmentSize { get; set; } = 0.7;
    public int MinDurationMs { get; set; } = 600;
    public int MaxDurationMs { get; set; } = 900;

    public async Task Run(Control root)
    {
        List<FragmentState> fragments = CollectFragments(root);
        if (fragments.Count == 0)
            return;

        int outwardDuration = _random.Next(MinDurationMs, MaxDurationMs + 1);
        int returnDuration = Math.Max(260, outwardDuration / 2);

        await AnimatePhaseAsync(
            outwardDuration,
            fragments,
            progress =>
            {
                foreach (FragmentState fragment in fragments)
                {
                    double staggered = NormalizeStagger(progress, fragment.DelayMs / (double)outwardDuration);
                    ApplyFragmentState(fragment, staggered, reassemble: false);
                }
            }).ConfigureAwait(false);

        await Task.Delay(45).ConfigureAwait(false);

        await AnimatePhaseAsync(
            returnDuration,
            fragments,
            progress =>
            {
                foreach (FragmentState fragment in fragments)
                    ApplyFragmentState(fragment, progress, reassemble: true);
            }).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (FragmentState fragment in fragments)
                ResetFragment(fragment);
        }, DispatcherPriority.Render);
    }

    private List<FragmentState> CollectFragments(Control root)
    {
        IEnumerable<Control> controls = FragmentSize >= 0.55
            ? GetTopLevelFragments(root)
            : GetDetailedFragments(root);

        var fragments = new List<FragmentState>();
        double intensity = Math.Clamp(Intensity, 0.0, 1.0);

        foreach (Control control in controls.Distinct())
        {
            if (!control.IsVisible || control.Bounds.Width < 6 || control.Bounds.Height < 6)
                continue;

            double distanceScale = 18 + (70 * intensity);
            double offsetX = ((_random.NextDouble() * 2.0) - 1.0) * distanceScale;
            double offsetY = ((_random.NextDouble() * 2.0) - 1.0) * distanceScale;
            double rotation = ((_random.NextDouble() * 2.0) - 1.0) * (6.0 + (10.0 * intensity));

            fragments.Add(new FragmentState(
                control,
                control.Opacity,
                control.RenderTransform,
                offsetX,
                offsetY,
                rotation,
                _random.Next(0, 201)));
        }

        return fragments;
    }

    private static IEnumerable<Control> GetTopLevelFragments(Control root)
    {
        if (root is Panel panel)
            return panel.Children.OfType<Control>();

        return EnumerateChildControls(root);
    }

    private static IEnumerable<Control> GetDetailedFragments(Control root)
    {
        var result = new List<Control>();
        CollectDetailed(root, result, includeSelf: false);
        return result;
    }

    private static void CollectDetailed(Control control, List<Control> result, bool includeSelf)
    {
        if (includeSelf)
            result.Add(control);

        bool addedChild = false;
        foreach (Control child in EnumerateChildControls(control))
        {
            addedChild = true;
            CollectDetailed(child, result, includeSelf: true);
        }

        if (!addedChild && includeSelf)
            return;
    }

    private static IEnumerable<Control> EnumerateChildControls(Control control)
    {
        if (control is Panel panel)
        {
            foreach (Control panelChild in panel.Children.OfType<Control>())
                yield return panelChild;
        }

        if (control is Decorator { Child: Control decoratorChild })
            yield return decoratorChild;

        if (control is ContentControl { Content: Control content })
            yield return content;

        if (control is ScrollViewer { Content: Control scrollContent })
            yield return scrollContent;
    }

    private static async Task AnimatePhaseAsync(int durationMs, List<FragmentState> fragments, Action<double> apply)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (true)
        {
            double progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / durationMs, 0.0, 1.0);
            await Dispatcher.UIThread.InvokeAsync(() => apply(progress), DispatcherPriority.Render);
            if (progress >= 1.0)
                break;
            await Task.Delay(16).ConfigureAwait(false);
        }
    }

    private static double NormalizeStagger(double progress, double delayFraction)
    {
        if (progress <= delayFraction)
            return 0.0;

        double normalized = (progress - delayFraction) / Math.Max(0.01, 1.0 - delayFraction);
        return Math.Clamp(normalized, 0.0, 1.0);
    }

    private static void ApplyFragmentState(FragmentState fragment, double progress, bool reassemble)
    {
        double eased = EaseOutCubic(progress);
        double translateFactor = reassemble ? 1.0 - eased : eased;
        double opacityFactor = reassemble ? (0.08 + (0.92 * eased)) : (1.0 - (0.96 * eased));
        double angle = fragment.RotationDegrees * translateFactor;

        var group = new TransformGroup();
        if (fragment.OriginalTransform is Transform originalTransform)
            group.Children.Add(originalTransform);
        group.Children.Add(new RotateTransform(angle));
        group.Children.Add(new TranslateTransform(fragment.OffsetX * translateFactor, fragment.OffsetY * translateFactor));

        fragment.Control.RenderTransform = group;
        fragment.Control.Opacity = Math.Clamp(fragment.OriginalOpacity * opacityFactor, 0.0, fragment.OriginalOpacity);
    }

    private static void ResetFragment(FragmentState fragment)
    {
        fragment.Control.RenderTransform = fragment.OriginalTransform;
        fragment.Control.Opacity = fragment.OriginalOpacity;
    }

    private static double EaseOutCubic(double value)
    {
        double t = Math.Clamp(value, 0.0, 1.0);
        return 1.0 - Math.Pow(1.0 - t, 3.0);
    }

    private sealed record FragmentState(
        Control Control,
        double OriginalOpacity,
        ITransform? OriginalTransform,
        double OffsetX,
        double OffsetY,
        double RotationDegrees,
        int DelayMs);
}
