using System;
using System.Collections.Generic;
using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Services;

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
/// I-046 源内进行中快照：扫描器在源内部按节流粒度（N 文件/N 秒）上报的中间态，
/// 供设置页数据源表在扫描期间显示「正在处理 3200/16403」——用户能区分「在跑」与「卡了」。
/// 仅 JSONL 增量扫描器（Claude/Codex 等共用引擎）上报；完成登记（<see cref="ReportScan"/>）
/// 覆盖掉进行中态，一轮结束后不残留中间快照。
/// </summary>
public sealed record IngestionScanProgress(
    string Agent,
    int FilesProcessed,
    int FilesDiscovered,
    DateTimeOffset StartedAt);

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

    /// <summary>该源正在扫描的进行中快照（I-046）；源不在扫描中返回 null。</summary>
    IngestionScanProgress? GetScanProgress(string agent);
}

/// <summary>
/// 采集扫描状态注册表（S-002）：<see cref="Tokenmetry.Infrastructure.LocalIngestion.LocalIngestionDataSource"/>
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
    private readonly Dictionary<string, IngestionScanProgress> _progress = new(StringComparer.Ordinal);

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

    /// <summary>
    /// 标记该源开始扫描（I-046）：清除上次结果快照进入"进行中"态（设置页不显示上一轮的旧结果
    /// 顶替本轮），后续 <see cref="ReportScanProgress"/> 逐节流上报中间进度。
    /// </summary>
    public void BeginScan(string agent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        lock (_gate)
        {
            if (_known.Add(agent))
            {
                _order.Add(agent);
            }

            _scans.Remove(agent);
            _progress[agent] = new IngestionScanProgress(agent, FilesProcessed: 0, FilesDiscovered: 0, DateTimeOffset.Now);
        }
    }

    /// <summary>上报源内进行中进度（I-046，节流后调用）；未 <see cref="BeginScan"/> 的源忽略（防御乱序）。</summary>
    public void ReportScanProgress(string agent, int filesProcessed, int filesDiscovered)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        lock (_gate)
        {
            // BeginScan 之前的进度回调（理论上不可能——回调只在扫描内发生）不凭空创建进行中态
            if (_progress.ContainsKey(agent))
            {
                _progress[agent] = new IngestionScanProgress(agent, filesProcessed, filesDiscovered, _progress[agent].StartedAt);
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
            // 完成登记覆盖进行中态（一轮结束不残留中间快照——进行中只在源扫描期间可见）
            _progress.Remove(agent);
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

    /// <inheritdoc />
    public IngestionScanProgress? GetScanProgress(string agent)
    {
        lock (_gate)
        {
            return _progress.GetValueOrDefault(agent);
        }
    }
}
