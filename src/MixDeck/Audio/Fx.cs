using System.Globalization;
using System.Text.RegularExpressions;
using MixDeck.Model;

namespace MixDeck.Audio;

/// <summary>Microphone processing chain: rumble filter → noise suppression → gate → compressor → gain.</summary>
public sealed class InputChain : IDisposable
{
    readonly Biquad? _highPass;
    readonly NoiseSuppressor? _denoise;
    readonly Gate? _gate;
    readonly Compressor? _comp;
    readonly float _gain;

    public InputChain(InputFxConfig cfg)
    {
        if (cfg.HighPass) _highPass = new Biquad(BiquadType.HighPass, 80, 0, 0.7);
        if (cfg.NoiseSuppression && NoiseSuppressor.Available) _denoise = new NoiseSuppressor();
        if (cfg.Gate) _gate = new Gate { ThresholdDb = cfg.GateThresholdDb };
        if (cfg.Compressor) _comp = new Compressor();
        _gain = Dsp.DbToGain(cfg.GainDb);
    }

    public void Process(Span<float> s)
    {
        _highPass?.Process(s);
        _denoise?.Process(s);
        _gate?.Process(s);
        _comp?.Process(s);
        if (_gain != 1) foreach (ref float x in s) x *= _gain;
    }

    public void Dispose() => _denoise?.Dispose();
}

/// <summary>Reads AutoEQ "ParametricEQ.txt" headphone correction files (github.com/jaakkopasanen/AutoEq).</summary>
public static partial class AutoEq
{
    [GeneratedRegex(@"Preamp:\s*(-?[\d.]+)\s*dB", RegexOptions.IgnoreCase)]
    private static partial Regex PreampRx();

    [GeneratedRegex(@"Filter\s*\d*:\s*ON\s+(\w+)\s+Fc\s+([\d.]+)\s*Hz\s+Gain\s+(-?[\d.]+)\s*dB(?:\s+Q\s+([\d.]+))?", RegexOptions.IgnoreCase)]
    private static partial Regex FilterRx();

    public static (double Preamp, List<EqBand> Bands) Parse(string text)
    {
        var inv = CultureInfo.InvariantCulture;
        double preamp = 0;
        var m = PreampRx().Match(text);
        if (m.Success) preamp = double.Parse(m.Groups[1].Value, inv);
        var bands = new List<EqBand>();
        foreach (Match f in FilterRx().Matches(text))
        {
            bands.Add(new EqBand
            {
                Type = f.Groups[1].Value.ToUpperInvariant(),
                Freq = double.Parse(f.Groups[2].Value, inv),
                Gain = double.Parse(f.Groups[3].Value, inv),
                Q = f.Groups[4].Success ? double.Parse(f.Groups[4].Value, inv) : 0.7,
            });
        }
        return (preamp, bands);
    }
}
