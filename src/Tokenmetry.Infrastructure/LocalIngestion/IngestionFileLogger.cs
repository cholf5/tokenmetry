using System.Text;

namespace Tokenmetry.Infrastructure.LocalIngestion;

/// <summary>
/// 采集管线文件日志（I-045）：轻量手写实现，不引入第三方日志框架。
/// <para>
/// <b>隐私红线（卡内定案）</b>：本 logger 只承载调用方给定的结构化事实——路径、计数、耗时、
/// 异常类型与堆栈。绝不记录 prompt / 代码 / 会话内容；调用方（采集管道埋点）不得传内容进 <see cref="WriteLine"/>。
/// </para>
/// <para>
/// 线程模型：采集管道串行（LocalIngestionDataSource 信号量），写入端几乎单线程；仍以锁保证
/// 行级原子（多埋点并发 / 未来调用面扩张时不交错）。写入失败（磁盘满 / 权限）静默降级为
/// <see cref="Console.Error"/>——日志是诊断通道，自身故障不得反噬采集。
/// </para>
/// <para>
/// 生命周期：App DI 单例（App 启动创建、进程退出随容器丢弃；<see cref="Dispose"/> 幂等兜底
/// 测试直用场景）。开关读取自 AppSettings.FileLoggingEnabled（默认开）；关闭时零文件零 IO。
/// </para>
/// </summary>
public sealed class IngestionFileLogger : IDisposable
{
    /// <summary>单文件大小上限（字节）：超过即轮转。</summary>
    private const long DefaultMaxFileBytes = 5 * 1024 * 1024;

    /// <summary>轮转保留份数：app.log.1（最新）… app.log.N（最旧），超出删除。</summary>
    private const int DefaultMaxArchives = 3;

    /// <summary>日志文件名（设置页「打开日志文件夹」与文档口径一致）。</summary>
    public const string LogFileName = "app.log";

    private readonly object _gate = new();
    private readonly long _maxFileBytes;
    private readonly int _maxArchives;
    private StreamWriter? _writer;
    private bool _disposed;

    // 关闭态为 null：不落目录不落文件（headless 测试宿主与 CI 零写盘——AC 硬性要求）
    private readonly string? _filePath;

    public IngestionFileLogger(
        string directory,
        bool enabled,
        long maxFileBytes = DefaultMaxFileBytes,
        int maxArchives = DefaultMaxArchives)
    {
        _maxFileBytes = maxFileBytes;
        _maxArchives = maxArchives;
        _filePath = enabled ? TryCreateDirectory(directory) : null;
    }

    /// <summary>当前日志文件全路径；开关关闭（或目录创建失败）为 null。</summary>
    public string? CurrentFilePath => _filePath;

    /// <summary>追加一行（UTC 时间戳前缀 + 载荷）。载荷内不应含换行（异常经重排为单行+缩进块）。</summary>
    public void WriteLine(string message, Exception? exception = null)
    {
        if (_filePath is null || _disposed)
        {
            return;
        }

        var line = string.Concat(
            DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            " ",
            message);

        if (exception is not null)
        {
            line += Environment.NewLine
                + exception.ToString().Replace(Environment.NewLine, Environment.NewLine + "  ", StringComparison.Ordinal);
        }

        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                RotateIfNeededUnlocked();
                WriteUnlocked(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[Tokenmetry] 采集日志写入失败（诊断通道降级 stderr）：{ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static string? TryCreateDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, LogFileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"[Tokenmetry] 采集日志目录创建失败（文件日志停用）：{directory}：{ex.Message}");
            return null;
        }
    }

    private void RotateIfNeededUnlocked()
    {
        var writer = _writer;
        if (writer is null)
        {
            OpenWriterUnlocked();
            return;
        }

        if (writer.BaseStream.Length < _maxFileBytes)
        {
            return;
        }

        writer.Flush();
        writer.Dispose();
        _writer = null;

        // 顺移归档：app.log.2 → .3、.1 → .2、app.log → .1；最旧超出份数即删除。
        // 从最旧往最新删/改名，避免目标被占。
        var oldest = $"{_filePath}.{_maxArchives}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var i = _maxArchives - 1; i >= 1; i--)
        {
            var from = $"{_filePath}.{i}";
            if (File.Exists(from))
            {
                File.Move(from, $"{_filePath}.{i + 1}");
            }
        }

        File.Move(_filePath!, $"{_filePath}.1");
        OpenWriterUnlocked();
    }

    private void OpenWriterUnlocked()
    {
        _writer = new StreamWriter(
            new FileStream(_filePath!, FileMode.Append, FileAccess.Write, FileShare.Read),
            Encoding.UTF8)
        {
            AutoFlush = true,
        };
    }

    private void WriteUnlocked(string line)
    {
        if (_writer is null)
        {
            OpenWriterUnlocked();
        }

        _writer!.WriteLine(line);
    }
}
