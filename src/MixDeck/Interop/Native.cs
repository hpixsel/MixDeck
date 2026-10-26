using System.Runtime.InteropServices;

namespace MixDeck.Interop;

/// <summary>Sets the Windows default playback device (undocumented but long-stable IPolicyConfig, Windows 7+).</summary>
public static class DefaultDevice
{
    public static void Set(string deviceId)
    {
        var config = (IPolicyConfig)new CPolicyConfigClient();
        try
        {
            for (int role = 0; role < 3; role++) // eConsole, eMultimedia, eCommunications
                Marshal.ThrowExceptionForHR(config.SetDefaultEndpoint(deviceId, role));
        }
        finally
        {
            Marshal.ReleaseComObject(config);
        }
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class CPolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
    }
}

/// <summary>
/// Per-app output device override, the same setting as Windows "Volume mixer → app → Output device"
/// (undocumented IAudioPolicyConfig, Windows 10 21H2+). Windows only accepts it for processes that
/// currently have an audio session.
/// </summary>
public static class AppDeviceOverride
{
    const string RenderSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    const string Prefix = @"\\?\SWD#MMDEVAPI#";

    /// <summary>Points the process at <paramref name="deviceId"/>, or back to "follow the default" when null.</summary>
    public static void Set(int pid, string? deviceId)
    {
        var factory = Create();
        IntPtr h = IntPtr.Zero;
        try
        {
            if (deviceId != null)
            {
                var full = Prefix + deviceId + RenderSuffix;
                Marshal.ThrowExceptionForHR(WindowsCreateString(full, full.Length, out h));
            }
            foreach (var role in new[] { 0, 1 }) // eConsole, eMultimedia
                Marshal.ThrowExceptionForHR(factory.SetPersistedDefaultAudioEndpoint((uint)pid, 0 /* eRender */, role, h));
        }
        finally
        {
            if (h != IntPtr.Zero) WindowsDeleteString(h);
            Marshal.ReleaseComObject(factory);
        }
    }

    static IAudioPolicyConfig Create()
    {
        const string cls = "Windows.Media.Internal.AudioPolicyConfig";
        Marshal.ThrowExceptionForHR(WindowsCreateString(cls, cls.Length, out var hcls));
        try
        {
            var iid = typeof(IAudioPolicyConfig).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hcls, ref iid, out var f));
            try { return (IAudioPolicyConfig)Marshal.GetObjectForIUnknown(f); }
            finally { Marshal.Release(f); }
        }
        finally
        {
            WindowsDeleteString(hcls);
        }
    }

    [ComImport, Guid("ab3d4648-e242-459f-b02f-541c70306324"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioPolicyConfig
    {
        // IInspectable
        void GetIids(); void GetRuntimeClassName(); void GetTrustLevel();
        // methods we do not use, kept for vtable order
        void add_CtxVolumeChange(); void remove_CtxVolumeChanged(); void add_RingerVibrateStateChanged(); void remove_RingerVibrateStateChange();
        void SetVolumeGroupGainForId(); void GetVolumeGroupGainForId(); void GetActiveVolumeGroupForEndpointId(); void GetVolumeGroupsForEndpoint();
        void GetCurrentVolumeContext(); void SetVolumeGroupMuteForId(); void GetVolumeGroupMuteForId(); void SetRingerVibrateState(); void GetRingerVibrateState();
        void SetPreferredChatApplication(); void ResetPreferredChatApplication(); void GetPreferredChatApplication(); void GetCurrentChatApplications();
        void add_ChatContextChanged(); void remove_ChatContextChanged();
        [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, IntPtr deviceId);
        [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, out IntPtr deviceId);
        [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
    }

    [DllImport("combase.dll")] static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string s, int length, out IntPtr hstring);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr activatableClassId, [In] ref Guid iid, out IntPtr factory);
}

/// <summary>Snapshot of running processes: pid → (parent pid, exe name).</summary>
public static class ProcessTable
{
    public readonly record struct Entry(int ParentPid, string Exe);

    public static Dictionary<int, Entry> Snapshot()
    {
        var result = new Dictionary<int, Entry>();
        IntPtr snap = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == new IntPtr(-1)) return result;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                result[(int)e.th32ProcessID] = new Entry((int)e.th32ParentProcessID, e.szExeFile.ToLowerInvariant());
        }
        finally
        {
            CloseHandle(snap);
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
