using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace MixDeck.Audio;

/// <summary>Captures a recording device (microphone) and converts it to 48 kHz float stereo.</summary>
public sealed class DeviceSource : AudioSource
{
    static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public string DeviceId { get; }
    public bool Faulted { get; private set; }

    /// <summary>Microphone effects, swapped as a whole by the UI; the old chain is disposed shortly after.</summary>
    public InputChain? Chain
    {
        get => _chain;
        set
        {
            var old = Interlocked.Exchange(ref _chain, value);
            if (old != null) Task.Delay(500).ContinueWith(_ => old.Dispose());
        }
    }
    InputChain? _chain;

    readonly WasapiCapture _capture;
    readonly WaveFormat _fmt;
    readonly bool _isFloat;
    readonly WdlResampler? _resampler;
    float[] _work = new float[4096];
    float[] _out = new float[4096];

    public DeviceSource(MMDevice device)
    {
        DeviceId = device.ID;
        _capture = new WasapiCapture(device, true, 20);
        _fmt = _capture.WaveFormat;
        _isFloat = _fmt.Encoding == WaveFormatEncoding.IeeeFloat
            || (_fmt is WaveFormatExtensible ext && ext.SubFormat == IeeeFloat);

        if (_fmt.SampleRate != Format.SampleRate)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(true);
            _resampler.SetRates(_fmt.SampleRate, Format.SampleRate);
        }

        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) =>
        {
            Faulted = true;
            if (e.Exception != null) Log.Write($"Input {device.FriendlyName} stopped: {e.Exception.Message}");
        };
        _capture.StartRecording();
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        int bytesPerSample = _fmt.BitsPerSample / 8;
        int channels = _fmt.Channels;
        int frames = e.BytesRecorded / (bytesPerSample * channels);
        if (_work.Length < frames * 2) _work = new float[frames * 2];

        var b = e.Buffer;
        for (int f = 0; f < frames; f++)
        {
            int at = f * channels * bytesPerSample;
            float l = ReadSample(b, at, bytesPerSample);
            float r = channels > 1 ? ReadSample(b, at + bytesPerSample, bytesPerSample) : l;
            _work[2 * f] = l;
            _work[2 * f + 1] = r;
        }

        if (_resampler == null)
        {
            var span = _work.AsSpan(0, frames * 2);
            Volatile.Read(ref _chain)?.Process(span);
            Push(span);
            return;
        }

        int inFrames = _resampler.ResamplePrepare(frames, 2, out float[] inBuf, out int inOffset);
        Array.Copy(_work, 0, inBuf, inOffset, inFrames * 2);
        int maxOut = (int)((long)frames * Format.SampleRate / _fmt.SampleRate) + 16;
        if (_out.Length < maxOut * 2) _out = new float[maxOut * 2];
        int got = _resampler.ResampleOut(_out, 0, inFrames, maxOut, 2);
        var outSpan = _out.AsSpan(0, got * 2);
        Volatile.Read(ref _chain)?.Process(outSpan);
        Push(outSpan);
    }

    float ReadSample(byte[] b, int at, int bytes)
    {
        if (_isFloat) return BitConverter.ToSingle(b, at);
        return bytes switch
        {
            2 => BitConverter.ToInt16(b, at) / 32768f,
            3 => ((b[at] << 8) | (b[at + 1] << 16) | (b[at + 2] << 24)) / 2147483648f,
            4 => BitConverter.ToInt32(b, at) / 2147483648f,
            _ => 0,
        };
    }

    public override void Dispose()
    {
        Chain = null;
        _capture.DataAvailable -= OnData;
        try { _capture.StopRecording(); } catch { }
        _capture.Dispose();
    }
}
