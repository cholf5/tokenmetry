namespace CCUsage.Infrastructure.JsonSource;

/// <summary>
/// 目录树下按扩展名递归收集文件（蓝本 adapters/common/src/lib.rs collect_files_with_extension 的移植，
/// I-010 Gemini / I-014 Amp / I-015 Droid 共用；JsonlFileCollector 是其 ".jsonl" 特化）：
/// - 扩展名精确匹配（区分大小写，蓝本 path.extension() == Some(ext) 同口径，".JSON" 不命中）；
/// - 读不了的目录静默跳过，符号链接（reparse point）不跟随（蓝本 file_type().is_file() 对符号链接
///   为 false 同口径）；
/// - 按目录枚举序返回（排序由调用方负责——蓝本 paths.rs 在收集后统一 sort）。
/// </summary>
public static class SourceFileCollector
{
    public static void Collect(string directory, string extension, List<string> files)
    {
        var suffix = extension.StartsWith(".", StringComparison.Ordinal) ? extension : $".{extension}";
        var dir = new DirectoryInfo(directory);
        if (!dir.Exists)
        {
            return;
        }

        Collect(dir, suffix, files);
    }

    /// <summary>文件修改时间（蓝本 file_modified_timestamp / settings_timestamp 的 mtime 回退）：
    /// 不可得（文件不存在 / IO 错误）返回 null，调用方按蓝本回退 UNIX 纪元。</summary>
    public static DateTimeOffset? TryGetModifiedTimestamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            return new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Collect(DirectoryInfo directory, string suffix, List<string> files)
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
                Collect(subdirectory, suffix, files);
            }
            else if (entry is FileInfo file && string.Equals(file.Extension, suffix, StringComparison.Ordinal))
            {
                files.Add(file.FullName);
            }
        }
    }
}
