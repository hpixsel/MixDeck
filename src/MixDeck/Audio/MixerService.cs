using System.Diagnostics;
using System.Text.RegularExpressions;
using MixDeck.Interop;
using MixDeck.Model;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MixDeck.Audio;

public sealed record DeviceInfo(string Id, string Name);

/// <param name="BypassDevice">Set when the app plays straight to a real device, past the mixer.</param>
public sealed record AppInfo(string Exe, string DisplayName, string? ImagePath, string ChannelId, bool Playing, string? BypassDevice);

public enum StatusLevel { Ok, Warning, Error }
public enum StatusFix { None, OpenSettings, ApplyDefault }
public sealed record StatusInfo(StatusLevel Level, string Text, StatusFix Fix = StatusFix.None);

/// <summary>
/// Owns the audio engine. Every app plays to a silent "collector" device (set as the Windows default);
/// each app's audio is captured with process loopback, put into a channel, and summed into the mixes.
/// Public methods are called from the UI thread; a background thread polls sessions and devices.
/// </summary>
public sealed class MixerService : IDisposable
{
    public const int PollIntervalMs = 1500;

    readonly object _gate = new();
    readonly AppConfig _cfg;
    readonly MixBus _bus = new();
    readonly Dictionary<string, EngineChannel> _channels = [];
    readonly Dictionary<string, MixOutput> _mixes = [];
    readonly Dictionary<int, ProcessSource> _procs = [];
    readonly HashSet<int> _pending = [];
    readonly HashSet<int> _failed = [];
    readonly Dictionary<string, DeviceSource> _inputs = [];
    readonly Dictionary<string, string> _displayNames = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string?> _imagePaths = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, List<int>> _bypassPids = new(StringComparer.OrdinalIgnoreCase);
    bool _appVolumesDirty = true;
    HashSet<int> _seenSessions = [];
    readonly HashSet<string> _adjustedApps = new(StringComparer.OrdinalIgnoreCase);
    string _runningSignature = "";
    long _lastGuardLog;
    readonly Thread _poller;
    readonly AutoResetEvent _wake = new(false);
    readonly Timer _saveTimer;
    readonly int _selfPid = Environment.ProcessId;
    volatile bool _stopping;
    bool _defaultApplied;
    string _deviceSignature = "";
    string _appsSignature = "";

    public event Action<IReadOnlyList<AppInfo>>? AppsChanged;
    public event Action? DevicesChanged;
    public event Action<StatusInfo>? StatusChanged;
    /// <summary>A mix switched to or from a fallback device.</summary>
    public event Action? OutputsChanged;
    /// <summary>Set of running executables (lower-case), raised when it changes. Used for profile auto-switching.</summary>
    public event Action<IReadOnlySet<string>>? RunningAppsChanged;
    public bool IsFirstRun { get; }

    public AppConfig Config => _cfg;
    public IReadOnlyList<DeviceInfo> RenderDevices { get; private set; } = [];
    public IReadOnlyList<DeviceInfo> InputDevices { get; private set; } = [];
    public string? CollectorId { get; private set; }
    public StatusInfo Status { get; private set; } = new(StatusLevel.Ok, "Starting…");

    public MixerService(AppConfig? config)
    {
        _cfg = config ?? CreateDefaultConfig();
        IsFirstRun = !_cfg.FirstRunDone;
        AudioSource.TargetMs = _cfg.LowLatency ? 10 : 20;
        MixOutput.LatencyMs = _cfg.LowLatency ? 15 : 25;
        Normalize(_cfg);

        int slot = 0;
        foreach (var m in _cfg.Mixes)
        {
            var mo = new MixOutput(_bus, slot++);
            mo.Master = ToGain(m.Volume);
            mo.Muted = m.Muted;
            _mixes[m.Id] = mo;
            ApplyMixFx(m);
        }
        foreach (var c in _cfg.Channels)
        {
            var ec = new EngineChannel();
            _channels[c.Id] = ec;
            ApplyChannel(c);
        }
        PublishChannels();
        ApplyDucking();

        _saveTimer = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
        _poller = new Thread(PollLoop) { IsBackground = true, Name = "MixDeck poll" };
        SaveNow();
    }

    public void Start() => _poller.Start();

    public static float ToGain(double volume) => (float)Math.Pow(Math.Clamp(volume, 0, 100) / 100.0, 2);

    #region UI-facing API

    public void SetChannel(string id, double volume, bool muted)
    {
        lock (_gate)
        {
            var c = FindChannel(id);
            if (c == null) return;
            c.Volume = volume;
            c.Muted = muted;
            ApplyChannel(c);
        }
        ScheduleSave();
    }

    public void SetSend(string channelId, string mixId, double volume, bool muted)
    {
        lock (_gate)
        {
            var c = FindChannel(channelId);
            if (c == null) return;
            var s = c.Send(mixId);
            s.Volume = volume;
            s.Muted = muted;
            ApplyChannel(c);
        }
        ScheduleSave();
    }

    public void SetMix(string mixId, double volume, bool muted)
    {
        lock (_gate)
        {
            var m = FindMix(mixId);
            if (m == null) return;
            m.Volume = volume;
            m.Muted = muted;
            _mixes[mixId].Master = ToGain(volume);
            _mixes[mixId].Muted = muted;
        }
        ScheduleSave();
    }

    public void SetMixOutput(string mixId, string? deviceId)
    {
        lock (_gate)
        {
            var m = FindMix(mixId);
            if (m == null || m.OutputDeviceId == deviceId) return;
            m.OutputDeviceId = deviceId;
            m.FallbackDeviceIds.Remove(deviceId ?? "");
        }
        ScheduleSave();
        _wake.Set();
    }

    public void RenameChannel(string id, string name)
    {
        lock (_gate) { if (FindChannel(id) is { } c) c.Name = name; }
        ScheduleSave();
    }

    public void RenameMix(string id, string name)
    {
        lock (_gate) { if (FindMix(id) is { } m) m.Name = name; }
        ScheduleSave();
    }

    public ChannelConfig AddChannel(string name, string kind, string icon, string? inputDeviceId = null)
    {
        var c = new ChannelConfig { Name = name, Kind = kind, Icon = icon, InputDeviceId = inputDeviceId };
        // Hearing your own microphone is rarely wanted, so new inputs start muted in every mix
        if (kind == ChannelKind.Input)
            foreach (var m in _cfg.Mixes) c.Send(m.Id).Muted = true;
        lock (_gate)
        {
            _cfg.Channels.Add(c);
            _channels[c.Id] = new EngineChannel();
            ApplyChannel(c);
            PublishChannels();
        }
        ScheduleSave();
        _wake.Set();
        return c;
    }

    public void RemoveChannel(string id)
    {
        DeviceSource? input = null;
        lock (_gate)
        {
            var c = FindChannel(id);
            if (c == null || c.Kind == ChannelKind.System) return;
            _cfg.Channels.Remove(c);
            _channels.Remove(id);
            if (_inputs.Remove(id, out input)) { }
            PublishChannels();
            RebuildSources();
        }
        input?.Dispose();
        ScheduleSave();
        _wake.Set();
    }

    public MixConfig? AddMix(string name, string icon)
    {
        lock (_gate)
        {
            var used = _mixes.Values.Select(m => m.Slot).ToHashSet();
            int slot = Enumerable.Range(0, AudioSource.MaxMixes).FirstOrDefault(s => !used.Contains(s), -1);
            if (slot < 0) return null;

            var m = new MixConfig { Name = name, Icon = icon };
            _cfg.Mixes.Add(m);
            var mo = new MixOutput(_bus, slot);
            _mixes[m.Id] = mo;
            ApplyMixFx(m);
            foreach (var src in AllSources()) src.EnsureRing(slot);
            foreach (var c in _cfg.Channels) ApplyChannel(c);
            ScheduleSave();
            return m;
        }
    }

    public void RemoveMix(string id)
    {
        MixOutput? mo;
        lock (_gate)
        {
            var m = FindMix(id);
            if (m == null || _cfg.Mixes.Count <= 1) return;
            _cfg.Mixes.Remove(m);
            _mixes.Remove(id, out mo);
            foreach (var c in _cfg.Channels) c.Sends.Remove(id);
            if (mo != null) foreach (var src in AllSources()) src.RemoveRing(mo.Slot);
        }
        mo?.Dispose();
        ScheduleSave();
    }

    public void AssignApp(string exe, string channelId)
    {
        lock (_gate)
        {
            var target = FindChannel(channelId);
            if (target == null) return;
            foreach (var c in _cfg.Channels) c.Apps.RemoveAll(a => a.Equals(exe, StringComparison.OrdinalIgnoreCase));
            if (target.Kind == ChannelKind.Apps) target.Apps.Add(exe.ToLowerInvariant());
            RebuildSources();
        }
        ScheduleSave();
        _wake.Set();
    }

    public void SetCollector(string deviceId)
    {
        lock (_gate)
        {
            RestoreDefault();
            _cfg.CollectorDevice = deviceId;
            _defaultApplied = false;
        }
        ScheduleSave();
        _wake.Set();
    }

    public void SetManageDefaultDevice(bool manage)
    {
        lock (_gate)
        {
            _cfg.ManageDefaultDevice = manage;
            if (!manage) RestoreDefault();
            _defaultApplied = false;
        }
        ScheduleSave();
        _wake.Set();
    }

    /// <summary>"Fix" button: take over the Windows default output again.</summary>
    public void ApplyDefaultNow()
    {
        lock (_gate)
        {
            _cfg.ManageDefaultDevice = true;
            _defaultApplied = false;
        }
        ScheduleSave();
        _wake.Set();
    }

    public void SetMixFallbacks(string mixId, IEnumerable<string> deviceIds)
    {
        lock (_gate)
        {
            var m = FindMix(mixId);
            if (m == null) return;
            m.FallbackDeviceIds = deviceIds.Where(id => id != m.OutputDeviceId).Distinct().ToList();
        }
        ScheduleSave();
        _wake.Set();
    }

    /// <summary>The device a mix is playing on right now (may be a fallback), or null when none is available.</summary>
    public string? ActiveDeviceOf(string mixId)
    {
        lock (_gate) return _mixes.TryGetValue(mixId, out var mo) && mo.Running ? mo.DeviceId : null;
    }

    /// <summary>
    /// Removes the per-app output override of an app that plays past the mixer, so it follows the
    /// default (the collector) again. Apps that pick a device in their own settings are not affected.
    /// </summary>
    public int RedirectApp(string exe)
    {
        List<int> pids;
        lock (_gate) pids = _bypassPids.GetValueOrDefault(exe) ?? [];
        int ok = 0;
        foreach (var pid in pids)
        {
            try
            {
                AppDeviceOverride.Set(pid, null);
                ok++;
            }
            catch (Exception ex)
            {
                Log.Write($"Redirect {exe}:{pid} failed: {ex.Message}");
            }
        }
        Log.Write($"Redirected {exe}: {ok}/{pids.Count} processes");
        _wake.Set();
        return ok;
    }

    public void SetInputFx(string channelId, InputFxConfig fx)
    {
        lock (_gate)
        {
            if (FindChannel(channelId) is not { } c) return;
            c.InputFx = fx;
            if (_inputs.TryGetValue(channelId, out var src)) src.Chain = new InputChain(fx);
        }
        ScheduleSave();
    }

    public void SetMixFx(string mixId, OutputFxConfig fx)
    {
        lock (_gate)
        {
            if (FindMix(mixId) is not { } m) return;
            m.Fx = fx;
            ApplyMixFx(m);
        }
        ScheduleSave();
    }

    public void SetDucking(DuckingConfig ducking)
    {
        lock (_gate)
        {
            _cfg.Ducking = ducking;
            ApplyDucking();
        }
        ScheduleSave();
    }

    public void SetLowLatency(bool low)
    {
        lock (_gate) _cfg.LowLatency = low;
        ScheduleSave();
    }

    /// <summary>Current ducking gain (1 = not ducked) and whether a channel is one of the ducked targets.</summary>
    public float DuckGain => _bus.DuckGain;
    public bool IsDuckTarget(string channelId)
    {
        lock (_gate) return _cfg.Ducking.Enabled && _cfg.Ducking.TargetChannelIds.Contains(channelId);
    }

    public double AppVolume(string exe)
    {
        lock (_gate) return _cfg.AppVolumes.TryGetValue(exe, out var v) ? v : 100;
    }

    /// <summary>Volume of one app inside its channel. Applied to the app's session on the next poll (woken now).</summary>
    public void SetAppVolume(string exe, double volume)
    {
        lock (_gate)
        {
            if (volume >= 99.5) _cfg.AppVolumes.Remove(exe);
            else _cfg.AppVolumes[exe] = volume;
            _appVolumesDirty = true;
        }
        ScheduleSave();
        _wake.Set();
    }

    public void MarkFirstRunDone()
    {
        lock (_gate) _cfg.FirstRunDone = true;
        ScheduleSave();
    }

    public void Save() => ScheduleSave();

    public float TakeChannelPeak(string id)
    {
        lock (_gate) return _channels.TryGetValue(id, out var c) ? c.TakePeak() : 0;
    }

    public float TakeMixPeak(string id)
    {
        lock (_gate) return _mixes.TryGetValue(id, out var m) ? m.TakePeak() : 0;
    }

    public string ChannelFor(string exe)
    {
        foreach (var c in _cfg.Channels)
            if (c.Kind == ChannelKind.Apps && c.Apps.Contains(exe, StringComparer.OrdinalIgnoreCase))
                return c.Id;
        return _cfg.Channels.First(c => c.Kind == ChannelKind.System).Id;
    }

    #endregion

    #region Engine wiring (call under _gate)

    ChannelConfig? FindChannel(string id) => _cfg.Channels.FirstOrDefault(c => c.Id == id);
    MixConfig? FindMix(string id) => _cfg.Mixes.FirstOrDefault(m => m.Id == id);

    IEnumerable<AudioSource> AllSources() => _procs.Values.Cast<AudioSource>().Concat(_inputs.Values);

    void ApplyChannel(ChannelConfig c)
    {
        var ec = _channels[c.Id];
        ec.Gain = ToGain(c.Volume);
        ec.Muted = c.Muted;
        foreach (var m in _cfg.Mixes)
        {
            var s = c.Send(m.Id);
            int slot = _mixes[m.Id].Slot;
            ec.SendGain[slot] = ToGain(s.Volume);
            ec.SendMuted[slot] = s.Muted;
        }
    }

    void PublishChannels()
    {
        _bus.Channels = _cfg.Channels.Select(c => _channels[c.Id]).ToArray();
        ApplyDucking();
    }

    void ApplyMixFx(MixConfig m)
    {
        var fx = new OutputFx(m.Fx);
        _mixes[m.Id].Fx = fx.IsEmpty ? null : fx;
    }

    void ApplyDucking()
    {
        var d = _cfg.Ducking;
        _bus.Ducking = d.Enabled && d.TriggerChannelId != null && _channels.TryGetValue(d.TriggerChannelId, out var trigger)
            ? new DuckSettings(trigger,
                d.TargetChannelIds.Where(id => id != d.TriggerChannelId && _channels.ContainsKey(id)).Select(id => _channels[id]).ToHashSet(),
                Dsp.DbToGain(-Math.Abs(d.AmountDb)),
                Dsp.DbToGain(d.ThresholdDb))
            : null;
    }

    void RebuildSources()
    {
        var lists = _cfg.Channels.ToDictionary(c => c.Id, _ => new List<AudioSource>());
        foreach (var p in _procs.Values) lists[ChannelFor(p.Exe)].Add(p);
        foreach (var (channelId, input) in _inputs)
            if (lists.TryGetValue(channelId, out var l)) l.Add(input);
        foreach (var (id, list) in lists) _channels[id].Sources = list.ToArray();
    }

    #endregion

    #region Polling

    void PollLoop()
    {
        while (!_stopping)
        {
            try { Poll(); }
            catch (Exception ex) { Log.Write($"Poll failed: {ex}"); }
            _wake.WaitOne(PollIntervalMs);
        }
    }

    void Poll()
    {
        using var en = new MMDeviceEnumerator();
        var renders = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        var captures = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        try
        {
            var collector = renders.FirstOrDefault(d => d.ID == _cfg.CollectorDevice)
                ?? renders.FirstOrDefault(d => d.FriendlyName.Contains(_cfg.CollectorDevice, StringComparison.OrdinalIgnoreCase));
            CollectorId = collector?.ID;
            UpdateDeviceLists(renders, captures);

            lock (_gate)
            {
                if (!_defaultApplied && collector != null) ApplyDefault(en, collector);
                else if (collector != null) GuardDefault(en, collector);
            }

            ScanSessions(renders, collector);

            lock (_gate)
            {
                if (_stopping) return;
                UpdateOutputs(en, renders);
                EnsureInputs(en);
            }

            UpdateStatus(en, collector);
        }
        finally
        {
            foreach (var d in renders) d.Dispose();
            foreach (var d in captures) d.Dispose();
        }
    }

    /// <summary>Plays each mix on its device, or the first available fallback; switches back when the primary returns.</summary>
    void UpdateOutputs(MMDeviceEnumerator en, List<MMDevice> renders)
    {
        var active = renders.Select(d => d.ID).ToHashSet();
        bool changed = false;
        foreach (var m in _cfg.Mixes)
        {
            var mo = _mixes[m.Id];
            var desired = new[] { m.OutputDeviceId }.Concat(m.FallbackDeviceIds)
                .FirstOrDefault(id => id != null && id != CollectorId && active.Contains(id));
            if (desired != mo.DeviceId)
            {
                Log.Write($"Mix {m.Name}: using {renders.FirstOrDefault(d => d.ID == desired)?.FriendlyName ?? "(no device)"}");
                mo.SetDevice(desired);
                changed = true;
            }
            bool wasRunning = mo.Running;
            mo.TryStart(en);
            changed |= wasRunning != mo.Running;
        }
        if (changed) OutputsChanged?.Invoke();
    }

    void UpdateDeviceLists(List<MMDevice> renders, List<MMDevice> captures)
    {
        var r = renders.Select(d => new DeviceInfo(d.ID, d.FriendlyName)).OrderBy(d => d.Name).ToList();
        var c = captures.Select(d => new DeviceInfo(d.ID, d.FriendlyName)).OrderBy(d => d.Name).ToList();
        var sig = string.Join("|", r.Select(d => d.Id)) + "#" + string.Join("|", c.Select(d => d.Id)) + "#" + CollectorId;
        if (sig == _deviceSignature) return;
        _deviceSignature = sig;
        RenderDevices = r;
        InputDevices = c;
        DevicesChanged?.Invoke();
    }

    void ScanSessions(List<MMDevice> renders, MMDevice? collector)
    {
        var table = ProcessTable.Snapshot();
        // Only apps the user has adjusted in MixDeck are touched; the rest keep their Windows volume
        Dictionary<string, double> appVolumes;
        HashSet<string> managedApps;
        bool volumesChanged;
        lock (_gate)
        {
            appVolumes = new(_cfg.AppVolumes, StringComparer.OrdinalIgnoreCase);
            _adjustedApps.UnionWith(appVolumes.Keys);
            managedApps = new(_adjustedApps, StringComparer.OrdinalIgnoreCase);
            volumesChanged = _appVolumesDirty;
            _appVolumesDirty = false;
        }
        var onCollector = new Dictionary<int, bool>(); // pid -> currently playing
        var bypass = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // exe -> device
        var bypassPids = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var dev in renders)
        {
            bool isCollector = collector != null && dev.ID == collector.ID;
            try
            {
                var mgr = dev.AudioSessionManager;
                mgr.RefreshSessions();
                var sessions = mgr.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    int pid = (int)s.GetProcessID;
                    var state = s.State;
                    if (pid == 0 || pid == _selfPid || state == AudioSessionState.AudioSessionStateExpired) continue;
                    if (!table.TryGetValue(pid, out var proc)) continue;
                    bool playing = state == AudioSessionState.AudioSessionStateActive;
                    if (isCollector)
                    {
                        onCollector[pid] = onCollector.GetValueOrDefault(pid) || playing;
                        // New session or a slider moved: apply the app's own volume (100% when reset)
                        if ((volumesChanged || !_seenSessions.Contains(pid)) && managedApps.Contains(proc.Exe))
                        {
                            float want = (float)(appVolumes.GetValueOrDefault(proc.Exe, 100) / 100);
                            var sv = s.SimpleAudioVolume;
                            if (Math.Abs(sv.Volume - want) > 0.005f) sv.Volume = want;
                        }
                    }
                    else if (playing)
                    {
                        bypass[proc.Exe] = dev.FriendlyName;
                        if (!bypassPids.TryGetValue(proc.Exe, out var list)) bypassPids[proc.Exe] = list = [];
                        list.Add(pid);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write($"Sessions on {dev.FriendlyName}: {ex.Message}");
            }
        }

        _seenSessions = onCollector.Keys.ToHashSet();

        var running = table.Values.Select(e => e.Exe).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var runningSig = string.Join("|", running.Order());
        if (runningSig != _runningSignature)
        {
            _runningSignature = runningSig;
            RunningAppsChanged?.Invoke(running);
        }

        // Process loopback captures a whole process tree, so skip pids whose ancestor is captured already
        var roots = onCollector.Keys.Where(pid => !HasCapturedAncestor(pid, onCollector, table)).ToHashSet();

        var toDispose = new List<ProcessSource>();
        lock (_gate)
        {
            if (_stopping) return;
            bool changed = false;
            foreach (var (pid, src) in _procs.ToList())
            {
                if (roots.Contains(pid) && table[pid].Exe == src.Exe) continue;
                _procs.Remove(pid);
                toDispose.Add(src);
                changed = true;
            }
            _failed.RemoveWhere(pid => !table.ContainsKey(pid));
            _bypassPids = bypassPids;
            foreach (var pid in roots)
                if (!_procs.ContainsKey(pid) && !_pending.Contains(pid) && !_failed.Contains(pid))
                    StartCapture(pid, table[pid].Exe);
            if (changed) RebuildSources();
        }
        foreach (var src in toDispose)
        {
            Log.Write($"Released {src.Exe}:{src.Pid}");
            src.Dispose();
        }

        // Publish the app list for the UI
        var apps = new List<AppInfo>();
        lock (_gate)
        {
            foreach (var g in roots.GroupBy(pid => table[pid].Exe))
                apps.Add(new AppInfo(g.Key, DisplayName(g.First(), g.Key), _imagePaths.GetValueOrDefault(g.Key), ChannelFor(g.Key), g.Any(p => onCollector[p]), null));
            foreach (var (exe, device) in bypass)
                if (apps.All(a => a.Exe != exe))
                    apps.Add(new AppInfo(exe, DisplayName(table.First(t => t.Value.Exe == exe).Key, exe), _imagePaths.GetValueOrDefault(exe), ChannelFor(exe), true, device));
        }
        apps.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        var sig = string.Join("|", apps);
        if (sig != _appsSignature)
        {
            _appsSignature = sig;
            AppsChanged?.Invoke(apps);
        }
    }

    static bool HasCapturedAncestor(int pid, Dictionary<int, bool> captured, Dictionary<int, ProcessTable.Entry> table)
    {
        int cur = pid;
        for (int depth = 0; depth < 32 && table.TryGetValue(cur, out var e); depth++)
        {
            cur = e.ParentPid;
            if (cur == 0 || cur == pid) return false;
            if (captured.ContainsKey(cur)) return true;
        }
        return false;
    }

    void StartCapture(int pid, string exe)
    {
        _pending.Add(pid);
        Task.Run(async () =>
        {
            try
            {
                var src = await ProcessSource.CreateAsync(pid, exe);
                lock (_gate)
                {
                    _pending.Remove(pid);
                    if (_stopping) { src.Dispose(); return; }
                    foreach (var mo in _mixes.Values) src.EnsureRing(mo.Slot);
                    _procs[pid] = src;
                    RebuildSources();
                }
                Log.Write($"Capturing {exe}:{pid} -> channel {FindChannel(ChannelFor(exe))?.Name}");
            }
            catch (Exception ex)
            {
                lock (_gate) { _pending.Remove(pid); _failed.Add(pid); }
                Log.Write($"Capture of {exe}:{pid} failed: {ex.Message}");
            }
        });
    }

    string DisplayName(int pid, string exe)
    {
        if (_displayNames.TryGetValue(exe, out var name)) return name;
        name = Path.GetFileNameWithoutExtension(exe);
        try
        {
            using var p = Process.GetProcessById(pid);
            _imagePaths[exe] = p.MainModule?.FileName;
            var desc = p.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(desc)) name = desc.Trim();
        }
        catch { }
        if (name.Length > 0 && char.IsLower(name[0])) name = char.ToUpper(name[0]) + name[1..];
        return _displayNames[exe] = name;
    }

    void EnsureInputs(MMDeviceEnumerator en)
    {
        bool changed = false;
        foreach (var c in _cfg.Channels.Where(c => c.Kind == ChannelKind.Input))
        {
            if (_inputs.TryGetValue(c.Id, out var cur) && !cur.Faulted && cur.DeviceId == c.InputDeviceId) continue;
            if (cur != null)
            {
                _inputs.Remove(c.Id);
                cur.Dispose();
                changed = true;
            }
            if (c.InputDeviceId == null) continue;
            try
            {
                var dev = en.GetDevice(c.InputDeviceId);
                if (dev.State != DeviceState.Active) continue;
                var src = new DeviceSource(dev) { Chain = new InputChain(c.InputFx) };
                foreach (var mo in _mixes.Values) src.EnsureRing(mo.Slot);
                _inputs[c.Id] = src;
                changed = true;
                Log.Write($"Input {dev.FriendlyName} started");
            }
            catch (Exception ex)
            {
                Log.Write($"Input for channel {c.Name} failed: {ex.Message}");
            }
        }
        if (changed) RebuildSources();
    }

    void UpdateStatus(MMDeviceEnumerator en, MMDevice? collector)
    {
        StatusInfo status;
        if (collector == null)
        {
            status = new(StatusLevel.Error, $"Collector device “{_cfg.CollectorDevice}” not found", StatusFix.OpenSettings);
        }
        else
        {
            string? def = null;
            try { def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }
            List<string> missing, fallback;
            int apps;
            lock (_gate)
            {
                apps = _procs.Count;
                missing = _cfg.Mixes.Where(m => !_mixes[m.Id].Running).Select(m => m.Name).ToList();
                fallback = _cfg.Mixes.Where(m => _mixes[m.Id].Running && _mixes[m.Id].DeviceId != m.OutputDeviceId).Select(m => m.Name).ToList();
            }
            if (def != collector.ID)
                status = new(StatusLevel.Warning, "Apps are bypassing the mixer: another device is the Windows default output", StatusFix.ApplyDefault);
            else if (missing.Count > 0)
                status = new(StatusLevel.Warning, $"Output not connected: {string.Join(", ", missing)}");
            else if (fallback.Count > 0)
                status = new(StatusLevel.Ok, $"Running · on fallback device: {string.Join(", ", fallback)}");
            else
                status = new(StatusLevel.Ok, apps == 1 ? "Running · 1 app" : $"Running · {apps} apps");
        }
        if (status == Status) return;
        Status = status;
        StatusChanged?.Invoke(status);
    }

    #endregion

    #region Default device

    void ApplyDefault(MMDeviceEnumerator en, MMDevice collector)
    {
        _defaultApplied = true;
        if (!_cfg.ManageDefaultDevice) return;
        string? current = null;
        try { current = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }
        if (current == collector.ID) return;
        // Keep the first remembered device: after a crash the current default may be a stray one
        _cfg.RestoreDefaultId ??= current;
        SaveNow();
        try
        {
            DefaultDevice.Set(collector.ID);
            Log.Write($"Default device set to collector {collector.FriendlyName} (was {current})");
        }
        catch (Exception ex)
        {
            Log.Write($"Setting default device failed: {ex.Message}");
        }
    }

    void GuardDefault(MMDeviceEnumerator en, MMDevice collector)
    {
        if (!_cfg.ManageDefaultDevice || !_cfg.GuardDefaultDevice) return;
        bool ours = true;
        foreach (var role in new[] { Role.Multimedia, Role.Communications })
        {
            try { ours &= en.GetDefaultAudioEndpoint(DataFlow.Render, role).ID == collector.ID; }
            catch { ours = false; }
        }
        if (ours) return;
        try
        {
            DefaultDevice.Set(collector.ID);
            if (Environment.TickCount64 - _lastGuardLog > 60_000)
            {
                _lastGuardLog = Environment.TickCount64;
                Log.Write("Default device was changed by something else; took it back");
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Guarding default device failed: {ex.Message}");
        }
    }

    void RestoreDefault()
    {
        RestoreDefaultDevice(_cfg, CollectorId);
        SaveNow();
    }

    /// <summary>
    /// Puts back the default device that was active before MixDeck took over (also used by the crash watchdog).
    /// Clears <see cref="AppConfig.RestoreDefaultId"/>; the caller saves the config.
    /// </summary>
    public static void RestoreDefaultDevice(AppConfig cfg, string? collectorId)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            collectorId ??= FindCollectorId(en, cfg);
            string? current = null;
            try { current = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }
            // Only restore if we are still the default; the user may have picked something else meanwhile
            if (collectorId == null || current != collectorId) return;

            var candidates = new[] { cfg.RestoreDefaultId }.Concat(cfg.Mixes.Select(m => m.OutputDeviceId));
            foreach (var id in candidates.OfType<string>())
            {
                try
                {
                    using var dev = en.GetDevice(id);
                    if (dev.State != DeviceState.Active || id == collectorId) continue;
                    DefaultDevice.Set(id);
                    Log.Write($"Default device restored to {dev.FriendlyName}");
                    break;
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Restoring default device failed: {ex.Message}");
        }
        finally
        {
            cfg.RestoreDefaultId = null;
        }
    }

    static string? FindCollectorId(MMDeviceEnumerator en, AppConfig cfg)
    {
        var renders = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        var dev = renders.FirstOrDefault(d => d.ID == cfg.CollectorDevice)
            ?? renders.FirstOrDefault(d => d.FriendlyName.Contains(cfg.CollectorDevice, StringComparison.OrdinalIgnoreCase));
        var id = dev?.ID;
        foreach (var d in renders) d.Dispose();
        return id;
    }

    #endregion

    #region Config

    void ScheduleSave() => _saveTimer.Change(500, Timeout.Infinite);

    void SaveNow()
    {
        try
        {
            lock (_gate) ConfigStore.Save(_cfg);
        }
        catch (Exception ex)
        {
            Log.Write($"Saving config failed: {ex.Message}");
        }
    }

    static void Normalize(AppConfig cfg)
    {
        var systems = cfg.Channels.Where(c => c.Kind == ChannelKind.System).ToList();
        if (systems.Count == 0)
            cfg.Channels.Insert(0, new ChannelConfig { Name = "System", Kind = ChannelKind.System, Icon = "system" });
        foreach (var extra in systems.Skip(1)) extra.Kind = ChannelKind.Apps;
        if (cfg.Mixes.Count > AudioSource.MaxMixes) cfg.Mixes.RemoveRange(AudioSource.MaxMixes, cfg.Mixes.Count - AudioSource.MaxMixes);
        if (cfg.Mixes.Count == 0) cfg.Mixes.Add(new MixConfig { Name = "Headphones", Icon = "headphones" });
    }

    static readonly Regex VirtualDevice = new("Elgato Virtual|Steam Streaming|CABLE|VB-Audio|Voicemeeter|Oculus Virtual", RegexOptions.IgnoreCase);
    static readonly Regex HeadphoneDevice = new("headset|headphone|earphone|słuchawk", RegexOptions.IgnoreCase);

    static AppConfig CreateDefaultConfig()
    {
        var cfg = new AppConfig();
        try
        {
            using var en = new MMDeviceEnumerator();
            var real = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Where(d => !VirtualDevice.IsMatch(d.FriendlyName))
                .Select(d => new DeviceInfo(d.ID, d.FriendlyName))
                .ToList();
            var headphones = real.FirstOrDefault(d => HeadphoneDevice.IsMatch(d.Name)) ?? real.FirstOrDefault();
            var monitor = real.FirstOrDefault(d => d.Id != headphones?.Id);
            cfg.Mixes.Add(new MixConfig { Name = "Headphones", Icon = "headphones", OutputDeviceId = headphones?.Id });
            cfg.Mixes.Add(new MixConfig { Name = "Monitor", Icon = "speakers", OutputDeviceId = monitor?.Id });
        }
        catch (Exception ex)
        {
            Log.Write($"Device detection for default config failed: {ex.Message}");
        }

        cfg.Channels.Add(new ChannelConfig { Name = "System", Kind = ChannelKind.System, Icon = "system" });
        cfg.Channels.Add(new ChannelConfig
        {
            Name = "Browser", Icon = "browser",
            Apps = ["chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe", "zen.exe"],
        });
        cfg.Channels.Add(new ChannelConfig { Name = "Music", Icon = "music", Apps = ["spotify.exe"] });
        cfg.Channels.Add(new ChannelConfig { Name = "Chat", Icon = "chat", Apps = ["discord.exe", "ms-teams.exe", "teams.exe"] });
        Log.Write("Created default config");
        return cfg;
    }

    #endregion

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        if (_poller.IsAlive) _poller.Join(3000);

        List<IDisposable> all;
        lock (_gate)
        {
            all = [.. _mixes.Values, .. _procs.Values, .. _inputs.Values];
            _procs.Clear();
            _inputs.Clear();
        }
        foreach (var d in all)
        {
            try { d.Dispose(); } catch { }
        }
        RestoreDefault();
        _saveTimer.Dispose();
        SaveNow();
    }
}
