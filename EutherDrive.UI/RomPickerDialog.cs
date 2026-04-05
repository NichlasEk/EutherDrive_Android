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
    private readonly TextBox _romLibraryText;
    private readonly TextBox _statusText;
    private readonly TextBox _searchBox;
    private readonly ComboBox _sortCombo;
    private readonly ComboBox _starsFilterCombo;
    private readonly ListBox _listBox;
    private readonly Button _openButton;
    private readonly Button _romsButton;
    private readonly double _uiScale;
    private string? _romLibraryPath;
    private string _currentDirectory;

    public string? SelectedPath { get; private set; }
    public string? RomLibraryPath => _romLibraryPath;

    public RomPickerDialog(string? initialPath, string? romLibraryPath, double uiScale, Func<string, RomPickerStats> statsProvider)
    {
        _uiScale = uiScale;
        _statsProvider = statsProvider;
        _romLibraryPath = NormalizeDirectoryPath(romLibraryPath);
        _currentDirectory = ResolveInitialDirectory(initialPath, _romLibraryPath);

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

        _romLibraryText = new TextBox
        {
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            TextWrapping = TextWrapping.NoWrap
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

        _romsButton = new Button { Content = "Roms", MinWidth = 84 };
        _romsButton.Click += (_, _) => NavigateToRomLibrary();

        var setRomsButton = new Button { Content = "Set Roms", MinWidth = 98 };
        setRomsButton.Click += (_, _) => SetCurrentDirectoryAsRomLibrary();

        var drivesButton = new Button { Content = "Drives", MinWidth = 88 };
        drivesButton.Click += async (_, _) => await OpenDrivePickerAsync();

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
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                        ColumnSpacing = 12,
                        Children =
                        {
                            new StackPanel
                            {
                                [Grid.ColumnProperty] = 0,
                                Orientation = Orientation.Horizontal,
                                Spacing = 8,
                                Children = { upButton, homeButton, _romsButton, drivesButton, refreshButton }
                            },
                            new Border
                            {
                                [Grid.ColumnProperty] = 1,
                                Background = Brushes.Transparent,
                                Child = setRomsButton
                            }
                        }
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
                    _romLibraryText,
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
        UpdateRomLibraryUi();
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

    private void NavigateToRomLibrary()
    {
        if (string.IsNullOrWhiteSpace(_romLibraryPath))
        {
            _statusText.Text = "ROM library folder is not set.";
            return;
        }

        if (!Directory.Exists(_romLibraryPath))
        {
            _statusText.Text = $"ROM library folder is missing: {_romLibraryPath}";
            return;
        }

        NavigateTo(_romLibraryPath);
    }

    private void SetCurrentDirectoryAsRomLibrary()
    {
        _romLibraryPath = NormalizeDirectoryPath(_currentDirectory);
        UpdateRomLibraryUi();
        _statusText.Text = $"ROM library set to {_romLibraryPath}";
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

    private async System.Threading.Tasks.Task OpenDrivePickerAsync()
    {
        IReadOnlyList<DriveTarget> targets = BuildDriveTargets();
        var dialog = new DrivePickerDialog(targets, _uiScale);
        string? selectedPath = await dialog.ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(selectedPath))
            NavigateTo(selectedPath);
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

    private void UpdateRomLibraryUi()
    {
        bool hasValue = !string.IsNullOrWhiteSpace(_romLibraryPath);
        bool exists = hasValue && Directory.Exists(_romLibraryPath!);
        _romsButton.IsEnabled = hasValue;
        ToolTip.SetTip(_romsButton, hasValue ? _romLibraryPath : "ROM library folder is not set.");

        _romLibraryText.Text = hasValue
            ? exists
                ? $"ROM library: {_romLibraryPath}"
                : $"ROM library: {_romLibraryPath} (missing)"
            : "ROM library: not set";
    }

    private static string ResolveInitialDirectory(string? initialPath, string? romLibraryPath)
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

        if (!string.IsNullOrWhiteSpace(romLibraryPath) && Directory.Exists(romLibraryPath))
            return romLibraryPath;

        string home = ResolveHomeDirectory();
        if (Directory.Exists(home))
            return home;

        return Directory.GetCurrentDirectory();
    }

    private static string? NormalizeDirectoryPath(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveHomeDirectory()
        => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static IReadOnlyList<DriveTarget> BuildDriveTargets()
    {
        var results = new List<DriveTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddTarget(string label, string path)
        {
            string? fullPath = NormalizeDirectoryPath(path);
            if (string.IsNullOrWhiteSpace(fullPath) || !Directory.Exists(fullPath))
                return;

            string key = Path.TrimEndingDirectorySeparator(fullPath);
            if (key.Length == 0)
                key = fullPath;

            if (!seen.Add(key))
                return;

            results.Add(new DriveTarget(label, fullPath, fullPath));
        }

        string home = ResolveHomeDirectory();
        string filesystemRoot = Path.GetPathRoot(home) ?? Path.DirectorySeparatorChar.ToString();
        AddTarget("Filesystem", filesystemRoot);
        AddTarget("Home", home);

        foreach (DriveInfo drive in SafeGetDrives().OrderBy(static drive => drive.Name, StringComparer.OrdinalIgnoreCase))
            AddTarget(BuildDriveLabel(drive), drive.RootDirectory.FullName);

        foreach (string mountPath in EnumerateExtraMountPaths().OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
            AddTarget(BuildMountLabel(mountPath), mountPath);

        return results;
    }

    private static IEnumerable<DriveInfo> SafeGetDrives()
    {
        try
        {
            return DriveInfo.GetDrives();
        }
        catch
        {
            return Array.Empty<DriveInfo>();
        }
    }

    private static IEnumerable<string> EnumerateExtraMountPaths()
    {
        var bases = new List<string>();
        string home = ResolveHomeDirectory();
        string userName = Path.GetFileName(Path.TrimEndingDirectorySeparator(home));

        if (!string.IsNullOrWhiteSpace(userName))
        {
            bases.Add(Path.Combine("/run/media", userName));
            bases.Add(Path.Combine("/media", userName));
        }

        bases.Add("/media");
        bases.Add("/mnt");
        bases.Add("/Volumes");

        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in bases)
        {
            if (!Directory.Exists(root))
                continue;

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(root);
            }
            catch
            {
                continue;
            }

            foreach (string directory in directories)
            {
                if (yielded.Add(directory))
                    yield return directory;
            }
        }
    }

    private static string BuildDriveLabel(DriveInfo drive)
    {
        try
        {
            string label = drive.VolumeLabel?.Trim() ?? string.Empty;
            if (label.Length > 0)
                return label;
        }
        catch
        {
        }

        return BuildMountLabel(drive.RootDirectory.FullName);
    }

    private static string BuildMountLabel(string path)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(path);
        string root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path) ?? string.Empty);
        if (trimmed.Length == 0 || string.Equals(trimmed, root, StringComparison.OrdinalIgnoreCase))
            return "Filesystem";

        string name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }

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

    private sealed record DriveTarget(string Label, string FullPath, string DetailText);

    private sealed class DrivePickerDialog : Window
    {
        private readonly ListBox _listBox;
        private readonly Button _openButton;

        public DrivePickerDialog(IReadOnlyList<DriveTarget> targets, double uiScale)
        {
            Title = "Drives";
            Width = ScaleDialogSize(360, uiScale);
            Height = ScaleDialogSize(420, uiScale);
            MinWidth = ScaleDialogSize(300, uiScale);
            MinHeight = ScaleDialogSize(280, uiScale);
            Background = new SolidColorBrush(Color.Parse("#0B1219"));
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            _listBox = new ListBox
            {
                ItemsSource = targets,
                ItemTemplate = new FuncDataTemplate<DriveTarget>((target, _) => BuildDriveEntryView(target), true)
            };
            _listBox.DoubleTapped += (_, _) => OpenSelectedTarget();

            _openButton = new Button
            {
                Content = "Open",
                MinWidth = 96,
                IsEnabled = targets.Count > 0
            };
            _openButton.Classes.Add("action");
            _openButton.Click += (_, _) => OpenSelectedTarget();
            _listBox.SelectionChanged += (_, _) => _openButton.IsEnabled = _listBox.SelectedItem is DriveTarget;

            var cancelButton = new Button { Content = "Cancel", MinWidth = 96 };
            cancelButton.Click += (_, _) => Close(null);

            var root = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
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
                        new TextBlock { Text = "DRIVES", Classes = { "deck-label" } },
                        new TextBlock
                        {
                            Text = "Jump to Filesystem, Home, and mounted storage.",
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
                Padding = new Thickness(8),
                Child = _listBox
            });

            root.Children.Add(new StackPanel
            {
                [Grid.RowProperty] = 2,
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Children = { cancelButton, _openButton }
            });

            Content = WrapDialogForUiScale(root, uiScale);
            _listBox.SelectedItem = targets.FirstOrDefault();
        }

        private static Control BuildDriveEntryView(DriveTarget target)
        {
            var stack = new StackPanel { Spacing = 3 };
            stack.Children.Add(new TextBlock
            {
                Text = target.Label,
                FontWeight = FontWeight.SemiBold
            });
            stack.Children.Add(new TextBlock
            {
                Text = target.DetailText,
                Classes = { "muted" },
                FontSize = 11,
                TextWrapping = TextWrapping.NoWrap
            });

            return new Border
            {
                Background = new SolidColorBrush(Color.Parse("#161C24")),
                BorderBrush = new SolidColorBrush(Color.Parse("#304355")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Child = stack
            };
        }

        private void OpenSelectedTarget()
        {
            if (_listBox.SelectedItem is DriveTarget target)
                Close(target.FullPath);
        }
    }
}

public sealed record RomPickerStats(int Stars, string DetailText, int LaunchCount, double PlaySeconds);
