using NAudio.Wave;

namespace MixDeck.Audio;

/// <summary>Something that produces 48 kHz float stereo audio and fans it out to one ring buffer per mix.</summary>
public abstract class AudioSource : IDisposable
{
    public const int MaxMixes = 8;
    public static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    // 32768 samples = ~340 ms of stereo audio
    const int Capacity = 1 << 15;

    /// <summary>Audio kept waiting per mix: enough to ride out 10 ms capture packets arriving in bursts.</summary>
    public static int TargetMs { get; set; } = 20;
    static int TargetSamples => 48000 * 2 * TargetMs / 1000;
    static int MaxSamples => 48000 * 2 * (TargetMs + 80) / 1000;

    readonly RingBuffer?[] _rings = new RingBuffer?[MaxMixes];
    float _peak;

    public RingBuffer? Ring(int slot) => Volatile.Read(ref _rings[slot]);

    public void EnsureRing(int slot)
    {
        if (Volatile.Read(ref _rings[slot]) == null)
            Volatile.Write(ref _rings[slot], new RingBuffer(Capacity, TargetSamples, MaxSamples));
    }

    public void RemoveRing(int slot) => Volatile.Write(ref _rings[slot], null);

    /// <summary>Peak since the last call (read by the UI meter timer).</summary>
    public float TakePeak()
    {
        float p = _peak;
        _peak = 0;
        return p;
    }

    protected void Push(ReadOnlySpan<float> samples)
    {
        float peak = 0;
        foreach (var s in samples)
        {
            float a = Math.Abs(s);
            if (a > peak) peak = a;
        }
        if (peak > _peak) _peak = peak;

        for (int i = 0; i < MaxMixes; i++)
            Volatile.Read(ref _rings[i])?.Write(samples);
    }

    public abstract void Dispose();
}
