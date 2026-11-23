using Tokenmetry.Core.Models;
using Tokenmetry.Plugin.Abstractions;

namespace Tokenmetry.Infrastructure.Plugins;

/// <summary>
/// 单个已安装插件的采集源：把宿主 DTO（<see cref="PluginUsageRecord"/>）映射为统一的
/// <see cref="UsageRecord"/>，并持有 last-known-good 快照以覆盖"宿主无变化 / 宿主故障"两类轮次。
/// 会话 id 统一编码为 <c>plugin:&lt;pluginId&gt;:&lt;raw&gt;</c>（<see cref="PluginSessionId"/>），
/// 避免插件间及插件与内置源之间撞号。
/// </summary>
public sealed class PluginUsageSource
{
    private readonly PluginInstallation _installation;
    private readonly IPluginHostClient _hostClient;

    // 最近一次成功产出的快照（records + 诊断 + roots）。HasChanges=false 与宿主故障时复用它。
    private Snapshot? _lastKnownGood;

    public PluginUsageSource(PluginInstallation installation, IPluginHostClient hostClient)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(hostClient);
        _installation = installation;
        _hostClient = hostClient;
    }

    /// <summary>写进 facts 的 Agent 键（manifest.DisplayName）。</summary>
    public string Agent => _installation.Manifest.DisplayName;

    public async Task<JsonlScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        // 没有 last-known-good 时必须向宿主请求全量：否则首次 HasChanges=false 无法出数。
        var forceFull = Volatile.Read(ref _lastKnownGood) is null;

        PluginScanOutcome outcome;
        try
        {
            outcome = await _hostClient
                .ScanAsync(_installation, forceFull, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 超时 / 宿主动协议错误 / 意外异常：不向采集管道抛，用上一轮成功快照兜底。
            return BuildFailureResult();
        }

        if (!outcome.HasChanges)
        {
            // 无变化：复用上一轮快照；首次扫描就无变化说明宿主没按全量跑，按错误收口。
            return TryReuseSnapshot() ?? EmptyResult(JsonlScanStatus.Error);
        }

        // 宿主自报 error（全部记录不合规等）不是可用的快照，不覆盖 last-known-good。
        if (outcome.Diagnostics.Status == PluginScanStatus.Error)
        {
            return BuildFailureResult();
        }

        try
        {
            var mapped = MapRecords(outcome.Records);
            return StoreAndBuild(outcome.Diagnostics, mapped);
        }
        catch (Exception)
        {
            // 映射/落快照阶段不应抛（含宿主流里混入 null 记录等畸形输入）：
            // 兜底走故障路径，绝不向采集管道泄漏异常。
            return BuildFailureResult();
        }
    }

    private JsonlScanResult StoreAndBuild(PluginDiagnostics host, MappedRecords mapped)
    {
        var invalid = host.InvalidRecords + mapped.InvalidRecords;
        var records = mapped.Records;

        var diagnostics = new JsonlScanDiagnostics
        {
            Status = MapStatus(host.Status),
            FilesDiscovered = host.FilesDiscovered,
            FilesRead = host.FilesRead,
            FilesChanged = 1,
            // LinesSkipped 是"不含 usage 标记而被跳过"的行数，宿主不透传该计数，置 0；
            // 被校验拒绝的记录只计 InvalidRecords，两者不混用。
            LinesSkipped = 0,
            MalformedLines = 0,
            InvalidRecords = invalid,
            DuplicateRecords = host.DuplicateRecords + mapped.DuplicateRecords,
            MissingCostRecords = records.Count(static record => record.CostUsd is null),
            UnknownModelRecords = records.Count(static record => string.IsNullOrEmpty(record.Model)),
        };

        var roots = new[] { _installation.Directory };

        // 缓存诊断的副本：调用方拿到的对象与 last-known-good 模板解耦，即使被改写也不污染缓存。
        Volatile.Write(ref _lastKnownGood, new Snapshot(records, roots, Clone(diagnostics)));

        return new JsonlScanResult
        {
            Roots = roots,
            Records = records,
            Diagnostics = diagnostics,
        };
    }

    private JsonlScanResult? TryReuseSnapshot()
    {
        var snapshot = Volatile.Read(ref _lastKnownGood);
        return snapshot is null ? null : BuildFromSnapshot(snapshot, snapshot.Diagnostics.Status);
    }

    private JsonlScanResult BuildFailureResult()
    {
        var snapshot = Volatile.Read(ref _lastKnownGood);
        return snapshot is null
            ? EmptyResult(JsonlScanStatus.Error)
            : BuildFromSnapshot(snapshot, JsonlScanStatus.Error);
    }

    /// <summary>用缓存快照出数：记录原样复用，FilesChanged / FilesRead 归零（本轮未读文件）。</summary>
    private static JsonlScanResult BuildFromSnapshot(Snapshot snapshot, JsonlScanStatus status)
    {
        var diagnostics = Clone(snapshot.Diagnostics);
        diagnostics.Status = status;
        diagnostics.FilesRead = 0;
        diagnostics.FilesChanged = 0;

        return new JsonlScanResult
        {
            Roots = snapshot.Roots,
            Records = snapshot.Records,
            Diagnostics = diagnostics,
        };
    }

    private static JsonlScanDiagnostics Clone(JsonlScanDiagnostics source) => new()
    {
        Status = source.Status,
        FilesDiscovered = source.FilesDiscovered,
        FilesRead = source.FilesRead,
        FilesChanged = source.FilesChanged,
        LinesSkipped = source.LinesSkipped,
        MalformedLines = source.MalformedLines,
        InvalidRecords = source.InvalidRecords,
        DuplicateRecords = source.DuplicateRecords,
        MissingCostRecords = source.MissingCostRecords,
        UnknownModelRecords = source.UnknownModelRecords,
    };

    private static JsonlScanResult EmptyResult(JsonlScanStatus status) => new()
    {
        Roots = [],
        Records = [],
        Diagnostics = new JsonlScanDiagnostics { Status = status, FilesChanged = 0 },
    };

    private MappedRecords MapRecords(IReadOnlyList<PluginUsageRecord> dtos)
    {
        var pluginId = _installation.Manifest.Id;
        var agent = _installation.Manifest.DisplayName;
        var records = new List<UsageRecord>(dtos.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var invalid = 0;
        var duplicates = 0;

        foreach (var dto in dtos)
        {
            // 宿主流由 source-gen JSON 反序列化，不保证元素非 null；null 记录视为不合规丢弃。
            if (dto is null)
            {
                invalid++;
                continue;
            }

            if (PluginRecordValidator.Validate(dto).Count > 0)
            {
                invalid++;
                continue;
            }

            if (!seen.Add(dto.RecordId))
            {
                duplicates++;
                continue;
            }

            records.Add(new UsageRecord
            {
                Timestamp = dto.Timestamp,
                Agent = agent,
                SessionId = PluginSessionId.Encode(pluginId, dto.SessionId),
                ProjectPath = dto.ProjectPath,
                ProjectDisplayName = dto.ProjectDisplayName,
                Model = dto.Model,
                Provider = dto.Provider ?? string.Empty,
                InputTokens = dto.InputTokens,
                OutputTokens = dto.OutputTokens,
                CacheCreationTokens = dto.CacheCreationTokens,
                CacheCreationEphemeral5mTokens = dto.CacheCreationEphemeral5mTokens,
                CacheCreationEphemeral1hTokens = dto.CacheCreationEphemeral1hTokens,
                CacheReadTokens = dto.CacheReadTokens,
                CostUsd = dto.CostUsd,
                Speed = MapSpeed(dto.Speed),
            });
        }

        return new MappedRecords(records, invalid, duplicates);
    }

    private static JsonlScanStatus MapStatus(PluginScanStatus status) => status switch
    {
        PluginScanStatus.Ok => JsonlScanStatus.Ok,
        PluginScanStatus.NoData => JsonlScanStatus.NoData,
        PluginScanStatus.Partial => JsonlScanStatus.Partial,
        PluginScanStatus.Error => JsonlScanStatus.Error,
        _ => JsonlScanStatus.Error,
    };

    private static SpeedTier MapSpeed(PluginSpeedTier speed) => speed switch
    {
        PluginSpeedTier.Standard => SpeedTier.Standard,
        PluginSpeedTier.Fast => SpeedTier.Fast,
        PluginSpeedTier.Flex => SpeedTier.Flex,
        PluginSpeedTier.Ultrafast => SpeedTier.Ultrafast,
        _ => SpeedTier.Standard,
    };

    private sealed record Snapshot(
        IReadOnlyList<UsageRecord> Records,
        IReadOnlyList<string> Roots,
        JsonlScanDiagnostics Diagnostics);

    private readonly record struct MappedRecords(
        IReadOnlyList<UsageRecord> Records,
        int InvalidRecords,
        int DuplicateRecords);
}
