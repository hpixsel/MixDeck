using System.Runtime.InteropServices;

namespace MixDeck.Audio;

/// <summary>Small DSP building blocks. All work on interleaved stereo float at 48 kHz.</summary>
public static class Dsp
{
    public const float SampleRate = 48000f;

    public static float DbToGain(double db) => (float)Math.Pow(10, db / 20);
    public static double GainToDb(double g) => g <= 1e-9 ? -180 : 20 * Math.Log10(g);

    /// <summary>One-pole smoothing coefficient for a time constant in ms (per frame).</summary>
    public static float Coef(double ms) => ms <= 0 ? 0 : (float)Math.Exp(-1.0 / (SampleRate * ms / 1000.0));
}

public enum BiquadType { Peaking, LowShelf, HighShelf, HighPass, LowPass }

/// <summary>RBJ "Audio EQ cookbook" biquad, stereo, transposed direct form II.</summary>
public sealed class Biquad
{
    double _b0, _b1, _b2, _a1, _a2;
    double _z1L, _z2L, _z1R, _z2R;

    public Biquad(BiquadType type, double freq, double gainDb, double q) => Set(type, freq, gainDb, q);

    public void Set(BiquadType type, double freq, double gainDb, double q)
    {
        double a = Math.Pow(10, gainDb / 40);
        double w0 = 2 * Math.PI * Math.Clamp(freq, 10, Dsp.SampleRate / 2 - 100) / Dsp.SampleRate;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);
        double alpha = sin / (2 * Math.Max(q, 0.05));
        double b0, b1, b2, a0, a1, a2;
        switch (type)
        {
            case BiquadType.LowShelf:
            {
                double s = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) - (a - 1) * cos + s); b1 = 2 * a * ((a - 1) - (a + 1) * cos); b2 = a * ((a + 1) - (a - 1) * cos - s);
                a0 = (a + 1) + (a - 1) * cos + s; a1 = -2 * ((a - 1) + (a + 1) * cos); a2 = (a + 1) + (a - 1) * cos - s;
                break;
            }
            case BiquadType.HighShelf:
            {
                double s = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) + (a - 1) * cos + s); b1 = -2 * a * ((a - 1) + (a + 1) * cos); b2 = a * ((a + 1) + (a - 1) * cos - s);
                a0 = (a + 1) - (a - 1) * cos + s; a1 = 2 * ((a - 1) - (a + 1) * cos); a2 = (a + 1) - (a - 1) * cos - s;
                break;
            }
            case BiquadType.HighPass:
                b0 = (1 + cos) / 2; b1 = -(1 + cos); b2 = (1 + cos) / 2;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            case BiquadType.LowPass:
                b0 = (1 - cos) / 2; b1 = 1 - cos; b2 = (1 - cos) / 2;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            default: // Peaking
                b0 = 1 + alpha * a; b1 = -2 * cos; b2 = 1 - alpha * a;
                a0 = 1 + alpha / a; a1 = -2 * cos; a2 = 1 - alpha / a;
                break;
        }
        _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
    }

    public void Process(Span<float> s)
    {
        for (int i = 0; i < s.Length; i += 2)
        {
            double l = s[i], r = s[i + 1];
            double yl = _b0 * l + _z1L; _z1L = _b1 * l - _a1 * yl + _z2L; _z2L = _b2 * l - _a2 * yl;
            double yr = _b0 * r + _z1R; _z1R = _b1 * r - _a1 * yr + _z2R; _z2R = _b2 * r - _a2 * yr;
            s[i] = (float)yl;
            s[i + 1] = (float)yr;
        }
    }
}

/// <summary>Follows the level of a signal with separate attack and release (peak detector).</summary>
public sealed class Envelope
{
    float _env;
    float _att, _rel;

    public Envelope(double attackMs, double releaseMs) => Set(attackMs, releaseMs);
    public void Set(double attackMs, double releaseMs) { _att = Dsp.Coef(attackMs); _rel = Dsp.Coef(releaseMs); }
    public float Value => _env;

    public float Next(float x)
    {
        float c = x > _env ? _att : _rel;
        _env = c * _env + (1 - c) * x;
        return _env;
    }
}

/// <summary>Noise gate: silences the signal below a threshold, with hold and smooth fades.</summary>
public sealed class Gate
{
    readonly Envelope _env = new(1, 120);
    float _gain;
    int _hold;
    public double ThresholdDb { get; set; } = -45;

    public void Process(Span<float> s)
    {
        float thr = Dsp.DbToGain(ThresholdDb);
        float open = Dsp.Coef(3), close = Dsp.Coef(80);
        for (int i = 0; i < s.Length; i += 2)
        {
            float lvl = _env.Next(Math.Max(Math.Abs(s[i]), Math.Abs(s[i + 1])));
            if (lvl > thr) _hold = (int)(Dsp.SampleRate * 0.15); // keep open 150 ms after the voice drops
            float target = _hold-- > 0 ? 1 : 0;
            float c = target > _gain ? open : close;
            _gain = c * _gain + (1 - c) * target;
            s[i] *= _gain;
            s[i + 1] *= _gain;
        }
    }
}

/// <summary>Feed-forward compressor with soft knee and automatic make-up gain.</summary>
public sealed class Compressor
{
    readonly Envelope _env = new(5, 150);
    public double ThresholdDb { get; set; } = -24;
    public double Ratio { get; set; } = 3;

    public void Process(Span<float> s)
    {
        double makeup = -ThresholdDb * (1 - 1 / Ratio) * 0.5;
        for (int i = 0; i < s.Length; i += 2)
        {
            float lvl = _env.Next(Math.Max(Math.Abs(s[i]), Math.Abs(s[i + 1])));
            double over = Dsp.GainToDb(lvl) - ThresholdDb;
            double reduction = over > 0 ? over * (1 - 1 / Ratio) : 0;
            float g = Dsp.DbToGain(makeup - reduction);
            s[i] *= g;
            s[i + 1] *= g;
        }
    }
}

/// <summary>
/// Output limiter: instant attack, smooth release, so loud peaks are turned down instead of clipping
/// (protects ears from sudden loud sounds). Ceiling in dBFS.
/// </summary>
public sealed class Limiter
{
    float _gain = 1;
    readonly float _release = Dsp.Coef(250);
    public double CeilingDb { get; set; } = -1;
    public float Reduction => _gain;

    public void Process(Span<float> s)
    {
        float ceiling = Dsp.DbToGain(CeilingDb);
        for (int i = 0; i < s.Length; i += 2)
        {
            float peak = Math.Max(Math.Abs(s[i]), Math.Abs(s[i + 1]));
            float target = peak > ceiling ? ceiling / peak : 1;
            // Turn down instantly, recover slowly
            _gain = target < _gain ? target : _release * _gain + (1 - _release) * target;
            s[i] *= _gain;
            s[i + 1] *= _gain;
        }
    }
}

/// <summary>
/// Reads a ring buffer at a slightly variable speed (±0.5 %) using 4-point Hermite interpolation,
/// so the buffer level stays at its target when capture and output clocks drift apart,
/// without the clicks of dropping or repeating audio.
/// </summary>
public sealed class DriftResampler
{
    double _pos;          // fractional read position (frames) within _hist
    float[] _hist = new float[8192];
    int _histFrames;      // frames currently in _hist
    double _ratio = 1;
    double _avgFill = -1;

    /// <summary>Real clock drift is well under 0.01 %; ±0.08 % (≈1.4 cents) is enough headroom and stays inaudible.</summary>
    const double MaxDeviation = 0.0008;

    public double Ratio => _ratio;

    /// <param name="fill">Frames waiting in the source ring.</param>
    /// <param name="target">Desired average number of frames waiting.</param>
    public void Steer(int fill, int target)
    {
        // The fill level jumps by a whole capture packet every 10 ms, so steer on its smoothed average
        _avgFill = _avgFill < 0 ? fill : 0.98 * _avgFill + 0.02 * fill;
        double error = (_avgFill - target) / target;
        double want = 1 + Math.Clamp(error * 0.0005, -MaxDeviation, MaxDeviation);
        _ratio = 0.995 * _ratio + 0.005 * want;
    }

    /// <summary>Frames of input needed to produce <paramref name="outFrames"/> output frames.</summary>
    public int InputNeeded(int outFrames) => Math.Max(0, (int)Math.Ceiling(_pos + outFrames * _ratio) + 3 - _histFrames);

    public void Feed(ReadOnlySpan<float> input)
    {
        int frames = input.Length / 2;
        if ((_histFrames + frames) * 2 > _hist.Length) Array.Resize(ref _hist, (_histFrames + frames) * 4);
        input.CopyTo(_hist.AsSpan(_histFrames * 2));
        _histFrames += frames;
    }

    public void Produce(Span<float> output)
    {
        int frames = output.Length / 2;
        for (int f = 0; f < frames; f++)
        {
            int i = (int)_pos;
            float t = (float)(_pos - i);
            for (int c = 0; c < 2; c++)
            {
                float xm1 = Sample(i - 1, c), x0 = Sample(i, c), x1 = Sample(i + 1, c), x2 = Sample(i + 2, c);
                float c1 = 0.5f * (x1 - xm1);
                float c2 = xm1 - 2.5f * x0 + 2f * x1 - 0.5f * x2;
                float c3 = 0.5f * (x2 - xm1) + 1.5f * (x0 - x1);
                output[2 * f + c] = ((c3 * t + c2) * t + c1) * t + x0;
            }
            _pos += _ratio;
        }
        // Drop consumed history, keeping one frame before the read position for interpolation
        int drop = Math.Max(0, (int)_pos - 1);
        if (drop > 0)
        {
            drop = Math.Min(drop, _histFrames);
            _hist.AsSpan(drop * 2, (_histFrames - drop) * 2).CopyTo(_hist);
            _histFrames -= drop;
            _pos -= drop;
        }
    }

    float Sample(int frame, int ch) => frame < 0 || frame >= _histFrames ? 0 : _hist[frame * 2 + ch];
}

/// <summary>RNNoise (Xiph) neural noise suppression, mono, 480-sample frames at 48 kHz.</summary>
public sealed class NoiseSuppressor : IDisposable
{
    const int Frame = 480;
    IntPtr _state;
    readonly float[] _in = new float[Frame];
    readonly float[] _out = new float[Frame];
    readonly Queue<float> _pending = new();
    readonly Queue<float> _ready = new();

    public static bool Available { get; } = Probe();

    public NoiseSuppressor() => _state = rnnoise_create(IntPtr.Zero);

    static bool Probe()
    {
        try
        {
            var s = rnnoise_create(IntPtr.Zero);
            rnnoise_destroy(s);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"RNNoise unavailable: {ex.Message}");
            return false;
        }
    }

    /// <summary>Denoises in place. Adds one 10 ms frame of delay.</summary>
    public void Process(Span<float> s)
    {
        for (int i = 0; i < s.Length; i += 2) _pending.Enqueue((s[i] + s[i + 1]) * 0.5f);
        while (_pending.Count >= Frame)
        {
            // RNNoise expects 16-bit sample scale
            for (int i = 0; i < Frame; i++) _in[i] = _pending.Dequeue() * 32768f;
            rnnoise_process_frame(_state, _out, _in);
            for (int i = 0; i < Frame; i++) _ready.Enqueue(_out[i] / 32768f);
        }
        for (int i = 0; i < s.Length; i += 2)
        {
            float v = _ready.Count > 0 ? _ready.Dequeue() : 0;
            s[i] = v;
            s[i + 1] = v;
        }
    }

    public void Dispose()
    {
        if (_state != IntPtr.Zero) rnnoise_destroy(_state);
        _state = IntPtr.Zero;
    }

    [DllImport("rnnoise", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr rnnoise_create(IntPtr model);
    [DllImport("rnnoise", CallingConvention = CallingConvention.Cdecl)] static extern void rnnoise_destroy(IntPtr st);
    [DllImport("rnnoise", CallingConvention = CallingConvention.Cdecl)] static extern float rnnoise_process_frame(IntPtr st, float[] output, float[] input);
}
