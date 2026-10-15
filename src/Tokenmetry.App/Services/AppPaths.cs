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
    }

    public string AppDataDirectory { get; }
    public string CacheDbPath { get; }
    public string SettingsFilePath { get; }

    public static AppPaths CreateDefault()
    {
        return new AppPaths(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Tokenmetry"));
    }
}
