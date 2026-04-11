using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private Application? _application;
    private ResourceDictionary? _dynamicResources;
    
    public event EventHandler<SkinChangedEventArgs>? SkinChanged;
    
    public ApaSkin CurrentSkin => _currentSkin;
    public IReadOnlyList<ApaSkin> LoadedSkins => _loadedSkins.AsReadOnly();
    
    private SkinManager() { }
    
    /// <summary>
    /// Initialize the skin manager with the application instance.
    /// Call this in App.axaml.cs OnFrameworkInitializationCompleted.
    /// </summary>
    public void Initialize(Application application)
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
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"[SkinManager] Skin file not found: {filePath}");
                return false;
            }
            
            var skin = await Task.Run(() => _loader.LoadFromFile(filePath));
            
            if (!skin.IsValid)
            {
                Console.WriteLine($"[SkinManager] Invalid skin file: {filePath}");
                return false;
            }
            
            // Remove previous skin from loaded list if same path
            _loadedSkins.RemoveAll(s => s.SourcePath == filePath);
            _loadedSkins.Add(skin);
            
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

        // Update Brushes
        SetResource(resources, "EdBgBrush", new SolidColorBrush(Color.Parse(colors.Background)));
        SetResource(resources, "EdBgSoftBrush", new SolidColorBrush(Color.Parse(colors.BackgroundSoft)));
        SetResource(resources, "EdPanelBrush", new SolidColorBrush(Color.Parse(colors.Panel)));
        SetResource(resources, "EdPanelRaisedBrush", new SolidColorBrush(Color.Parse(colors.PanelRaised)));
        SetResource(resources, "EdPanelGlassBrush", CreateTransparentBrush(colors.PanelGlass));
        SetResource(resources, "EdStrokeBrush", new SolidColorBrush(Color.Parse(colors.Stroke)));
        SetResource(resources, "EdStrokeBrightBrush", new SolidColorBrush(Color.Parse(colors.StrokeBright)));
        SetResource(resources, "EdTextBrush", new SolidColorBrush(Color.Parse(colors.Text)));
        SetResource(resources, "EdTextMutedBrush", new SolidColorBrush(Color.Parse(colors.TextMuted)));
        SetResource(resources, "EdAccentBrush", new SolidColorBrush(Color.Parse(colors.Accent)));
        SetResource(resources, "EdAccentWarmBrush", new SolidColorBrush(Color.Parse(colors.AccentWarm)));
        SetResource(resources, "EdAccentHotBrush", new SolidColorBrush(Color.Parse(colors.AccentHot)));
        SetResource(resources, "EdButtonPadding", new Thickness(layout.ButtonPaddingX, layout.ButtonPaddingY));
        SetResource(resources, "EdPanelCornerRadius", new CornerRadius(panels.BorderRadius > 0 ? panels.BorderRadius : layout.BorderRadiusLarge));
        SetResource(resources, "EdSubPanelCornerRadius", new CornerRadius(panels.SubPanelRadius > 0 ? panels.SubPanelRadius : layout.BorderRadiusMedium));
        SetResource(resources, "EdOptionCardCornerRadius", new CornerRadius(panels.OptionCardRadius > 0 ? panels.OptionCardRadius : layout.BorderRadiusMedium));
        SetResource(resources, "EdButtonCornerRadius", new CornerRadius(buttons.BorderRadius > 0 ? buttons.BorderRadius : layout.BorderRadiusMedium));
        SetResource(resources, "EdInputCornerRadius", new CornerRadius(inputs.BorderRadius > 0 ? inputs.BorderRadius : layout.BorderRadiusMedium));
        SetResource(resources, "EdPrimaryFont", new FontFamily(string.IsNullOrWhiteSpace(typo.PrimaryFont) ? "Inter" : typo.PrimaryFont));
        SetResource(resources, "EdDisplayFont", new FontFamily(string.IsNullOrWhiteSpace(typo.DisplayFont) ? "Inter" : typo.DisplayFont));
        SetResource(resources, "EdDeckLabelFont", new FontFamily(string.IsNullOrWhiteSpace(typo.DeckLabelFont) ? "Noto Sans, Inter" : typo.DeckLabelFont));
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
        if (_application is App app)
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
