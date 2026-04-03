using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace EutherDrive.UI;

public sealed class RomPickerDialog : Window
{
    private static readonly HashSet<string> s_supportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bin", ".md", ".gen", ".smd", ".sms", ".sg", ".gg", ".nes", ".smc", ".sfc",
        ".pce", ".cue", ".zip", ".7z", ".iso", ".img", ".chd", ".pbp", ".exe"
    };

    private readonly Func<string, RomPickerStats> _statsProvider;
    private readonly ObservableCollection<RomPickerEntry> _entries = new();
    private readonly List<RomPickerEntry> _allEntries = new();
    private readonly TextBox _pathText;
    private readonly TextBox _statusText;
    private readonly TextBox _searchBox;
    private readonly ComboBox _sortCombo;
    private readonly ComboBox _starsFilterCombo;
    private readonly ListBox _listBox;
    private readonly Button _openButton;
    private readonly double _uiScale;
    private string _currentDirectory;

    public string? SelectedPath { get; private set; }

    public RomPickerDialog(string? initialPath, double uiScale, Func<string, RomPickerStats> statsProvider)
    {
        _uiScale = uiScale;
        _statsProvider = statsProvider;
        _currentDirectory = ResolveInitialDirectory(initialPath);

        Title = "ROM Picker";
        Width = ScaleDialogSize(980, uiScale);
        Height = ScaleDialogSize(680, uiScale);
        MinWidth = ScaleDialogSize(760, uiScale);
        MinHeight = ScaleDialogSize(520, uiScale);
        Background = new SolidColorBrush(Color.Parse("#0B1219"));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _pathText = new TextBox
        {
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Text = _currentDirectory,
            TextWrapping = TextWrapping.NoWrap
        };

        _statusText = new TextBox
        {
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Text = "Choose a ROM or navigate into a directory."
        };

        _searchBox = new TextBox
        {
            Watermark = "Search in current folder",
            MinWidth = 220
        };
        _searchBox.TextChanged += (_, _) => ApplyFilters();

        _sortCombo = new ComboBox
        {
            Width = 170,
            ItemsSource = new[]
            {
                "Stars",
                "Play Time",
                "Launch Count",
                "Name"
            },
            SelectedIndex = 0
        };
        _sortCombo.SelectionChanged += (_, _) => ApplyFilters();

        _starsFilterCombo = new ComboBox
        {
            Width = 150,
            ItemsSource = new[]
            {
                "All stars",
                "1+ stars",
                "2+ stars",
                "3+ stars",
                "4+ stars",
                "5+ stars"
            },
            SelectedIndex = 0
        };
        _starsFilterCombo.SelectionChanged += (_, _) => ApplyFilters();

        _listBox = new ListBox
        {
            ItemsSource = _entries,
            ItemTemplate = new FuncDataTemplate<RomPickerEntry>((entry, _) => BuildEntryView(entry), true)
        };
        _listBox.SelectionChanged += OnSelectionChanged;
        _listBox.DoubleTapped += OnListDoubleTapped;

        _openButton = new Button
        {
            Content = "Open",
            MinWidth = 96,
            IsEnabled = false
        };
        _openButton.Classes.Add("action");
        _openButton.Click += (_, _) => OpenSelectedEntry();

        var upButton = new Button { Content = "Up", MinWidth = 74 };
        upButton.Click += (_, _) => NavigateToParent();

        var homeButton = new Button { Content = "Home", MinWidth = 84 };
        homeButton.Click += (_, _) => NavigateTo(ResolveHomeDirectory());

        var refreshButton = new Button { Content = "Refresh", MinWidth = 88 };
        refreshButton.Click += (_, _) => LoadDirectory(_currentDirectory);

        var cancelButton = new Button { Content = "Cancel", MinWidth = 96 };
        cancelButton.Click += (_, _) => Close(false);

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            RowSpacing = 10,
            Margin = new Thickness(16)
        };

        root.Children.Add(new Border
        {
            [Grid.RowProperty] = 0,
            Classes = { "panel" },
            Padding = new Thickness(12, 10),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "ROM PICKER", Classes = { "deck-label" } },
                    new TextBlock
                    {
                        Text = "Browse local ROM folders with stars, launch counts and total play time.",
                        Classes = { "muted" },
                        FontSize = 12
                    }
                }
            }
        });

        root.Children.Add(new Border
        {
            [Grid.RowProperty] = 1,
            Classes = { "panel" },
            Padding = new Thickness(12, 10),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { upButton, homeButton, refreshButton }
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            _searchBox,
                            _sortCombo,
                            _starsFilterCombo
                        }
                    },
                    _pathText,
                    _statusText
                }
            }
        });

        root.Children.Add(new Border
        {
            [Grid.RowProperty] = 2,
            Classes = { "panel" },
            Padding = new Thickness(8),
            Child = _listBox
        });

        root.Children.Add(new StackPanel
        {
            [Grid.RowProperty] = 3,
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelButton, _openButton }
        });

        Content = WrapDialogForUiScale(root, uiScale);
        LoadDirectory(_currentDirectory);
    }

    private static Control BuildEntryView(RomPickerEntry entry)
    {
        string accent = entry.IsDirectory
            ? "#5EEAD4"
            : entry.Stars >= 5 ? "#FB7185"
            : entry.Stars >= 3 ? "#F6D365"
            : "#A7F3D0";
        string badgeText = entry.IsDirectory ? "DIR" : BuildStarsText(entry.Stars);
        string titleText = entry.Name;
        string background = entry.IsDirectory
            ? "#D0132030"
            : entry.Stars >= 5 ? "#331E232E"
            : entry.Stars >= 3 ? "#2A2A2416"
            : "#161C24";
        string border = entry.IsDirectory
            ? "#304355"
            : entry.Stars >= 5 ? "#B14D63"
            : entry.Stars >= 3 ? "#9E7A2C"
            : "#304355";

        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 12,
            Children =
            {
                new TextBlock
                {
                    [Grid.ColumnProperty] = 0,
                    Text = entry.IsDirectory ? "DIR" : "ROM",
                    Classes = { "kicker" },
                    VerticalAlignment = VerticalAlignment.Center
                },
                new TextBlock
                {
                    [Grid.ColumnProperty] = 1,
                    Text = titleText,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.NoWrap
                },
                new TextBlock
                {
                    [Grid.ColumnProperty] = 2,
                    Text = badgeText,
                    Foreground = new SolidColorBrush(Color.Parse(accent)),
                    FontWeight = FontWeight.Bold,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        });

        stack.Children.Add(new TextBlock
        {
            Text = entry.DetailText,
            Classes = { "muted" },
            FontSize = 11,
            TextWrapping = TextWrapping.NoWrap
        });

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(background)),
            BorderBrush = new SolidColorBrush(Color.Parse(border)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = stack
        };
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_listBox.SelectedItem is not RomPickerEntry entry)
        {
            _openButton.IsEnabled = false;
            return;
        }

        _openButton.IsEnabled = true;
        _openButton.Content = entry.IsDirectory ? "Enter" : "Open";
        _statusText.Text = entry.DetailText;
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        OpenSelectedEntry();
    }

    private void OpenSelectedEntry()
    {
        if (_listBox.SelectedItem is not RomPickerEntry entry)
            return;

        if (entry.IsDirectory)
        {
            NavigateTo(entry.FullPath);
            return;
        }

        SelectedPath = entry.FullPath;
        Close(true);
    }

    private void NavigateToParent()
    {
        try
        {
            DirectoryInfo? parent = Directory.GetParent(_currentDirectory);
            if (parent != null)
                NavigateTo(parent.FullName);
        }
        catch
        {
            _statusText.Text = "Unable to move to parent directory.";
        }
    }

    private void NavigateTo(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        LoadDirectory(path);
    }

    private void LoadDirectory(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                _statusText.Text = "Folder does not exist.";
                return;
            }

            _currentDirectory = fullPath;
            _pathText.Text = _currentDirectory;
            _allEntries.Clear();
            _entries.Clear();

            foreach (string dir in Directory.EnumerateDirectories(_currentDirectory).OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
            {
                _allEntries.Add(new RomPickerEntry(
                    Path.GetFileName(dir),
                    dir,
                    IsDirectory: true,
                    Stars: 0,
                    DetailText: dir));
            }

            foreach (string file in Directory.EnumerateFiles(_currentDirectory).Where(IsSupportedRomFile).OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
            {
                RomPickerStats stats = _statsProvider(file);
                _allEntries.Add(new RomPickerEntry(
                    Path.GetFileName(file),
                    file,
                    IsDirectory: false,
                    Stars: stats.Stars,
                    DetailText: stats.DetailText)
                {
                    LaunchCount = stats.LaunchCount,
                    PlaySeconds = stats.PlaySeconds
                });
            }

            ApplyFilters();
            _statusText.Text = _entries.Count == 0
                ? "No ROMs or directories found here."
                : $"{_entries.Count} entries in {_currentDirectory}";
            _listBox.SelectedItem = _entries.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Unable to read folder: {ex.Message}";
        }
    }

    private void ApplyFilters()
    {
        string search = _searchBox.Text?.Trim() ?? string.Empty;
        int minStars = _starsFilterCombo.SelectedIndex switch
        {
            1 => 1,
            2 => 2,
            3 => 3,
            4 => 4,
            5 => 5,
            _ => 0
        };

        IEnumerable<RomPickerEntry> items = _allEntries.Where(entry =>
            search.Length == 0
            || entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entry.DetailText.Contains(search, StringComparison.OrdinalIgnoreCase));

        items = items.Where(entry => entry.IsDirectory || entry.Stars >= minStars);

        items = (_sortCombo.SelectedItem as string) switch
        {
            "Play Time" => items
                .OrderBy(static entry => entry.IsDirectory ? 0 : 1)
                .ThenByDescending(static entry => entry.PlaySeconds)
                .ThenBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase),
            "Launch Count" => items
                .OrderBy(static entry => entry.IsDirectory ? 0 : 1)
                .ThenByDescending(static entry => entry.LaunchCount)
                .ThenBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase),
            "Name" => items
                .OrderBy(static entry => entry.IsDirectory ? 0 : 1)
                .ThenBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase),
            _ => items
                .OrderBy(static entry => entry.IsDirectory ? 0 : 1)
                .ThenByDescending(static entry => entry.Stars)
                .ThenByDescending(static entry => entry.PlaySeconds)
                .ThenByDescending(static entry => entry.LaunchCount)
                .ThenBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
        };

        RomPickerEntry? selected = _listBox.SelectedItem as RomPickerEntry;
        string? selectedPath = selected?.FullPath;
        _entries.Clear();
        foreach (RomPickerEntry item in items)
            _entries.Add(item);

        if (selectedPath != null)
        {
            RomPickerEntry? newSelected = _entries.FirstOrDefault(entry => string.Equals(entry.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (newSelected != null)
            {
                _listBox.SelectedItem = newSelected;
                return;
            }
        }

        _listBox.SelectedItem = _entries.FirstOrDefault();
        Dispatcher.UIThread.Post(() =>
        {
            if (_entries.Count == 0)
                _statusText.Text = "No entries match the current search/filter.";
        }, DispatcherPriority.Background);
    }

    private static bool IsSupportedRomFile(string path)
        => s_supportedExtensions.Contains(Path.GetExtension(path));

    private static string BuildStarsText(int stars)
    {
        int clamped = Math.Clamp(stars, 0, 6);
        return new string('★', clamped) + new string('☆', 6 - clamped);
    }

    private static string ResolveInitialDirectory(string? initialPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(initialPath))
            {
                if (Directory.Exists(initialPath))
                    return Path.GetFullPath(initialPath);

                string? parent = Path.GetDirectoryName(initialPath);
                if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                    return Path.GetFullPath(parent);
            }
        }
        catch
        {
        }

        string home = ResolveHomeDirectory();
        if (Directory.Exists(home))
            return home;

        return Directory.GetCurrentDirectory();
    }

    private static string ResolveHomeDirectory()
        => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static double ScaleDialogSize(double value, double uiScale) => Math.Round(value * uiScale);

    private static Control WrapDialogForUiScale(Control content, double uiScale)
    {
        if (Math.Abs(uiScale - 1.0) < 0.001)
            return content;

        return new LayoutTransformControl
        {
            LayoutTransform = new ScaleTransform(uiScale, uiScale),
            Child = content
        };
    }

    private sealed record RomPickerEntry(
        string Name,
        string FullPath,
        bool IsDirectory,
        int Stars,
        string DetailText)
    {
        public int LaunchCount { get; init; }
        public double PlaySeconds { get; init; }
    }
}

public sealed record RomPickerStats(int Stars, string DetailText, int LaunchCount, double PlaySeconds);
