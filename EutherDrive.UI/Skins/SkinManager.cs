using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaGradientStop = Avalonia.Media.GradientStop;

namespace EutherDrive.UI.Skins;

/// <summary>
/// Manages loading, applying and switching between .apa skins.
/// Provides global access to the current skin and handles dynamic resource updates.
/// </summary>
public class SkinManager
{
    private static SkinManager? _instance;
    public static SkinManager Instance => _instance ??= new SkinManager();
    
    private readonly ApaSkinLoader _loader = new();
    private readonly List<ApaSkin> _loadedSkins = new();
    private ApaSkin _currentSkin = new();
    private Avalonia.Application? _application;
    private ResourceDictionary? _dynamicResources;
    
    public event EventHandler<SkinChangedEventArgs>? SkinChanged;
    
    public ApaSkin CurrentSkin => _currentSkin;
    public IReadOnlyList<ApaSkin> LoadedSkins => _loadedSkins.AsReadOnly();
    
    private SkinManager() { }
    
    /// <summary>
    /// Initialize the skin manager with the application instance.
    /// Call this in App.axaml.cs OnFrameworkInitializationCompleted.
    /// </summary>
    public void Initialize(Avalonia.Application application)
    {
        _application = application;
        
        // Create or get the dynamic resources dictionary
        _dynamicResources = new ResourceDictionary();
        
        // Insert dynamic resources before application resources so they take precedence
        if (application.Resources is ResourceDictionary appResources)
        {
            // We'll merge dynamic resources into app resources
            // But first let's create a merged approach
        }
        
        // Load built-in default skin
        LoadDefaultSkin();
    }
    
    /// <summary>
    /// Load and apply a skin from a file path.
    /// </summary>
    public async Task<bool> LoadSkinAsync(string filePath)
    {
        try
        {
            var skin = await Task.Run(() => TryLoadSkinFromFile(filePath));
            if (skin == null)
                return false;
            return ApplySkin(skin);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SkinManager] Failed to load skin: {ex.Message}");
            return false;
        }
    }

    public bool LoadSkin(string filePath)
    {
        try
        {
            var skin = TryLoadSkinFromFile(filePath);
            if (skin == null)
                return false;
            return ApplySkin(skin);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SkinManager] Failed to load skin: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Apply a loaded skin to the UI.
    /// </summary>
    public bool ApplySkin(ApaSkin skin)
    {
        try
        {
            _currentSkin = skin;
            
            // Update on UI thread
            Dispatcher.UIThread.Post(() =>
            {
                ApplySkinResources(skin);
                SkinChanged?.Invoke(this, new SkinChangedEventArgs(skin));
            });
            
            Console.WriteLine($"[SkinManager] Applied skin: {skin.SkinName} by {skin.Author}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SkinManager] Failed to apply skin: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Reload the current skin from disk.
    /// </summary>
    public async Task<bool> ReloadCurrentSkinAsync()
    {
        if (string.IsNullOrEmpty(_currentSkin.SourcePath))
            return false;
            
        return await LoadSkinAsync(_currentSkin.SourcePath);
    }
    
    /// <summary>
    /// Get all available skins from a directory.
    /// </summary>
    public IEnumerable<ApaSkin> ScanSkinsDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return Array.Empty<ApaSkin>();

        var skins = new List<ApaSkin>();

        foreach (var file in Directory.GetFiles(directoryPath, "*.apa"))
        {
            try
            {
                var skin = _loader.LoadFromFile(file);
                if (skin.IsValid)
                    skins.Add(skin);
            }
            catch { /* Skip invalid skins */ }
        }

        return skins;
    }
    
    /// <summary>
    /// Create the default built-in skin matching the original hardcoded styles.
    /// </summary>
    private void LoadDefaultSkin()
    {
        _currentSkin = CreateDefaultSkin();
        _loadedSkins.Add(_currentSkin);
    }

    private ApaSkin? TryLoadSkinFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Console.WriteLine($"[SkinManager] Skin file not found: {filePath}");
            return null;
        }

        var skin = _loader.LoadFromFile(filePath);
        if (!skin.IsValid)
        {
            Console.WriteLine($"[SkinManager] Invalid skin file: {filePath}");
            return null;
        }

        _loadedSkins.RemoveAll(s => string.Equals(s.SourcePath, filePath, StringComparison.OrdinalIgnoreCase));
        _loadedSkins.Add(skin);
        return skin;
    }
    
    /// <summary>
    /// Apply skin resources to the application.
    /// </summary>
    private void ApplySkinResources(ApaSkin skin)
    {
        if (_application?.Resources is not ResourceDictionary resources)
            return;
        
        var colors = skin.Colors;
        var layout = skin.Layout;
        var buttons = skin.Buttons;
        var panels = skin.Panels;
        var inputs = skin.Inputs;
        var typo = skin.Typography;
        var effects = skin.Effects;
        bool useMetalSurface = TryGetStyleBool(skin, "metal_surface");
        double metalGloss = TryGetStyleDouble(skin, "metal_surface_gloss", 0.72, 0.0, 1.0);
        double metalContrast = TryGetStyleDouble(skin, "metal_surface_contrast", 0.22, 0.0, 0.6);
        Color metalSpecular = TryGetStyleColor(skin, "metal_surface_specular", Color.Parse(colors.Text));
        Color metalShadow = TryGetStyleColor(skin, "metal_surface_shadow", Color.Parse(colors.Background));
        
        // Update Colors
        SetResource(resources, "EdBg", Color.Parse(colors.Background));
        SetResource(resources, "EdBgSoft", Color.Parse(colors.BackgroundSoft));
        SetResource(resources, "EdPanel", Color.Parse(colors.Panel));
        SetResource(resources, "EdPanelRaised", Color.Parse(colors.PanelRaised));
        SetResource(resources, "EdPanelGlass", Color.Parse(colors.PanelGlass));
        SetResource(resources, "EdStroke", Color.Parse(colors.Stroke));
        SetResource(resources, "EdStrokeBright", Color.Parse(colors.StrokeBright));
        SetResource(resources, "EdText", Color.Parse(colors.Text));
        SetResource(resources, "EdTextMuted", Color.Parse(colors.TextMuted));
        SetResource(resources, "EdAccent", Color.Parse(colors.Accent));
        SetResource(resources, "EdAccentWarm", Color.Parse(colors.AccentWarm));
        SetResource(resources, "EdAccentHot", Color.Parse(colors.AccentHot));
        SetResource(resources, "EdButtonBg", Color.Parse(buttons.Background));
        SetResource(resources, "EdButtonBgHover", Color.Parse(buttons.BackgroundHover));
        SetResource(resources, "EdButtonBgPressed", Color.Parse(buttons.BackgroundPressed));
        SetResource(resources, "EdButtonActionBg", Color.Parse(buttons.BackgroundAction));
        SetResource(resources, "EdButtonActionBgHover", Color.Parse(buttons.BackgroundActionHover));
        SetResource(resources, "EdButtonGhostBg", Color.Parse(buttons.BackgroundGhost));
        SetResource(resources, "EdButtonBorder", Color.Parse(buttons.Border));
        SetResource(resources, "EdButtonBorderHover", Color.Parse(buttons.BorderHover));
        SetResource(resources, "EdButtonGhostBorder", Color.Parse(buttons.BorderGhost));
        SetResource(resources, "EdToggleBg", Color.Parse(buttons.Background));
        SetResource(resources, "EdToggleCheckedBg", Color.Parse(buttons.BackgroundAction));
        SetResource(resources, "EdSubPanelBg", Color.Parse(panels.SubPanelBackground));
        SetResource(resources, "EdSubPanelBorder", Color.Parse(panels.SubPanelBorder));
        SetResource(resources, "EdOptionCardBg", Color.Parse(panels.OptionCardBackground));
        SetResource(resources, "EdOptionCardBorder", Color.Parse(panels.OptionCardBorder));
        SetResource(resources, "EdInputBg", Color.Parse(inputs.Background));
        SetResource(resources, "EdInputBgFocus", Color.Parse(inputs.BackgroundFocus));
        SetResource(resources, "EdInputBorder", Color.Parse(inputs.Border));
        SetResource(resources, "EdInputBorderFocus", Color.Parse(inputs.BorderFocus));
        SetResource(resources, "EdBgFx1", Color.Parse(effects.BackgroundEffectColor1));
        SetResource(resources, "EdBgFx2", Color.Parse(effects.BackgroundEffectColor2));
        SetResource(resources, "EdScreenShellBg", Color.Parse(colors.BackgroundAlt));
        SetResource(resources, "EdLogPanelBg", Color.Parse(colors.BackgroundSoft));
        SetResource(resources, "EdLogPanelBorder", Color.Parse(colors.StrokeBright));
        SetResource(resources, "EdLogPanelAccentBg", Color.Parse(panels.SubPanelBackground));
        SetResource(resources, "EdLogPanelAccentBorder", Color.Parse(colors.Accent));
        SetResource(resources, "EdLogPanelText", Color.Parse(colors.Text));
        SetResource(resources, "EdLogPanelAccentText", Color.Parse(colors.Text));

        // Update Brushes
        SetResource(resources, "EdBgBrush", new SolidColorBrush(Color.Parse(colors.Background)));
        SetResource(resources, "EdBgSoftBrush", new SolidColorBrush(Color.Parse(colors.BackgroundSoft)));
        SetResource(resources, "EdPanelBrush", CreateSurfaceBrush(Color.Parse(colors.Panel), metalSpecular, metalShadow, useMetalSurface, Math.Max(0.0, metalGloss - 0.04), metalContrast));
        SetResource(resources, "EdPanelRaisedBrush", CreateSurfaceBrush(Color.Parse(colors.PanelRaised), metalSpecular, metalShadow, useMetalSurface, Math.Min(1.0, metalGloss + 0.02), Math.Min(0.6, metalContrast + 0.02)));
        SetResource(resources, "EdPanelGlassBrush", CreateSurfaceBrush(Color.Parse(colors.PanelGlass), metalSpecular, metalShadow, useMetalSurface, metalGloss, metalContrast));
        SetResource(resources, "EdStrokeBrush", new SolidColorBrush(Color.Parse(colors.Stroke)));
        SetResource(resources, "EdStrokeBrightBrush", new SolidColorBrush(Color.Parse(colors.StrokeBright)));
        SetResource(resources, "EdTextBrush", new SolidColorBrush(Color.Parse(colors.Text)));
        SetResource(resources, "EdTextMutedBrush", new SolidColorBrush(Color.Parse(colors.TextMuted)));
        SetResource(resources, "EdAccentBrush", new SolidColorBrush(Color.Parse(colors.Accent)));
        SetResource(resources, "EdAccentWarmBrush", new SolidColorBrush(Color.Parse(colors.AccentWarm)));
        SetResource(resources, "EdAccentHotBrush", new SolidColorBrush(Color.Parse(colors.AccentHot)));
        SetResource(resources, "EdButtonBgBrush", CreateSurfaceBrush(Color.Parse(buttons.Background), metalSpecular, metalShadow, useMetalSurface, metalGloss, metalContrast));
        SetResource(resources, "EdButtonBgHoverBrush", CreateSurfaceBrush(Color.Parse(buttons.BackgroundHover), metalSpecular, metalShadow, useMetalSurface, Math.Min(1.0, metalGloss + 0.08), Math.Min(0.6, metalContrast + 0.03)));
        SetResource(resources, "EdButtonBgPressedBrush", CreateSurfaceBrush(Color.Parse(buttons.BackgroundPressed), metalSpecular, metalShadow, useMetalSurface, Math.Max(0.0, metalGloss - 0.16), Math.Min(0.6, metalContrast + 0.04)));
        SetResource(resources, "EdButtonActionBgBrush", CreateSurfaceBrush(Color.Parse(buttons.BackgroundAction), metalSpecular, metalShadow, useMetalSurface, Math.Min(1.0, metalGloss + 0.06), Math.Min(0.6, metalContrast + 0.02)));
        SetResource(resources, "EdButtonActionBgHoverBrush", CreateSurfaceBrush(Color.Parse(buttons.BackgroundActionHover), metalSpecular, metalShadow, useMetalSurface, Math.Min(1.0, metalGloss + 0.12), Math.Min(0.6, metalContrast + 0.05)));
        SetResource(resources, "EdButtonGhostBgBrush", CreateSurfaceBrush(Color.Parse(buttons.BackgroundGhost), metalSpecular, metalShadow, useMetalSurface, Math.Max(0.0, metalGloss - 0.08), metalContrast));
        SetResource(resources, "EdButtonBorderBrush", CreateMetalBorderBrush(Color.Parse(buttons.Border), metalSpecular, useMetalSurface));
        SetResource(resources, "EdButtonBorderHoverBrush", CreateMetalBorderBrush(Color.Parse(buttons.BorderHover), metalSpecular, useMetalSurface));
        SetResource(resources, "EdButtonGhostBorderBrush", CreateMetalBorderBrush(Color.Parse(buttons.BorderGhost), metalSpecular, useMetalSurface));
        SetResource(resources, "EdToggleBgBrush", CreateSurfaceBrush(Color.Parse(buttons.Background), metalSpecular, metalShadow, useMetalSurface, metalGloss, metalContrast));
        SetResource(resources, "EdToggleCheckedBgBrush", CreateSurfaceBrush(Color.Parse(buttons.BackgroundAction), metalSpecular, metalShadow, useMetalSurface, Math.Min(1.0, metalGloss + 0.08), Math.Min(0.6, metalContrast + 0.03)));
        SetResource(resources, "EdSubPanelBrush", CreateSurfaceBrush(Color.Parse(panels.SubPanelBackground), metalSpecular, metalShadow, useMetalSurface, Math.Max(0.0, metalGloss - 0.08), metalContrast));
        SetResource(resources, "EdSubPanelBorderBrush", CreateMetalBorderBrush(Color.Parse(panels.SubPanelBorder), metalSpecular, useMetalSurface));
        SetResource(resources, "EdOptionCardBrush", CreateSurfaceBrush(Color.Parse(panels.OptionCardBackground), metalSpecular, metalShadow, useMetalSurface, Math.Max(0.0, metalGloss - 0.03), metalContrast));
        SetResource(resources, "EdOptionCardBorderBrush", CreateMetalBorderBrush(Color.Parse(panels.OptionCardBorder), metalSpecular, useMetalSurface));
        SetResource(resources, "EdInputBgBrush", CreateSurfaceBrush(Color.Parse(inputs.Background), metalSpecular, metalShadow, useMetalSurface, Math.Max(0.0, metalGloss - 0.12), metalContrast));
        SetResource(resources, "EdInputBgFocusBrush", CreateSurfaceBrush(Color.Parse(inputs.BackgroundFocus), metalSpecular, metalShadow, useMetalSurface, metalGloss, Math.Min(0.6, metalContrast + 0.02)));
        SetResource(resources, "EdInputBorderBrush", CreateMetalBorderBrush(Color.Parse(inputs.Border), metalSpecular, useMetalSurface));
        SetResource(resources, "EdInputBorderFocusBrush", CreateMetalBorderBrush(Color.Parse(inputs.BorderFocus), metalSpecular, useMetalSurface));
        SetResource(resources, "EdScreenShellBrush", CreateSurfaceBrush(Color.Parse(colors.BackgroundAlt), metalSpecular, metalShadow, useMetalSurface, Math.Min(1.0, metalGloss + 0.05), metalContrast));
        SetResource(resources, "EdLogPanelBrush", CreateSurfaceBrush(Color.Parse(colors.BackgroundSoft), metalSpecular, metalShadow, useMetalSurface, Math.Max(0.0, metalGloss - 0.1), metalContrast));
        SetResource(resources, "EdLogPanelBorderBrush", CreateMetalBorderBrush(Color.Parse(colors.StrokeBright), metalSpecular, useMetalSurface));
        SetResource(resources, "EdLogPanelAccentBrush", CreateSurfaceBrush(Color.Parse(panels.SubPanelBackground), metalSpecular, metalShadow, useMetalSurface, Math.Max(0.0, metalGloss - 0.04), metalContrast));
        SetResource(resources, "EdLogPanelAccentBorderBrush", new SolidColorBrush(Color.Parse(colors.Accent)));
        SetResource(resources, "EdLogPanelTextBrush", new SolidColorBrush(Color.Parse(colors.Text)));
        SetResource(resources, "EdLogPanelAccentTextBrush", new SolidColorBrush(Color.Parse(colors.Text)));
        SetResource(resources, "EdCornerSmall", new CornerRadius(layout.BorderRadiusSmall));
        SetResource(resources, "EdCornerMedium", new CornerRadius(layout.BorderRadiusMedium));
        SetResource(resources, "EdCornerLarge", new CornerRadius(layout.BorderRadiusLarge));
        SetResource(resources, "EdCornerXLarge", new CornerRadius(layout.BorderRadiusXLarge));
        SetResource(resources, "EdCornerFull", new CornerRadius(layout.BorderRadiusFull));
        SetResource(resources, "EdButtonPadding", new Thickness(layout.ButtonPaddingX, layout.ButtonPaddingY));
        SetResource(resources, "EdButtonCompactPadding", new Thickness(Math.Max(6, layout.ButtonPaddingX - 2), Math.Max(4, layout.ButtonPaddingY - 2)));
        SetResource(resources, "EdButtonSmallPadding", new Thickness(Math.Max(5, layout.ButtonPaddingX - 4), Math.Max(3, layout.ButtonPaddingY - 4)));
        SetResource(resources, "EdInputPadding", new Thickness(inputs.Padding, Math.Max(4, inputs.Padding - 2)));
        SetResource(resources, "EdPanelCornerRadius", new CornerRadius(panels.BorderRadius > 0 ? panels.BorderRadius : layout.BorderRadiusLarge));
        SetResource(resources, "EdSubPanelCornerRadius", new CornerRadius(panels.SubPanelRadius > 0 ? panels.SubPanelRadius : layout.BorderRadiusMedium));
        SetResource(resources, "EdOptionCardCornerRadius", new CornerRadius(panels.OptionCardRadius > 0 ? panels.OptionCardRadius : layout.BorderRadiusMedium));
        SetResource(resources, "EdButtonCornerRadius", new CornerRadius(buttons.BorderRadius > 0 ? buttons.BorderRadius : layout.BorderRadiusMedium));
        SetResource(resources, "EdInputCornerRadius", new CornerRadius(inputs.BorderRadius > 0 ? inputs.BorderRadius : layout.BorderRadiusMedium));
        SetResource(resources, "EdPrimaryFont", new FontFamily(string.IsNullOrWhiteSpace(typo.PrimaryFont) ? "Inter" : typo.PrimaryFont));
        SetResource(resources, "EdDisplayFont", new FontFamily(string.IsNullOrWhiteSpace(typo.DisplayFont) ? "Inter" : typo.DisplayFont));
        SetResource(resources, "EdDeckLabelFont", new FontFamily(string.IsNullOrWhiteSpace(typo.DeckLabelFont) ? "Noto Sans, Inter" : typo.DeckLabelFont));
        SetResource(resources, "EdMonoFont", new FontFamily(string.IsNullOrWhiteSpace(typo.MonoFont) ? "JetBrains Mono, Consolas, Menlo, monospace" : typo.MonoFont));
        SetResource(resources, "EdDeckLabelSize", typo.DeckLabelSize > 0 ? typo.DeckLabelSize : 14.0);
        SetResource(resources, "EdDeckLabelSpacing", typo.DeckLabelSpacing);
        SetResource(resources, "EdKickerSize", typo.KickerSize > 0 ? typo.KickerSize : 11.0);
        SetResource(resources, "EdDisplaySize", typo.DisplaySize > 0 ? typo.DisplaySize : 28.0);
        SetResource(resources, "EdBgFxOpacity", effects.UseBackgroundEffects ? effects.BackgroundEffectOpacity : 0.0);
        SetResource(resources, "EdScreenGlowOpacity", effects.UseGlow ? effects.GlowIntensity : 0.0);

        // Update Gradients
        if (colors.HeroGradient != null)
        {
            SetResource(resources, "EdHeroBrush", CreateGradientBrush(colors.HeroGradient));
        }
        
        if (colors.ScreenGlowGradient != null)
        {
            SetResource(resources, "EdScreenGlowBrush", CreateGradientBrush(colors.ScreenGlowGradient));
        }
        
        // Force style refresh
        RefreshStyles();
    }
    
    private void SetResource(ResourceDictionary resources, string key, object value)
    {
        if (resources.ContainsKey(key))
            resources[key] = value;
        else
            resources.Add(key, value);
    }
    
    private IBrush CreateTransparentBrush(string hexColor)
    {
        // Parse hex with alpha (e.g., #CC162330)
        if (hexColor.Length == 9 && hexColor[0] == '#')
        {
            var alpha = Convert.ToByte(hexColor.Substring(1, 2), 16);
            var r = Convert.ToByte(hexColor.Substring(3, 2), 16);
            var g = Convert.ToByte(hexColor.Substring(5, 2), 16);
            var b = Convert.ToByte(hexColor.Substring(7, 2), 16);
            return new SolidColorBrush(new Color(alpha, r, g, b));
        }
        
        return new SolidColorBrush(Color.Parse(hexColor));
    }

    private static IBrush CreateSurfaceBrush(Color baseColor, Color specularColor, Color shadowColor, bool metallic, double gloss, double contrast)
    {
        if (!metallic)
            return new SolidColorBrush(baseColor);

        Color top = Blend(Lighten(baseColor, 0.22 + (gloss * 0.22)), specularColor, 0.30 + (gloss * 0.22));
        Color upperMid = Blend(Lighten(baseColor, 0.10 + (gloss * 0.08)), specularColor, 0.10 + (gloss * 0.10));
        Color lowerMid = Blend(Darken(baseColor, 1.0 - contrast), shadowColor, 0.12 + (contrast * 0.4));
        Color bottom = Blend(Darken(baseColor, 0.72 - (contrast * 0.18)), shadowColor, 0.24 + (contrast * 0.42));

        byte alpha = baseColor.A;
        top = WithAlpha(top, alpha);
        upperMid = WithAlpha(upperMid, alpha);
        lowerMid = WithAlpha(lowerMid, alpha);
        bottom = WithAlpha(bottom, alpha);

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new AvaloniaGradientStop(top, 0.0),
                new AvaloniaGradientStop(upperMid, 0.18),
                new AvaloniaGradientStop(WithAlpha(Blend(specularColor, baseColor, 0.65), (byte)(alpha * (0.35 + (gloss * 0.2)))), 0.24),
                new AvaloniaGradientStop(lowerMid, 0.56),
                new AvaloniaGradientStop(WithAlpha(Blend(specularColor, baseColor, 0.78), (byte)(alpha * (0.18 + (gloss * 0.12)))), 0.78),
                new AvaloniaGradientStop(bottom, 1.0)
            }
        };
    }

    private static IBrush CreateMetalBorderBrush(Color baseColor, Color specularColor, bool metallic)
    {
        if (!metallic)
            return new SolidColorBrush(baseColor);

        byte alpha = baseColor.A;
        Color top = WithAlpha(Blend(Lighten(baseColor, 0.16), specularColor, 0.28), alpha);
        Color middle = WithAlpha(baseColor, alpha);
        Color bottom = WithAlpha(Darken(baseColor, 0.78), alpha);

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new AvaloniaGradientStop(top, 0.0),
                new AvaloniaGradientStop(middle, 0.48),
                new AvaloniaGradientStop(bottom, 1.0)
            }
        };
    }

    private static bool TryGetStyleBool(ApaSkin skin, string key)
        => skin.StyleOverrides.TryGetValue(key, out string? raw)
            && bool.TryParse(raw, out bool enabled)
            && enabled;

    private static double TryGetStyleDouble(ApaSkin skin, string key, double defaultValue, double min, double max)
    {
        if (skin.StyleOverrides.TryGetValue(key, out string? raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return Math.Clamp(value, min, max);
        }

        return defaultValue;
    }

    private static Color TryGetStyleColor(ApaSkin skin, string key, Color fallback)
    {
        if (skin.StyleOverrides.TryGetValue(key, out string? raw))
        {
            try
            {
                return Color.Parse(raw);
            }
            catch
            {
            }
        }

        return fallback;
    }

    private static Color Lighten(Color color, double amount)
        => Color.FromArgb(
            color.A,
            (byte)Math.Clamp((int)Math.Round(color.R + ((255 - color.R) * amount)), 0, 255),
            (byte)Math.Clamp((int)Math.Round(color.G + ((255 - color.G) * amount)), 0, 255),
            (byte)Math.Clamp((int)Math.Round(color.B + ((255 - color.B) * amount)), 0, 255));

    private static Color Darken(Color color, double factor)
        => Color.FromArgb(
            color.A,
            (byte)Math.Clamp((int)Math.Round(color.R * factor), 0, 255),
            (byte)Math.Clamp((int)Math.Round(color.G * factor), 0, 255),
            (byte)Math.Clamp((int)Math.Round(color.B * factor), 0, 255));

    private static Color Blend(Color a, Color b, double amount)
    {
        double t = Math.Clamp(amount, 0.0, 1.0);
        return Color.FromArgb(
            (byte)Math.Clamp((int)Math.Round(a.A + ((b.A - a.A) * t)), 0, 255),
            (byte)Math.Clamp((int)Math.Round(a.R + ((b.R - a.R) * t)), 0, 255),
            (byte)Math.Clamp((int)Math.Round(a.G + ((b.G - a.G) * t)), 0, 255),
            (byte)Math.Clamp((int)Math.Round(a.B + ((b.B - a.B) * t)), 0, 255));
    }

    private static Color WithAlpha(Color color, byte alpha)
        => Color.FromArgb(alpha, color.R, color.G, color.B);
    
    private LinearGradientBrush CreateGradientBrush(SkinGradient gradient)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative)
        };
        
        foreach (var stop in gradient.Stops.OrderBy(s => s.Offset))
        {
            brush.GradientStops.Add(new AvaloniaGradientStop
            {
                Color = Color.Parse(stop.Color),
                Offset = stop.Offset
            });
        }
        
        return brush;
    }
    
    private void RefreshStyles()
    {
        // Trigger style refresh by notifying of theme change
        if (_application is { } app)
        {
            // Force resource reload
            var currentTheme = app.RequestedThemeVariant;
            app.RequestedThemeVariant = null;
            app.RequestedThemeVariant = currentTheme;
        }
    }
    
    /// <summary>
    /// Create the default skin that matches the original hardcoded styling.
    /// </summary>
    public static ApaSkin CreateDefaultSkin()
    {
        return new ApaSkin
        {
            SkinName = "EutherDrive Default",
            Author = "EutherDrive Team",
            Version = "1.0",
            Description = "The classic EutherDrive dark theme with teal accents",
            
            Colors = new SkinColors
            {
                Background = "#091119",
                BackgroundSoft = "#0F1923",
                BackgroundAlt = "#0A0F14",
                Panel = "#121C27",
                PanelRaised = "#172433",
                PanelGlass = "#CC162330",
                SubPanel = "#B8142230",
                OptionCard = "#D0132030",
                Stroke = "#324356",
                StrokeBright = "#4E6B86",
                Text = "#EEF6FF",
                TextMuted = "#91A8BD",
                Accent = "#5EEAD4",
                AccentWarm = "#F59E0B",
                AccentHot = "#FB7185",
                HeroGradient = new SkinGradient
                {
                    Angle = 45,
                    Stops = new List<GradientStop>
                    {
                        new() { Color = "#183249", Offset = 0.0 },
                        new() { Color = "#111F2D", Offset = 0.42 },
                        new() { Color = "#271A2A", Offset = 1.0 }
                    }
                },
                ScreenGlowGradient = new SkinGradient
                {
                    Angle = 45,
                    Stops = new List<GradientStop>
                    {
                        new() { Color = "#6626C6DA", Offset = 0.0 },
                        new() { Color = "#0026C6DA", Offset = 0.7 },
                        new() { Color = "#44F59E0B", Offset = 1.0 }
                    }
                }
            },
            
            Typography = new SkinTypography
            {
                PrimaryFont = "Inter",
                DisplayFont = "Inter",
                MonoFont = "JetBrains Mono, Consolas, Menlo, monospace",
                DeckLabelFont = "Noto Sans, Inter",
                BaseSize = 14,
                SmallSize = 11,
                DisplaySize = 28,
                KickerSize = 11,
                DeckLabelSize = 14,
                LetterSpacing = 0.4,
                DeckLabelSpacing = 1.1
            },
            
            Layout = new SkinLayout
            {
                BorderRadiusSmall = 10,
                BorderRadiusMedium = 12,
                BorderRadiusLarge = 18,
                BorderRadiusXLarge = 22,
                BorderRadiusFull = 999,
                PanelPadding = 10,
                ButtonPaddingX = 12,
                ButtonPaddingY = 8,
                SpacingSmall = 4,
                SpacingMedium = 6,
                SpacingLarge = 12,
                ShadowOpacity = 0.27,
                ShadowBlur = 50,
                ShadowSpread = 18
            },
            
            Buttons = new SkinButtons
            {
                Background = "#172433",
                BackgroundHover = "#1D3143",
                BackgroundPressed = "#12202C",
                BackgroundAction = "#163B41",
                BackgroundActionHover = "#1A4B53",
                BackgroundGhost = "#22131D",
                Border = "#324356",
                BorderHover = "#5EEAD4",
                BorderGhost = "#6E4758",
                BorderRadius = 12,
                TransitionDuration = 0.18,
                HoverScale = 1.025,
                PressedScale = 0.985
            },
            
            Panels = new SkinPanels
            {
                GlassBackground = "#CC162330",
                GlassOpacity = 0.8,
                SubPanelBackground = "#B8142230",
                SubPanelBorder = "#35506A",
                OptionCardBackground = "#D0132030",
                OptionCardBorder = "#304355",
                BorderRadius = 18,
                SubPanelRadius = 14,
                OptionCardRadius = 12
            },
            
            Inputs = new SkinInputs
            {
                Background = "#101B26",
                BackgroundFocus = "#0E1822",
                Border = "#324356",
                BorderFocus = "#5EEAD4",
                BorderRadius = 12,
                Padding = 8
            },
            
            Effects = new SkinEffects
            {
                UseTransparency = true,
                UseBlur = false,
                BlurRadius = 10,
                UseGlow = true,
                GlowColor = "#5EEAD4",
                GlowIntensity = 0.28,
                UseAnimations = true,
                AnimationSpeed = 1.0,
                UseBackgroundEffects = true,
                BackgroundEffectColor1 = "#182A3D",
                BackgroundEffectColor2 = "#2A1B24",
                BackgroundEffectOpacity = 0.18
            }
        };
    }
}

public class SkinChangedEventArgs : EventArgs
{
    public ApaSkin NewSkin { get; }
    
    public SkinChangedEventArgs(ApaSkin skin)
    {
        NewSkin = skin;
    }
}
