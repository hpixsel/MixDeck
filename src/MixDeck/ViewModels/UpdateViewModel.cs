using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MixDeck.ViewModels;

public partial class MainViewModel
{
    readonly Updater _updater = new();

    /// <summary>Set by the app: clean shutdown right before the updater restarts the process.</summary>
    public Action? BeforeUpdateRestart { get; set; }

    public string AppVersion => Updater.CurrentVersion;

    [ObservableProperty] string updateSource = "";
    [ObservableProperty] string updateStatus = "";
    [ObservableProperty] bool updateAvailable;
    [ObservableProperty] bool updateBusy;
    bool _loadingUpdates;

    async void InitUpdates()
    {
        _loadingUpdates = true;
        UpdateSource = Service.Config.UpdateSource ?? "";
        _loadingUpdates = false;
        if (string.IsNullOrWhiteSpace(UpdateSource)) return;
        await Task.Delay(TimeSpan.FromSeconds(15)); // don't compete with start-up
        await CheckUpdates();
    }

    partial void OnUpdateSourceChanged(string value)
    {
        if (_loadingUpdates) return;
        Service.Config.UpdateSource = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        Service.Save();
        UpdateAvailable = false;
        UpdateStatus = "";
    }

    [RelayCommand]
    async Task CheckUpdates()
    {
        UpdateBusy = true;
        UpdateStatus = "Checking…";
        UpdateStatus = await _updater.CheckAsync(UpdateSource);
        UpdateAvailable = _updater.AvailableVersion != null;
        UpdateBusy = false;
    }

    [RelayCommand]
    async Task InstallUpdate()
    {
        UpdateBusy = true;
        UpdateStatus = $"Downloading version {_updater.AvailableVersion}…";
        try
        {
            await _updater.InstallAsync(UpdateSource, () => BeforeUpdateRestart?.Invoke());
        }
        catch (Exception ex)
        {
            Log.Write($"Update failed: {ex}");
            UpdateStatus = "Installing the update failed. See the event log for details.";
            UpdateBusy = false;
        }
    }
}
