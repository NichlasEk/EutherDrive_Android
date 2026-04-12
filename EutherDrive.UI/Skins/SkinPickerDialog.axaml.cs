using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace EutherDrive.UI.Skins;

public partial class SkinPickerDialog : Window
{
    private ApaSkin? _selectedSkin;
    private readonly SkinManager _skinManager;
    
    public ApaSkin? SelectedSkin => _selectedSkin;
    
    public SkinPickerDialog()
    {
        InitializeComponent();
        _skinManager = SkinManager.Instance;
        LoadSkinsAsync();
    }
    
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
    
    private void LoadSkinsAsync()
    {
        var container = this.FindControl<StackPanel>("SkinsContainer");
        if (container == null) return;
        
        container.Children.Clear();
        
        // Add built-in default skin
        ApaSkin builtInDefaultSkin = SkinManager.CreateDefaultSkin();
        AddSkinCard(container, builtInDefaultSkin, true);
        
        // Scan for skins in the skins directory
        var skinsDir = GetSkinsDirectory();
        if (Directory.Exists(skinsDir))
        {
            foreach (var skin in _skinManager.ScanSkinsDirectory(skinsDir))
            {
                if (IsDuplicateOfBuiltInDefault(skin, builtInDefaultSkin))
                    continue;

                AddSkinCard(container, skin, false);
            }
        }
        
        // Add currently loaded skins
        foreach (var skin in _skinManager.LoadedSkins.Where(s => !string.IsNullOrEmpty(s.SourcePath)))
        {
            if (IsDuplicateOfBuiltInDefault(skin, builtInDefaultSkin))
                continue;

            // Check if already added
            bool alreadyAdded = container.Children.OfType<Border>().Any(b => 
            {
                if (b.Tag is ApaSkin s)
                    return s.SourcePath == skin.SourcePath;
                return false;
            });
            
            if (!alreadyAdded)
            {
                AddSkinCard(container, skin, false);
            }
        }
    }
    
    private void AddSkinCard(StackPanel container, ApaSkin skin, bool isBuiltIn)
    {
        bool isActive = IsActiveSkinCard(skin, isBuiltIn);

        IBrush? activeBackground = TryGetBrush("EdToggleCheckedBg");
        IBrush? activeBorder = TryGetBrush("EdAccentBrush");
        IBrush? defaultBackground = TryGetBrush("EdOptionCardBrush");
        IBrush? defaultBorder = TryGetBrush("EdOptionCardBorderBrush");
        IBrush? strongText = TryGetBrush("EdTextBrush");
        IBrush? mutedText = TryGetBrush("EdTextMutedBrush");
        IBrush? accentText = TryGetBrush("EdAccentBrush");
        IBrush? faintText = TryGetBrush("EdStrokeBrightBrush");
        
        var card = new Border
        {
            Classes = { "option-card" },
            Padding = new Thickness(12),
            Tag = skin,
            Background = isActive ? activeBackground : defaultBackground,
            BorderBrush = isActive ? activeBorder : defaultBorder,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        
        // Thumbnail or icon
        var icon = new TextBlock
        {
            Text = isBuiltIn ? "🎨" : "📄",
            FontSize = 32,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);
        
        // Info
        var infoStack = new StackPanel { Spacing = 2 };
        
        var nameRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        nameRow.Children.Add(new TextBlock 
        { 
            Text = skin.SkinName, 
            FontWeight = FontWeight.Bold,
            FontSize = 15,
            Foreground = strongText
        });
        
        if (isActive)
        {
            nameRow.Children.Add(new TextBlock
            {
                Text = "✓ ACTIVE",
                Foreground = accentText,
                FontSize = 11,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
        }
        
        if (isBuiltIn)
        {
            nameRow.Children.Add(new TextBlock
            {
                Text = "Built-in",
                Foreground = mutedText,
                FontSize = 11,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                FontStyle = FontStyle.Italic
            });
        }
        
        infoStack.Children.Add(nameRow);
        
        if (!string.IsNullOrEmpty(skin.Author))
        {
            infoStack.Children.Add(new TextBlock
            {
                Text = $"by {skin.Author}",
                Foreground = mutedText,
                FontSize = 12
            });
        }
        
        if (!string.IsNullOrEmpty(skin.Description))
        {
            infoStack.Children.Add(new TextBlock
            {
                Text = skin.Description,
                Foreground = mutedText,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 400
            });
        }
        
        var versionText = $"v{skin.Version}";
        if (!string.IsNullOrEmpty(skin.SourcePath))
        {
            versionText += $" • {Path.GetFileName(skin.SourcePath)}";
        }
        infoStack.Children.Add(new TextBlock
        {
            Text = versionText,
            Foreground = faintText,
            FontSize = 10,
            Margin = new Thickness(0, 4, 0, 0)
        });
        
        Grid.SetColumn(infoStack, 1);
        grid.Children.Add(infoStack);
        
        // Preview colors
        var previewGrid = new Grid { Width = 60, Height = 40 };
        previewGrid.RowDefinitions.Add(new RowDefinition());
        previewGrid.RowDefinitions.Add(new RowDefinition());
        previewGrid.ColumnDefinitions.Add(new ColumnDefinition());
        previewGrid.ColumnDefinitions.Add(new ColumnDefinition());
        
        try
        {
            var bg = new Border { Background = new SolidColorBrush(Color.Parse(skin.Colors.Background)) };
            var panel = new Border { Background = new SolidColorBrush(Color.Parse(skin.Colors.Panel)) };
            var accent = new Border { Background = new SolidColorBrush(Color.Parse(skin.Colors.Accent)) };
            var text = new Border { Background = new SolidColorBrush(Color.Parse(skin.Colors.Text)) };
            
            Grid.SetRow(bg, 0); Grid.SetColumn(bg, 0);
            Grid.SetRow(panel, 0); Grid.SetColumn(panel, 1);
            Grid.SetRow(accent, 1); Grid.SetColumn(accent, 0);
            Grid.SetRow(text, 1); Grid.SetColumn(text, 1);
            
            previewGrid.Children.Add(bg);
            previewGrid.Children.Add(panel);
            previewGrid.Children.Add(accent);
            previewGrid.Children.Add(text);
        }
        catch { /* Invalid colors, skip preview */ }
        
        Grid.SetColumn(previewGrid, 2);
        grid.Children.Add(previewGrid);
        
        card.Child = grid;
        
        // Click handler
        card.PointerPressed += (s, e) =>
        {
            // Deselect all
            foreach (var child in container.Children.OfType<Border>())
            {
                child.Background = defaultBackground;
                child.BorderBrush = defaultBorder;
            }
            
            // Select this one
            card.Background = activeBackground;
            card.BorderBrush = activeBorder;
            
            _selectedSkin = skin;
            
            var applyButton = this.FindControl<Button>("ApplyButton");
            if (applyButton != null)
                applyButton.IsEnabled = true;
        };
        
        container.Children.Add(card);
    }

    private bool IsActiveSkinCard(ApaSkin skin, bool isBuiltIn)
    {
        ApaSkin currentSkin = _skinManager.CurrentSkin;
        bool currentIsBuiltIn = string.IsNullOrWhiteSpace(currentSkin.SourcePath);

        if (isBuiltIn)
        {
            return currentIsBuiltIn
                && string.Equals(currentSkin.SkinName, skin.SkinName, StringComparison.Ordinal);
        }

        if (string.IsNullOrWhiteSpace(skin.SourcePath) || currentIsBuiltIn)
            return false;

        return string.Equals(currentSkin.SourcePath, skin.SourcePath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDuplicateOfBuiltInDefault(ApaSkin candidate, ApaSkin builtInDefaultSkin)
    {
        return !string.IsNullOrWhiteSpace(candidate.SourcePath)
            && AreEquivalentSkins(candidate, builtInDefaultSkin);
    }

    private static bool AreEquivalentSkins(ApaSkin left, ApaSkin right)
    {
        return EqualText(left.SkinName, right.SkinName)
            && EqualText(left.Author, right.Author)
            && EqualText(left.Version, right.Version)
            && EqualText(left.Description, right.Description)
            && EqualText(left.ThumbnailPath, right.ThumbnailPath)
            && AreEquivalentColors(left.Colors, right.Colors)
            && AreEquivalentTypography(left.Typography, right.Typography)
            && AreEquivalentLayout(left.Layout, right.Layout)
            && AreEquivalentButtons(left.Buttons, right.Buttons)
            && AreEquivalentPanels(left.Panels, right.Panels)
            && AreEquivalentInputs(left.Inputs, right.Inputs)
            && AreEquivalentEffects(left.Effects, right.Effects)
            && AreEquivalentStyleOverrides(left.StyleOverrides, right.StyleOverrides);
    }

    private static bool AreEquivalentColors(SkinColors left, SkinColors right)
    {
        return EqualText(left.Background, right.Background)
            && EqualText(left.BackgroundSoft, right.BackgroundSoft)
            && EqualText(left.BackgroundAlt, right.BackgroundAlt)
            && EqualText(left.Panel, right.Panel)
            && EqualText(left.PanelRaised, right.PanelRaised)
            && EqualText(left.PanelGlass, right.PanelGlass)
            && EqualText(left.SubPanel, right.SubPanel)
            && EqualText(left.OptionCard, right.OptionCard)
            && EqualText(left.Stroke, right.Stroke)
            && EqualText(left.StrokeBright, right.StrokeBright)
            && EqualText(left.Text, right.Text)
            && EqualText(left.TextMuted, right.TextMuted)
            && EqualText(left.Accent, right.Accent)
            && EqualText(left.AccentWarm, right.AccentWarm)
            && EqualText(left.AccentHot, right.AccentHot)
            && AreEquivalentGradient(left.HeroGradient, right.HeroGradient)
            && AreEquivalentGradient(left.ScreenGlowGradient, right.ScreenGlowGradient)
            && AreEquivalentGradient(left.BackgroundGradient, right.BackgroundGradient);
    }

    private static bool AreEquivalentGradient(SkinGradient? left, SkinGradient? right)
    {
        if (left == null || right == null)
            return left == right;

        if (left.Angle != right.Angle || left.Stops.Count != right.Stops.Count)
            return false;

        for (int i = 0; i < left.Stops.Count; i++)
        {
            GradientStop leftStop = left.Stops[i];
            GradientStop rightStop = right.Stops[i];

            if (!EqualText(leftStop.Color, rightStop.Color) || leftStop.Offset != rightStop.Offset)
                return false;
        }

        return true;
    }

    private static bool AreEquivalentTypography(SkinTypography left, SkinTypography right)
    {
        return EqualText(left.PrimaryFont, right.PrimaryFont)
            && EqualText(left.DisplayFont, right.DisplayFont)
            && EqualText(left.MonoFont, right.MonoFont)
            && EqualText(left.DeckLabelFont, right.DeckLabelFont)
            && left.BaseSize == right.BaseSize
            && left.SmallSize == right.SmallSize
            && left.DisplaySize == right.DisplaySize
            && left.KickerSize == right.KickerSize
            && left.DeckLabelSize == right.DeckLabelSize
            && left.LetterSpacing == right.LetterSpacing
            && left.DeckLabelSpacing == right.DeckLabelSpacing;
    }

    private static bool AreEquivalentLayout(SkinLayout left, SkinLayout right)
    {
        return left.BorderRadiusSmall == right.BorderRadiusSmall
            && left.BorderRadiusMedium == right.BorderRadiusMedium
            && left.BorderRadiusLarge == right.BorderRadiusLarge
            && left.BorderRadiusXLarge == right.BorderRadiusXLarge
            && left.BorderRadiusFull == right.BorderRadiusFull
            && left.PanelPadding == right.PanelPadding
            && left.ButtonPaddingX == right.ButtonPaddingX
            && left.ButtonPaddingY == right.ButtonPaddingY
            && left.SpacingSmall == right.SpacingSmall
            && left.SpacingMedium == right.SpacingMedium
            && left.SpacingLarge == right.SpacingLarge
            && left.ShadowOpacity == right.ShadowOpacity
            && left.ShadowBlur == right.ShadowBlur
            && left.ShadowSpread == right.ShadowSpread;
    }

    private static bool AreEquivalentButtons(SkinButtons left, SkinButtons right)
    {
        return EqualText(left.Background, right.Background)
            && EqualText(left.BackgroundHover, right.BackgroundHover)
            && EqualText(left.BackgroundPressed, right.BackgroundPressed)
            && EqualText(left.BackgroundAction, right.BackgroundAction)
            && EqualText(left.BackgroundActionHover, right.BackgroundActionHover)
            && EqualText(left.BackgroundGhost, right.BackgroundGhost)
            && EqualText(left.Border, right.Border)
            && EqualText(left.BorderHover, right.BorderHover)
            && EqualText(left.BorderGhost, right.BorderGhost)
            && left.BorderRadius == right.BorderRadius
            && left.TransitionDuration == right.TransitionDuration
            && left.HoverScale == right.HoverScale
            && left.PressedScale == right.PressedScale;
    }

    private static bool AreEquivalentPanels(SkinPanels left, SkinPanels right)
    {
        return EqualText(left.GlassBackground, right.GlassBackground)
            && left.GlassOpacity == right.GlassOpacity
            && EqualText(left.SubPanelBackground, right.SubPanelBackground)
            && EqualText(left.SubPanelBorder, right.SubPanelBorder)
            && EqualText(left.OptionCardBackground, right.OptionCardBackground)
            && EqualText(left.OptionCardBorder, right.OptionCardBorder)
            && left.BorderRadius == right.BorderRadius
            && left.SubPanelRadius == right.SubPanelRadius
            && left.OptionCardRadius == right.OptionCardRadius;
    }

    private static bool AreEquivalentInputs(SkinInputs left, SkinInputs right)
    {
        return EqualText(left.Background, right.Background)
            && EqualText(left.BackgroundFocus, right.BackgroundFocus)
            && EqualText(left.Border, right.Border)
            && EqualText(left.BorderFocus, right.BorderFocus)
            && left.BorderRadius == right.BorderRadius
            && left.Padding == right.Padding;
    }

    private static bool AreEquivalentEffects(SkinEffects left, SkinEffects right)
    {
        return left.UseTransparency == right.UseTransparency
            && left.UseBlur == right.UseBlur
            && left.BlurRadius == right.BlurRadius
            && left.UseGlow == right.UseGlow
            && EqualText(left.GlowColor, right.GlowColor)
            && left.GlowIntensity == right.GlowIntensity
            && left.UseAnimations == right.UseAnimations
            && left.AnimationSpeed == right.AnimationSpeed
            && left.UseBackgroundEffects == right.UseBackgroundEffects
            && EqualText(left.BackgroundEffectColor1, right.BackgroundEffectColor1)
            && EqualText(left.BackgroundEffectColor2, right.BackgroundEffectColor2)
            && left.BackgroundEffectOpacity == right.BackgroundEffectOpacity;
    }

    private static bool AreEquivalentStyleOverrides(
        System.Collections.Generic.Dictionary<string, string> left,
        System.Collections.Generic.Dictionary<string, string> right)
    {
        if (left.Count != right.Count)
            return false;

        foreach ((string key, string value) in left)
        {
            if (!right.TryGetValue(key, out string? rightValue) || !EqualText(value, rightValue))
                return false;
        }

        return true;
    }

    private static bool EqualText(string? left, string? right)
    {
        return string.Equals(
            left?.Trim(),
            right?.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private IBrush? TryGetBrush(string key)
    {
        if (Application.Current?.TryGetResource(key, ActualThemeVariant, out object? value) == true
            && value is IBrush brush)
            return brush;
        return null;
    }
    
    private async void OnLoadFromFile(object? sender, RoutedEventArgs e)
    {
        if (StorageProvider == null)
        {
            await ShowErrorAsync("File picker is unavailable in this window.");
            return;
        }

        IStorageFolder? startFolder = null;
        string skinsDir = GetSkinsDirectory();
        if (Directory.Exists(skinsDir))
        {
            startFolder = await StorageProvider.TryGetFolderFromPathAsync(skinsDir);
        }

        var options = new FilePickerOpenOptions
        {
            Title = "Load Skin File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("APA Skin Files")
                {
                    Patterns = new[] { "*.apa" }
                }
            }
        };

        if (startFolder != null)
            options.SuggestedStartLocation = startFolder;

        var files = await StorageProvider.OpenFilePickerAsync(options);
        if (files.Count > 0)
        {
            string? selectedPath = files[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                await ShowErrorAsync("Selected skin file is not available as a local path.");
                return;
            }

            var success = await _skinManager.LoadSkinAsync(selectedPath);
            if (success)
            {
                LoadSkinsAsync(); // Refresh list
            }
            else
            {
                await ShowErrorAsync("Failed to load skin file. Please check the file format.");
            }
        }
    }
    
    private void OnOpenSkinsFolder(object? sender, RoutedEventArgs e)
    {
        var skinsDir = GetSkinsDirectory();
        
        // Create directory if it doesn't exist
        if (!Directory.Exists(skinsDir))
        {
            Directory.CreateDirectory(skinsDir);
        }
        
        // Open folder
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start("explorer.exe", skinsDir);
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start("xdg-open", skinsDir);
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", skinsDir);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SkinPicker] Failed to open folder: {ex.Message}");
        }
    }
    
    private void OnReload(object? sender, RoutedEventArgs e)
    {
        LoadSkinsAsync();
    }
    
    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (_selectedSkin != null)
        {
            _skinManager.ApplySkin(_selectedSkin);
        }
        Close(_selectedSkin);
    }
    
    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
    
    private string GetSkinsDirectory()
    {
        string workingDirSkins = Path.Combine(Directory.GetCurrentDirectory(), "skins");
        if (Directory.Exists(workingDirSkins))
            return workingDirSkins;

        string appDirSkins = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "skins");
        if (Directory.Exists(appDirSkins))
            return appDirSkins;

        return workingDirSkins;
    }
    
    private async Task ShowErrorAsync(string message)
    {
        var okButton = new Button
        {
            Content = "OK",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };

        var dialog = new Window
        {
            Title = "Error",
            Width = 400,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    okButton
                }
            }
        };

        okButton.Click += (_, _) => dialog.Close();
        
        if (this.VisualRoot is Window parent)
        {
            await dialog.ShowDialog(parent);
        }
    }
}

// Simple relay command for dialog button
public class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action _execute;
    
    public RelayCommand(Action execute)
    {
        _execute = execute;
    }
    
    public event EventHandler? CanExecuteChanged;
    
    public bool CanExecute(object? parameter) => true;
    
    public void Execute(object? parameter) => _execute();
}
