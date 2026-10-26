using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MixDeck.Audio;
using MixDeck.Model;

namespace MixDeck.ViewModels;

public static class Palette
{
    /// <summary>One accent per output, so every slider in an output's column shares its colour.</summary>
    static readonly string[] OutputAccents = ["#A78BFA", "#FBBF24", "#38BDF8", "#F472B6", "#A3E635", "#FB923C", "#22D3EE", "#FB7185"];

    public static Brush OutputAccent(int index) => Brush(OutputAccents[index % OutputAccents.Length]);

    public static (string Glyph, string Color) Icon(string icon) => icon switch
    {
        "system" => ("", "#60A5FA"),
        "browser" => ("", "#FB923C"),
        "music" => ("", "#3DDC97"),
        "chat" => ("", "#818CF8"),
        "game" => ("", "#F25F5C"),
        "mic" => ("", "#2DD4BF"),
        "headphones" => ("", ""),
        "speakers" => ("", ""),
        "stream" => ("", ""),
        _ => ("", "#C084FC"),
    };

    public static Brush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>The same colour at low opacity, for icon tiles.</summary>
    public static Brush Tint(Brush accent, byte alpha = 0x2E)
    {
        var c = ((SolidColorBrush)accent).Color;
        var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }
}

/// <summary>Maps a peak amplitude to a 0..1 meter position on a 60 dB scale.</summary>
static class Meter
{
    public static double ToFraction(double peak) =>
        peak <= 0.001 ? 0 : Math.Clamp((20 * Math.Log10(peak) + 60) / 60, 0, 1);

    public static double Fall(double current, double next) => Math.Max(next, current - 0.04);
}

static class AppIcons
{
    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? For(string? path)
    {
        if (path == null) return null;
        if (Cache.TryGetValue(path, out var img)) return img;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon != null)
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                src.Freeze();
                img = src;
            }
        }
        catch { }
        return Cache[path] = img;
    }
}

public sealed record DeviceOption(string? Id, string Name);

public static class Converters
{
    public static readonly System.Windows.Data.IValueConverter IsPositive = new IsPositiveConverter();
    /// <summary>Visible when the bound int equals the ConverterParameter (wizard steps).</summary>
    public static readonly System.Windows.Data.IValueConverter StepIs = new StepConverter();
    /// <summary>Brand colour for the current wizard step's dot, faint for the others.</summary>
    public static readonly System.Windows.Data.IValueConverter StepDot = new StepDotConverter();

    sealed class StepDotConverter : System.Windows.Data.IValueConverter
    {
        static readonly Brush On = Palette.Brush("#2DD4BF"), Off = Palette.Brush("#2A2F3B");
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) =>
            value is int i && int.TryParse(p?.ToString(), out int want) && i == want ? On : Off;
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }
    public static readonly System.Windows.Data.IValueConverter NotNullToVisibility = new Lambda(v => v != null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed);
    /// <summary>0..1 × ConverterParameter (pixels) → width.</summary>
    public static readonly System.Windows.Data.IValueConverter FractionToWidth = new FractionWidthConverter();

    sealed class FractionWidthConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) =>
            value is double d && double.TryParse(p?.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var w) ? Math.Clamp(d, 0, 1) * w : 0.0;
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>[enabled, url]: show the status as an error when enabled but no URL (server failed).</summary>
    public static readonly System.Windows.Data.IMultiValueConverter ErrorVisibility = new ErrorVisibilityConverter();

    sealed class ErrorVisibilityConverter : System.Windows.Data.IMultiValueConverter
    {
        public object Convert(object[] v, Type t, object p, System.Globalization.CultureInfo c) =>
            v.Length == 2 && v[0] is true && v[1] is string { Length: 0 } ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        public object[] ConvertBack(object value, Type[] t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    public static readonly System.Windows.Data.IValueConverter Not = new NotConverter();

    sealed class NotConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => value is not true;
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => value is not true;
    }

    public static readonly System.Windows.Data.IValueConverter NullToVisibility = new Lambda(v => v == null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed);
    public static readonly System.Windows.Data.IValueConverter InverseBoolToVisibility = new Lambda(v => v is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible);

    sealed class StepConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) =>
            value is int i && int.TryParse(p?.ToString(), out int want) && i == want ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    public static readonly System.Windows.Data.IValueConverter PositiveToVisibility =new Lambda(v => v is int i && i > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed);
    public static readonly System.Windows.Data.IValueConverter ZeroToVisibility = new Lambda(v => v is int i && i == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed);
    public static readonly System.Windows.Data.IValueConverter BoolToVisibility =new Lambda(v => v is true ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed);
    public static readonly System.Windows.Data.IValueConverter NotEmptyToVisibility = new Lambda(v => v is string { Length: > 0 } ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed);

    sealed class Lambda(Func<object?, object> f) : System.Windows.Data.IValueConverter
    {
        public object Convert(object? value, Type t, object p, System.Globalization.CultureInfo c) => f(value);
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    sealed class IsPositiveConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => value is int i && i > 0;
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }
}

public partial class MixViewModel : ObservableObject
{
    readonly MainViewModel _owner;
    public string Id { get; }
    public string Glyph { get; }
    public Brush Accent { get; }
    public Brush AccentTint { get; }

    [ObservableProperty] string name;
    [ObservableProperty] double volume;
    [ObservableProperty] bool muted;
    [ObservableProperty] string? outputDeviceId;
    [ObservableProperty] double level;
    /// <summary>Shown under the device picker, e.g. "Playing on fallback: …" or "Not connected".</summary>
    [ObservableProperty] string activeNote = "";

    public IReadOnlyList<string> Fallbacks => _owner.Service.Config.Mixes.FirstOrDefault(m => m.Id == Id)?.FallbackDeviceIds ?? [];

    public void ToggleFallback(string deviceId)
    {
        var list = Fallbacks.ToList();
        if (!list.Remove(deviceId)) list.Add(deviceId);
        _owner.Service.SetMixFallbacks(Id, list);
    }

    public MixViewModel(MainViewModel owner, MixConfig m, int index)
    {
        _owner = owner;
        Id = m.Id;
        Glyph = Palette.Icon(m.Icon).Glyph;
        Accent = Palette.OutputAccent(index);
        AccentTint = Palette.Tint(Accent);
        name = m.Name;
        volume = m.Volume;
        muted = m.Muted;
        outputDeviceId = m.OutputDeviceId;
    }

    partial void OnNameChanged(string value) => _owner.Service.RenameMix(Id, value);
    partial void OnVolumeChanged(double value) => _owner.Service.SetMix(Id, value, Muted);
    partial void OnMutedChanged(bool value) => _owner.Service.SetMix(Id, Volume, value);

    partial void OnOutputDeviceIdChanged(string? value)
    {
        // ComboBox briefly pushes null while its item list is being replaced; ignore that
        if (value != null) _owner.Service.SetMixOutput(Id, value);
    }

    [RelayCommand]
    void Delete() => _owner.RemoveMix(this);
}

public partial class SendViewModel : ObservableObject
{
    readonly MainViewModel _owner;
    readonly ChannelViewModel _channel;
    public MixViewModel Mix { get; }
    public ChannelViewModel Channel => _channel;

    [ObservableProperty] double volume;
    [ObservableProperty] bool muted;
    [ObservableProperty] double level;

    public SendViewModel(MainViewModel owner, ChannelViewModel channel, MixViewModel mix, SendConfig s)
    {
        _owner = owner;
        _channel = channel;
        Mix = mix;
        volume = s.Volume;
        muted = s.Muted;
    }

    partial void OnVolumeChanged(double value) => _owner.Service.SetSend(_channel.Id, Mix.Id, value, Muted);
    partial void OnMutedChanged(bool value) => _owner.Service.SetSend(_channel.Id, Mix.Id, Volume, value);
}

public partial class ChannelViewModel : ObservableObject
{
    readonly MainViewModel _owner;
    public string Id { get; }
    public string Kind { get; }
    public string Glyph { get; }
    public Brush Accent { get; }
    public Brush AccentTint { get; }
    public bool CanDelete => Kind != ChannelKind.System;
    public bool IsAppChannel => Kind != ChannelKind.Input;
    public bool IsInput => Kind == ChannelKind.Input;
    public ObservableCollection<SendViewModel> Sends { get; } = [];
    public ObservableCollection<AppViewModel> Apps { get; } = [];

    [ObservableProperty] string name;
    [ObservableProperty] bool muted;
    [ObservableProperty] double level;
    [ObservableProperty] string hint = "";
    [ObservableProperty] string moreText = "";
    [ObservableProperty] string moreTooltip = "";
    [ObservableProperty] bool isDropTarget;
    /// <summary>Turned down right now by automatic ducking.</summary>
    [ObservableProperty] bool isDucked;

    public ChannelViewModel(MainViewModel owner, ChannelConfig c)
    {
        _owner = owner;
        Id = c.Id;
        Kind = c.Kind;
        name = c.Name;
        muted = c.Muted;
        var (glyph, color) = Palette.Icon(c.Kind == ChannelKind.Input ? "mic" : c.Icon);
        Glyph = glyph;
        Accent = Palette.Brush(color);
        AccentTint = Palette.Tint(Accent);
    }

    partial void OnNameChanged(string value) => _owner.Service.RenameChannel(Id, value);
    partial void OnMutedChanged(bool value) => _owner.Service.SetChannel(Id, 100, value);

    [RelayCommand]
    void Delete() => _owner.RemoveChannel(this);
}

public partial class AppViewModel : ObservableObject
{
    readonly MixerService _service;
    public string Exe { get; }
    public string ChannelId { get; set; } = "";

    [ObservableProperty] string displayName = "";
    [ObservableProperty] bool playing;
    [ObservableProperty] string? bypassDevice;
    [ObservableProperty] ImageSource? icon;
    /// <summary>The app's own volume inside its channel (0–100).</summary>
    [ObservableProperty] double volume;

    partial void OnVolumeChanged(double value) => _service.SetAppVolume(Exe, value);

    public string Tooltip => BypassDevice != null
        ? $"{DisplayName} ({Exe}) plays straight to: {BypassDevice}"
        : $"{DisplayName} ({Exe}){(Playing ? " · playing now" : "")}\nDrag it to another channel, click for its own volume, right-click to move it.";

    public AppViewModel(MixerService service, string exe)
    {
        _service = service;
        Exe = exe;
        volume = service.AppVolume(exe);
    }

    public void Update(AppInfo info)
    {
        DisplayName = info.DisplayName;
        Playing = info.Playing;
        BypassDevice = info.BypassDevice;
        ChannelId = info.ChannelId;
        Icon ??= AppIcons.For(info.ImagePath);
        OnPropertyChanged(nameof(Tooltip));
    }
}

public partial class MainViewModel : ObservableObject
{
    readonly System.Windows.Threading.Dispatcher _dispatcher;
    readonly System.Windows.Threading.DispatcherTimer _meterTimer;
    readonly Dictionary<string, AppViewModel> _apps = new(StringComparer.OrdinalIgnoreCase);

    public MixerService Service { get; }
    public ObservableCollection<MixViewModel> Mixes { get; } = [];
    public ObservableCollection<ChannelViewModel> Channels { get; } = [];
    public ObservableCollection<AppViewModel> BypassApps { get; } = [];
    public ObservableCollection<DeviceOption> OutputDevices { get; } = [];
    public ObservableCollection<DeviceOption> InputDevices { get; } = [];
    public ObservableCollection<DeviceOption> CollectorDevices { get; } = [];

    [ObservableProperty] string statusText = "";
    [ObservableProperty] StatusLevel statusLevel;
    [ObservableProperty] StatusFix statusFix;
    [ObservableProperty] bool manageDefaultDevice;
    [ObservableProperty] bool guardDefaultDevice;
    [ObservableProperty] bool redirectTried;
    [ObservableProperty] bool startWithWindows;
    [ObservableProperty] string? collectorId;
    [ObservableProperty] bool settingsOpen;

    public MainViewModel(MixerService service, System.Windows.Threading.Dispatcher dispatcher)
    {
        Service = service;
        _dispatcher = dispatcher;
        manageDefaultDevice = service.Config.ManageDefaultDevice;
        guardDefaultDevice = service.Config.GuardDefaultDevice;
        startWithWindows = Autostart.IsEnabled;
        ApplyStatus(service.Status);

        for (int i = 0; i < service.Config.Mixes.Count; i++) Mixes.Add(new MixViewModel(this, service.Config.Mixes[i], i));
        foreach (var c in service.Config.Channels) AddChannelVm(c);
        RefreshDevices();

        service.AppsChanged += apps => _dispatcher.BeginInvoke(() => SyncApps(apps));
        service.DevicesChanged += () => _dispatcher.BeginInvoke(RefreshDevices);
        service.StatusChanged += s => _dispatcher.BeginInvoke(() => ApplyStatus(s));
        service.OutputsChanged += () => _dispatcher.BeginInvoke(UpdateOutputNotes);

        _meterTimer = new(TimeSpan.FromMilliseconds(33), System.Windows.Threading.DispatcherPriority.Render, (_, _) => UpdateMeters(), dispatcher);
        _meterTimer.Start();
        InitFeatures();
    }

    void ApplyStatus(StatusInfo s)
    {
        StatusText = s.Text;
        StatusLevel = s.Level;
        StatusFix = s.Fix;
    }

    void AddChannelVm(ChannelConfig c)
    {
        var vm = new ChannelViewModel(this, c);
        foreach (var mix in Mixes) vm.Sends.Add(new SendViewModel(this, vm, mix, c.Send(mix.Id)));
        Channels.Add(vm);
        UpdateChannelHints();
    }

    void UpdateMeters()
    {
        foreach (var ch in Channels)
        {
            double peak = ch.Muted ? 0 : Service.TakeChannelPeak(ch.Id);
            ch.Level = Meter.Fall(ch.Level, Meter.ToFraction(peak));
            foreach (var s in ch.Sends)
            {
                double send = s.Muted ? 0 : peak * MixerService.ToGain(s.Volume);
                s.Level = Meter.Fall(s.Level, Meter.ToFraction(send));
            }
        }
        foreach (var m in Mixes)
            m.Level = Meter.Fall(m.Level, Meter.ToFraction(Service.TakeMixPeak(m.Id)));
        UpdateDuckBadges();
    }

    void SyncApps(IReadOnlyList<AppInfo> apps)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in apps)
        {
            if (!_apps.TryGetValue(info.Exe, out var vm)) _apps[info.Exe] = vm = new AppViewModel(Service, info.Exe);
            vm.Update(info);
            seen.Add(info.Exe);
        }
        foreach (var gone in _apps.Keys.Where(k => !seen.Contains(k)).ToList()) _apps.Remove(gone);

        var routed = apps.Where(a => a.BypassDevice == null).Select(a => _apps[a.Exe]).ToList();
        foreach (var ch in Channels)
            Reconcile(ch.Apps, routed.Where(a => a.ChannelId == ch.Id));
        Reconcile(BypassApps, apps.Where(a => a.BypassDevice != null).Select(a => _apps[a.Exe]));
        UpdateChannelHints();
    }

    /// <summary>Updates a collection in place so chips that did not move are not re-created.</summary>
    static void Reconcile<T>(ObservableCollection<T> target, IEnumerable<T> desired)
    {
        var list = desired.ToList();
        for (int i = target.Count - 1; i >= 0; i--)
            if (!list.Contains(target[i])) target.RemoveAt(i);
        for (int i = 0; i < list.Count; i++)
        {
            int at = target.IndexOf(list[i]);
            if (at == i) continue;
            if (at >= 0) target.Move(at, i);
            else target.Insert(i, list[i]);
        }
    }

    void UpdateChannelHints()
    {
        foreach (var ch in Channels)
        {
            var c = Service.Config.Channels.FirstOrDefault(x => x.Id == ch.Id);
            if (c == null) continue;
            ch.Hint = c.Kind switch
            {
                ChannelKind.System => "Apps without a channel",
                ChannelKind.Input => InputDevices.FirstOrDefault(d => d.Id == c.InputDeviceId)?.Name ?? "Microphone not connected",
                _ => "Drag an app here",
            };
            // Assigned apps that are not running right now are summarised, not shown as chips
            var idle = c.Apps.Where(exe => ch.Apps.All(a => !a.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase))).ToList();
            ch.MoreText = idle.Count == 0 ? "" : ch.Apps.Count == 0 ? $"{idle.Count} assigned, not running" : $"+{idle.Count} not running";
            ch.MoreTooltip = string.Join("\n", idle.Select(e => Path.GetFileNameWithoutExtension(e)));
        }
    }

    void RefreshDevices()
    {
        var collector = Service.CollectorId;
        var outputs = Service.RenderDevices.Where(d => d.Id != collector).Select(d => new DeviceOption(d.Id, d.Name)).ToList();
        // Keep configured-but-unplugged devices selectable so the choice is not lost
        foreach (var m in Service.Config.Mixes)
            if (m.OutputDeviceId != null && outputs.All(o => o.Id != m.OutputDeviceId))
                outputs.Add(new DeviceOption(m.OutputDeviceId, "Device not connected"));
        Replace(OutputDevices, outputs);
        Replace(InputDevices, Service.InputDevices.Select(d => new DeviceOption(d.Id, d.Name)));
        Replace(CollectorDevices, Service.RenderDevices.Select(d => new DeviceOption(d.Id, d.Name)));

        // Re-assert selections after the lists changed
        foreach (var m in Mixes)
        {
            var id = Service.Config.Mixes.FirstOrDefault(x => x.Id == m.Id)?.OutputDeviceId;
            m.OutputDeviceId = null;
            m.OutputDeviceId = id;
        }
        CollectorId = collector;
        UpdateChannelHints();
        UpdateOutputNotes();
    }

    void UpdateOutputNotes()
    {
        foreach (var m in Mixes)
        {
            var active = Service.ActiveDeviceOf(m.Id);
            m.ActiveNote = active == null
                ? "Not connected, nothing is playing"
                : active != m.OutputDeviceId
                    ? $"Playing on fallback: {Service.RenderDevices.FirstOrDefault(d => d.Id == active)?.Name ?? active}"
                    : "";
        }
    }

    static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    public void MoveApp(AppViewModel app, ChannelViewModel channel)
    {
        if (!channel.IsAppChannel || app.ChannelId == channel.Id) return;
        Service.AssignApp(app.Exe, channel.Id);
        app.ChannelId = channel.Id;
        foreach (var ch in Channels) ch.Apps.Remove(app);
        if (app.BypassDevice == null) channel.Apps.Add(app);
        UpdateChannelHints();
    }

    public void RemoveChannel(ChannelViewModel ch)
    {
        Service.RemoveChannel(ch.Id);
        Channels.Remove(ch);
        var system = Channels.First(c => c.Kind == ChannelKind.System);
        foreach (var app in ch.Apps.ToList())
        {
            app.ChannelId = system.Id;
            system.Apps.Add(app);
        }
        UpdateChannelHints();
    }

    public void RemoveMix(MixViewModel mix)
    {
        if (Mixes.Count <= 1) return;
        Service.RemoveMix(mix.Id);
        Mixes.Remove(mix);
        foreach (var ch in Channels)
            if (ch.Sends.FirstOrDefault(s => s.Mix == mix) is { } s) ch.Sends.Remove(s);
    }

    public void AddAppChannel(string icon)
    {
        var names = new Dictionary<string, string>
        {
            ["app"] = "New channel", ["browser"] = "Browser", ["music"] = "Music", ["chat"] = "Chat", ["game"] = "Games",
        };
        AddChannelVm(Service.AddChannel(names.GetValueOrDefault(icon, "New channel"), ChannelKind.Apps, icon));
    }

    public void AddInputChannel(DeviceOption device)
    {
        if (device.Id == null) return;
        AddChannelVm(Service.AddChannel(device.Name.Split(" (")[0], ChannelKind.Input, "mic", device.Id));
    }

    [RelayCommand]
    void AddMix()
    {
        var m = Service.AddMix($"Output {Mixes.Count + 1}", "stream");
        if (m == null) return;
        var vm = new MixViewModel(this, m, Mixes.Count);
        Mixes.Add(vm);
        foreach (var ch in Channels)
        {
            var c = Service.Config.Channels.First(x => x.Id == ch.Id);
            ch.Sends.Add(new SendViewModel(this, ch, vm, c.Send(m.Id)));
        }
    }

    [RelayCommand]
    void FixStatus()
    {
        if (StatusFix == StatusFix.ApplyDefault)
        {
            ManageDefaultDevice = true;
            Service.ApplyDefaultNow();
        }
        else if (StatusFix == StatusFix.OpenSettings)
        {
            OpenSettings("routing");
        }
    }

    partial void OnManageDefaultDeviceChanged(bool value) => Service.SetManageDefaultDevice(value);

    partial void OnGuardDefaultDeviceChanged(bool value)
    {
        Service.Config.GuardDefaultDevice = value;
        Service.SetManageDefaultDevice(ManageDefaultDevice);
    }

    [RelayCommand]
    void RedirectBypass()
    {
        foreach (var app in BypassApps.ToList()) Service.RedirectApp(app.Exe);
        RedirectTried = true;
    }
    partial void OnStartWithWindowsChanged(bool value) => Autostart.Set(value);

    partial void OnCollectorIdChanged(string? value)
    {
        // RefreshDevices also writes this property; only a real user choice should reach the service
        if (value != null && value != Service.CollectorId) Service.SetCollector(value);
    }

    [RelayCommand]
    void OpenLog()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.FilePath) { UseShellExecute = true }); } catch { }
    }
}
