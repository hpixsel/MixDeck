namespace MixDeck.Audio;

/// <summary>
/// Single-producer / single-consumer float ring buffer with latency control.
/// The producer is a capture thread, the consumer is one output (mix) thread.
/// All reads and writes are in interleaved stereo, so positions stay even.
/// Clock drift between capture and output is absorbed by reading slightly faster or slower
/// (<see cref="DriftResampler"/>); only large bursts fall back to skipping.
/// </summary>
public sealed class RingBuffer
{
    readonly float[] _buf;
    readonly int _mask;
    readonly int _target;
    readonly int _max;
    readonly DriftResampler _resampler = new();
    float[] _scratch = new float[4096];
    long _write;
    long _read;
    bool _primed;

    public RingBuffer(int capacityPow2, int targetSamples, int maxSamples)
    {
        _buf = new float[capacityPow2];
        _mask = capacityPow2 - 1;
        _target = targetSamples;
        _max = maxSamples;
    }

    /// <summary>Samples waiting (diagnostics).</summary>
    public int Available => (int)(Volatile.Read(ref _write) - Volatile.Read(ref _read));
    public double Ratio => _resampler.Ratio;

    public void Write(ReadOnlySpan<float> data)
    {
        long w = _write;
        long r = Volatile.Read(ref _read);
        int free = _buf.Length - (int)(w - r);
        if (data.Length > free) data = data[..(free & ~1)]; // consumer stalled: drop the newest audio

        int start = (int)(w & _mask);
        int first = Math.Min(data.Length, _buf.Length - start);
        data[..first].CopyTo(_buf.AsSpan(start));
        data[first..].CopyTo(_buf);
        Volatile.Write(ref _write, w + data.Length);
    }

    /// <summary>Fills <paramref name="dest"/>; returns false (dest untouched) when there is nothing to play yet.</summary>
    public bool Read(Span<float> dest)
    {
        long r = _read;
        long w = Volatile.Read(ref _write);
        int avail = (int)(w - r);

        if (!_primed)
        {
            if (avail < _target + dest.Length) return false;
            _primed = true;
        }
        if (avail > _max)
        {
            // A burst far beyond what drift correction can absorb: jump back to the target latency
            r = w - _target - dest.Length;
            avail = _target + dest.Length;
        }

        // Right after a read the buffer holds about the target; just before the next one, target + one block
        _resampler.Steer(avail / 2, (_target + dest.Length / 2) / 2);
        int need = _resampler.InputNeeded(dest.Length / 2) * 2;
        if (avail < need)
        {
            _primed = false; // underrun: wait until the buffer refills to the target
            return false;
        }

        if (_scratch.Length < need) _scratch = new float[need * 2];
        var input = _scratch.AsSpan(0, need);
        int start = (int)(r & _mask);
        int first = Math.Min(need, _buf.Length - start);
        _buf.AsSpan(start, first).CopyTo(input);
        _buf.AsSpan(0, need - first).CopyTo(input[first..]);
        Volatile.Write(ref _read, r + need);

        _resampler.Feed(input);
        _resampler.Produce(dest);
        return true;
    }
}
