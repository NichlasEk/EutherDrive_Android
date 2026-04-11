using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Media;

namespace EutherDrive.UI.Skins;

/// <summary>
/// Loads and parses .apa skin files (TOML-like format).
/// </summary>
public class ApaSkinLoader
{
    private static readonly Regex SectionRegex = new(@"^\s*\[([^\]]+)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex KeyValueRegex = new(@"^\s*([^=]+)\s*=\s*(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex ColorRegex = new(@"^#[0-9A-Fa-f]{6,8}$", RegexOptions.Compiled);
    
    public ApaSkin LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Skin file not found: {filePath}");
        
        var content = File.ReadAllText(filePath);
        var skin = Parse(content);
        skin.SourcePath = filePath;
        skin.SkinDirectory = Path.GetDirectoryName(filePath) ?? "";
        return skin;
    }
    
    public ApaSkin LoadFromString(string content)
    {
        return Parse(content);
    }
    
    private ApaSkin Parse(string content)
    {
        var skin = new ApaSkin();
        var lines = content.Split('\n');
        string currentSection = "";
        var gradientStops = new List<GradientStop>();
        SkinGradient? currentGradient = null;
        string currentGradientName = "";
        
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            // Skip empty lines and comments
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                continue;
            
            // Check for section
            var sectionMatch = SectionRegex.Match(line);
            if (sectionMatch.Success)
            {
                // Save any pending gradient
                if (currentGradient != null && !string.IsNullOrEmpty(currentGradientName))
                {
                    SaveGradient(skin, currentGradientName, currentGradient);
                }
                
                currentSection = sectionMatch.Groups[1].Value.Trim();
                gradientStops.Clear();
                currentGradient = null;
                currentGradientName = "";
                continue;
            }
            
            // Parse key-value pair
            var kvMatch = KeyValueRegex.Match(line);
            if (!kvMatch.Success) continue;
            
            var key = kvMatch.Groups[1].Value.Trim();
            var value = kvMatch.Groups[2].Value.Trim();
            
            // Remove quotes from string values
            if (value.StartsWith("\"") && value.EndsWith("\""))
                value = value.Substring(1, value.Length - 2);
            else if (value.StartsWith("'") && value.EndsWith("'"))
                value = value.Substring(1, value.Length - 2);
            
            ParseKeyValue(skin, currentSection, key, value);
        }
        
        // Save final gradient if any
        if (currentGradient != null && !string.IsNullOrEmpty(currentGradientName))
        {
            SaveGradient(skin, currentGradientName, currentGradient);
        }
        
        return skin;
    }
    
    private void ParseKeyValue(ApaSkin skin, string section, string key, string value)
    {
        switch (section.ToLowerInvariant())
        {
            case "skin":
            case "meta":
            case "metadata":
                ParseMetadata(skin, key, value);
                break;
                
            case "colors":
            case "palette":
            case "color":
                ParseColor(skin.Colors, key, value);
                break;
                
            case "typography":
            case "fonts":
            case "font":
            case "text":
                ParseTypography(skin.Typography, key, value);
                break;
                
            case "layout":
            case "spacing":
                ParseLayout(skin.Layout, key, value);
                break;
                
            case "buttons":
            case "button":
                ParseButtons(skin.Buttons, key, value);
                break;
                
            case "panels":
            case "panel":
                ParsePanels(skin.Panels, key, value);
                break;
                
            case "inputs":
            case "input":
            case "controls":
                ParseInputs(skin.Inputs, key, value);
                break;
                
            case "effects":
            case "fx":
                ParseEffects(skin.Effects, key, value);
                break;
                
            case "gradient.hero":
                ParseGradient(skin.Colors.HeroGradient ??= new(), key, value);
                break;
                
            case "gradient.screen_glow":
            case "gradient.screenglow":
            case "gradient.glow":
                ParseGradient(skin.Colors.ScreenGlowGradient ??= new(), key, value);
                break;
                
            case "gradient.background":
            case "gradient.bg":
                ParseGradient(skin.Colors.BackgroundGradient ??= new(), key, value);
                break;
                
            case "style_overrides":
            case "overrides":
            case "custom":
                skin.StyleOverrides[key] = value;
                break;
        }
    }
    
    private void ParseMetadata(ApaSkin skin, string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "name":
            case "skin_name":
                skin.SkinName = value;
                break;
            case "author":
            case "creator":
                skin.Author = value;
                break;
            case "version":
                skin.Version = value;
                break;
            case "description":
            case "desc":
                skin.Description = value;
                break;
            case "thumbnail":
            case "preview":
                skin.ThumbnailPath = value;
                break;
        }
    }
    
    private void ParseColor(SkinColors colors, string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "background":
            case "bg":
                colors.Background = ParseColorValue(value);
                break;
            case "background_soft":
            case "bg_soft":
                colors.BackgroundSoft = ParseColorValue(value);
                break;
            case "background_alt":
            case "bg_alt":
                colors.BackgroundAlt = ParseColorValue(value);
                break;
            case "panel":
                colors.Panel = ParseColorValue(value);
                break;
            case "panel_raised":
                colors.PanelRaised = ParseColorValue(value);
                break;
            case "panel_glass":
            case "glass":
                colors.PanelGlass = ParseColorValue(value);
                break;
            case "sub_panel":
            case "subpanel":
                colors.SubPanel = ParseColorValue(value);
                break;
            case "option_card":
            case "optioncard":
                colors.OptionCard = ParseColorValue(value);
                break;
            case "stroke":
            case "border":
                colors.Stroke = ParseColorValue(value);
                break;
            case "stroke_bright":
            case "border_bright":
            case "bright_stroke":
                colors.StrokeBright = ParseColorValue(value);
                break;
            case "text":
            case "foreground":
                colors.Text = ParseColorValue(value);
                break;
            case "text_muted":
            case "muted":
            case "secondary":
                colors.TextMuted = ParseColorValue(value);
                break;
            case "accent":
            case "primary":
                colors.Accent = ParseColorValue(value);
                break;
            case "accent_warm":
            case "warm":
                colors.AccentWarm = ParseColorValue(value);
                break;
            case "accent_hot":
            case "hot":
            case "danger":
                colors.AccentHot = ParseColorValue(value);
                break;
        }
    }
    
    private void ParseTypography(SkinTypography typo, string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "primary_font":
            case "font":
            case "font_family":
                typo.PrimaryFont = value;
                break;
            case "display_font":
                typo.DisplayFont = value;
                break;
            case "mono_font":
            case "code_font":
                typo.MonoFont = value;
                break;
            case "deck_font":
            case "deck_label_font":
                typo.DeckLabelFont = value;
                break;
            case "base_size":
            case "size":
                typo.BaseSize = ParseDouble(value);
                break;
            case "small_size":
                typo.SmallSize = ParseDouble(value);
                break;
            case "display_size":
                typo.DisplaySize = ParseDouble(value);
                break;
            case "kicker_size":
                typo.KickerSize = ParseDouble(value);
                break;
            case "deck_size":
            case "deck_label_size":
                typo.DeckLabelSize = ParseDouble(value);
                break;
            case "letter_spacing":
            case "spacing":
                typo.LetterSpacing = ParseDouble(value);
                break;
            case "deck_spacing":
                typo.DeckLabelSpacing = ParseDouble(value);
                break;
        }
    }
    
    private void ParseLayout(SkinLayout layout, string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "border_radius":
            case "radius":
                layout.BorderRadiusMedium = ParseDouble(value);
                break;
            case "border_radius_small":
                layout.BorderRadiusSmall = ParseDouble(value);
                break;
            case "border_radius_large":
                layout.BorderRadiusLarge = ParseDouble(value);
                break;
            case "border_radius_xlarge":
            case "border_radius_xl":
                layout.BorderRadiusXLarge = ParseDouble(value);
                break;
            case "border_radius_full":
                layout.BorderRadiusFull = ParseDouble(value);
                break;
            case "padding":
                layout.PanelPadding = ParseDouble(value);
                break;
            case "button_padding_x":
                layout.ButtonPaddingX = ParseDouble(value);
                break;
            case "button_padding_y":
                layout.ButtonPaddingY = ParseDouble(value);
                break;
            case "spacing":
                layout.SpacingMedium = ParseDouble(value);
                break;
            case "spacing_small":
                layout.SpacingSmall = ParseDouble(value);
                break;
            case "spacing_large":
                layout.SpacingLarge = ParseDouble(value);
                break;
            case "shadow_opacity":
                layout.ShadowOpacity = ParseDouble(value);
                break;
            case "shadow_blur":
                layout.ShadowBlur = ParseDouble(value);
                break;
            case "shadow_spread":
                layout.ShadowSpread = ParseDouble(value);
                break;
        }
    }
    
    private void ParseButtons(SkinButtons buttons, string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "background":
            case "bg":
                buttons.Background = ParseColorValue(value);
                break;
            case "background_hover":
            case "hover_bg":
                buttons.BackgroundHover = ParseColorValue(value);
                break;
            case "background_pressed":
            case "pressed_bg":
                buttons.BackgroundPressed = ParseColorValue(value);
                break;
            case "background_action":
            case "action_bg":
                buttons.BackgroundAction = ParseColorValue(value);
                break;
            case "background_action_hover":
                buttons.BackgroundActionHover = ParseColorValue(value);
                break;
            case "background_ghost":
            case "ghost_bg":
                buttons.BackgroundGhost = ParseColorValue(value);
                break;
            case "border":
                buttons.Border = ParseColorValue(value);
                break;
            case "border_hover":
            case "hover_border":
                buttons.BorderHover = ParseColorValue(value);
                break;
            case "border_ghost":
            case "ghost_border":
                buttons.BorderGhost = ParseColorValue(value);
                break;
            case "border_radius":
            case "radius":
                buttons.BorderRadius = ParseDouble(value);
                break;
            case "transition_duration":
            case "duration":
                buttons.TransitionDuration = ParseDouble(value);
                break;
            case "hover_scale":
                buttons.HoverScale = ParseDouble(value);
                break;
            case "pressed_scale":
                buttons.PressedScale = ParseDouble(value);
                break;
        }
    }
    
    private void ParsePanels(SkinPanels panels, string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "glass_background":
            case "glass_bg":
                panels.GlassBackground = ParseColorValue(value);
                break;
            case "glass_opacity":
                panels.GlassOpacity = ParseDouble(value);
                break;
            case "sub_panel_background":
            case "subpanel_background":
            case "subpanel_bg":
                panels.SubPanelBackground = ParseColorValue(value);
                break;
            case "sub_panel_border":
            case "subpanel_border":
                panels.SubPanelBorder = ParseColorValue(value);
                break;
            case "option_card_background":
            case "optioncard_background":
            case "optioncard_bg":
                panels.OptionCardBackground = ParseColorValue(value);
                break;
            case "option_card_border":
            case "optioncardborder":
            case "optioncard_border":
                panels.OptionCardBorder = ParseColorValue(value);
                break;
            case "border_radius":
            case "radius":
                panels.BorderRadius = ParseDouble(value);
                break;
            case "subpanel_radius":
                panels.SubPanelRadius = ParseDouble(value);
                break;
            case "optioncard_radius":
                panels.OptionCardRadius = ParseDouble(value);
                break;
        }
    }
    
    private void ParseInputs(SkinInputs inputs, string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "background":
            case "bg":
                inputs.Background = ParseColorValue(value);
                break;
            case "background_focus":
            case "focus_bg":
                inputs.BackgroundFocus = ParseColorValue(value);
                break;
            case "border":
                inputs.Border = ParseColorValue(value);
                break;
            case "border_focus":
            case "focus_border":
                inputs.BorderFocus = ParseColorValue(value);
                break;
            case "border_radius":
            case "radius":
                inputs.BorderRadius = ParseDouble(value);
                break;
            case "padding":
                inputs.Padding = ParseDouble(value);
                break;
        }
    }
    
    private void ParseEffects(SkinEffects effects, string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "use_transparency":
            case "transparency":
                effects.UseTransparency = ParseBool(value);
                break;
            case "use_blur":
            case "blur":
                effects.UseBlur = ParseBool(value);
                break;
            case "blur_radius":
                effects.BlurRadius = ParseDouble(value);
                break;
            case "use_glow":
            case "glow":
                effects.UseGlow = ParseBool(value);
                break;
            case "glow_color":
                effects.GlowColor = ParseColorValue(value);
                break;
            case "glow_intensity":
            case "intensity":
                effects.GlowIntensity = ParseDouble(value);
                break;
            case "use_animations":
            case "animations":
                effects.UseAnimations = ParseBool(value);
                break;
            case "animation_speed":
            case "speed":
                effects.AnimationSpeed = ParseDouble(value);
                break;
            case "use_background_effects":
            case "background_fx":
                effects.UseBackgroundEffects = ParseBool(value);
                break;
            case "background_color_1":
            case "bg_color_1":
                effects.BackgroundEffectColor1 = ParseColorValue(value);
                break;
            case "background_color_2":
            case "bg_color_2":
                effects.BackgroundEffectColor2 = ParseColorValue(value);
                break;
            case "background_opacity":
            case "bg_opacity":
                effects.BackgroundEffectOpacity = ParseDouble(value);
                break;
        }
    }
    
    private void ParseGradient(SkinGradient gradient, string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "angle":
                gradient.Angle = ParseDouble(value);
                break;
            case "start_point":
            case "start":
                // Format: "0,0" or "0 0"
                break;
            case "end_point":
            case "end":
                // Format: "1,1" or "1 1"
                break;
            default:
                // Parse gradient stops: stop0, stop1, etc.
                if (key.StartsWith("stop"))
                {
                    var parts = value.Split(',');
                    if (parts.Length >= 2)
                    {
                        gradient.Stops.Add(new GradientStop
                        {
                            Color = ParseColorValue(parts[0].Trim()),
                            Offset = ParseDouble(parts[1].Trim())
                        });
                    }
                }
                break;
        }
    }
    
    private void SaveGradient(ApaSkin skin, string name, SkinGradient gradient)
    {
        // Already handled in ParseKeyValue
    }
    
    private string ParseColorValue(string value)
    {
        value = value.Trim();
        
        // Handle hex colors
        if (value.StartsWith("#"))
        {
            // Ensure proper hex format
            if (value.Length == 7 || value.Length == 9)
                return value;
            if (value.Length == 4) // #RGB
                return $"#{value[1]}{value[1]}{value[2]}{value[2]}{value[3]}{value[3]}";
        }
        
        // Handle rgb/rgba
        if (value.StartsWith("rgb(") || value.StartsWith("rgba("))
        {
            // Convert RGB to hex
            var nums = Regex.Matches(value, @"\d+")
                .Cast<Match>()
                .Select(m => int.Parse(m.Value))
                .ToArray();
            
            if (nums.Length >= 3)
            {
                var alpha = nums.Length >= 4 ? nums[3] / 255.0 : 1.0;
                var hex = $"#{alpha:0.00}{nums[0]:X2}{nums[1]:X2}{nums[2]:X2}";
                return hex;
            }
        }
        
        // Handle named colors
        return value;
    }
    
    private double ParseDouble(string value)
    {
        value = value.Trim().ToLowerInvariant();
        
        // Handle percentages
        if (value.EndsWith("%"))
        {
            if (double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                return pct / 100.0;
        }
        
        // Handle px suffix
        if (value.EndsWith("px"))
            value = value.TrimEnd('p', 'x');
        
        // Handle ms/s suffix
        if (value.EndsWith("ms"))
        {
            if (double.TryParse(value.TrimEnd('m', 's'), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
                return ms / 1000.0;
        }
        if (value.EndsWith("s"))
            value = value.TrimEnd('s');
        
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            return result;
        
        return 0;
    }
    
    private bool ParseBool(string value)
    {
        value = value.Trim().ToLowerInvariant();
        return value is "true" or "yes" or "1" or "on" or "enabled";
    }
}
