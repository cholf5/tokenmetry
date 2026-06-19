using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CCUsage.App.Services;
using CCUsage.Core.Models;

namespace CCUsage.App.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly IAppSettingsStore _store;

    [ObservableProperty]
    private string _ccusageCommand = string.Empty;

    [ObservableProperty]
    private string _refreshMinutesText = string.Empty;

    [ObservableProperty]
    private bool _anomalyNotificationsEnabled;

    [ObservableProperty]
    private bool _showDebugInfo;

    [ObservableProperty]
    private string _status = "ready";

    [ObservableProperty]
    private bool _isSaving;

    public SettingsViewModel(AppSettings settings, AppPaths paths, IAppSettingsStore store)
    {
        _settings = settings;
        _store = store;
        CacheDbPath = paths.CacheDbPath;
        SettingsFilePath = paths.SettingsFilePath;
        _ccusageCommand = settings.CcusageCommand;
        _refreshMinutesText = settings.RefreshMinutes.ToString();
        _anomalyNotificationsEnabled = settings.AnomalyNotificationsEnabled;
        _showDebugInfo = settings.ShowDebugInfo;
    }

    public string CacheDbPath { get; }
    public string SettingsFilePath { get; }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (!TryValidateRefreshMinutes(out var refreshMinutes, out var error))
        {
            Status = error;
            return;
        }

        IsSaving = true;
        Status = "saving";

        try
        {
            _settings.CcusageCommand = string.IsNullOrWhiteSpace(CcusageCommand)
                ? "ccusage"
                : CcusageCommand.Trim();
            _settings.RefreshMinutes = refreshMinutes;
            _settings.AnomalyNotificationsEnabled = AnomalyNotificationsEnabled;
            _settings.ShowDebugInfo = ShowDebugInfo;

            await _store.SaveAsync(_settings);
            CcusageCommand = _settings.CcusageCommand;
            RefreshMinutesText = _settings.RefreshMinutes.ToString();
            Status = "settings saved";
        }
        catch (Exception ex)
        {
            Status = $"error: {ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    private bool TryValidateRefreshMinutes(out int refreshMinutes, out string error)
    {
        refreshMinutes = 0;
        error = string.Empty;

        if (!int.TryParse(RefreshMinutesText, out var parsed))
        {
            error = "refresh interval must be a number";
            return false;
        }

        if (parsed < 1 || parsed > 1440)
        {
            error = "refresh interval must be between 1 and 1440 minutes";
            return false;
        }

        refreshMinutes = parsed;
        return true;
    }
}
