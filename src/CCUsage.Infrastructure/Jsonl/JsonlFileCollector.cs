namespace CCUsage.Infrastructure.Jsonl;

/// <summary>
/// 目录树下的 .jsonl 文件递归收集（claude.rs usage_files 与 codex.rs collect_jsonl_files 共用）：
/// 读不了的目录静默跳过，符号链接（reparse point）不跟随，按目录枚举序返回（排序由调用方负责）。
/// </summary>
public static class JsonlFileCollector
{
    public static void Collect(string directory, List<string> files)
    {
        var dir = new DirectoryInfo(directory);
        if (!dir.Exists)
        {
            return;
        }

        Collect(dir, files);
    }

    public static void Collect(DirectoryInfo directory, List<string> files)
    {
        FileSystemInfo[] entries;
        try
        {
            entries = directory.EnumerateFileSystemInfos().ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 与蓝本一致：读不了的目录静默跳过。
            return;
        }

        foreach (var entry in entries)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // 与蓝本一致：符号链接不跟随。
                continue;
            }

            if (entry is DirectoryInfo subdirectory)
            {
                Collect(subdirectory, files);
            }
            else if (entry is FileInfo file && string.Equals(file.Extension, ".jsonl", StringComparison.Ordinal))
            {
                files.Add(file.FullName);
            }
        }
    }
}
