using MixDeck.Audio;
using MixDeck.Model;

namespace MixDeck.Tests;

public class DspTests
{
    static float[] Sine(double freq, int frames, float amp = 0.5f)
    {
        var s = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float v = amp * (float)Math.Sin(2 * Math.PI * freq * i / 48000);
            s[2 * i] = v;
            s[2 * i + 1] = v;
        }
        return s;
    }

    static double Rms(ReadOnlySpan<float> s)
    {
        double sum = 0;
        foreach (var x in s) sum += x * x;
        return Math.Sqrt(sum / s.Length);
    }

    [Theory]
    [InlineData(BiquadType.Peaking, 1000, 6, 1000, 6)]     // boost at the centre frequency
    [InlineData(BiquadType.Peaking, 1000, 6, 8000, 0)]     // leaves far frequencies alone
    [InlineData(BiquadType.LowShelf, 120, -6, 40, -6)]     // cuts below the shelf
    [InlineData(BiquadType.HighShelf, 8000, 6, 16000, 6)]  // boosts above the shelf
    public void Biquad_has_expected_gain(BiquadType type, double fc, double gainDb, double probe, double expectDb)
    {
        var f = new Biquad(type, fc, gainDb, 0.7);
        var s = Sine(probe, 48000);
        double before = Rms(s.AsSpan(48000));
        f.Process(s);
        double after = Rms(s.AsSpan(48000)); // second half: filter settled
        Assert.InRange(Dsp.GainToDb(after / before), expectDb - 1, expectDb + 1);
    }

    [Fact]
    public void HighPass_removes_rumble_but_keeps_voice()
    {
        var low = Sine(30, 48000);
        var voice = Sine(300, 48000);
        new Biquad(BiquadType.HighPass, 80, 0, 0.7).Process(low);
        new Biquad(BiquadType.HighPass, 80, 0, 0.7).Process(voice);
        Assert.True(Rms(low.AsSpan(48000)) < 0.06);   // 30 Hz: about -16 dB or more
        Assert.True(Rms(voice.AsSpan(48000)) > 0.34);  // 300 Hz: nearly untouched (0.354)
    }

    [Fact]
    public void Limiter_keeps_peaks_under_the_ceiling()
    {
        var s = Sine(500, 24000, amp: 2f); // +6 dBFS, would clip hard
        new Limiter { CeilingDb = -1 }.Process(s);
        float ceiling = Dsp.DbToGain(-1);
        Assert.All(s, x => Assert.True(Math.Abs(x) <= ceiling + 1e-4));
    }

    [Fact]
    public void Limiter_passes_quiet_audio_unchanged()
    {
        var s = Sine(500, 4800, amp: 0.3f);
        var copy = (float[])s.Clone();
        new Limiter().Process(s);
        Assert.Equal(copy, s);
    }

    [Fact]
    public void Gate_silences_noise_and_opens_for_voice()
    {
        var noise = Sine(200, 24000, amp: 0.001f);   // -60 dBFS hiss
        var voice = Sine(200, 24000, amp: 0.3f);
        new Gate { ThresholdDb = -45 }.Process(noise);
        new Gate { ThresholdDb = -45 }.Process(voice);
        Assert.True(Rms(noise.AsSpan(24000)) < 1e-5);
        Assert.True(Rms(voice.AsSpan(24000)) > 0.2);
    }

    /// <summary>
    /// Pushes a 440 Hz tone through a ring buffer in 10 ms packets while the reader pulls 10 ms blocks
    /// at a slightly different clock, and returns the frequency that comes out plus the number of gaps.
    /// </summary>
    static (double Freq, int Gaps) ThroughRing(double producerSpeed, int seconds)
    {
        var ring = new RingBuffer(1 << 15, 1920, 9600);
        var output = new List<float>();
        var outBuf = new float[960];
        double phase = 0, owed = 0;
        int gaps = 0;
        for (int block = 0; block < seconds * 100; block++)
        {
            owed += producerSpeed;
            while (owed >= 1)
            {
                var packet = new float[960];
                for (int f = 0; f < 480; f++)
                {
                    packet[2 * f] = packet[2 * f + 1] = 0.5f * (float)Math.Sin(phase);
                    phase += 2 * Math.PI * 440 / 48000;
                }
                ring.Write(packet);
                owed -= 1;
            }
            if (ring.Read(outBuf)) { for (int i = 0; i < 960; i += 2) output.Add(outBuf[i]); }
            else if (block > 10) gaps++;
        }
        // Strongest frequency in the last 2 s (Goertzel, 0.05 Hz steps)
        var tail = output.Skip(output.Count - 96000).ToArray();
        double best = 0, bestAmp = 0;
        for (double f = 435; f <= 445; f += 0.05)
        {
            double w = 2 * Math.PI * f / 48000, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            foreach (var x in tail) { var s0 = x + c * s1 - s2; s2 = s1; s1 = s0; }
            double a = s1 * s1 + s2 * s2 - c * s1 * s2;
            if (a > bestAmp) { bestAmp = a; best = f; }
        }
        return (best, gaps);
    }

    [Fact]
    public void RingBuffer_keeps_pitch_when_clocks_match()
    {
        var (freq, gaps) = ThroughRing(1.0, 20);
        Assert.Equal(0, gaps);
        Assert.InRange(freq, 439.9, 440.1); // under 0.4 cents
    }

    [Fact]
    public void RingBuffer_absorbs_realistic_clock_drift()
    {
        // Two sound cards typically differ by up to ~100 ppm; use 300 ppm to be safe
        var (freq, gaps) = ThroughRing(1.0003, 60);
        Assert.Equal(0, gaps);
        Assert.InRange(freq, 439.8, 440.4); // the output follows the faster input clock, with no dropouts
    }

    [Fact]
    public void DriftResampler_preserves_a_tone_at_unity_speed()
    {
        var r = new DriftResampler();
        var input = Sine(1000, 4800);
        r.Feed(input);
        var output = new float[4000 * 2];
        r.Produce(output);
        // Hermite interpolation at integer positions returns the input samples exactly
        for (int i = 100; i < 4000; i++) Assert.Equal(input[2 * i], output[2 * i], 4);
    }

    [Fact]
    public void AutoEq_parses_parametric_file()
    {
        const string text = """
            Preamp: -6.4 dB
            Filter 1: ON LSC Fc 105 Hz Gain 6.2 dB Q 0.70
            Filter 2: ON PK Fc 2481 Hz Gain -3.5 dB Q 1.41
            Filter 3: ON HSC Fc 10000 Hz Gain -2.0 dB Q 0.70
            """;
        var (preamp, bands) = AutoEq.Parse(text);
        Assert.Equal(-6.4, preamp, 3);
        Assert.Equal(3, bands.Count);
        Assert.Equal("LSC", bands[0].Type);
        Assert.Equal(2481, bands[1].Freq);
        Assert.Equal(-3.5, bands[1].Gain, 3);
        Assert.Equal(1.41, bands[1].Q, 3);
    }

    [Fact]
    public void OutputFx_with_defaults_only_limits()
    {
        var fx = new OutputFx(new OutputFxConfig());
        Assert.False(fx.IsEmpty);
        Assert.True(new OutputFx(new OutputFxConfig { Limiter = false }).IsEmpty);
    }

    [Fact]
    public void Rnnoise_native_library_loads_and_reduces_noise()
    {
        Assert.True(NoiseSuppressor.Available);
        // Fan-like (low-passed) noise for 3 s; the network needs a moment to settle, so judge the last second
        var rnd = new Random(1);
        int n = 48000 * 3;
        var noise = new float[n * 2];
        float state = 0;
        for (int i = 0; i < n; i++) noise[2 * i] = noise[2 * i + 1] = state = 0.98f * state + 0.04f * (float)(rnd.NextDouble() - 0.5);
        var tail = (n - 48000) * 2;
        double before = Rms(noise.AsSpan(tail));
        using var ns = new NoiseSuppressor();
        for (int off = 0; off < noise.Length; off += 960) ns.Process(noise.AsSpan(off, 960));
        double after = Rms(noise.AsSpan(tail));
        Assert.True(Dsp.GainToDb(after / before) < -20, $"fan noise should drop by over 20 dB: {Dsp.GainToDb(after / before):F1} dB");
    }
}
