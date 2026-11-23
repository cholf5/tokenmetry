using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;

namespace Tokenmetry.App.Update;

/// <summary>这次更新怎么落地。两种形态的差别只有一件事：谁来替文件。</summary>
public enum UpdateApplyMode
{
    /// <summary>Windows 已安装副本 → 交给 Inno 安装器静默升级（它懂文件锁重试与回滚）。</summary>
    Installer,

    /// <summary>便携版 / macOS / Linux → 应用自己做「同卷两次 rename」的原子替换，然后重启自己。</summary>
    PortableSwap,
}

/// <summary>
/// 一次「已下载 + 已解包 + 已验证载荷」的更新，随时可以落地。
/// <see cref="StagedPayloadPath"/> 与 <see cref="TargetPath"/> 必须同卷（同目录下的暂存区），
/// 否则替换退化成跨卷拷贝、失去原子性——<see cref="UpdateStager"/> 保证这一点。
/// </summary>
public sealed record PreparedUpdate(
    string Tag,
    UpdateApplyMode Mode,
    string AssetPath,
    string TargetPath,
    string BackupPath,
    string? StagedPayloadPath,
    string? StagingDirectory,
    // 便携 zip / Linux 的插件宿主与主程序版本耦合：PreparePortable 记录两个目录，
    // Apply 在同一轮 rename 里一起替换。macOS 的宿主在 .app 内，随整包替换，这两项为 null。
    string? StagedHostDirectory = null,
    string? TargetHostDirectory = null);

/// <summary>
/// 便携形态的自替换（P2）。三步、两次 rename：
/// <list type="number">
/// <item>把新版本解包到目标同目录的暂存区 <c>.tokenmetry-update-{tag}</c>（慢，但此时还没动旧文件）；</item>
/// <item>把正在跑的目标 rename 成 <c>.old</c>（元数据操作，Windows/Unix 都允许对运行中的镜像改名）；</item>
/// <item>把暂存区里的新载荷 rename 到目标路径（同卷，原子）。</item>
/// </list>
/// 任何一步失败都回滚第 2 步，让用户留在旧版本而不是半装状态。刻意不写接力脚本：
/// Windows 批处理的编码（中文用户名/路径）与「等进程退出」轮询、macOS 的 shebang/xattr，
/// 每个都是能静默炸掉升级的坑；而 rename 两连发把危险窗口压到微秒级，比脚本更短也更可测。
/// </summary>
public class UpdateStager
{
    /// <summary>暂存区目录名前缀（同目录下的隐藏目录；下次启动清理残留）。</summary>
    public const string StagingPrefix = ".tokenmetry-update-";

    /// <summary>便携 zip / Linux 包内与主程序并列的插件宿主目录名（版本与主程序耦合，随包一起换）。</summary>
    internal const string PluginHostDirectoryName = "plugin-host";

    private readonly string _os;
    private readonly int _processId;
    private readonly Func<PreparedUpdate, bool>? _relaunchOverride;

    /// <summary>os 注入 "Windows" / "OSX" / "Linux"（测试锁平台分支）；processId 只是给重启命令用。</summary>
    public UpdateStager(string? os = null, int? processId = null,
        Func<PreparedUpdate, bool>? relaunchOverride = null)
    {
        _os = os ?? RuntimeInformation.RuntimeIdentifier.Split('-')[0] switch
        {
            "win" => "Windows",
            "osx" => "OSX",
            _ => "Linux",
        };
        _processId = processId ?? Environment.ProcessId;
        _relaunchOverride = relaunchOverride;
    }

    /// <summary>
    /// 解包并定位新载荷。这一步在应用还活着时执行（只写暂存区，不碰目标）。
    /// 抛出的异常都是可展示的：目录不可写（装到只读位置）、包内布局不是预期的平台产物。
    /// </summary>
    public virtual PreparedUpdate PreparePortable(string tag, string archivePath, string processPath)
    {
        var target = ResolveTarget(processPath);
        var targetIsDirectory = Directory.Exists(target);
        var targetDirectory = Path.GetDirectoryName(target)
                              ?? throw new InvalidOperationException($"cannot locate directory of {target}");

        var staging = Path.Combine(targetDirectory, StagingPrefix + Sanitize(tag));
        CleanStaging(staging);
        Directory.CreateDirectory(staging);

        var extracted = Path.Combine(staging, "payload");
        ZipFile.ExtractToDirectory(archivePath, extracted, overwriteFiles: true);

        var payload = LocatePayload(extracted, targetIsDirectory);
        var backup = target + ".old";

        // 便携 zip / Linux 的包根是「可执行文件 + plugin-host/」。插件宿主与主程序版本耦合，
        // 记录暂存区与目标旁的两个目录，Apply 里一起换；macOS 的宿主在 .app 内，随整包替换。
        string? stagedHost = null;
        string? targetHost = null;
        if (!targetIsDirectory)
        {
            var candidate = Path.Combine(extracted, PluginHostDirectoryName);
            if (Directory.Exists(candidate))
            {
                stagedHost = candidate;
                targetHost = Path.Combine(targetDirectory, PluginHostDirectoryName);
            }
        }

        return new PreparedUpdate(tag, UpdateApplyMode.PortableSwap, archivePath, target, backup, payload, staging,
            stagedHost, targetHost);
    }

    /// <summary>Windows 已安装副本：不用解包，安装器自己会装。</summary>
    public virtual PreparedUpdate PrepareInstaller(string tag, string installerPath, string processPath) =>
        new(tag, UpdateApplyMode.Installer, installerPath, processPath,
            processPath + ".old", StagedPayloadPath: null, StagingDirectory: null);

    /// <summary>
    /// 落地：rename 旧目标 → rename 新载荷 → 回滚兜底。成功返回 true；
    /// 失败抛异常且保证目标路径仍是旧版本（调用方据此留在 ReadyToRestart 并展示原因）。
    /// </summary>
    public virtual void Apply(PreparedUpdate update)
    {
        if (update.Mode != UpdateApplyMode.PortableSwap || update.StagedPayloadPath is not { } payload)
        {
            throw new InvalidOperationException("Apply 只处理便携形态；已安装副本交给安装器");
        }

        var targetIsDirectory = Directory.Exists(update.TargetPath);
        var backupExists = Directory.Exists(update.BackupPath) || File.Exists(update.BackupPath);
        if (backupExists)
        {
            // 上一次更新残留的旧版本：删掉再让位，否则 rename 目标失败。
            Delete(update.BackupPath, Directory.Exists(update.BackupPath));
        }

        // 便携 zip / Linux：主程序与 plugin-host/ 版本耦合，必须同一轮换掉。
        // macOS 的目标是 .app，宿主在 bundle 内随整包替换，不走这条旁路。
        // stagedHost 只在 PreparePortable 确认包内有宿主目录时才有值；到这里若它消失了
        // （暂存区被清），不能静默只换 exe——让下面的 Move 失败并走整套回滚。
        var stagedHost = update.StagedHostDirectory;
        var targetHost = update.TargetHostDirectory;
        var swapHost = !targetIsDirectory && stagedHost is not null && targetHost is not null;
        var backupHost = swapHost ? targetHost + ".old" : null;
        if (swapHost && backupHost is not null
            && (Directory.Exists(backupHost) || File.Exists(backupHost)))
        {
            Delete(backupHost, Directory.Exists(backupHost));
        }

        var hostBackedUp = false;
        Move(update.TargetPath, update.BackupPath, targetIsDirectory);
        try
        {
            if (swapHost && targetHost is not null && Directory.Exists(targetHost))
            {
                Move(targetHost, backupHost!, isDirectory: true);
                hostBackedUp = true;
            }
        }
        catch
        {
            // 宿主让位失败：主程序已改名，先放回去，用户仍是完整旧版本。
            Move(update.BackupPath, update.TargetPath, targetIsDirectory);
            throw;
        }

        try
        {
            Move(payload, update.TargetPath, targetIsDirectory);
            if (swapHost && targetHost is not null)
            {
                Move(stagedHost!, targetHost, isDirectory: true);
            }
        }
        catch
        {
            // 任一步没就位 → 新主程序（若已放下）先清走，再把旧 exe + 旧宿主一起放回去，
            // 绝不留下「新 exe 配旧宿主 / 无宿主」的半装状态。
            var targetExists = targetIsDirectory
                ? Directory.Exists(update.TargetPath)
                : File.Exists(update.TargetPath);
            if (targetExists)
            {
                // 尽力删除半装的新版本：IOException（共享冲突）与 UnauthorizedAccessException
                // （杀软 / ACL）都不该让下面的旧版本恢复被跳过。
                try { Delete(update.TargetPath, targetIsDirectory); }
                catch (Exception) { }
            }

            // 恢复同样尽力而为：任何一步失败都不能替换/吞掉原始异常——用户至少要看到真正的原因。
            if (hostBackedUp && targetHost is not null && backupHost is not null)
            {
                try { Move(backupHost, targetHost, isDirectory: true); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Tokenmetry] 回滚恢复插件宿主失败（{targetHost}）：{ex.Message}");
                }
            }

            try { Move(update.BackupPath, update.TargetPath, targetIsDirectory); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Tokenmetry] 回滚恢复旧版本失败（{update.TargetPath}）：{ex.Message}");
            }

            throw;
        }

        if (!OperatingSystem.IsWindows())
        {
            MakeExecutable(update.TargetPath, targetIsDirectory);

            if (targetIsDirectory)
            {
                // macOS：插件宿主在 .app 内（Contents/MacOS/plugin-host/），随整包替换。
                // zip 解包同样不保证保留 Unix 可执行位，宿主丢了 +x 就起不来。
                var nestedHost = Path.Combine(
                    update.TargetPath, "Contents", "MacOS", PluginHostDirectoryName,
                    "Tokenmetry.PluginHost");
                if (File.Exists(nestedHost))
                {
                    EnsureUserExecute(nestedHost);
                }
            }

            // zip 解包不保证保留 Unix 可执行位；宿主是独立进程，丢了 +x 就起不来。
            if (swapHost && targetHost is not null)
            {
                var hostBinary = Path.Combine(targetHost, "Tokenmetry.PluginHost");
                if (File.Exists(hostBinary))
                {
                    EnsureUserExecute(hostBinary);
                }
            }
        }
    }

    /// <summary>
    /// 启动新版本并让调用方退出。新进程带 <c>--wait-for-exit &lt;pid&gt;</c>：它会先等本进程真的退出、
    /// 再抢单实例互斥量——否则新实例会在旧实例还活着时抢不到锁、发个激活请求就自杀，
    /// 用户看到的是「更新完应用不见了」。
    /// </summary>
    public virtual bool Relaunch(PreparedUpdate update)
    {
        if (_relaunchOverride is { } relaunch)
        {
            return relaunch(update);
        }

        if (update.Mode != UpdateApplyMode.PortableSwap)
        {
            return false;
        }

        try
        {
            var startInfo = _os == "OSX"
                // macOS 的「可执行文件」是 .app：open 让 LaunchServices 恢复 Dock 图标与激活语义
                ? new ProcessStartInfo("open", $"-n \"{update.TargetPath}\" --args --wait-for-exit {_processId}")
                : new ProcessStartInfo(update.TargetPath)
                {
                    Arguments = $"--wait-for-exit {_processId}",
                    WorkingDirectory = Path.GetDirectoryName(update.TargetPath) ?? Environment.CurrentDirectory,
                };
            startInfo.UseShellExecute = _os == "OSX";
            startInfo.CreateNoWindow = _os != "OSX";
            return Process.Start(startInfo) is not null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                       or IOException)
        {
            Console.Error.WriteLine($"[Tokenmetry] 启动新版本失败（{update.TargetPath}）：{ex.Message}");
            return false;
        }
    }

    /// <summary>清掉上次更新残留的暂存区（启动时调用；失败只打诊断，不影响启动）。</summary>
    public static void CleanStaleStaging(string processPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(ResolveTarget(processPath));
            if (directory is null || !Directory.Exists(directory))
            {
                return;
            }

            foreach (var stale in Directory.EnumerateDirectories(directory, StagingPrefix + "*"))
            {
                try
                {
                    Directory.Delete(stale, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 暂存区残留不影响使用（下次会被同名覆盖前清掉）。
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录不可读就当没有残留。
        }
    }

    /// <summary>
    /// macOS 上进程路径在 .app 内部（…/Tokenmetry.app/Contents/MacOS/Tokenmetry），
    /// 要替换的是整个 bundle；其余平台进程路径就是目标文件本身。
    /// </summary>
    internal static string ResolveTarget(string processPath)
    {
        if (!processPath.Contains(".app" + Path.DirectorySeparatorChar)
            && !processPath.Contains(".app/"))
        {
            return processPath;
        }

        var directory = new DirectoryInfo(Path.GetDirectoryName(processPath)!);
        while (directory is not null && !directory.Name.EndsWith(".app", StringComparison.Ordinal))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? processPath;
    }

    /// <summary>包内布局校验：macOS 必须恰好一个 .app 目录；Windows/Linux 必须恰好一个可执行文件
    /// （外加可选的 <c>plugin-host/</c> 目录）。不猜、不「尽量找到能跑的东西」——布局不符说明
    /// 资产挑错了（比如在 Linux 上下了 macOS 包）。</summary>
    internal static string LocatePayload(string extractedDirectory, bool targetIsDirectory)
    {
        if (targetIsDirectory)
        {
            var bundles = Directory.EnumerateDirectories(extractedDirectory)
                .Where(d => d.EndsWith(".app", StringComparison.Ordinal))
                .ToArray();
            if (bundles.Length != 1)
            {
                throw new InvalidDataException(
                    $"expected exactly one .app bundle in the archive, found {bundles.Length}");
            }

            return bundles[0];
        }

        var entries = Directory.EnumerateFileSystemEntries(extractedDirectory).ToArray();
        var files = entries.Where(File.Exists).ToArray();
        var directories = entries.Where(Directory.Exists).ToArray();

        // 便携 zip / Linux 的包根是「可执行文件 + plugin-host/」；宿主目录允许存在，
        // 但任何其它顶层条目都说明资产错了（旧版本更新包没有这个目录，也仍可接受）。
        var hostDirectoryOk = directories.Length == 0
            || (directories.Length == 1
                && string.Equals(Path.GetFileName(directories[0]), PluginHostDirectoryName,
                    StringComparison.Ordinal));
        if (files.Length != 1 || !hostDirectoryOk)
        {
            throw new InvalidDataException(
                $"expected exactly one executable file (plus optional {PluginHostDirectoryName}/) " +
                $"in the archive, found {entries.Length} entries");
        }

        EnsureExecutablePayload(files[0]);
        return files[0];
    }

    /// <summary>载荷魔数：Windows 便携包是 PE（MZ），Linux 是 ELF。下载器的魔数校验只管「是 zip」，
    /// 这一层管「zip 里装的是这个平台的可执行文件」。</summary>
    private static void EnsureExecutablePayload(string path)
    {
        Span<byte> magic = stackalloc byte[4];
        int read;
        using (var stream = File.OpenRead(path))
        {
            read = stream.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
        }

        var isPe = read >= 2 && magic[0] == 0x4D && magic[1] == 0x5A;
        var isElf = read >= 4 && magic[0] == 0x7F && magic[1] == 0x45 && magic[2] == 0x4C && magic[3] == 0x46;
        if (!isPe && !isElf)
        {
            throw new InvalidDataException("archive payload is not a Windows PE or Linux ELF executable");
        }
    }

    /// <summary>同卷 rename。virtual 只是给回滚路径的故障注入留缝（测试用），生产实现不变。</summary>
    protected virtual void Move(string source, string destination, bool isDirectory)
    {
        if (isDirectory)
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination, overwrite: false);
        }
    }

    /// <summary>删除文件/目录。virtual 只是给回滚路径的故障注入留缝（测试用）。</summary>
    protected virtual void Delete(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            Directory.Delete(path, recursive: true);
        }
        else
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 只补「解包后丢了可执行位」这一种情况。zip 里本来带着权限位（<c>zip -r</c> 会把 Unix mode 写进
    /// 外部属性），多此一举地重设权限会改动文件元数据——macOS 的 ad-hoc 代码签名封印把权限也算在内，
    /// 改了就可能被判 "damaged"，用户点开就是「应用已损坏」。
    /// </summary>
    private static void MakeExecutable(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        if (isDirectory)
        {
            var binary = Path.Combine(path, "Contents", "MacOS", "Tokenmetry");
            if (File.Exists(binary))
            {
                EnsureUserExecute(binary);
            }

            return;
        }

        EnsureUserExecute(path);
    }

    private static void EnsureUserExecute(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var mode = File.GetUnixFileMode(path);
        if ((mode & UnixFileMode.UserExecute) != 0)
        {
            return;
        }

        File.SetUnixFileMode(path, mode | UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    private static string Sanitize(string tag) =>
        new(tag.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());

    private static void CleanStaging(string staging)
    {
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"staging directory is not writable: {staging}", ex);
        }
    }
}
