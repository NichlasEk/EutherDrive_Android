using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace EutherDrive.UI.Skins;

/// <summary>
/// Represents a complete .apa skin for EutherDrive UI.
/// APAs are TOML-based skin files that can customize colors, fonts, 
/// transparency, button styles, and more.
/// </summary>
public class ApaSkin
{
    public string SkinName { get; set; } = "Unnamed Skin";
    public string Author { get; set; } = "Unknown";
    public string Version { get; set; } = "1.0";
    public string Description { get; set; } = "";
    public string ThumbnailPath { get; set; } = "";
    
    /// <summary>
    /// The file path this skin was loaded from
    /// </summary>
    public string SourcePath { get; set; } = "";
    
    /// <summary>
    /// Directory containing the skin file (for relative asset paths)
    /// </summary>
    public string SkinDirectory { get; set; } = "";
    
    // Color palette
    public SkinColors Colors { get; set; } = new();
    
    // Typography
    public SkinTypography Typography { get; set; } = new();
    
    // Layout & Spacing
    public SkinLayout Layout { get; set; } = new();
    
    // Component styles
    public SkinButtons Buttons { get; set; } = new();
    public SkinPanels Panels { get; set; } = new();
    public SkinInputs Inputs { get; set; } = new();
    
    // Effects & Transparency
    public SkinEffects Effects { get; set; } = new();
    
    // Custom CSS-like style overrides
    public Dictionary<string, string> StyleOverrides { get; set; } = new();
    
    public bool IsValid => !string.IsNullOrWhiteSpace(SkinName);
}

public class SkinColors
{
    // Background colors
    public string Background { get; set; } = "#091119";
    public string BackgroundSoft { get; set; } = "#0F1923";
    public string BackgroundAlt { get; set; } = "#0A0F14";
    
    // Panel colors
    public string Panel { get; set; } = "#121C27";
    public string PanelRaised { get; set; } = "#172433";
    public string PanelGlass { get; set; } = "#CC162330";
    public string SubPanel { get; set; } = "#B8142230";
    public string OptionCard { get; set; } = "#D0132030";
    
    // Border/Stroke colors
    public string Stroke { get; set; } = "#324356";
    public string StrokeBright { get; set; } = "#4E6B86";
    
    // Text colors
    public string Text { get; set; } = "#EEF6FF";
    public string TextMuted { get; set; } = "#91A8BD";
    
    // Accent colors
    public string Accent { get; set; } = "#5EEAD4";
    public string AccentWarm { get; set; } = "#F59E0B";
    public string AccentHot { get; set; } = "#FB7185";
    
    // Gradient definitions
    public SkinGradient? HeroGradient { get; set; }
    public SkinGradient? ScreenGlowGradient { get; set; }
    public SkinGradient? BackgroundGradient { get; set; }
}

public class SkinGradient
{
    public double Angle { get; set; } = 45;
    public List<GradientStop> Stops { get; set; } = new();
}

public class GradientStop
{
    public string Color { get; set; } = "#000000";
    public double Offset { get; set; } = 0;
}

public class SkinTypography
{
    public string PrimaryFont { get; set; } = "Inter";
    public string DisplayFont { get; set; } = "Inter";
    public string MonoFont { get; set; } = "JetBrains Mono, Consolas, Menlo, monospace";
    public string DeckLabelFont { get; set; } = "Noto Sans, Inter";
    
    public double BaseSize { get; set; } = 14;
    public double SmallSize { get; set; } = 11;
    public double DisplaySize { get; set; } = 28;
    public double KickerSize { get; set; } = 11;
    public double DeckLabelSize { get; set; } = 14;
    
    public double LetterSpacing { get; set; } = 0.4;
    public double DeckLabelSpacing { get; set; } = 1.1;
}

public class SkinLayout
{
    public double BorderRadiusSmall { get; set; } = 10;
    public double BorderRadiusMedium { get; set; } = 12;
    public double BorderRadiusLarge { get; set; } = 18;
    public double BorderRadiusXLarge { get; set; } = 22;
    public double BorderRadiusFull { get; set; } = 999;
    
    public double PanelPadding { get; set; } = 10;
    public double ButtonPaddingX { get; set; } = 12;
    public double ButtonPaddingY { get; set; } = 8;
    
    public double SpacingSmall { get; set; } = 4;
    public double SpacingMedium { get; set; } = 6;
    public double SpacingLarge { get; set; } = 12;
    
    public double ShadowOpacity { get; set; } = 0.27;
    public double ShadowBlur { get; set; } = 50;
    public double ShadowSpread { get; set; } = 18;
}

public class SkinButtons
{
    public string Background { get; set; } = "#172433";
    public string BackgroundHover { get; set; } = "#1D3143";
    public string BackgroundPressed { get; set; } = "#12202C";
    public string BackgroundAction { get; set; } = "#163B41";
    public string BackgroundActionHover { get; set; } = "#1A4B53";
    public string BackgroundGhost { get; set; } = "#22131D";
    
    public string Border { get; set; } = "#324356";
    public string BorderHover { get; set; } = "#5EEAD4";
    public string BorderGhost { get; set; } = "#6E4758";
    
    public double BorderRadius { get; set; } = 12;
    public double TransitionDuration { get; set; } = 0.18;
    public double HoverScale { get; set; } = 1.025;
    public double PressedScale { get; set; } = 0.985;
}

public class SkinPanels
{
    public string GlassBackground { get; set; } = "#CC162330";
    public double GlassOpacity { get; set; } = 0.8;
    public string SubPanelBackground { get; set; } = "#B8142230";
    public string SubPanelBorder { get; set; } = "#35506A";
    public string OptionCardBackground { get; set; } = "#D0132030";
    public string OptionCardBorder { get; set; } = "#304355";
    
    public double BorderRadius { get; set; } = 18;
    public double SubPanelRadius { get; set; } = 14;
    public double OptionCardRadius { get; set; } = 12;
}

public class SkinInputs
{
    public string Background { get; set; } = "#101B26";
    public string BackgroundFocus { get; set; } = "#0E1822";
    public string Border { get; set; } = "#324356";
    public string BorderFocus { get; set; } = "#5EEAD4";
    
    public double BorderRadius { get; set; } = 12;
    public double Padding { get; set; } = 8;
}

public class SkinEffects
{
    public bool UseTransparency { get; set; } = true;
    public bool UseBlur { get; set; } = false;
    public double BlurRadius { get; set; } = 10;
    
    public bool UseGlow { get; set; } = true;
    public string GlowColor { get; set; } = "#5EEAD4";
    public double GlowIntensity { get; set; } = 0.28;
    
    public bool UseAnimations { get; set; } = true;
    public double AnimationSpeed { get; set; } = 1.0;
    
    public bool UseBackgroundEffects { get; set; } = true;
    public string BackgroundEffectColor1 { get; set; } = "#182A3D";
    public string BackgroundEffectColor2 { get; set; } = "#2A1B24";
    public double BackgroundEffectOpacity { get; set; } = 0.18;
}
