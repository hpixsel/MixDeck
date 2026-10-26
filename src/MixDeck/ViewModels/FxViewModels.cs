using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MixDeck.Audio;
using MixDeck.Model;

namespace MixDeck.ViewModels;

/// <summary>Microphone effects of one input channel. Every change rebuilds the engine chain.</summary>
public partial class InputFxViewModel : ObservableObject
{
    readonly MixerService _service;
    public ChannelViewModel Channel { get; }
    public bool NoiseSuppressionAvailable => NoiseSuppressor.Available;

    [ObservableProperty] bool noiseSuppression;
    [ObservableProperty] bool highPass;
    [ObservableProperty] bool gate;
    [ObservableProperty] double gateThresholdDb;
    [ObservableProperty] bool compressor;
    [ObservableProperty] double gainDb;

    public InputFxViewModel(MixerService service, ChannelViewModel channel, InputFxConfig c)
    {
        _service = service;
        Channel = channel;
        noiseSuppression = c.NoiseSuppression;
        highPass = c.HighPass;
        gate = c.Gate;
        gateThresholdDb = c.GateThresholdDb;
        compressor = c.Compressor;
        gainDb = c.GainDb;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        _service.SetInputFx(Channel.Id, new InputFxConfig
        {
            NoiseSuppression = NoiseSuppression,
            HighPass = HighPass,
            Gate = Gate,
            GateThresholdDb = Math.Round(GateThresholdDb),
            Compressor = Compressor,
            GainDb = Math.Round(GainDb),
        });
    }
}

/// <summary>EQ, headphone profile and limiter of one output.</summary>
public partial class OutputFxViewModel : ObservableObject
{
    readonly MixerService _service;
    readonly OutputFxConfig _cfg;
    public MixViewModel Mix { get; }

    [ObservableProperty] double bass;
    [ObservableProperty] double mid;
    [ObservableProperty] double treble;
    [ObservableProperty] bool limiter;
    [ObservableProperty] string? profileName;

    public OutputFxViewModel(MixerService service, MixViewModel mix, OutputFxConfig c)
    {
        _service = service;
        Mix = mix;
        _cfg = c;
        bass = c.Bass;
        mid = c.Mid;
        treble = c.Treble;
        limiter = c.Limiter;
        profileName = c.ProfileName;
    }

    partial void OnBassChanged(double value) => Push();
    partial void OnMidChanged(double value) => Push();
    partial void OnTrebleChanged(double value) => Push();
    partial void OnLimiterChanged(bool value) => Push();

    void Push()
    {
        _service.SetMixFx(Mix.Id, new OutputFxConfig
        {
            Bass = Math.Round(Bass, 1),
            Mid = Math.Round(Mid, 1),
            Treble = Math.Round(Treble, 1),
            Limiter = Limiter,
            ProfileName = ProfileName,
            ProfilePreamp = _cfg.ProfilePreamp,
            ProfileBands = _cfg.ProfileBands,
        });
    }

    [RelayCommand]
    void LoadProfile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the AutoEQ “ParametricEQ.txt” file for your headphones",
            Filter = "AutoEQ ParametricEQ (*.txt)|*.txt|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        var (preamp, bands) = AutoEq.Parse(File.ReadAllText(dlg.FileName));
        if (bands.Count == 0)
        {
            System.Windows.MessageBox.Show("This file has no AutoEQ filters (lines like “Filter 1: ON PK Fc … Hz Gain … dB Q …”).",
                "MixDeck", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        _cfg.ProfilePreamp = preamp;
        _cfg.ProfileBands = bands;
        ProfileName = Path.GetFileNameWithoutExtension(dlg.FileName).Replace(" ParametricEQ", "");
        Push();
    }

    [RelayCommand]
    void ClearProfile()
    {
        _cfg.ProfileBands = [];
        _cfg.ProfilePreamp = 0;
        ProfileName = null;
        Push();
    }

    [RelayCommand]
    void Reset()
    {
        Bass = Mid = Treble = 0;
    }
}

public partial class DuckTargetViewModel(MainViewModel owner, ChannelViewModel channel, bool selected) : ObservableObject
{
    public ChannelViewModel Channel { get; } = channel;
    [ObservableProperty] bool isSelected = selected;
    partial void OnIsSelectedChanged(bool value) => owner.PushDucking();
}

public partial class MainViewModel
{
    public ObservableCollection<DuckTargetViewModel> DuckTargets { get; } = [];

    [ObservableProperty] bool duckingEnabled;
    [ObservableProperty] string? duckTriggerId;
    [ObservableProperty] double duckAmountDb;
    [ObservableProperty] double duckThresholdDb;
    [ObservableProperty] bool lowLatency;

    // Editors opened from the "⋯" menus
    [ObservableProperty] InputFxViewModel? inputFxEditor;
    [ObservableProperty] OutputFxViewModel? outputFxEditor;

    bool _loadingDucking;

    void InitFx()
    {
        var d = Service.Config.Ducking;
        _loadingDucking = true;
        // Sensible first-time defaults: duck music/browser while Chat is active
        DuckTriggerId = d.TriggerChannelId ?? Channels.FirstOrDefault(c => Service.Config.Channels.First(x => x.Id == c.Id).Icon == "chat")?.Id;
        DuckAmountDb = d.AmountDb;
        DuckThresholdDb = d.ThresholdDb;
        DuckingEnabled = d.Enabled;
        LowLatency = Service.Config.LowLatency;
        RebuildDuckTargets(d.TargetChannelIds.Count > 0
            ? d.TargetChannelIds
            : Channels.Where(c => Service.Config.Channels.First(x => x.Id == c.Id).Icon is "music" or "browser").Select(c => c.Id).ToList());
        _loadingDucking = false;
        Channels.CollectionChanged += (_, _) => RebuildDuckTargets(DuckTargets.Where(t => t.IsSelected).Select(t => t.Channel.Id).ToList());
    }

    void RebuildDuckTargets(IReadOnlyCollection<string> selected)
    {
        DuckTargets.Clear();
        foreach (var ch in Channels.Where(c => c.Id != DuckTriggerId))
            DuckTargets.Add(new DuckTargetViewModel(this, ch, selected.Contains(ch.Id)));
    }

    partial void OnDuckingEnabledChanged(bool value) => PushDucking();
    partial void OnDuckAmountDbChanged(double value) => PushDucking();
    partial void OnDuckThresholdDbChanged(double value) => PushDucking();

    partial void OnDuckTriggerIdChanged(string? value)
    {
        if (_loadingDucking) return;
        RebuildDuckTargets(DuckTargets.Where(t => t.IsSelected).Select(t => t.Channel.Id).ToList());
        PushDucking();
    }

    public void PushDucking()
    {
        if (_loadingDucking) return;
        Service.SetDucking(new DuckingConfig
        {
            Enabled = DuckingEnabled,
            TriggerChannelId = DuckTriggerId,
            TargetChannelIds = DuckTargets.Where(t => t.IsSelected).Select(t => t.Channel.Id).ToList(),
            AmountDb = Math.Round(DuckAmountDb),
            ThresholdDb = Math.Round(DuckThresholdDb),
        });
    }

    partial void OnLowLatencyChanged(bool value) => Service.SetLowLatency(value);

    public void OpenInputFx(ChannelViewModel ch)
    {
        var cfg = Service.Config.Channels.First(c => c.Id == ch.Id).InputFx;
        InputFxEditor = new InputFxViewModel(Service, ch, cfg);
    }

    public void OpenOutputFx(MixViewModel mix)
    {
        var cfg = Service.Config.Mixes.First(m => m.Id == mix.Id).Fx;
        OutputFxEditor = new OutputFxViewModel(Service, mix, cfg);
    }

    void UpdateDuckBadges()
    {
        bool active = DuckingEnabled && Service.DuckGain < 0.9f;
        foreach (var ch in Channels) ch.IsDucked = active && DuckTargets.Any(t => t.IsSelected && t.Channel == ch);
    }
}
