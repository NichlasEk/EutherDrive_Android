using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using EutherDrive.Core.Savestates;

namespace EutherDrive.UI.Savestates;

public sealed class SavestateViewModel : INotifyPropertyChanged
{
    private readonly SavestateService _service;
    private readonly Func<ISavestateCapable?> _coreProvider;
    private readonly Func<bool> _pauseEmu;
    private readonly Action<bool> _resumeEmu;
    private readonly Action<string> _statusReporter;

    private string _slot1Label = "S1: Empty";
    private string _slot2Label = "S2: Empty";
    private string _slot3Label = "S3: Empty";
    private bool _isAvailable;

    public SavestateViewModel(
        SavestateService service,
        Func<ISavestateCapable?> coreProvider,
        Func<bool> pauseEmu,
        Action<bool> resumeEmu,
        Action<string> statusReporter)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _coreProvider = coreProvider ?? throw new ArgumentNullException(nameof(coreProvider));
        _pauseEmu = pauseEmu ?? throw new ArgumentNullException(nameof(pauseEmu));
        _resumeEmu = resumeEmu ?? throw new ArgumentNullException(nameof(resumeEmu));
        _statusReporter = statusReporter ?? throw new ArgumentNullException(nameof(statusReporter));

        SaveSlot1Command = new RelayCommand(_ => ExecuteSave(1), _ => IsAvailable);
        SaveSlot2Command = new RelayCommand(_ => ExecuteSave(2), _ => IsAvailable);
        SaveSlot3Command = new RelayCommand(_ => ExecuteSave(3), _ => IsAvailable);
        LoadSlot1Command = new RelayCommand(_ => ExecuteLoad(1), _ => IsAvailable);
        LoadSlot2Command = new RelayCommand(_ => ExecuteLoad(2), _ => IsAvailable);
        LoadSlot3Command = new RelayCommand(_ => ExecuteLoad(3), _ => IsAvailable);
        ClearSlot1Command = new RelayCommand(_ => ExecuteClear(1), _ => IsAvailable);
        ClearSlot2Command = new RelayCommand(_ => ExecuteClear(2), _ => IsAvailable);
        ClearSlot3Command = new RelayCommand(_ => ExecuteClear(3), _ => IsAvailable);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand SaveSlot1Command { get; }
    public ICommand SaveSlot2Command { get; }
    public ICommand SaveSlot3Command { get; }
    public ICommand LoadSlot1Command { get; }
    public ICommand LoadSlot2Command { get; }
    public ICommand LoadSlot3Command { get; }
    public ICommand ClearSlot1Command { get; }
    public ICommand ClearSlot2Command { get; }
    public ICommand ClearSlot3Command { get; }

    public bool IsAvailable
    {
        get => _isAvailable;
        private set
        {
            if (value == _isAvailable)
                return;
            _isAvailable = value;
            OnPropertyChanged();
            RaiseCommandState();
        }
    }

    public string Slot1Label
    {
        get => _slot1Label;
        private set => SetField(ref _slot1Label, value);
    }

    public string Slot2Label
    {
        get => _slot2Label;
        private set => SetField(ref _slot2Label, value);
    }

    public string Slot3Label
    {
        get => _slot3Label;
        private set => SetField(ref _slot3Label, value);
    }

    public void Refresh()
    {
        var core = _coreProvider();
        if (core == null || core.RomIdentity == null)
        {
            IsAvailable = false;
            Slot1Label = "S1: Empty";
            Slot2Label = "S2: Empty";
            Slot3Label = "S3: Empty";
            return;
        }

        IsAvailable = true;
        var slots = _service.GetSlotInfo(core);
        Slot1Label = FormatSlotLabel(slots[0]);
        Slot2Label = FormatSlotLabel(slots[1]);
        Slot3Label = FormatSlotLabel(slots[2]);
    }

    private string FormatSlotLabel(SavestateSlotInfo info)
    {
        string slotTag = $"S{info.SlotIndex}";
        if (info.Error != null)
            return $"{slotTag}: {info.Error}";
        if (!info.HasData)
            return $"{slotTag}: Empty";
        if (info.IsCorrupt)
            return $"{slotTag}: Corrupt";
        string time = info.SavedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-";
        return $"{slotTag}: {time}";
    }

    private void ExecuteSave(int slotIndex)
    {
        var core = _coreProvider();
        if (core == null || core.RomIdentity == null)
        {
            _statusReporter("Savestate: no ROM loaded.");
            return;
        }

        bool wasRunning = _pauseEmu();
        try
        {
            _service.Save(core, slotIndex);
            _statusReporter($"Savestate: saved S{slotIndex}.");
        }
        catch (Exception ex)
        {
            _statusReporter($"Savestate save failed: {ex.Message}");
        }
        finally
        {
            _resumeEmu(wasRunning);
        }

        Refresh();
    }

    private void ExecuteLoad(int slotIndex)
    {
        var core = _coreProvider();
        if (core == null || core.RomIdentity == null)
        {
            _statusReporter("Savestate: no ROM loaded.");
            return;
        }

        bool wasRunning = _pauseEmu();
        try
        {
            _service.Load(core, slotIndex);
            _statusReporter($"Savestate: loaded S{slotIndex}.");
        }
        catch (Exception ex)
        {
            _statusReporter($"Savestate load failed: {ex.Message}");
        }
        finally
        {
            _resumeEmu(wasRunning);
        }

        Refresh();
    }

    private void ExecuteClear(int slotIndex)
    {
        var core = _coreProvider();
        if (core == null || core.RomIdentity == null)
        {
            _statusReporter("Savestate: no ROM loaded.");
            return;
        }

        bool wasRunning = _pauseEmu();
        try
        {
            _service.Clear(core, slotIndex);
            _statusReporter($"Savestate: cleared S{slotIndex}.");
        }
        catch (Exception ex)
        {
            _statusReporter($"Savestate clear failed: {ex.Message}");
        }
        finally
        {
            _resumeEmu(wasRunning);
        }

        Refresh();
    }

    private void RaiseCommandState()
    {
        (SaveSlot1Command as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveSlot2Command as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveSlot3Command as RelayCommand)?.RaiseCanExecuteChanged();
        (LoadSlot1Command as RelayCommand)?.RaiseCanExecuteChanged();
        (LoadSlot2Command as RelayCommand)?.RaiseCanExecuteChanged();
        (LoadSlot3Command as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearSlot1Command as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearSlot2Command as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearSlot3Command as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(name);
    }
}
