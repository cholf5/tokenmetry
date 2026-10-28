using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace Tokenmetry.App.Services;

/// <summary>
/// 跨平台单实例守卫（B-013）：命名 Mutex 独占（.NET 10 三平台原生支持命名同步基元，
/// 不需要 Linux 文件锁分支）+ 命名管道激活通道。第二实例启动时拿不到 Mutex → 经管道给
/// 首实例发激活请求后静默退出——托盘常驻应用双开的最常见诉求是"把界面叫出来"，故取
/// "激活既有窗口"而非纯静默（卡内 UX 定案）。S-006 开机自启动 + 手动双开由此不再产生
/// 双托盘与 SQLite 并发写。
/// headless 测试宿主（TOKENMETRY_HEADLESS=1）在 Program.Main 整体旁路（多测试进程并行）。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public const string DefaultInstanceName = "Tokenmetry";

    /// <summary>管道行协议：一行请求（以后扩展多一行一个动作），服务器读到请求即回执一行
    /// "ok"。回执是送达凭证：Unix FIFO 在服务器换实例的间隙（旧实例已停止读取、新实例未就绪）
    /// 客户端 Connect 仍可能成功而写入被静默吞掉，客户端必须凭回执判定成败，吞掉的写入才会
    /// 以无回执败露并退避重试到新实例上。</summary>
    public const string ActivationRequest = "show";

    private static readonly byte[] ActivationAckBytes = "ok\n"u8.ToArray();

    private readonly string _mutexName;
    private readonly string _pipeName;
    private Mutex? _mutex;
    private CancellationTokenSource? _listenerCancellation;

    public SingleInstanceGuard(string instanceName)
    {
        // Windows 用 Local\ 前缀限定会话作用域（fast user switching 下各会话独立数据目录互不
        // 干扰；同用户跨会话共享同一 LOCALAPPDATA 的双开是已知边界）。Unix 命名基元名字不能带
        // 反斜杠，用裸名（.NET 按用户隔离命名对象）。
        _mutexName = OperatingSystem.IsWindows()
            ? $@"Local\{instanceName}.SingleInstance"
            : $"{instanceName}.SingleInstance";
        _pipeName = $"{instanceName}.Activation";
    }

    /// <summary>
    /// 尝试成为首实例。false = 已有实例在运行（调用方应发激活请求后退出）。
    /// 上一实例崩溃遗留的 abandoned mutex 视为已获得——WaitOne 抛 AbandonedMutexException 时
    /// 所有权已转移给本线程。
    /// </summary>
    public bool TryAcquire()
    {
        _mutex = new Mutex(false, _mutexName);
        try
        {
            return _mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    /// <summary>
    /// 首实例监听激活请求，读到请求先回执再回调（管道线程上下文，调用方自行 marshal 到 UI
    /// 线程）。循环直到 Dispose；单次连接的客户端异常（秒断、写坏）只作废该次请求，不终止监听。
    /// </summary>
    public void StartListener(Action onActivationRequest)
    {
        if (_mutex is null)
        {
            throw new InvalidOperationException("StartListener 必须在 TryAcquire 成功之后调用");
        }

        _listenerCancellation = new CancellationTokenSource();
        var token = _listenerCancellation.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var reader = new StreamReader(server);
                    var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    if (line == ActivationRequest)
                    {
                        // 先回执再回调：客户端凭回执判定送达（见管道行协议注释）。
                        await server.WriteAsync(ActivationAckBytes, token).ConfigureAwait(false);
                        onActivationRequest();
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                    break;
                }
                catch (IOException)
                {
                    // 客户端连接后秒断 / 管道读写失败：丢弃本次请求，重建监听继续。
                }
                finally
                {
                    server?.Dispose();
                }
            }
        });
    }

    /// <summary>
    /// 第二实例侧：向首实例发激活请求并等回执。带重试——首实例从拿锁到开管道之间有启动
    /// 窗口期，立即双开也要能送达；连上将死实例被吞的写入以无回执败露，同样退避重试。
    /// true = 首实例确认收到（凭回执，不只是"写进了管道"）。全部失败返回 false（调用方
    /// 仍应退出：互斥保证了不该自己上位）。
    /// </summary>
    public static bool TrySignalActivation(
        string instanceName,
        int attempts = 3,
        int connectTimeoutMs = 1500)
    {
        var pipeName = $"{instanceName}.Activation";
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                client.Connect(connectTimeoutMs);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(ActivationRequest);
                if (ReadActivationAck(client, connectTimeoutMs))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException
                or UnauthorizedAccessException or OperationCanceledException)
            {
                // 首实例监听未就绪 / 管道忙（服务器实例数 1 且被占）/ 名字被异物占用 /
                // 回执超时（连上了将死实例、写入被吞）：退避重试，落到新实例上。
            }

            Thread.Sleep(200);
        }

        return false;
    }

    /// <summary>
    /// 读服务器送达回执（阻塞，超时预算与连接相同）：服务器读到请求即回执，正常亚毫秒；
    /// EOF（连上了将死的旧实例）、半截回执、超时都返回 false 交由重试——真回执只会来自
    /// 仍活着并真正读到请求的服务器实例。
    /// </summary>
    private static bool ReadActivationAck(NamedPipeClientStream client, int connectTimeoutMs)
    {
        var buffer = new byte[ActivationAckBytes.Length];
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(connectTimeoutMs));
        var read = 0;
        while (read < buffer.Length)
        {
            var n = client.ReadAsync(buffer.AsMemory(read), timeout.Token).GetAwaiter().GetResult();
            if (n <= 0)
            {
                return false;
            }

            read += n;
        }

        return buffer.AsSpan().SequenceEqual(ActivationAckBytes);
    }

    public void Dispose()
    {
        _listenerCancellation?.Cancel();
        _listenerCancellation?.Dispose();
        _listenerCancellation = null;
        _mutex?.Dispose();
        _mutex = null;
    }
}
