using System.Collections.ObjectModel;
using PhysicalKey = Avalonia.Input.PhysicalKey;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rewire.Core.Keyboards;
using Rewire.Core.Layouts;
using Rewire.Device.Advantage2;
using Rewire.Device.Platform;
using Rewire.Device.Snapshots;
using Rewire.Services;
using Rewire.Trainer;

namespace Rewire.ViewModels;

public sealed record ActionOption(string Token, string Legend)
{
    public string Display => string.Equals(Legend, Token, StringComparison.OrdinalIgnoreCase) ? Legend : $"{Legend}  ({Token})";
}

public sealed record ProfileItem(string FileName, string Name, int ChangeCount)
{
    public bool HasChanges => ChangeCount > 0;

    public string Summary => ChangeCount switch
    {
        0 => FileName,
        1 => $"{FileName} · 1 change",
        _ => $"{FileName} · {ChangeCount} changes",
    };
}

public sealed record ChangeRow(string Description, string Raw);

public sealed record MacroItem(Macro Macro, string Trigger, string Steps);

public sealed record PresetItem(Preset Preset, PresetState State, string? Warning)
{
    public string Status => State switch
    {
        PresetState.Applied => "Applied",
        PresetState.PartlyApplied => "Partly applied",
        _ => Preset.Description,
    };

    public bool CanApply => State != PresetState.Applied;

    public bool CanRemove => State != PresetState.NotApplied;
}

public sealed record ChangeGroup(string ProfileName, IReadOnlyList<ChangeRow> Changes);

public sealed record SnapshotItem(Snapshot Snapshot)
{
    public string When => Snapshot.TakenAt.ToLocalTime().ToString("d MMM yyyy, HH:mm");

    public string Note => Snapshot.Note;
}

/// <summary>
/// The configurator: edits local working copies of the keyboard's layout files, reviews what
/// changed and applies it through the v-drive.
/// </summary>
public sealed partial class ConfiguratorViewModel : ViewModelBase
{
    private readonly ProfileStore _store;
    private readonly IDrivePlatform _platform;
    private readonly SnapshotStore _snapshots;
    private readonly IReadOnlyList<Preset> _presets;
    private LayoutFile _working = LayoutFile.Empty;
    private string? _swapFrom;

    public ConfiguratorViewModel(
        KeyboardPack pack,
        ProfileStore store,
        IDrivePlatform platform,
        SnapshotStore snapshots,
        IReadOnlyList<Preset>? presets = null)
    {
        Pack = pack;
        _presets = presets ?? [];
        _store = store;
        _platform = platform;
        _snapshots = snapshots;
        Flow = new DeviceFlowViewModel(platform, snapshots);
        Actions = pack.KnownActions
            .Select(token => new ActionOption(token, pack.LegendFor(token)))
            .OrderBy(PickerGroup)
            .ThenBy(action => action.Legend, NaturalOrder)
            .ToList();
        SelectedLayer = pack.Layers[0];
        Reload();
    }

    public KeyboardPack Pack { get; }

    public DeviceFlowViewModel Flow { get; }

    public IReadOnlyList<KeyboardLayer> Layers => Pack.Layers;

    public ObservableCollection<MacroItem> KeyMacros { get; } = [];

    public bool HasKeyMacros => KeyMacros.Count > 0;

    /// <summary>The selected-key panel's tab: 0 for its action, 1 for its macros.</summary>
    [ObservableProperty]
    public partial int KeyPanelTab { get; set; }

    public bool IsEditingMacro => MacroEditor is not null;

    public ObservableCollection<PresetItem> Presets { get; } = [];

    /// <summary>The macro being written or changed, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingMacro))]
    public partial MacroEditorViewModel? MacroEditor { get; private set; }

    public ObservableCollection<ProfileItem> Profiles { get; } = [];

    public ObservableCollection<SnapshotItem> History { get; } = [];

    public IReadOnlyList<ActionOption> Actions { get; }

    public bool HasProfiles => Profiles.Count > 0;

    public bool HasHistory => History.Count > 0;

    public int PendingChangeCount => Profiles.Sum(profile => profile.ChangeCount);

    public bool HasPendingChanges => PendingChangeCount > 0;

    public string PendingSummary => PendingChangeCount switch
    {
        0 => "No changes to apply.",
        1 => "1 change not yet on the keyboard.",
        var count => $"{count} changes not yet on the keyboard.",
    };

    [ObservableProperty]
    public partial EffectiveLayout? Layout { get; private set; }

    [ObservableProperty]
    public partial ProfileItem? SelectedProfile { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedKeyTitle), nameof(SelectedKeyDetail))]
    public partial KeyboardLayer SelectedLayer { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedKeyTitle), nameof(SelectedKeyDetail), nameof(HasSelectedKey))]
    public partial string? SelectedKeyId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredActions))]
    public partial string ActionFilter { get; set; } = "";

    [ObservableProperty]
    public partial string? Prompt { get; private set; }

    [ObservableProperty]
    public partial bool IsCapturing { get; private set; }

    [ObservableProperty]
    public partial bool IsReviewing { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ChangeGroup> Review { get; private set; } = [];

    [ObservableProperty]
    public partial string ApplyNote { get; set; } = "";

    public bool HasSelectedKey => SelectedKey is not null;

    public IEnumerable<ActionOption> FilteredActions => ActionFilter.Trim() is { Length: > 0 } filter
        ? Actions.Where(action =>
            action.Token.Contains(filter, StringComparison.OrdinalIgnoreCase) || action.Legend.Contains(filter, StringComparison.OrdinalIgnoreCase))
        : Actions;

    private EffectiveKey? SelectedKey =>
        SelectedKeyId is null || Layout is null ? null : Layout.Find(SelectedLayer.Id, SelectedKeyId);

    // Named by its keycap, which is the top layer's default, whichever layer is showing.
    public string SelectedKeyTitle => SelectedKey is { } key
        ? $"{Keycap(key.Key.Id)} key, {key.Key.Hand.ToString().ToLowerInvariant()} {key.Key.Finger.ToString().ToLowerInvariant()}"
        : "Select a key to change what it does.";

    public string SelectedKeyDetail => SelectedKey switch
    {
        null => "",
        { Change: KeyChange.TapHold, TapHold: { } tapHold } key =>
            $"Tap for {Legend(tapHold.Tap.Name)}, hold for {tapHold.DelayMilliseconds} ms for {Legend(tapHold.Hold.Name)}. Location token: {key.Location}",
        { Change: KeyChange.Remapped } key => $"Remapped from {Legend(key.DefaultAction)} to {Legend(key.Action)}. Location token: {key.Location}",
        var key => $"Does its default, {Legend(key.DefaultAction)}. Location token: {key.Location}",
    } + MacroSummary();

    [RelayCommand]
    private void ChooseAction(ActionOption option) => SetSelectedKey(option.Token);

    [RelayCommand]
    private void ResetKey()
    {
        if (SelectedKeyId is { } keyId)
            Edit(file => LayoutEditor.ResetToDefault(file, Pack, SelectedLayer.Id, keyId));
    }

    [RelayCommand]
    private void BeginSwap()
    {
        if (SelectedKeyId is null)
            return;

        _swapFrom = SelectedKeyId;
        IsCapturing = false;
        Prompt = $"Select the key to swap with {Keycap(SelectedKeyId)}.";
    }

    [RelayCommand]
    private void BeginCapture()
    {
        if (SelectedKeyId is null)
            return;

        _swapFrom = null;
        IsCapturing = true;
        Prompt = $"Press the key {Keycap(SelectedKeyId)} should produce.";
    }

    [RelayCommand]
    private void CancelPrompt()
    {
        _swapFrom = null;
        IsCapturing = false;
        Prompt = null;
    }

    /// <summary>Called by the window for every key press while <see cref="IsCapturing"/> is on.</summary>
    public void KeyCaptured(PhysicalKey key)
    {
        if (!IsCapturing)
            return;

        if (KeyCapture.ActionFor(key) is { } action)
        {
            SetSelectedKey(action);
            CancelPrompt();
        }
        else
        {
            Prompt = "That key has no Advantage2 equivalent. Pick an action from the list instead.";
        }
    }

    [RelayCommand]
    private void DiscardChanges()
    {
        if (SelectedProfile is null || _store.Find(SelectedProfile.FileName) is not { } profile)
            return;

        _store.SaveWorking(profile.FileName, profile.Keyboard);
        Reload();
    }

    [RelayCommand]
    private void BeginReview()
    {
        var groups = _store.List()
            .Where(profile => profile.HasChanges)
            .Select(profile => new ChangeGroup(
                $"{profile.Name} ({profile.FileName})",
                LayoutDiff.Compare(LayoutFile.Parse(profile.Keyboard), LayoutFile.Parse(profile.Working), Pack)
                    .Select(change => new ChangeRow(change.Description, Raw(change)))
                    .ToList()))
            .ToList();
        if (groups.Count == 0)
            return;

        Review = groups;
        ApplyNote = string.Join(", ", groups.SelectMany(group => group.Changes).Select(change => change.Description).Take(4));
        IsReviewing = true;
    }

    [RelayCommand]
    private void EndReview() => IsReviewing = false;

    [RelayCommand]
    private async Task ApplyAsync()
    {
        var profiles = _store.List().Where(profile => profile.HasChanges).ToList();
        var writes = profiles.Select(profile => new LayoutWrite(profile.FileName, profile.Keyboard, LayoutFile.Parse(profile.Working))).ToList();

        await Flow.ApplyAsync(_ => writes, ApplyNote.Trim() is { Length: > 0 } note ? note : "Changes from Rewire", result =>
        {
            foreach (var write in writes)
                _store.MarkWritten(write.FileName, write.Updated.ToBytes());
        });
        Reload();
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (await Flow.ImportAsync() is { } files)
            _store.Import(files);
        Reload();
    }

    [RelayCommand]
    private async Task RestoreAsync(SnapshotItem item)
    {
        var writer = new VDriveWriter(_platform, _snapshots);
        IReadOnlyList<LayoutWrite> writes = [];

        // A restore replaces the working copies too: the user asked for that version back.
        await Flow.ApplyAsync(
            drive => writes = writer.PlanRestore(drive, item.Snapshot),
            $"Restore {item.When}: {item.Note}",
            _ =>
            {
                foreach (var write in writes)
                {
                    var bytes = write.Updated.ToBytes();
                    if (_store.Find(write.FileName) is null)
                        _store.Import(new Dictionary<string, byte[]> { [write.FileName] = bytes });
                    else
                        _store.MarkWritten(write.FileName, bytes);
                }
            });
        Reload();
    }

    partial void OnSelectedProfileChanged(ProfileItem? value)
    {
        CancelPrompt();
        LoadWorkingCopy();
    }

    [RelayCommand]
    private void NewMacro()
    {
        if (SelectedKeyId is { } keyId)
        {
            MacroEditor = new MacroEditorViewModel(Pack, _working, SelectedLayer, keyId, existing: null);
            KeyPanelTab = 1;
        }
    }

    [RelayCommand]
    private void EditMacro(MacroItem item)
    {
        if (SelectedKeyId is { } keyId)
        {
            MacroEditor = new MacroEditorViewModel(Pack, _working, SelectedLayer, keyId, item.Macro);
            KeyPanelTab = 1;
        }
    }

    [RelayCommand]
    private void DeleteMacro(MacroItem item) => Edit(file => Macros.Remove(file, item.Macro.Trigger));

    [RelayCommand]
    private void SaveMacro()
    {
        if (MacroEditor is not { CanSave: true } editor)
            return;

        var macro = editor.Build();
        MacroEditor = null;
        Edit(file =>
        {
            if (editor.Original is null)
                return Macros.Set(file, macro);

            // An edited macro keeps its place in the file, so the diff shows a change rather than a move.
            var index = Macros.IndexOf(file, editor.Original.Trigger);
            var clash = Macros.IndexOf(file, macro.Trigger);
            if (clash >= 0 && clash != index)
            {
                file = file.RemoveAt(clash);
                if (clash < index)
                    index--;
            }

            return file.Replace(index, macro);
        });
    }

    [RelayCommand]
    private void CancelMacro() => MacroEditor = null;

    [RelayCommand]
    private void ApplyPreset(PresetItem item) => Edit(file => item.Preset.Apply(file, Pack));

    [RelayCommand]
    private void RemovePreset(PresetItem item) => Edit(file => item.Preset.Remove(file, Pack));

    /// <summary>Called by the window for every key press; true when it was used here.</summary>
    public bool KeyPressed(PhysicalKey key)
    {
        if (MacroEditor is { IsRecording: true } editor)
            return editor.KeyDown(key);

        if (!IsCapturing)
            return false;

        KeyCaptured(key);
        return true;
    }

    public bool KeyReleased(PhysicalKey key) => MacroEditor is { IsRecording: true } editor && editor.KeyUp(key);

    partial void OnSelectedLayerChanged(KeyboardLayer value) => RefreshKeyMacros();

    private void RefreshKeyMacros()
    {
        KeyMacros.Clear();
        OnPropertyChanged(nameof(HasKeyMacros));
        if (SelectedKey is not { } key)
            return;

        foreach (var macro in key.Macros)
        {
            var trigger = string.Join(" + ", macro.Trigger.Select(token => Legend(Unprefixed(token.Name))));
            KeyMacros.Add(new MacroItem(macro, trigger, Typed(macro)));
        }

        OnPropertyChanged(nameof(HasKeyMacros));
    }

    // What the macro types, with Shift holds applied, so {-lshift}{9}{+lshift} reads as "(".
    private string Typed(Macro macro)
    {
        var text = new System.Text.StringBuilder();
        var shifts = 0;
        foreach (var token in macro.Playback.Select(MacroToken.Read))
        {
            switch (token)
            {
                case { Kind: MacroTokenKind.Press, Value: "lshift" or "rshift" }:
                    shifts++;
                    break;
                case { Kind: MacroTokenKind.Release, Value: "lshift" or "rshift" }:
                    shifts = Math.Max(0, shifts - 1);
                    break;
                case { Kind: MacroTokenKind.Key }:
                    var character = shifts > 0 ? CharacterActions.ShiftedCharacterFor(token.Value) : CharacterActions.CharacterFor(token.Value);
                    text.Append(character is { } typed ? typed.ToString() : $"[{Legend(token.Value)}]");
                    break;
            }
        }

        return text.ToString();
    }

    private static string Unprefixed(string location) =>
        location.StartsWith("kp-", StringComparison.OrdinalIgnoreCase) ? location[3..] : location;

    private void RefreshPresets()
    {
        Presets.Clear();
        if (Layout is not { } layout)
            return;

        foreach (var preset in _presets)
        {
            var overrides = preset.StateIn(layout) == PresetState.Applied ? [] : preset.Overrides(layout);
            var warning = overrides.Count == 0
                ? null
                : $"Replaces your remap of {string.Join(", ", overrides.Select(key => $"{Legend(key.DefaultAction)} ({Legend(key.Action)})"))}.";
            Presets.Add(new PresetItem(preset, preset.StateIn(layout), warning));
        }
    }

    private void LoadWorkingCopy()
    {
        _working = SelectedProfile is { } item && _store.Find(item.FileName) is { } profile
            ? LayoutFile.Parse(profile.Working)
            : LayoutFile.Empty;
        Layout = EffectiveLayout.Build(Pack, _working);
        RefreshKeyMacros();
        RefreshPresets();
        OnPropertyChanged(nameof(SelectedKeyTitle));
        OnPropertyChanged(nameof(SelectedKeyDetail));
        OnPropertyChanged(nameof(HasSelectedKey));
    }

    partial void OnSelectedKeyIdChanged(string? oldValue, string? newValue)
    {
        RefreshKeyMacros();
        if (newValue != oldValue)
            MacroEditor = null;

        if (_swapFrom is not { } from || newValue is null || newValue == from)
            return;

        _swapFrom = null;
        Prompt = null;
        Edit(file => LayoutEditor.Swap(file, Pack, SelectedLayer.Id, from, newValue));
    }

    private void SetSelectedKey(string action)
    {
        if (SelectedKeyId is { } keyId)
            Edit(file => LayoutEditor.SetAction(file, Pack, SelectedLayer.Id, keyId, action));
    }

    private void Edit(Func<LayoutFile, LayoutFile> change)
    {
        if (SelectedProfile is not { } profile)
            return;

        var edited = change(_working);
        if (ReferenceEquals(edited, _working))
            return;

        _store.SaveWorking(profile.FileName, edited.ToBytes());
        Reload();
    }

    private void Reload()
    {
        var selectedFile = SelectedProfile?.FileName;
        var selectedKey = SelectedKeyId;

        Profiles.Clear();
        foreach (var profile in _store.List())
        {
            var changes = profile.HasChanges
                ? LayoutDiff.Compare(LayoutFile.Parse(profile.Keyboard), LayoutFile.Parse(profile.Working), Pack).Count
                : 0;
            Profiles.Add(new ProfileItem(profile.FileName, profile.Name, changes));
        }

        History.Clear();
        foreach (var snapshot in _snapshots.List())
            History.Add(new SnapshotItem(snapshot));

        SelectedProfile = Profiles.FirstOrDefault(profile => profile.FileName == selectedFile) ?? Profiles.FirstOrDefault();

        // An edit can leave the profile row equal to before, which would not raise a selection change.
        LoadWorkingCopy();
        SelectedKeyId = selectedKey;
        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(PendingChangeCount));
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(PendingSummary));
    }

    private string MacroSummary() => SelectedKey?.Macros.Count switch
    {
        null or 0 => "",
        1 => $"{Environment.NewLine}Triggers a macro: {SelectedKey.Macros[0].Render()}",
        var count => $"{Environment.NewLine}Triggers {count} macros: {string.Join("  ", SelectedKey.Macros.Select(macro => macro.Render()))}",
    };

    private static string Raw(LayoutChange change) => change.Kind switch
    {
        LayoutChangeKind.Added => $"+ {change.After!.Render()}",
        LayoutChangeKind.Removed => $"− {change.Before!.Render()}",
        _ => $"{change.Before!.Render()}  →  {change.After!.Render()}",
    };

    private string Keycap(string keyId) =>
        Layout?.Find(Layers[0].Id, keyId) is { } topLayerKey ? Legend(topLayerKey.DefaultAction) : Legend(keyId);

    private string Legend(string action) => Pack.LegendFor(action);

    // Letters, then digits, then everything else, so the commonest choices come first.
    private static int PickerGroup(ActionOption action) => action.Token switch
    {
        [var c] when char.IsAsciiLetter(c) => 0,
        [var c] when char.IsAsciiDigit(c) => 1,
        _ => 2,
    };

    // Orders F2 before F10, and Kp 2 before Kp 10.
    private static readonly IComparer<string> NaturalOrder = Comparer<string>.Create((left, right) =>
    {
        var (leftStem, leftNumber) = SplitTrailingNumber(left);
        var (rightStem, rightNumber) = SplitTrailingNumber(right);
        var byStem = string.Compare(leftStem, rightStem, StringComparison.OrdinalIgnoreCase);
        return byStem != 0 ? byStem : leftNumber.CompareTo(rightNumber);
    });

    private static (string Stem, int Number) SplitTrailingNumber(string text)
    {
        var digits = text.Length - text.AsSpan().TrimEnd("0123456789").Length;
        return digits is > 0 and < 9 && digits < text.Length
            ? (text[..^digits], int.Parse(text[^digits..]))
            : (text, -1);
    }

}
