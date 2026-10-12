using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CCUsage.App.Services;
using CCUsage.Core.Models;

namespace CCUsage.App.ViewModels;

/// <summary>外观主题选项（Value 对应 AppSettings.Theme 的取值）。</summary>
public sealed record ThemeOption(string Value, string Label);

public partial class SettingsViewModel : ViewModelBase
{
    public static IReadOnlyList<ThemeOption> ThemeOptions { get; } = new[]
    {
        new ThemeOption("light", "日间"),
        new ThemeOption("dark", "夜间"),
        new ThemeOption("system", "跟随系统"),
    };

    private readonly AppSettings _settings;
    private readonly IAppSettingsStore _store;
    private readonly IThemeService _themeService;

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

    [ObservableProperty]
    private ThemeOption _selectedTheme;

    public SettingsViewModel(AppSettings settings, AppPaths paths, IAppSettingsStore store, IThemeService themeService)
    {
        _settings = settings;
        _store = store;
        _themeService = themeService;
        CacheDbPath = paths.CacheDbPath;
        SettingsFilePath = paths.SettingsFilePath;
        _ccusageCommand = settings.CcusageCommand;
        _refreshMinutesText = settings.RefreshMinutes.ToString();
        _anomalyNotificationsEnabled = settings.AnomalyNotificationsEnabled;
        _showDebugInfo = settings.ShowDebugInfo;
        _selectedTheme = ResolveThemeOption(settings.Theme);
    }

    public string CacheDbPath { get; }
    public string SettingsFilePath { get; }

    /// <summary>切换选项时立即预览效果；持久化仍由保存设置触发。</summary>
    partial void OnSelectedThemeChanged(ThemeOption value)
    {
        if (value is not null)
        {
            _themeService.Apply(value.Value);
        }
    }

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
            _settings.Theme = SelectedTheme?.Value ?? "system";

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

    private static ThemeOption ResolveThemeOption(string? preference)
    {
        var normalized = (preference ?? "system").Trim().ToLowerInvariant();
        return ThemeOptions.FirstOrDefault(o => o.Value == normalized) ?? ThemeOptions[2];
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
