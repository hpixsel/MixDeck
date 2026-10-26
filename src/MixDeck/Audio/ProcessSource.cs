using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MixDeck.Audio;

/// <summary>
/// Captures the audio rendered by one process tree using WASAPI process loopback (Windows 10 2004+).
/// Captures post per-app volume/mute, independent of the endpoint the process plays to.
/// </summary>
public sealed class ProcessSource : AudioSource
{
    public int Pid { get; }
    public string Exe { get; }

    readonly AudioClient _client;
    readonly AutoResetEvent _event = new(false);
    readonly Thread _thread;
    volatile bool _running = true;

    ProcessSource(int pid, string exe, AudioClient client)
    {
        Pid = pid;
        Exe = exe;
        _client = client;
        _client.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle());
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = $"Capture {exe}:{pid}" };
        _thread.Start();
    }

    public static async Task<ProcessSource> CreateAsync(int pid, string exe)
    {
        var p = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = (uint)pid,
            ProcessLoopbackMode = 0, // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
        };
        int size = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        IntPtr pParams = Marshal.AllocHGlobal(size);
        IntPtr pVariant = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT_BLOB>());
        try
        {
            Marshal.StructureToPtr(p, pParams, false);
            Marshal.StructureToPtr(new PROPVARIANT_BLOB { vt = 65 /* VT_BLOB */, cbSize = (uint)size, pBlobData = pParams }, pVariant, false);
            var handler = new CompletionHandler();
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(
                "VAD\\Process_Loopback", typeof(IAudioClient).GUID, pVariant, handler, out _));
            var client = new AudioClient((IAudioClient)await handler.Task.ConfigureAwait(false));

            // Process loopback has no mix format; we request ours and let Windows convert
            const AudioClientStreamFlags flags = AudioClientStreamFlags.Loopback
                | AudioClientStreamFlags.EventCallback
                | (AudioClientStreamFlags)0x80000000  // AUTOCONVERTPCM
                | (AudioClientStreamFlags)0x08000000; // SRC_DEFAULT_QUALITY
            client.Initialize(AudioClientShareMode.Shared, flags, 200_000, 0, Format, Guid.Empty);
            return new ProcessSource(pid, exe, client);
        }
        finally
        {
            Marshal.FreeHGlobal(pParams);
            Marshal.FreeHGlobal(pVariant);
        }
    }

    void Loop()
    {
        var buf = new float[48000 * 2];
        try
        {
            var cc = _client.AudioCaptureClient;
            _client.Start();
            bool debug = Environment.GetEnvironmentVariable("MIXDECK_DEBUG") == "1";
            long packets = 0, wakes = 0;
            var lastReport = Environment.TickCount64;
            while (_running)
            {
                bool signaled = _event.WaitOne(100);
                if (signaled) wakes++;
                if (debug && Environment.TickCount64 - lastReport > 1000)
                {
                    Log.Write($"[dbg] {Exe}:{Pid} wakes={wakes} packets={packets} peak={TakePeak():F4}");
                    lastReport = Environment.TickCount64;
                }
                while (_running && cc.GetNextPacketSize() > 0)
                {
                    IntPtr data = cc.GetBuffer(out int frames, out var bufferFlags);
                    int n = frames * 2;
                    if (n > buf.Length) buf = new float[n];
                    if ((bufferFlags & AudioClientBufferFlags.Silent) != 0) Array.Clear(buf, 0, n);
                    else Marshal.Copy(data, buf, 0, n);
                    cc.ReleaseBuffer(frames);
                    packets++;
                    Push(buf.AsSpan(0, n));
                }
            }
            _client.Stop();
        }
        catch (Exception ex) when (!_running)
        {
            _ = ex; // shutting down
        }
        catch (Exception ex)
        {
            Log.Write($"Capture {Exe}:{Pid} failed: {ex.Message}");
        }
    }

    public override void Dispose()
    {
        _running = false;
        _event.Set();
        _thread.Join(500);
        _client.Dispose();
        _event.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AUDIOCLIENT_ACTIVATION_PARAMS { public int ActivationType; public uint TargetProcessId; public int ProcessLoopbackMode; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT_BLOB { public ushort vt; ushort r1, r2, r3; public uint cbSize; public IntPtr pBlobData; }

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr activationParams,
        IActivateCompletionHandler handler, out IActivateOperation op);

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateOperation { void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface); }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateCompletionHandler { void ActivateCompleted(IActivateOperation op); }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    sealed class CompletionHandler : IActivateCompletionHandler, IAgileObject
    {
        readonly TaskCompletionSource<object> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<object> Task => _tcs.Task;

        public void ActivateCompleted(IActivateOperation op)
        {
            op.GetActivateResult(out int hr, out var iface);
            if (hr < 0) _tcs.TrySetException(Marshal.GetExceptionForHR(hr)!);
            else _tcs.TrySetResult(iface);
        }
    }
}
