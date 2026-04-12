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
        AddSkinCard(container, SkinManager.CreateDefaultSkin(), true);
        
        // Scan for skins in the skins directory
        var skinsDir = GetSkinsDirectory();
        if (Directory.Exists(skinsDir))
        {
            foreach (var skin in _skinManager.ScanSkinsDirectory(skinsDir))
            {
                AddSkinCard(container, skin, false);
            }
        }
        
        // Add currently loaded skins
        foreach (var skin in _skinManager.LoadedSkins.Where(s => !string.IsNullOrEmpty(s.SourcePath)))
        {
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
        bool isActive = _skinManager.CurrentSkin.SkinName == skin.SkinName &&
                       (isBuiltIn || _skinManager.CurrentSkin.SourcePath == skin.SourcePath);

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
