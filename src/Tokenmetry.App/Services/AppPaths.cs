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
    }

    public string AppDataDirectory { get; }
    public string CacheDbPath { get; }
    public string SettingsFilePath { get; }

    /// <summary>采集管线文件日志目录（I-045）：与缓存库/设置文件同根（%LOCALAPPDATA%\Tokenmetry\logs），
    /// 跟随现有数据目录约定（卡内开放问题定案：便携 zip 形态不另设安装目录旁路径）。</summary>
    public string LogDirectory { get; }

    public static AppPaths CreateDefault()
    {
        return new AppPaths(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Tokenmetry"));
    }
}
