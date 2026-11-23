using System;
using System.IO;

namespace Tokenmetry.App.Services;

public sealed class AppPaths
{
    public AppPaths(string appDataDirectory)
    {
        AppDataDirectory = appDataDirectory;
        CacheDbPath = Path.Combine(appDataDirectory, "tokenmetry.db");
        SettingsFilePath = Path.Combine(appDataDirectory, "settings.json");
        LogDirectory = Path.Combine(appDataDirectory, "logs");
        PluginsDirectory = Path.Combine(appDataDirectory, "plugins");
        PluginDataDirectory = Path.Combine(appDataDirectory, "plugin-data");
        PluginRegistryFilePath = Path.Combine(appDataDirectory, "plugins.json");
    }

    public string AppDataDirectory { get; }
    public string CacheDbPath { get; }
    public string SettingsFilePath { get; }

    /// <summary>采集管线文件日志目录（I-045）：与缓存库/设置文件同根（%LOCALAPPDATA%\Tokenmetry\logs），
    /// 跟随现有数据目录约定（卡内开放问题定案：便携 zip 形态不另设安装目录旁路径）。</summary>
    public string LogDirectory { get; }

    /// <summary>已安装插件根目录：plugins/&lt;id&gt;/&lt;version&gt;/（Task 4 安装器写入）。</summary>
    public string PluginsDirectory { get; }

    /// <summary>插件解析缓存根目录：plugin-data/&lt;id&gt;/（与安装目录分离，升级/卸载不误删）。</summary>
    public string PluginDataDirectory { get; }

    /// <summary>插件启用状态注册表（plugins.json），是启用状态的唯一来源。</summary>
    public string PluginRegistryFilePath { get; }

    public static AppPaths CreateDefault()
    {
        return new AppPaths(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Tokenmetry"));
    }
}
