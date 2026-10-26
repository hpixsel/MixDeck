using System.Text.Json;
using System.Text.Json.Serialization;

namespace MixDeck.Model;

public static class ChannelKind
{
    public const string System = "system"; // every app not assigned elsewhere
    public const string Apps = "apps";     // a list of executables
    public const string Input = "input";   // a recording device
}

/// <summary>Microphone processing, in signal order.</summary>
public sealed class InputFxConfig
{
    public bool NoiseSuppression { get; set; }
    public bool HighPass { get; set; } = true;     // removes rumble below ~80 Hz
    public bool Gate { get; set; }
    public double GateThresholdDb { get; set; } = -45;
    public bool Compressor { get; set; }
    public double GainDb { get; set; }
}

public sealed class EqBand
{
    public string Type { get; set; } = "PK";        // PK, LSC, HSC (AutoEQ notation)
    public double Freq { get; set; }
    public double Gain { get; set; }
    public double Q { get; set; } = 0.7;
}

/// <summary>Per-output tone shaping and protection.</summary>
public sealed class OutputFxConfig
{
    public double Bass { get; set; }                // dB, low shelf 120 Hz
    public double Mid { get; set; }                 // dB, peak 1 kHz
    public double Treble { get; set; }              // dB, high shelf 8 kHz
    public bool Limiter { get; set; } = true;
    /// <summary>Headphone correction imported from an AutoEQ "ParametricEQ.txt".</summary>
    public string? ProfileName { get; set; }
    public double ProfilePreamp { get; set; }
    public List<EqBand> ProfileBands { get; set; } = [];
}

/// <summary>Turn some channels down while another one (usually voice chat) is active.</summary>
public sealed class DuckingConfig
{
    public bool Enabled { get; set; }
    public string? TriggerChannelId { get; set; }
    public List<string> TargetChannelIds { get; set; } = [];
    public double AmountDb { get; set; } = 12;
    public double ThresholdDb { get; set; } = -40;
}

public sealed class SendConfig
{
    public double Volume { get; set; } = 100;
    public bool Muted { get; set; }
}

public sealed class ChannelConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Kind { get; set; } = ChannelKind.Apps;
    public string Icon { get; set; } = "app";
    public List<string> Apps { get; set; } = [];
    public string? InputDeviceId { get; set; }
    public InputFxConfig InputFx { get; set; } = new();
    public double Volume { get; set; } = 100;
    public bool Muted { get; set; }
    public Dictionary<string, SendConfig> Sends { get; set; } = [];

    public SendConfig Send(string mixId)
    {
        if (!Sends.TryGetValue(mixId, out var s)) Sends[mixId] = s = new SendConfig();
        return s;
    }
}

public sealed class MixConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "headphones";
    public string? OutputDeviceId { get; set; }
    /// <summary>Used in order when <see cref="OutputDeviceId"/> is unplugged; switches back when it returns.</summary>
    public List<string> FallbackDeviceIds { get; set; } = [];
    public OutputFxConfig Fx { get; set; } = new();
    public double Volume { get; set; } = 100;
    public bool Muted { get; set; }
}

public static class HotkeyAction
{
    public const string MuteChannel = "mute-channel";       // TargetId = channel
    public const string MuteMix = "mute-mix";               // TargetId = mix
    public const string VolumeUp = "volume-up";             // TargetId = channel, MixId = mix or null for all
    public const string VolumeDown = "volume-down";
    public const string Profile = "profile";                // TargetId = profile
}

public sealed class HotkeyConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>e.g. "Ctrl+Alt+M"</summary>
    public string Gesture { get; set; } = "";
    public string Action { get; set; } = HotkeyAction.MuteChannel;
    public string? TargetId { get; set; }
    public string? MixId { get; set; }
}

/// <summary>A saved set of volumes and mutes (not the channel layout).</summary>
public sealed class ProfileConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public Dictionary<string, ChannelState> Channels { get; set; } = [];
    public Dictionary<string, SendConfig> Mixes { get; set; } = [];
    /// <summary>Switch to this profile automatically while one of these apps is running.</summary>
    public List<string> TriggerApps { get; set; } = [];
}

public sealed class ChannelState
{
    public bool Muted { get; set; }
    public Dictionary<string, SendConfig> Sends { get; set; } = [];
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    /// <summary>Name (or id) of the silent playback device every app plays to; we capture apps from it.</summary>
    public string CollectorDevice { get; set; } = "Steam Streaming Speakers";
    /// <summary>Make the collector the Windows default output while MixDeck runs.</summary>
    public bool ManageDefaultDevice { get; set; } = true;
    /// <summary>Take the default back when something else (a game, Wave Link, the user) changes it.</summary>
    public bool GuardDefaultDevice { get; set; } = true;
    /// <summary>Default device to restore on exit (kept on disk so a crash can still restore it).</summary>
    public string? RestoreDefaultId { get; set; }
    public List<ChannelConfig> Channels { get; set; } = [];
    public List<MixConfig> Mixes { get; set; } = [];
    public List<HotkeyConfig> Hotkeys { get; set; } = [];
    public List<ProfileConfig> Profiles { get; set; } = [];
    /// <summary>Per-app volume inside its channel, 0–100 (applied as the app's Windows session volume).</summary>
    public Dictionary<string, double> AppVolumes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool FirstRunDone { get; set; }
    public bool ShowOsd { get; set; } = true;
    public DuckingConfig Ducking { get; set; } = new();
    /// <summary>Smaller buffers (≈75 ms end to end instead of ≈90 ms); may crackle on a busy PC. Applied on restart.</summary>
    public bool LowLatency { get; set; }
    public RemoteConfig Remote { get; set; } = new();
    /// <summary>Where new versions come from: a GitHub repository URL (Releases) or a folder. Empty = no update checks.</summary>
    public string? UpdateSource { get; set; }
}

/// <summary>Local HTTP/WebSocket API and the phone remote page.</summary>
public sealed class RemoteConfig
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 7317;
    /// <summary>Reachable from other devices in the home network (phone, Home Assistant), not only this PC.</summary>
    public bool AllowLan { get; set; } = true;
    /// <summary>Every request must carry this (?token= or "Authorization: Bearer").</summary>
    public string Token { get; set; } = NewToken();

    public static string NewToken() =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
}

public static class ConfigStore
{
    public static string Directory { get; } = CreateDirectory();
    static string FilePath => Path.Combine(Directory, "config.json");

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    static string CreateDirectory()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MixDeck");
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    public static AppConfig? Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Options);
        }
        catch (Exception ex)
        {
            Log.Write($"Config load failed, starting fresh: {ex.Message}");
            File.Copy(FilePath, FilePath + ".broken", true);
        }
        return null;
    }

    public static void Save(AppConfig config)
    {
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Options));
        File.Move(tmp, FilePath, true);
    }
}
