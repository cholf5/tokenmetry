using System;
using System.Collections.Generic;
using CCUsage.Core.Models;

namespace CCUsage.Core.Services;

/// <summary>
/// 单个数据源上次扫描的快照（S-002 数据源表的真实数据面）：
/// 状态与记录数来自该适配器当次 <c>ScanAsync</c> 返回的 <see cref="JsonlScanResult"/>
/// （<see cref="JsonlScanDiagnostics.Status"/> 与累计 <c>Records.Count</c>），
/// <see cref="ScannedAt"/> 为该源扫描完成的真实时刻（管道串行，逐源各自记录）。
/// </summary>
public sealed record IngestionSourceScanSnapshot(
    string Agent,
    JsonlScanStatus Status,
    int RecordCount,
    DateTimeOffset ScannedAt);

/// <summary>
/// 采集扫描状态注册表的<b>读取面</b>（S-002 设置页等 UI 消费）：已注册数据源清单（采集管道组成，
/// 即"已识别的数据源"行序）+ 各源上次扫描快照。应用启动后首轮管道完成前快照为 null——
/// UI 应如实显示"尚未扫描"，不得伪造状态。
/// </summary>
public interface IIngestionScanReader
{
    /// <summary>已注册数据源键（注册序 = 管道扫描序；键为各适配器声明的源名）。</summary>
    IReadOnlyList<string> RegisteredSources { get; }

    /// <summary>该源上次扫描快照；尚未扫描（本轮启动后管道未跑到）返回 null。</summary>
    IngestionSourceScanSnapshot? GetLastScan(string agent);
}

/// <summary>
/// 采集扫描状态注册表（S-002）：<see cref="CCUsage.Infrastructure.LocalIngestion.LocalIngestionDataSource"/>
/// 构造时注册其管道组合（适配器清单——"从采集层注册表来，别手写清单"），每轮管道运行逐源登记扫描快照。
/// 写入只发生在采集管道内（被管道信号量串行化），读取端（UI 线程）经锁取不可变快照；
/// 重复 <see cref="ReportScan"/> 幂等覆盖（最新扫描生效）。
/// </summary>
public sealed class IngestionScanRegistry : IIngestionScanReader
{
    private readonly object _gate = new();
    private readonly List<string> _order = new();
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IngestionSourceScanSnapshot> _scans = new(StringComparer.Ordinal);

    /// <summary>注册一个数据源（键 = 适配器声明的源名）；重复注册幂等且不改变注册序。</summary>
    public void RegisterSource(string agent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        lock (_gate)
        {
            if (_known.Add(agent))
            {
                _order.Add(agent);
            }
        }
    }

    /// <summary>登记该源一次扫描的快照（状态 / 累计记录数 / 完成时刻）；未注册的源自动补注册。</summary>
    public void ReportScan(string agent, JsonlScanStatus status, int recordCount, DateTimeOffset scannedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        lock (_gate)
        {
            if (_known.Add(agent))
            {
                _order.Add(agent);
            }

            _scans[agent] = new IngestionSourceScanSnapshot(agent, status, recordCount, scannedAt);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> RegisteredSources
    {
        get
        {
            lock (_gate)
            {
                return _order.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public IngestionSourceScanSnapshot? GetLastScan(string agent)
    {
        lock (_gate)
        {
            return _scans.GetValueOrDefault(agent);
        }
    }
}
