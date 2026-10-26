using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MixDeck.Model;
using MixDeck.Remote;
using QRCoder;

namespace MixDeck.ViewModels;

/// <summary>The API bridge (runs on the UI thread) and the phone-remote settings.</summary>
public partial class MainViewModel : IRemoteBridge
{
    RemoteServer? _remote;
    bool _loadingRemote;

    [ObservableProperty] bool remoteEnabled;
    [ObservableProperty] bool remoteAllowLan;
    [ObservableProperty] string remoteUrl = "";
    [ObservableProperty] string remoteStatus = "";
    [ObservableProperty] ImageSource? remoteQr;

    RemoteConfig RemoteCfg => Service.Config.Remote;

    async void InitRemote()
    {
        _loadingRemote = true;
        RemoteEnabled = RemoteCfg.Enabled;
        RemoteAllowLan = RemoteCfg.AllowLan;
        _loadingRemote = false;
        if (RemoteCfg.Enabled) await RestartRemote();
    }

    partial void OnRemoteEnabledChanged(bool value)
    {
        if (_loadingRemote) return;
        RemoteCfg.Enabled = value;
        Service.Save();
        _ = RestartRemote();
    }

    partial void OnRemoteAllowLanChanged(bool value)
    {
        if (_loadingRemote) return;
        RemoteCfg.AllowLan = value;
        Service.Save();
        _ = RestartRemote();
    }

    [RelayCommand]
    async Task NewRemoteToken()
    {
        RemoteCfg.Token = RemoteConfig.NewToken();
        Service.Save();
        await RestartRemote();
    }

    [RelayCommand]
    void CopyRemoteUrl()
    {
        try { System.Windows.Clipboard.SetText(RemoteUrl); } catch { }
    }

    async Task RestartRemote()
    {
        if (_remote != null)
        {
            await _remote.DisposeAsync();
            _remote = null;
        }
        RemoteUrl = "";
        RemoteQr = null;
        if (!RemoteCfg.Enabled)
        {
            RemoteStatus = "";
            return;
        }

        _remote = new RemoteServer(RemoteCfg, this, f => _dispatcher.Invoke(f));
        await _remote.StartAsync();
        if (_remote.Error != null)
        {
            RemoteStatus = _remote.Error;
            return;
        }
        RemoteUrl = _remote.Urls().First();
        RemoteQr = Qr(RemoteUrl);
        RemoteStatus = RemoteCfg.AllowLan
            ? "Running. Scan the code with a phone on the same Wi-Fi. If Windows asks about the firewall, allow private networks."
            : "Running on this PC only.";
    }

    public async Task StopRemote()
    {
        if (_remote != null) await _remote.DisposeAsync();
    }

    static ImageSource Qr(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8, [232, 234, 240], [22, 25, 32]);
        var img = new BitmapImage();
        img.BeginInit();
        img.StreamSource = new MemoryStream(png);
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
    }

    #region IRemoteBridge (always called on the UI thread)

    static string Hex(Brush b) => ((SolidColorBrush)b).Color.ToString().Remove(1, 2); // #AARRGGBB → #RRGGBB

    public object State() => new
    {
        mixes = Mixes.Select(m => new { m.Id, m.Name, volume = Math.Round(m.Volume), m.Muted, accent = Hex(m.Accent) }),
        channels = Channels.Select(c => new
        {
            c.Id,
            c.Name,
            c.Kind,
            c.Muted,
            color = Hex(c.Accent),
            apps = c.Apps.Select(a => a.DisplayName),
            sends = c.Sends.Select(s => new { mix = s.Mix.Id, volume = Math.Round(s.Volume), s.Muted }),
        }),
        profiles = Profiles.Select(p => new { p.Id, p.Name, active = p.IsActive }),
    };

    public object Levels() => new
    {
        sends = Channels.SelectMany(c => c.Sends.Select(s => new { k = $"{c.Id}|{s.Mix.Id}", v = Math.Round(s.Level, 3) })).ToDictionary(x => x.k, x => x.v),
        mixes = Mixes.ToDictionary(m => m.Id, m => Math.Round(m.Level, 3)),
    };

    public bool SetSend(string channelId, string mixId, double? volume, bool? muted)
    {
        var send = Channels.FirstOrDefault(c => c.Id == channelId)?.Sends.FirstOrDefault(s => s.Mix.Id == mixId);
        if (send == null) return false;
        if (volume is { } v) send.Volume = v;
        if (muted is { } m) send.Muted = m;
        return true;
    }

    public bool SetChannelMute(string channelId, bool? muted)
    {
        var ch = Channels.FirstOrDefault(c => c.Id == channelId);
        if (ch == null) return false;
        ch.Muted = muted ?? !ch.Muted;
        return true;
    }

    public bool SetMix(string mixId, double? volume, bool? muted)
    {
        var mix = Mixes.FirstOrDefault(m => m.Id == mixId);
        if (mix == null) return false;
        if (volume is { } v) mix.Volume = v;
        if (muted is { } m) mix.Muted = m;
        return true;
    }

    public bool ApplyProfile(string profileId)
    {
        var p = Profiles.FirstOrDefault(x => x.Id == profileId);
        if (p == null) return false;
        ApplyProfile(p, automatic: false);
        return true;
    }

    #endregion
}
