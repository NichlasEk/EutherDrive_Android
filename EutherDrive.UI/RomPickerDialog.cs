using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using EutherDrive.Core;

namespace EutherDrive.UI;

public sealed class RomPickerDialog : Window
{
    private static readonly HttpClient s_httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly HashSet<string> s_supportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bin", ".md", ".gen", ".smd", ".sms", ".sg", ".gg", ".nes", ".smc", ".sfc",
        ".pce", ".gba", ".agb", ".cue", ".zip", ".7z", ".iso", ".img", ".chd", ".pbp", ".exe"
    };

    private readonly Func<string, RomPickerStats> _statsProvider;
    private readonly ObservableCollection<RomPickerEntry> _entries = new();
    private readonly List<RomPickerEntry> _allEntries = new();
    private readonly TextBox _pathText;
    private readonly TextBox _romLibraryText;
    private readonly TextBox _coverCacheText;
    private readonly TextBox _statusText;
    private readonly TextBlock _coverSyncStatusText;
    private readonly TextBox _searchBox;
    private readonly ComboBox _sortCombo;
    private readonly ComboBox _starsFilterCombo;
    private readonly ListBox _listBox;
    private readonly Image _coverPreviewImage;
    private readonly TextBlock _coverPreviewTitle;
    private readonly Button _openButton;
    private readonly Button _romsButton;
    private readonly Button _syncCoversButton;
    private readonly double _uiScale;
    private readonly object _coverIndexLock = new();
    private string? _romLibraryPath;
    private string _currentDirectory;
    private Bitmap? _coverPreviewBitmap;
    private Task? _coverSyncTask;
    private CancellationTokenSource? _coverSyncCts;
    private string? _coverSyncLibraryPath;
    private Task? _coverDeltaSyncTask;
    private string? _coverDeltaSyncLibraryPath;
    private string _coverSyncStatus = "Cover sync: idle";
    private Dictionary<string, CoverIndexEntry> _coverIndexByPath = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, CoverIndexEntry> _coverIndexByHash = new(StringComparer.OrdinalIgnoreCase);
    private bool _coverIndexDirty;

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

        _coverCacheText = new TextBox
        {
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            TextWrapping = TextWrapping.NoWrap
        };

        _coverSyncStatusText = new TextBlock
        {
            Text = _coverSyncStatus,
            Classes = { "muted" },
            FontSize = 11,
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
            ItemTemplate = new FuncDataTemplate<RomPickerEntry>((entry, _) => BuildEntryView(entry), true),
            Styles =
            {
                new Style(x => x.OfType<ListBoxItem>())
                {
                    Setters =
                    {
                        new Setter(Layoutable.MarginProperty, new Thickness(0)),
                        new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                        new Setter(Layoutable.MinHeightProperty, 0d),
                        new Setter(Visual.ClipToBoundsProperty, true)
                    }
                }
            }
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

        _syncCoversButton = new Button { Content = "Sync Covers", MinWidth = 112 };
        _syncCoversButton.Click += (_, _) => StartCoverSync(forceRestart: true);

        var drivesButton = new Button { Content = "Drives", MinWidth = 88 };
        drivesButton.Click += async (_, _) => await OpenDrivePickerAsync();

        var refreshButton = new Button { Content = "Refresh", MinWidth = 88 };
        refreshButton.Click += (_, _) => LoadDirectory(_currentDirectory);

        var cancelButton = new Button { Content = "Cancel", MinWidth = 96 };
        cancelButton.Click += (_, _) => Close(false);

        _coverPreviewImage = new Image
        {
            Width = 104,
            Height = 144,
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _coverPreviewTitle = new TextBlock
        {
            Text = "No cover",
            Classes = { "muted" },
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };

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
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 12,
                Children =
                {
                    new StackPanel
                    {
                        [Grid.ColumnProperty] = 0,
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
                    },
                    new StackPanel
                    {
                        [Grid.ColumnProperty] = 1,
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        VerticalAlignment = VerticalAlignment.Top,
                        Children = { _syncCoversButton, setRomsButton }
                    }
                }
            }
        });

        root.Children.Add(new Border
        {
            [Grid.RowProperty] = 1,
            Classes = { "panel" },
            Padding = new Thickness(12, 10),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 16,
                Children =
                {
                    new StackPanel
                    {
                        [Grid.ColumnProperty] = 0,
                        Spacing = 8,
                        Children =
                        {
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 8,
                                Children = { upButton, homeButton, _romsButton, drivesButton, refreshButton }
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
                            _coverCacheText,
                            new Grid
                            {
                                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                                ColumnSpacing = 12,
                                Children =
                                {
                                    _statusText,
                                    new Border
                                    {
                                        [Grid.ColumnProperty] = 1,
                                        Background = new SolidColorBrush(Color.Parse("#1B2430")),
                                        BorderBrush = new SolidColorBrush(Color.Parse("#304355")),
                                        BorderThickness = new Thickness(1),
                                        CornerRadius = new CornerRadius(8),
                                        Padding = new Thickness(8, 6),
                                        Child = _coverSyncStatusText
                                    }
                                }
                            }
                        }
                    },
                    new StackPanel
                    {
                        [Grid.ColumnProperty] = 1,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(12, 0, 0, 0),
                        Children =
                        {
                            new Border
                            {
                                Width = 104,
                                Height = 144,
                                CornerRadius = new CornerRadius(12),
                                BorderThickness = new Thickness(1),
                                BorderBrush = new SolidColorBrush(Color.Parse("#304355")),
                                Background = new SolidColorBrush(Color.Parse("#111821")),
                                Padding = new Thickness(4),
                                Child = _coverPreviewImage
                            },
                            _coverPreviewTitle
                        }
                    }
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
        UpdateCoverCacheUi();
        ReloadCoverIndex();
        SetIdleCoverSyncStatus();
        LoadDirectory(_currentDirectory);
        StartCoverDeltaSyncIfNeeded();
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
            ColumnSpacing = 10,
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
                    Text = entry.IsDirectory ? $"{titleText}  ·  {entry.DetailText}" : titleText,
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 13,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                new TextBlock
                {
                    [Grid.ColumnProperty] = 2,
                    Text = badgeText,
                    Foreground = new SolidColorBrush(Color.Parse(accent)),
                    FontWeight = FontWeight.Bold,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        });

        if (!entry.IsDirectory)
        {
            stack.Children.Add(new TextBlock
            {
                Text = entry.DetailText,
                Classes = { "muted" },
                FontSize = 10,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(background)),
            BorderBrush = new SolidColorBrush(Color.Parse(border)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0),
            Child = stack
        };
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_listBox.SelectedItem is not RomPickerEntry entry)
        {
            _openButton.IsEnabled = false;
            UpdateCoverPreview(null);
            return;
        }

        _openButton.IsEnabled = true;
        _openButton.Content = entry.IsDirectory ? "Enter" : "Open";
        _statusText.Text = entry.DetailText;
        UpdateCoverPreview(entry);
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
        _coverDeltaSyncLibraryPath = null;
        UpdateRomLibraryUi();
        UpdateCoverCacheUi();
        ReloadCoverIndex();
        SetIdleCoverSyncStatus();
        _statusText.Text = $"ROM library set to {_romLibraryPath}";
        StartCoverDeltaSyncIfNeeded();
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
            if (_listBox.SelectedItem == null)
                UpdateCoverPreview(null);
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
                ? $"ROMs: {_romLibraryPath}"
                : $"ROM library: {_romLibraryPath} (missing)"
            : "ROM library: not set";
    }

    private void UpdateCoverCacheUi()
    {
        string? coverCacheRoot = GetCoverCacheRoot();
        bool hasValue = !string.IsNullOrWhiteSpace(coverCacheRoot);
        bool exists = hasValue && Directory.Exists(coverCacheRoot!);

        _syncCoversButton.IsEnabled = !string.IsNullOrWhiteSpace(_romLibraryPath) && Directory.Exists(_romLibraryPath!);
        _coverCacheText.Text = hasValue
            ? exists
                ? $"Covers: {coverCacheRoot}"
                : $"Covers: {coverCacheRoot} (empty)"
            : "Covers: set a ROM library first";
        _coverSyncStatusText.Text = _coverSyncStatus;
    }

    private void SetIdleCoverSyncStatus()
    {
        string? coverCacheRoot = GetCoverCacheRoot();
        if (string.IsNullOrWhiteSpace(coverCacheRoot))
        {
            _coverSyncStatus = "Cover sync: set a ROM library first";
            UpdateCoverCacheUi();
            return;
        }

        int indexedCount;
        lock (_coverIndexLock)
            indexedCount = _coverIndexByPath.Count;

        _coverSyncStatus = indexedCount > 0
            ? $"Cover sync: cache ready ({indexedCount} indexed). Click Sync Covers to refresh."
            : "Cover sync: cache empty. Click Sync Covers to download covers.";
        UpdateCoverCacheUi();
    }

    private void UpdateCoverPreview(RomPickerEntry? entry)
    {
        DisposeCoverPreviewBitmap();

        if (entry == null || entry.IsDirectory)
        {
            _coverPreviewImage.Source = null;
            _coverPreviewTitle.Text = "No cover";
            return;
        }

        string? coverCacheRoot = GetCoverCacheRoot();
        string? coverPath = TryResolveCoverPathFromIndex(entry.FullPath, coverCacheRoot)
            ?? (string.IsNullOrWhiteSpace(coverCacheRoot)
            ? null
            : ResolveCoverPath(entry.FullPath, coverCacheRoot));
        if (string.IsNullOrWhiteSpace(coverPath) || !File.Exists(coverPath))
        {
            _coverPreviewImage.Source = null;
            _coverPreviewTitle.Text = "Missing";
            QueueCoverDownload(entry.FullPath);
            return;
        }

        try
        {
            _coverPreviewBitmap = new Bitmap(coverPath);
            _coverPreviewImage.Source = _coverPreviewBitmap;
            _coverPreviewTitle.Text = Path.GetFileNameWithoutExtension(coverPath);
        }
        catch
        {
            _coverPreviewImage.Source = null;
            _coverPreviewTitle.Text = "Unreadable cover";
        }
    }

    private void DisposeCoverPreviewBitmap()
    {
        if (_coverPreviewBitmap != null)
        {
            _coverPreviewImage.Source = null;
            _coverPreviewBitmap.Dispose();
            _coverPreviewBitmap = null;
        }
    }

    private static string? ResolveCoverPath(string romPath, string thumbnailPackPath)
    {
        if (string.IsNullOrWhiteSpace(thumbnailPackPath))
            return null;

        string romName = Path.GetFileNameWithoutExtension(romPath);
        if (string.IsNullOrWhiteSpace(romName))
            return null;

        string[] nameCandidates =
        [
            NormalizeThumbnailName(romName),
            NormalizeThumbnailName(RemoveBracketedSegments(romName)),
            NormalizeThumbnailName(romName.Replace('_', ' ')),
            NormalizeThumbnailName(RemoveBracketedSegments(romName.Replace('_', ' ')))
        ];

        IEnumerable<string> distinctNames = nameCandidates
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        string extension = Path.GetExtension(romPath);
        foreach (string systemDir in GetThumbnailPlaylistCandidates(romPath, extension))
        {
            foreach (string artDir in GetThumbnailArtDirectoryCandidates(thumbnailPackPath, systemDir))
            {
                string? match = FindThumbnailByName(artDir, distinctNames);
                if (match != null)
                    return match;
            }
        }

        foreach (string artDir in GetThumbnailArtDirectoryCandidates(thumbnailPackPath, systemDir: null))
        {
            string? match = FindThumbnailByName(artDir, distinctNames);
            if (match != null)
                return match;
        }

        return null;
    }

    private static IEnumerable<string> GetThumbnailPlaylistCandidates(string romPath, string extension)
    {
        var candidates = new List<string>();
        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !candidates.Contains(value, StringComparer.OrdinalIgnoreCase))
                candidates.Add(value);
        }

        string effectiveExtension = GetEffectiveCoverRomExtension(romPath, extension);

        switch (effectiveExtension.ToLowerInvariant())
        {
            case ".gba":
            case ".agb":
                Add("Nintendo - Game Boy Advance");
                Add("Nintendo - Game Boy Advance (No-Intro)");
                Add("Game Boy Advance");
                break;
            case ".gb":
                Add("Nintendo - Game Boy");
                Add("Game Boy");
                break;
            case ".gbc":
                Add("Nintendo - Game Boy Color");
                Add("Game Boy Color");
                break;
            case ".gg":
                Add("Sega - Game Gear");
                Add("Game Gear");
                break;
            case ".sms":
            case ".sg":
                Add("Sega - Master System - Mark III");
                Add("Sega - Master System");
                Add("Master System");
                break;
            case ".md":
            case ".gen":
            case ".bin":
            case ".smd":
                Add("Sega - Mega Drive - Genesis");
                Add("Sega Genesis");
                Add("Sega Mega Drive");
                break;
            case ".nes":
                Add("Nintendo - Nintendo Entertainment System");
                Add("Nintendo Entertainment System");
                Add("NES");
                break;
            case ".smc":
            case ".sfc":
                Add("Nintendo - Super Nintendo Entertainment System");
                Add("Super Nintendo Entertainment System");
                Add("SNES");
                break;
            case ".pce":
            case ".cue":
            case ".chd":
                Add("NEC - PC Engine - TurboGrafx 16");
                Add("NEC - PC Engine CD - TurboGrafx-CD");
                Add("PC Engine");
                break;
        }

        string directoryHint = Path.GetDirectoryName(romPath) ?? string.Empty;
        if (directoryHint.Contains("GameGear", StringComparison.OrdinalIgnoreCase))
            Add("Sega - Game Gear");
        if (directoryHint.Contains("gg", StringComparison.OrdinalIgnoreCase) || directoryHint.Contains("game gear", StringComparison.OrdinalIgnoreCase))
            Add("Sega - Game Gear");
        if (directoryHint.Contains("GBA", StringComparison.OrdinalIgnoreCase) || directoryHint.Contains("Game Boy Advance", StringComparison.OrdinalIgnoreCase))
            Add("Nintendo - Game Boy Advance");
        if (directoryHint.Contains("GB ", StringComparison.OrdinalIgnoreCase) || directoryHint.EndsWith("/GB", StringComparison.OrdinalIgnoreCase))
            Add("Nintendo - Game Boy");
        if (directoryHint.Contains("GBC", StringComparison.OrdinalIgnoreCase) || directoryHint.Contains("Game Boy Color", StringComparison.OrdinalIgnoreCase))
            Add("Nintendo - Game Boy Color");
        if (directoryHint.Contains("SMS", StringComparison.OrdinalIgnoreCase) || directoryHint.Contains("Master System", StringComparison.OrdinalIgnoreCase))
            Add("Sega - Master System - Mark III");
        if (directoryHint.Contains("Mega Drive", StringComparison.OrdinalIgnoreCase) || directoryHint.Contains("Genesis", StringComparison.OrdinalIgnoreCase) || directoryHint.EndsWith("/md", StringComparison.OrdinalIgnoreCase))
            Add("Sega - Mega Drive - Genesis");
        if (directoryHint.Contains("NES", StringComparison.OrdinalIgnoreCase) || directoryHint.Contains("Nintendo Entertainment System", StringComparison.OrdinalIgnoreCase))
            Add("Nintendo - Nintendo Entertainment System");
        if (directoryHint.Contains("SNES", StringComparison.OrdinalIgnoreCase) || directoryHint.Contains("Super Nintendo", StringComparison.OrdinalIgnoreCase) || directoryHint.Contains("Super Famicom", StringComparison.OrdinalIgnoreCase))
            Add("Nintendo - Super Nintendo Entertainment System");
        if (directoryHint.Contains("PCE", StringComparison.OrdinalIgnoreCase) || directoryHint.Contains("PC Engine", StringComparison.OrdinalIgnoreCase) || directoryHint.Contains("TurboGrafx", StringComparison.OrdinalIgnoreCase))
            Add("NEC - PC Engine - TurboGrafx 16");

        return candidates;
    }

    private static string GetEffectiveCoverRomExtension(string romPath, string extension)
    {
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".7z", StringComparison.OrdinalIgnoreCase))
        {
            if (RomArchiveExtractor.TryGetArchiveRomEntryExtension(romPath, out string archiveExtension) &&
                !string.IsNullOrWhiteSpace(archiveExtension))
            {
                return archiveExtension;
            }
        }

        return extension;
    }

    private static IEnumerable<string> GetThumbnailArtDirectoryCandidates(string root, string? systemDir)
    {
        static IEnumerable<string> ArtNames()
        {
            yield return "Named_Boxarts";
            yield return "Named Boxarts";
            yield return "Boxarts";
            yield return "Named_Snaps";
            yield return "Named Snaps";
        }

        if (!string.IsNullOrWhiteSpace(systemDir))
        {
            string systemPath = Path.Combine(root, systemDir);
            foreach (string artName in ArtNames())
                yield return Path.Combine(systemPath, artName);
        }

        foreach (string artName in ArtNames())
            yield return Path.Combine(root, artName);
    }

    private static string? FindThumbnailByName(string artDirectory, IEnumerable<string> nameCandidates)
    {
        if (!Directory.Exists(artDirectory))
            return null;

        foreach (string name in nameCandidates)
        {
            foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".webp" })
            {
                string path = Path.Combine(artDirectory, name + extension);
                if (File.Exists(path))
                    return path;
            }
        }

        return null;
    }

    private static string NormalizeThumbnailName(string value)
    {
        string normalized = value.Trim();
        foreach (char ch in "&*/:`<>?\\|\"")
            normalized = normalized.Replace(ch, '_');
        return normalized.Replace("  ", " ", StringComparison.Ordinal).Trim();
    }

    private static string RemoveBracketedSegments(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        int depthParen = 0;
        int depthBracket = 0;
        int written = 0;

        foreach (char ch in value)
        {
            switch (ch)
            {
                case '(':
                    depthParen++;
                    continue;
                case ')':
                    if (depthParen > 0)
                        depthParen--;
                    continue;
                case '[':
                    depthBracket++;
                    continue;
                case ']':
                    if (depthBracket > 0)
                        depthBracket--;
                    continue;
            }

            if (depthParen == 0 && depthBracket == 0)
                buffer[written++] = ch;
        }

        return new string(buffer[..written]).Trim();
    }

    private string? GetCoverCacheRoot()
    {
        if (string.IsNullOrWhiteSpace(_romLibraryPath))
            return null;

        return Path.Combine(_romLibraryPath, ".eutherdrive-thumbnails");
    }

    private void ReloadCoverIndex()
    {
        string? coverCacheRoot = GetCoverCacheRoot();
        var byPath = new Dictionary<string, CoverIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var byHash = new Dictionary<string, CoverIndexEntry>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(coverCacheRoot))
        {
            string indexPath = GetCoverIndexPath(coverCacheRoot);
            if (File.Exists(indexPath))
            {
                try
                {
                    CoverIndexFile? file = JsonSerializer.Deserialize<CoverIndexFile>(File.ReadAllText(indexPath));
                    if (file?.Entries != null)
                    {
                        foreach (CoverIndexEntry entry in file.Entries)
                        {
                            if (string.IsNullOrWhiteSpace(entry.RomPath) ||
                                string.IsNullOrWhiteSpace(entry.CoverRelativePath))
                            {
                                continue;
                            }

                            byPath[entry.RomPath] = entry;
                            if (!string.IsNullOrWhiteSpace(entry.HashHex))
                                byHash[entry.HashHex] = entry;
                        }
                    }
                }
                catch
                {
                }
            }
        }

        lock (_coverIndexLock)
        {
            _coverIndexByPath = byPath;
            _coverIndexByHash = byHash;
            _coverIndexDirty = false;
        }
    }

    private void SaveCoverIndexIfDirty()
    {
        string? coverCacheRoot = GetCoverCacheRoot();
        if (string.IsNullOrWhiteSpace(coverCacheRoot))
            return;

        CoverIndexEntry[] entries;
        lock (_coverIndexLock)
        {
            if (!_coverIndexDirty)
                return;

            entries = _coverIndexByPath.Values
                .OrderBy(static entry => entry.RomPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _coverIndexDirty = false;
        }

        try
        {
            Directory.CreateDirectory(coverCacheRoot);
            string json = JsonSerializer.Serialize(new CoverIndexFile { Entries = entries }, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(GetCoverIndexPath(coverCacheRoot), json);
        }
        catch
        {
            lock (_coverIndexLock)
                _coverIndexDirty = true;
        }
    }

    private void UpsertCoverIndex(string romPath, string? hashHex, string coverPath, bool saveNow)
    {
        string? coverCacheRoot = GetCoverCacheRoot();
        if (string.IsNullOrWhiteSpace(coverCacheRoot))
            return;

        FileInfo? romInfo = TryGetFileInfo(romPath);
        if (romInfo == null)
            return;

        string relativePath = Path.GetRelativePath(coverCacheRoot, coverPath);
        var entry = new CoverIndexEntry
        {
            RomPath = romPath,
            HashHex = hashHex,
            FileLength = romInfo.Length,
            LastWriteUtcTicks = romInfo.LastWriteTimeUtc.Ticks,
            CoverRelativePath = relativePath
        };

        lock (_coverIndexLock)
        {
            _coverIndexByPath[romPath] = entry;
            if (!string.IsNullOrWhiteSpace(hashHex))
                _coverIndexByHash[hashHex] = entry;
            _coverIndexDirty = true;
        }

        if (saveNow)
            SaveCoverIndexIfDirty();
    }

    private string? TryResolveCoverPathFromIndex(string romPath, string? coverCacheRoot)
    {
        if (string.IsNullOrWhiteSpace(coverCacheRoot))
            return null;

        FileInfo? fileInfo = TryGetFileInfo(romPath);
        if (fileInfo == null)
            return null;

        lock (_coverIndexLock)
        {
            if (!_coverIndexByPath.TryGetValue(romPath, out CoverIndexEntry? entry))
                return null;

            if (entry.FileLength != fileInfo.Length || entry.LastWriteUtcTicks != fileInfo.LastWriteTimeUtc.Ticks)
                return null;

            string fullCoverPath = Path.Combine(coverCacheRoot, entry.CoverRelativePath);
            return File.Exists(fullCoverPath) ? fullCoverPath : null;
        }
    }

    private string? TryResolveCoverPathByHash(string hashHex, string? coverCacheRoot)
    {
        if (string.IsNullOrWhiteSpace(coverCacheRoot) || string.IsNullOrWhiteSpace(hashHex))
            return null;

        lock (_coverIndexLock)
        {
            if (!_coverIndexByHash.TryGetValue(hashHex, out CoverIndexEntry? entry))
                return null;

            string fullCoverPath = Path.Combine(coverCacheRoot, entry.CoverRelativePath);
            return File.Exists(fullCoverPath) ? fullCoverPath : null;
        }
    }

    private static string GetCoverIndexPath(string coverCacheRoot)
        => Path.Combine(coverCacheRoot, ".cover-index.json");

    private void StartCoverSync(bool forceRestart)
    {
        string? romLibraryPath = _romLibraryPath;
        string? coverCacheRoot = GetCoverCacheRoot();
        if (string.IsNullOrWhiteSpace(romLibraryPath) || !Directory.Exists(romLibraryPath) || string.IsNullOrWhiteSpace(coverCacheRoot))
            return;

        if (!forceRestart &&
            _coverSyncTask is { IsCompleted: false } &&
            string.Equals(_coverSyncLibraryPath, romLibraryPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (forceRestart)
        {
            _coverSyncCts?.Cancel();
            _coverSyncTask = null;
        }

        _coverSyncLibraryPath = romLibraryPath;
        _coverSyncCts = new CancellationTokenSource();
        Directory.CreateDirectory(coverCacheRoot);
        _coverSyncStatus = "Cover sync: scanning ROM library...";
        UpdateCoverCacheUi();

        _coverSyncTask = Task.Run(() => SyncMissingCoversAsync(romLibraryPath, coverCacheRoot, _coverSyncCts.Token));
    }

    private void StartCoverDeltaSyncIfNeeded()
    {
        string? romLibraryPath = _romLibraryPath;
        string? coverCacheRoot = GetCoverCacheRoot();
        if (string.IsNullOrWhiteSpace(romLibraryPath) || !Directory.Exists(romLibraryPath) || string.IsNullOrWhiteSpace(coverCacheRoot))
            return;

        if (_coverSyncTask is { IsCompleted: false })
            return;

        if (_coverDeltaSyncTask is { IsCompleted: false } &&
            string.Equals(_coverDeltaSyncLibraryPath, romLibraryPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(_coverDeltaSyncLibraryPath, romLibraryPath, StringComparison.OrdinalIgnoreCase) &&
            _coverDeltaSyncTask?.IsCompleted == true)
        {
            return;
        }

        _coverDeltaSyncLibraryPath = romLibraryPath;
        _coverDeltaSyncTask = Task.Run(() => DeltaSyncNewRomsAsync(romLibraryPath, coverCacheRoot));
    }

    private async Task SyncMissingCoversAsync(string romLibraryPath, string coverCacheRoot, CancellationToken cancellationToken)
    {
        try
        {
            List<string> roms = EnumerateRomFiles(romLibraryPath).ToList();
            int total = roms.Count;
            int scanned = 0;
            int downloaded = 0;
            int cached = 0;
            int notFound = 0;
            int networkErrors = 0;

            if (total == 0)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _coverSyncStatus = "Cover sync: no ROMs found in ROM library.";
                    UpdateCoverCacheUi();
                });
                return;
            }

            foreach (string romPath in roms)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned++;

                CoverDownloadResult result = await EnsureCoverAsync(romPath, coverCacheRoot, cancellationToken);
                switch (result)
                {
                    case CoverDownloadResult.Downloaded:
                        downloaded++;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (_listBox.SelectedItem is RomPickerEntry selected &&
                                string.Equals(selected.FullPath, romPath, StringComparison.OrdinalIgnoreCase))
                            {
                                UpdateCoverPreview(selected);
                            }
                        });
                        break;
                    case CoverDownloadResult.AlreadyCached:
                        cached++;
                        break;
                    case CoverDownloadResult.NetworkError:
                        networkErrors++;
                        break;
                    case CoverDownloadResult.NotFound:
                        notFound++;
                        break;
                }

                if ((scanned % 10) == 0 || scanned == total)
                {
                    int percent = (int)Math.Round((double)scanned * 100 / total);
                    int scannedSnapshot = scanned;
                    int downloadedSnapshot = downloaded;
                    int cachedSnapshot = cached;
                    int notFoundSnapshot = notFound;
                    int networkSnapshot = networkErrors;
                    Dispatcher.UIThread.Post(() =>
                    {
                        _coverSyncStatus = networkSnapshot > 0 && downloadedSnapshot == 0 && cachedSnapshot == 0
                            ? $"Cover sync: {percent}% - no contact with Libretro thumbnail server ({scannedSnapshot}/{total})"
                            : $"Cover sync: {percent}% - {downloadedSnapshot} downloaded, {cachedSnapshot} cached, {notFoundSnapshot} missing ({scannedSnapshot}/{total})";
                        UpdateCoverCacheUi();
                    });
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                _coverSyncStatus = networkErrors > 0 && downloaded == 0 && cached == 0
                    ? "Cover sync: no contact with Libretro thumbnail server."
                    : $"Cover sync: done. {downloaded} downloaded, {cached} cached, {notFound} missing.";
                UpdateCoverCacheUi();
            });
            SaveCoverIndexIfDirty();
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _coverSyncStatus = "Cover sync: cancelled";
                UpdateCoverCacheUi();
            });
            SaveCoverIndexIfDirty();
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _coverSyncStatus = $"Cover sync: failed - {ex.Message}";
                UpdateCoverCacheUi();
            });
        }
    }

    private async Task DeltaSyncNewRomsAsync(string romLibraryPath, string coverCacheRoot)
    {
        try
        {
            List<string> pending = new();
            foreach (string romPath in EnumerateRomFiles(romLibraryPath))
            {
                if (!NeedsCoverSync(romPath, coverCacheRoot))
                    continue;
                pending.Add(romPath);
            }

            if (pending.Count == 0)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    SetIdleCoverSyncStatus();
                });
                return;
            }

            int done = 0;
            int downloaded = 0;
            int cached = 0;
            int notFound = 0;
            int networkErrors = 0;

            Dispatcher.UIThread.Post(() =>
            {
                _coverSyncStatus = $"Cover sync: delta mode found {pending.Count} new or changed ROMs";
                UpdateCoverCacheUi();
            });

            foreach (string romPath in pending)
            {
                CoverDownloadResult result = await EnsureCoverAsync(romPath, coverCacheRoot, CancellationToken.None);
                done++;
                switch (result)
                {
                    case CoverDownloadResult.Downloaded:
                        downloaded++;
                        break;
                    case CoverDownloadResult.AlreadyCached:
                        cached++;
                        break;
                    case CoverDownloadResult.NotFound:
                        notFound++;
                        break;
                    case CoverDownloadResult.NetworkError:
                        networkErrors++;
                        break;
                }

                if ((done % 5) == 0 || done == pending.Count)
                {
                    int percent = (int)Math.Round((double)done * 100 / pending.Count);
                    int downloadedSnapshot = downloaded;
                    int cachedSnapshot = cached;
                    int notFoundSnapshot = notFound;
                    int networkSnapshot = networkErrors;
                    int doneSnapshot = done;
                    Dispatcher.UIThread.Post(() =>
                    {
                        _coverSyncStatus = networkSnapshot > 0 && downloadedSnapshot == 0 && cachedSnapshot == 0
                            ? $"Cover sync: delta {percent}% - no contact with Libretro thumbnail server ({doneSnapshot}/{pending.Count})"
                            : $"Cover sync: delta {percent}% - {downloadedSnapshot} downloaded, {cachedSnapshot} cached, {notFoundSnapshot} missing ({doneSnapshot}/{pending.Count})";
                        UpdateCoverCacheUi();
                    });
                }
            }

            SaveCoverIndexIfDirty();
            Dispatcher.UIThread.Post(SetIdleCoverSyncStatus);
        }
        catch
        {
            Dispatcher.UIThread.Post(SetIdleCoverSyncStatus);
        }
    }

    private void QueueCoverDownload(string romPath)
    {
        string? coverCacheRoot = GetCoverCacheRoot();
        if (string.IsNullOrWhiteSpace(coverCacheRoot))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                CoverDownloadResult result = await EnsureCoverAsync(romPath, coverCacheRoot, CancellationToken.None);
                if (result == CoverDownloadResult.Downloaded || result == CoverDownloadResult.AlreadyCached)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_listBox.SelectedItem is RomPickerEntry selected &&
                            string.Equals(selected.FullPath, romPath, StringComparison.OrdinalIgnoreCase))
                        {
                            UpdateCoverPreview(selected);
                        }

                        UpdateCoverCacheUi();
                    });
                }

                SaveCoverIndexIfDirty();
            }
            catch
            {
            }
        });
    }

    private bool NeedsCoverSync(string romPath, string coverCacheRoot)
    {
        string? indexedPath = TryResolveCoverPathFromIndex(romPath, coverCacheRoot);
        if (!string.IsNullOrWhiteSpace(indexedPath) && File.Exists(indexedPath))
            return false;

        string? expectedPath = GetExpectedCoverLocalPath(romPath, coverCacheRoot);
        if (!string.IsNullOrWhiteSpace(expectedPath) && File.Exists(expectedPath))
            return false;

        return true;
    }

    private static IEnumerable<string> EnumerateRomFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string current = pending.Pop();
            IEnumerable<string> subdirs = Array.Empty<string>();
            IEnumerable<string> files = Array.Empty<string>();

            try
            {
                subdirs = Directory.EnumerateDirectories(current);
            }
            catch
            {
            }

            foreach (string subdir in subdirs)
            {
                string name = Path.GetFileName(subdir);
                if (name.Equals(".eutherdrive-thumbnails", StringComparison.OrdinalIgnoreCase))
                    continue;
                pending.Push(subdir);
            }

            try
            {
                files = Directory.EnumerateFiles(current);
            }
            catch
            {
            }

            foreach (string file in files)
            {
                if (IsSupportedRomFile(file))
                    yield return file;
            }
        }
    }

    private static string? GetExpectedCoverLocalPath(string romPath, string coverCacheRoot)
    {
        string romName = Path.GetFileNameWithoutExtension(romPath);
        if (string.IsNullOrWhiteSpace(romName))
            return null;

        string? playlist = GetThumbnailPlaylistCandidates(romPath, Path.GetExtension(romPath)).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(playlist))
            return null;

        string name = NormalizeThumbnailName(romName);
        return Path.Combine(coverCacheRoot, playlist, "Named_Boxarts", name + ".png");
    }

    private async Task<CoverDownloadResult> EnsureCoverAsync(string romPath, string coverCacheRoot, CancellationToken cancellationToken)
    {
        string? localPath = GetExpectedCoverLocalPath(romPath, coverCacheRoot);
        if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
        {
            string? cachedHash = await TryComputeRomHashHexAsync(romPath, cancellationToken);
            UpsertCoverIndex(romPath, cachedHash, localPath, saveNow: false);
            return CoverDownloadResult.AlreadyCached;
        }

        string? hashHex = await TryComputeRomHashHexAsync(romPath, cancellationToken);
        string? indexedCoverPath = TryResolveCoverPathByHash(hashHex ?? string.Empty, coverCacheRoot);
        if (!string.IsNullOrWhiteSpace(indexedCoverPath))
        {
            UpsertCoverIndex(romPath, hashHex, indexedCoverPath, saveNow: false);
            return CoverDownloadResult.AlreadyCached;
        }

        CoverDownloadResult result = await TryDownloadCoverAsync(romPath, coverCacheRoot, cancellationToken);
        if (result is CoverDownloadResult.Downloaded or CoverDownloadResult.AlreadyCached)
        {
            string? resolvedPath = TryResolveCoverPathFromIndex(romPath, coverCacheRoot)
                ?? ResolveCoverPath(romPath, coverCacheRoot)
                ?? GetExpectedCoverLocalPath(romPath, coverCacheRoot);

            if (!string.IsNullOrWhiteSpace(resolvedPath) && File.Exists(resolvedPath))
                UpsertCoverIndex(romPath, hashHex, resolvedPath, saveNow: false);
        }

        return result;
    }

    private static async Task<string?> TryComputeRomHashHexAsync(string romPath, CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = File.OpenRead(romPath);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return null;
        }
    }

    private static FileInfo? TryGetFileInfo(string romPath)
    {
        try
        {
            if (!File.Exists(romPath))
                return null;

            return new FileInfo(romPath);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<CoverDownloadResult> TryDownloadCoverAsync(string romPath, string coverCacheRoot, CancellationToken cancellationToken)
    {
        string romName = Path.GetFileNameWithoutExtension(romPath);
        if (string.IsNullOrWhiteSpace(romName))
            return CoverDownloadResult.NotFound;

        IEnumerable<string> playlistCandidates = GetThumbnailPlaylistCandidates(romPath, Path.GetExtension(romPath));
        IEnumerable<string> fileNameCandidates = new[]
        {
            NormalizeThumbnailName(romName),
            NormalizeThumbnailName(RemoveBracketedSegments(romName)),
            NormalizeThumbnailName(romName.Replace('_', ' ')),
            NormalizeThumbnailName(RemoveBracketedSegments(romName.Replace('_', ' ')))
        }.Where(static value => !string.IsNullOrWhiteSpace(value))
         .Distinct(StringComparer.OrdinalIgnoreCase);

        bool sawNetworkError = false;

        foreach (string playlist in playlistCandidates)
        {
            foreach (string fileName in fileNameCandidates)
            {
                string localDirectory = Path.Combine(coverCacheRoot, playlist, "Named_Boxarts");
                string localPath = Path.Combine(localDirectory, fileName + ".png");
                if (File.Exists(localPath))
                    return CoverDownloadResult.AlreadyCached;

                string url = $"https://thumbnails.libretro.com/{Uri.EscapeDataString(playlist)}/Named_Boxarts/{Uri.EscapeDataString(fileName)}.png";

                try
                {
                    using HttpResponseMessage response = await s_httpClient.GetAsync(url, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                        continue;

                    byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    if (bytes.Length == 0)
                        continue;

                    Directory.CreateDirectory(localDirectory);
                    await File.WriteAllBytesAsync(localPath, bytes, cancellationToken);
                    return CoverDownloadResult.Downloaded;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    sawNetworkError = true;
                }
            }
        }

        return sawNetworkError ? CoverDownloadResult.NetworkError : CoverDownloadResult.NotFound;
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

    protected override void OnClosed(EventArgs e)
    {
        _coverSyncCts?.Cancel();
        SaveCoverIndexIfDirty();
        DisposeCoverPreviewBitmap();
        base.OnClosed(e);
    }

    private enum CoverDownloadResult
    {
        AlreadyCached,
        Downloaded,
        NotFound,
        NetworkError
    }

    private sealed class CoverIndexFile
    {
        public CoverIndexEntry[]? Entries { get; set; }
    }

    private sealed class CoverIndexEntry
    {
        public string RomPath { get; set; } = string.Empty;
        public string? HashHex { get; set; }
        public long FileLength { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string CoverRelativePath { get; set; } = string.Empty;
    }
}

public sealed record RomPickerStats(int Stars, string DetailText, int LaunchCount, double PlaySeconds);
