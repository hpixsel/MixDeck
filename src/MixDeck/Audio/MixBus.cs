using MixDeck.Model;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MixDeck.Audio;

/// <summary>Engine-side state of one channel. Written by the UI/service, read lock-free by output threads.</summary>
public sealed class EngineChannel
{
    public volatile AudioSource[] Sources = [];
    public volatile float Gain = 1;
    public volatile bool Muted;
    public readonly float[] SendGain = Enumerable.Repeat(1f, AudioSource.MaxMixes).ToArray();
    public readonly bool[] SendMuted = new bool[AudioSource.MaxMixes];

    // Last gain applied per mix slot; each slot is touched only by its own output thread
    internal readonly float[] Applied = new float[AudioSource.MaxMixes];

    public float TakePeak()
    {
        float p = 0;
        foreach (var s in Sources) p = Math.Max(p, s.TakePeak());
        return p;
    }
}

/// <summary>Ducking settings resolved to engine channels (immutable; swapped as a whole).</summary>
public sealed record DuckSettings(EngineChannel Trigger, IReadOnlySet<EngineChannel> Targets, float AmountGain, float Threshold);

/// <summary>Shared state that every mix output reads from.</summary>
public sealed class MixBus
{
    public volatile EngineChannel[] Channels = [];
    public volatile DuckSettings? Ducking;
    /// <summary>Current ducking gain (1 = not ducked), published by the first running output for the UI.</summary>
    public volatile float DuckGain = 1;
}

/// <summary>Tone shaping and protection for one output (immutable filter set, swapped as a whole).</summary>
public sealed class OutputFx
{
    readonly Biquad[] _filters;
    readonly float _preamp;
    readonly Limiter? _limiter;

    public OutputFx(OutputFxConfig cfg)
    {
        var list = new List<Biquad>();
        if (cfg.Bass != 0) list.Add(new Biquad(BiquadType.LowShelf, 120, cfg.Bass, 0.7));
        if (cfg.Mid != 0) list.Add(new Biquad(BiquadType.Peaking, 1000, cfg.Mid, 0.7));
        if (cfg.Treble != 0) list.Add(new Biquad(BiquadType.HighShelf, 8000, cfg.Treble, 0.7));
        foreach (var b in cfg.ProfileBands)
        {
            var type = b.Type switch { "LSC" or "LS" => BiquadType.LowShelf, "HSC" or "HS" => BiquadType.HighShelf, _ => BiquadType.Peaking };
            list.Add(new Biquad(type, b.Freq, b.Gain, b.Q));
        }
        _filters = [.. list];
        _preamp = cfg.ProfileBands.Count > 0 ? Dsp.DbToGain(cfg.ProfilePreamp) : 1;
        _limiter = cfg.Limiter ? new Limiter() : null;
    }

    public bool IsEmpty => _filters.Length == 0 && _limiter == null;

    public void Process(Span<float> s)
    {
        if (_preamp != 1) foreach (ref float x in s) x *= _preamp;
        foreach (var f in _filters) f.Process(s);
        _limiter?.Process(s);
    }
}

/// <summary>
/// Delivers our 48 kHz stereo mix in the device's own shared-mode format, so Windows never has to convert it.
/// Stereo goes to front-left/right; other channels (e.g. the 8-channel "virtual 7.1" of gaming headsets) get silence,
/// a mono device gets the average. Other sample rates are resampled here.
/// </summary>
public sealed class DeviceAdapter : IWaveProvider
{
    static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    readonly ISampleProvider _source;
    readonly int _channels;
    float[] _stereo = new float[4096];

    public WaveFormat WaveFormat { get; }

    DeviceAdapter(ISampleProvider source, WaveFormat deviceFormat)
    {
        _source = source;
        _channels = deviceFormat.Channels;
        WaveFormat = deviceFormat;
    }

    public static IWaveProvider For(ISampleProvider mix, WaveFormat deviceFormat)
    {
        bool isFloat = deviceFormat.Encoding == WaveFormatEncoding.IeeeFloat
            || (deviceFormat is WaveFormatExtensible ext && ext.SubFormat == IeeeFloat);
        // Shared-mode mix formats are 32-bit float on every current Windows; anything else is left to NAudio
        if (!isFloat || deviceFormat.BitsPerSample != 32) return new SampleToWaveProvider(mix);

        ISampleProvider source = mix;
        if (deviceFormat.SampleRate != mix.WaveFormat.SampleRate)
            source = new WdlResamplingSampleProvider(mix, deviceFormat.SampleRate);
        return new DeviceAdapter(source, deviceFormat);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int frames = count / (4 * _channels);
        if (_stereo.Length < frames * 2) _stereo = new float[frames * 2];
        int got = _source.Read(_stereo, 0, frames * 2) / 2;

        var dest = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, frames * 4 * _channels));
        if (_channels == 2)
        {
            _stereo.AsSpan(0, got * 2).CopyTo(dest);
        }
        else
        {
            dest.Clear();
            for (int f = 0; f < got; f++)
            {
                float l = _stereo[2 * f], r = _stereo[2 * f + 1];
                if (_channels == 1) dest[f] = 0.5f * (l + r);
                else
                {
                    dest[f * _channels] = l;
                    dest[f * _channels + 1] = r;
                }
            }
        }
        return frames * 4 * _channels;
    }
}

/// <summary>One mix: sums all channels with per-mix gains and plays the result on a device.</summary>
public sealed class MixOutput : ISampleProvider, IDisposable
{
    /// <summary>WASAPI buffer length; lower is snappier but needs a quieter system.</summary>
    public static int LatencyMs { get; set; } = 25;

    readonly MixBus _bus;
    float[][] _chBuf = [];
    float[] _tmp = new float[4800];
    float _appliedMaster;
    float _peak;
    float _duckEnv, _duckGain = 1;
    WasapiOut? _out;

    public int Slot { get; }
    public string? DeviceId { get; private set; }
    public bool Running { get; private set; }
    public volatile float Master = 1;
    public volatile bool Muted;
    public volatile OutputFx? Fx;

    public WaveFormat WaveFormat => AudioSource.Format;

    public MixOutput(MixBus bus, int slot)
    {
        _bus = bus;
        Slot = slot;
    }

    public float TakePeak()
    {
        float p = _peak;
        _peak = 0;
        return p;
    }

    public void SetDevice(string? deviceId)
    {
        Stop();
        DeviceId = deviceId;
    }

    /// <summary>Starts playback on <see cref="DeviceId"/> if it is present. Safe to call repeatedly.</summary>
    public void TryStart(MMDeviceEnumerator enumerator)
    {
        if (Running || DeviceId == null) return;
        try
        {
            var device = enumerator.GetDevice(DeviceId);
            if (device.State != DeviceState.Active) return;
            var output = new WasapiOut(device, AudioClientShareMode.Shared, true, LatencyMs);
            output.Init(DeviceAdapter.For(this, device.AudioClient.MixFormat));
            output.PlaybackStopped += (_, e) =>
            {
                Running = false;
                if (e.Exception != null) Log.Write($"Mix slot {Slot} stopped: {e.Exception.Message}");
            };
            output.Play();
            _out = output;
            Running = true;
            Log.Write($"Mix slot {Slot} playing on {device.FriendlyName}");
        }
        catch (Exception ex)
        {
            Log.Write($"Mix slot {Slot} could not start: {ex.Message}");
        }
    }

    public void Stop()
    {
        var o = _out;
        _out = null;
        Running = false;
        if (o == null) return;
        try { o.Stop(); } catch { }
        o.Dispose();
    }

    static readonly bool Debug = Environment.GetEnvironmentVariable("MIXDECK_DEBUG") == "1";
    long _lastDebug;

    public int Read(float[] buffer, int offset, int count)
    {
        if (Debug && Environment.TickCount64 - _lastDebug > 2000)
        {
            _lastDebug = Environment.TickCount64;
            var ring = _bus.Channels.SelectMany(c => c.Sources).Select(s => s.Ring(Slot)).FirstOrDefault(r => r != null);
            Log.Write($"[dbg] mix {Slot}: block={count / 2 / 48.0:F1} ms" + (ring == null ? "" : $" ring={ring.Available / 2 / 48.0:F1} ms ratio={ring.Ratio:F5}"));
        }
        var dest = buffer.AsSpan(offset, count);
        dest.Clear();
        if (_tmp.Length < count) _tmp = new float[count];
        var tmp = _tmp.AsSpan(0, count);
        var channels = _bus.Channels;
        if (_chBuf.Length < channels.Length || (_chBuf.Length > 0 && _chBuf[0].Length < count))
            _chBuf = Enumerable.Range(0, channels.Length).Select(_ => new float[Math.Max(count, 4800)]).ToArray();

        // 1) Sum each channel's sources (always drain the rings, even when silent, so they stay in sync)
        var duck = _bus.Ducking;
        int triggerIndex = -1;
        for (int k = 0; k < channels.Length; k++)
        {
            var ch = channels[k];
            var buf = _chBuf[k].AsSpan(0, count);
            buf.Clear();
            foreach (var src in ch.Sources)
            {
                var ring = src.Ring(Slot);
                if (ring == null || !ring.Read(tmp)) continue;
                for (int i = 0; i < count; i++) buf[i] += tmp[i];
            }
            if (duck != null && ch == duck.Trigger) triggerIndex = k;
        }

        // 2) Ducking: follow the trigger channel's raw level (even if it is muted on this output)
        float duckStart = _duckGain;
        if (duck != null && triggerIndex >= 0)
        {
            float peak = 0;
            foreach (var x in _chBuf[triggerIndex].AsSpan(0, count)) peak = Math.Max(peak, Math.Abs(x));
            float blockMs = count / 2 / Dsp.SampleRate * 1000;
            _duckEnv = peak > _duckEnv ? peak : _duckEnv * (float)Math.Exp(-blockMs / 300); // hold speech pauses ~0.3 s
            float target = _duckEnv > duck.Threshold ? duck.AmountGain : 1;
            float speed = target < _duckGain ? blockMs / 60 : blockMs / 700;              // fast down, slow back up
            _duckGain += (target - _duckGain) * Math.Min(1, speed);
        }
        else _duckGain = 1;
        _bus.DuckGain = _duckGain; // every output computes the same value; any of them may publish it

        // 3) Mix with per-channel × per-output gains, ramped across the block to avoid clicks
        for (int k = 0; k < channels.Length; k++)
        {
            var ch = channels[k];
            bool ducked = duck != null && duck.Targets.Contains(ch);
            float target = ch.Muted || ch.SendMuted[Slot] ? 0 : ch.Gain * ch.SendGain[Slot];
            float start = ch.Applied[Slot];
            ch.Applied[Slot] = target;
            if (start == 0 && target == 0) continue;
            AddRamped(dest, _chBuf[k].AsSpan(0, count), start, target, ducked ? duckStart : 1, ducked ? _duckGain : 1);
        }

        // 4) Output: master volume, EQ, limiter (or hard clip as a last resort)
        float master = Muted ? 0 : Master;
        float m0 = _appliedMaster;
        _appliedMaster = master;
        int frames = count / 2;
        for (int f = 0; f < frames; f++)
        {
            float g = m0 + (master - m0) * f / frames;
            dest[2 * f] *= g;
            dest[2 * f + 1] *= g;
        }
        Fx?.Process(dest);

        float outPeak = 0;
        foreach (ref float s in dest)
        {
            s = Math.Clamp(s, -1f, 1f);
            outPeak = Math.Max(outPeak, Math.Abs(s));
        }
        if (outPeak > _peak) _peak = outPeak;
        return count;
    }

    static void AddRamped(Span<float> dest, ReadOnlySpan<float> src, float g0, float g1, float d0, float d1)
    {
        int frames = dest.Length / 2;
        if (g0 == g1 && d0 == d1)
        {
            float g = g1 * d1;
            for (int i = 0; i < dest.Length; i++) dest[i] += src[i] * g;
            return;
        }
        for (int f = 0; f < frames; f++)
        {
            float t = (float)f / frames;
            float g = (g0 + (g1 - g0) * t) * (d0 + (d1 - d0) * t);
            dest[2 * f] += src[2 * f] * g;
            dest[2 * f + 1] += src[2 * f + 1] * g;
        }
    }

    public void Dispose() => Stop();
}
