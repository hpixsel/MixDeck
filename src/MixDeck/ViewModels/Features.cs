using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MixDeck.Model;

namespace MixDeck.ViewModels;

public sealed record Choice(string? Id, string Name);

public partial class ProfileViewModel : ObservableObject
{
    readonly MainViewModel _owner;
    public ProfileConfig Config { get; }
    public string Id => Config.Id;

    [ObservableProperty] string name;
    [ObservableProperty] string triggerApps;
    [ObservableProperty] bool isActive;

    public ProfileViewModel(MainViewModel owner, ProfileConfig p)
    {
        _owner = owner;
        Config = p;
        name = p.Name;
        triggerApps = string.Join(", ", p.TriggerApps);
    }

    partial void OnNameChanged(string value)
    {
        Config.Name = value;
        _owner.Service.Save();
    }

    partial void OnTriggerAppsChanged(string value)
    {
        Config.TriggerApps = value.Split(',', ';', ' ')
            .Select(a => a.Trim().ToLowerInvariant())
            .Where(a => a.Length > 0)
            .Select(a => a.EndsWith(".exe") ? a : a + ".exe")
            .Distinct().ToList();
        _owner.Service.Save();
    }

    [RelayCommand] void Apply() => _owner.ApplyProfile(this, automatic: false);
    [RelayCommand] void Overwrite() => _owner.OverwriteProfile(this);
    [RelayCommand] void Delete() => _owner.DeleteProfile(this);
}

public partial class HotkeyViewModel : ObservableObject
{
    readonly MainViewModel _owner;
    public HotkeyConfig Config { get; }

    public static readonly IReadOnlyList<Choice> Actions =
    [
        new(HotkeyAction.MuteChannel, "Mute / unmute channel"),
        new(HotkeyAction.VolumeUp, "Channel volume up (+5%)"),
        new(HotkeyAction.VolumeDown, "Channel volume down (−5%)"),
        new(HotkeyAction.MuteMix, "Mute / unmute output"),
        new(HotkeyAction.Profile, "Switch to profile"),
    ];

    [ObservableProperty] string gesture;
    [ObservableProperty] string action;
    [ObservableProperty] string? targetId;
    [ObservableProperty] string? mixId;
    [ObservableProperty] bool failed;
    [ObservableProperty] bool isRecording;

    public IReadOnlyList<Choice> Targets => Action switch
    {
        HotkeyAction.MuteMix => _owner.Mixes.Select(m => new Choice(m.Id, m.Name)).ToList(),
        HotkeyAction.Profile => _owner.Profiles.Select(p => new Choice(p.Id, p.Name)).ToList(),
        _ => _owner.Channels.Select(c => new Choice(c.Id, c.Name)).ToList(),
    };

    public IReadOnlyList<Choice> MixChoices =>
        [new(null, "on all outputs"), .. _owner.Mixes.Select(m => new Choice(m.Id, $"on {m.Name}"))];

    public bool NeedsMix => Action is HotkeyAction.VolumeUp or HotkeyAction.VolumeDown;

    public HotkeyViewModel(MainViewModel owner, HotkeyConfig h)
    {
        _owner = owner;
        Config = h;
        gesture = h.Gesture;
        action = h.Action;
        targetId = h.TargetId;
        mixId = h.MixId;
    }

    partial void OnGestureChanged(string value) { Config.Gesture = value; _owner.HotkeysEdited(); }
    partial void OnTargetIdChanged(string? value) { Config.TargetId = value; _owner.HotkeysEdited(); }
    partial void OnMixIdChanged(string? value) { Config.MixId = value; _owner.HotkeysEdited(); }

    partial void OnActionChanged(string value)
    {
        Config.Action = value;
        OnPropertyChanged(nameof(Targets));
        OnPropertyChanged(nameof(NeedsMix));
        TargetId = Targets.FirstOrDefault()?.Id;
        _owner.HotkeysEdited();
    }

    public void RefreshChoices()
    {
        OnPropertyChanged(nameof(Targets));
        OnPropertyChanged(nameof(MixChoices));
    }

    [RelayCommand] void Delete() => _owner.DeleteHotkey(this);
}

public partial class MainViewModel
{
    public ObservableCollection<ProfileViewModel> Profiles { get; } = [];
    public ObservableCollection<HotkeyViewModel> Hotkeys { get; } = [];

    /// <summary>Raised to show the on-screen confirmation: glyph, accent, title, detail, level (or null).</summary>
    public event Action<string, Brush, string, string, double?>? OsdRequested;
    /// <summary>Raised after hotkeys change so the app can re-register them.</summary>
    public event Action? HotkeysChanged;

    [ObservableProperty] ProfileViewModel? activeProfile;
    /// <summary>Section to scroll to when the settings panel opens ("routing", "ducking", "updates").</summary>
    public string? SettingsSection { get; set; }

    public void OpenSettings(string? section)
    {
        SettingsSection = section;
        SettingsOpen = true;
    }
    [ObservableProperty] string newProfileName = "";
    [ObservableProperty] bool showOsd;

    // ---- Tray flyout: one output at a time ----
    [ObservableProperty] MixViewModel? flyoutMix;
    public IEnumerable<SendViewModel> FlyoutSends =>
        FlyoutMix == null ? [] : Channels.Select(c => c.Sends.FirstOrDefault(s => s.Mix == FlyoutMix)).OfType<SendViewModel>();
    partial void OnFlyoutMixChanged(MixViewModel? value) => OnPropertyChanged(nameof(FlyoutSends));
    public void RefreshFlyout()
    {
        FlyoutMix ??= Mixes.FirstOrDefault();
        OnPropertyChanged(nameof(FlyoutSends));
    }

    void InitFeatures()
    {
        ShowOsd = Service.Config.ShowOsd;
        foreach (var p in Service.Config.Profiles) Profiles.Add(new ProfileViewModel(this, p));
        foreach (var h in Service.Config.Hotkeys) Hotkeys.Add(new HotkeyViewModel(this, h));
        Service.RunningAppsChanged += running => _dispatcher.BeginInvoke(() => AutoSwitchProfile(running));
        Channels.CollectionChanged += (_, _) => { foreach (var h in Hotkeys) h.RefreshChoices(); RefreshFlyout(); };
        Mixes.CollectionChanged += (_, _) => { foreach (var h in Hotkeys) h.RefreshChoices(); RefreshFlyout(); };
        FlyoutMix = Mixes.FirstOrDefault();
        IsWizardOpen = Service.IsFirstRun;
        InitFx();
        InitRemote();
        InitUpdates();
        RefreshWizardChecks();
    }

    partial void OnShowOsdChanged(bool value)
    {
        Service.Config.ShowOsd = value;
        Service.Save();
    }

    #region Profiles

    ProfileConfig Snapshot(string name)
    {
        var p = new ProfileConfig { Name = name };
        foreach (var ch in Channels)
        {
            var st = new ChannelState { Muted = ch.Muted };
            foreach (var s in ch.Sends) st.Sends[s.Mix.Id] = new SendConfig { Volume = s.Volume, Muted = s.Muted };
            p.Channels[ch.Id] = st;
        }
        foreach (var m in Mixes) p.Mixes[m.Id] = new SendConfig { Volume = m.Volume, Muted = m.Muted };
        return p;
    }

    void ApplySnapshot(ProfileConfig p)
    {
        foreach (var ch in Channels)
        {
            if (!p.Channels.TryGetValue(ch.Id, out var st)) continue;
            ch.Muted = st.Muted;
            foreach (var s in ch.Sends)
                if (st.Sends.TryGetValue(s.Mix.Id, out var sc)) { s.Volume = sc.Volume; s.Muted = sc.Muted; }
        }
        foreach (var m in Mixes)
            if (p.Mixes.TryGetValue(m.Id, out var mc)) { m.Volume = mc.Volume; m.Muted = mc.Muted; }
    }

    [RelayCommand]
    void SaveNewProfile()
    {
        var name = string.IsNullOrWhiteSpace(NewProfileName) ? $"Profile {Profiles.Count + 1}" : NewProfileName.Trim();
        var p = Snapshot(name);
        Service.Config.Profiles.Add(p);
        Service.Save();
        var vm = new ProfileViewModel(this, p);
        Profiles.Add(vm);
        SetActive(vm);
        NewProfileName = "";
        foreach (var h in Hotkeys) h.RefreshChoices();
    }

    public void OverwriteProfile(ProfileViewModel vm)
    {
        var snap = Snapshot(vm.Name);
        vm.Config.Channels = snap.Channels;
        vm.Config.Mixes = snap.Mixes;
        Service.Save();
        SetActive(vm);
    }

    public void DeleteProfile(ProfileViewModel vm)
    {
        Service.Config.Profiles.Remove(vm.Config);
        Service.Save();
        Profiles.Remove(vm);
        if (ActiveProfile == vm) SetActive(null);
        foreach (var h in Hotkeys) h.RefreshChoices();
    }

    ProfileConfig? _beforeAuto;
    ProfileViewModel? _autoProfile;

    public void ApplyProfile(ProfileViewModel vm, bool automatic)
    {
        if (!automatic) { _beforeAuto = null; _autoProfile = null; }
        ApplySnapshot(vm.Config);
        SetActive(vm);
        OsdRequested?.Invoke("", Palette.Brush("#2DD4BF"), vm.Name, automatic ? "Profile switched on automatically" : "Profile switched on", null);
    }

    void SetActive(ProfileViewModel? vm)
    {
        foreach (var p in Profiles) p.IsActive = p == vm;
        ActiveProfile = vm;
    }

    /// <summary>Turns on a profile while one of its trigger apps runs, and restores the previous mix afterwards.</summary>
    void AutoSwitchProfile(IReadOnlySet<string> running)
    {
        var wanted = Profiles.FirstOrDefault(p => p.Config.TriggerApps.Any(running.Contains));
        if (wanted != null && wanted != ActiveProfile)
        {
            if (_autoProfile == null) _beforeAuto = Snapshot("before");
            _autoProfile = wanted;
            ApplyProfile(wanted, automatic: true);
        }
        else if (wanted == null && _autoProfile != null)
        {
            if (_beforeAuto != null) ApplySnapshot(_beforeAuto);
            OsdRequested?.Invoke("", Palette.Brush("#2DD4BF"), "Previous settings restored", $"The app of profile “{_autoProfile.Name}” was closed", null);
            _autoProfile = null;
            _beforeAuto = null;
            SetActive(null);
        }
    }

    #endregion

    #region Hotkeys

    [RelayCommand]
    void AddHotkey()
    {
        var h = new HotkeyConfig { Action = HotkeyAction.MuteChannel, TargetId = Channels.FirstOrDefault()?.Id };
        Service.Config.Hotkeys.Add(h);
        var vm = new HotkeyViewModel(this, h) { IsRecording = true };
        Hotkeys.Add(vm);
        Service.Save();
    }

    public void DeleteHotkey(HotkeyViewModel vm)
    {
        Service.Config.Hotkeys.Remove(vm.Config);
        Hotkeys.Remove(vm);
        HotkeysEdited();
    }

    public void HotkeysEdited()
    {
        Service.Save();
        HotkeysChanged?.Invoke();
    }

    public void MarkFailedHotkeys(IEnumerable<HotkeyConfig> failed)
    {
        var set = failed.ToHashSet();
        foreach (var h in Hotkeys) h.Failed = set.Contains(h.Config) && h.Config.Gesture.Length > 0;
    }

    public void ExecuteHotkey(HotkeyConfig h)
    {
        var ch = Channels.FirstOrDefault(c => c.Id == h.TargetId);
        switch (h.Action)
        {
            case HotkeyAction.MuteChannel when ch != null:
                ch.Muted = !ch.Muted;
                Osd(ch.Muted ? "" : "", ch.Accent, ch.Name, ch.Muted ? "Muted on all outputs" : "Unmuted", null);
                break;

            case HotkeyAction.VolumeUp or HotkeyAction.VolumeDown when ch != null:
                double step = h.Action == HotkeyAction.VolumeUp ? 5 : -5;
                var sends = ch.Sends.Where(s => h.MixId == null || s.Mix.Id == h.MixId).ToList();
                foreach (var s in sends) s.Volume = Math.Clamp(Math.Round(s.Volume + step), 0, 100);
                var shown = sends.FirstOrDefault();
                if (shown != null)
                    Osd("", shown.Mix.Accent, ch.Name, h.MixId == null ? $"{shown.Volume:0}% (all outputs)" : $"{shown.Volume:0}% on {shown.Mix.Name}", shown.Volume);
                break;

            case HotkeyAction.MuteMix when Mixes.FirstOrDefault(m => m.Id == h.TargetId) is { } mix:
                mix.Muted = !mix.Muted;
                Osd(mix.Muted ? "" : "", mix.Accent, mix.Name, mix.Muted ? "Output muted" : "Output unmuted", null);
                break;

            case HotkeyAction.Profile when Profiles.FirstOrDefault(p => p.Id == h.TargetId) is { } profile:
                ApplyProfile(profile, automatic: false);
                break;
        }
    }

    void Osd(string glyph, Brush accent, string title, string detail, double? level)
    {
        if (ShowOsd) OsdRequested?.Invoke(glyph, accent, title, detail, level);
    }

    #endregion

    #region First-run wizard

    const string WaveLinkStartupKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\Elgato.WaveLink_g54w8ztgkx496\E31D1357-81DB-467F-8B5B-61BEBA684A0F";

    [ObservableProperty] bool isWizardOpen;
    [ObservableProperty] int wizardStep;
    [ObservableProperty] bool waveLinkRunning;
    [ObservableProperty] bool waveLinkAutostart;
    [ObservableProperty] bool collectorFound;

    public int WizardStepCount => 4;
    public bool IsLastWizardStep => WizardStep == WizardStepCount - 1;
    partial void OnWizardStepChanged(int value) => OnPropertyChanged(nameof(IsLastWizardStep));

    public void RefreshWizardChecks()
    {
        WaveLinkRunning = Process.GetProcessesByName("Elgato.WaveLink").Length > 0;
        bool installed = Directory.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", "Elgato.WaveLink_g54w8ztgkx496"));
        using var key = Registry.CurrentUser.OpenSubKey(WaveLinkStartupKey);
        // StartupTask state: 2 = enabled, 4 = enabled by policy, 0/1 = disabled.
        // No key yet means the user never toggled it, so the manifest default (enabled) applies.
        WaveLinkAutostart = installed && (key?.GetValue("State") is not int state || state >= 2);
        CollectorFound = Service.CollectorId != null;
    }

    [RelayCommand] void WizardNext() { RefreshWizardChecks(); WizardStep = Math.Min(WizardStep + 1, WizardStepCount - 1); }
    [RelayCommand] void WizardBack() => WizardStep = Math.Max(WizardStep - 1, 0);

    [RelayCommand]
    void WizardFinish()
    {
        Service.MarkFirstRunDone();
        IsWizardOpen = false;
        WizardStep = 0;
    }

    [RelayCommand]
    void OpenWizard()
    {
        SettingsOpen = false;
        WizardStep = 0;
        RefreshWizardChecks();
        IsWizardOpen = true;
    }

    [RelayCommand]
    void CloseWaveLink()
    {
        foreach (var p in Process.GetProcessesByName("Elgato.WaveLink"))
        {
            try { p.CloseMainWindow(); if (!p.WaitForExit(3000)) p.Kill(); } catch { }
        }
        RefreshWizardChecks();
    }

    [RelayCommand]
    void DisableWaveLinkAutostart()
    {
        // Same switch as Settings → Apps → Startup (1 = disabled by user)
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(WaveLinkStartupKey);
            key.SetValue("State", 1, RegistryValueKind.DWord);
            Log.Write("Disabled Wave Link autostart");
        }
        catch (Exception ex)
        {
            Log.Write($"Disabling Wave Link autostart failed: {ex.Message}");
        }
        RefreshWizardChecks();
    }

    [RelayCommand]
    void OpenStartupSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }); } catch { }
    }

    #endregion
}
