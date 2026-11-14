using System.Text.Json;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.SqliteSource;
using Microsoft.Data.Sqlite;

namespace Tokenmetry.Infrastructure.OpenCodeSqlite;

/// <summary>
/// OpenCode 本地 SQLite 数据直读适配器（I-009，移植蓝本 ../ccusage/rust/adapters/opencode/src/loader.rs
/// 的 SQLite 主线；蓝本许可：MIT License，Copyright (c) 2025 ryoppippi（../ccusage/LICENSE））：
/// 目录定位（<see cref="OpenCodeDataLocator"/>）→ 每目录选一个库（opencode.db 优先，否则按名排序取第一个
/// opencode-*.db 频道库）→ 逐库两级探针（I-049，与 <see cref="Tokenmetry.Infrastructure.ZCodeSqlite.ZCodeSqliteAdapter"/>
/// 的两级探针同构，重复实现以注释互相指向、不抽公共件）：
/// ① 文件指纹快路径——主库 + `-wal` 的 (size, mtime) 与"干净指纹"一致即账面必然未动（WAL 追加写必动
/// `-wal`，检查点/改值必动库文件），探针值直接复用记忆、连 SQL 都不发（空闲轮 IO 降为两次 stat）；
/// ② SQL 探针——v2 `session_message` 与（存在时的）v1 `message` 各一条
/// <c>SELECT COUNT(*), COALESCE(MAX(rowid), 0)</c>，两值拼进探针元组；缺失表按 0/0 参与元组
/// （本机库是 v2 形态，v1 表不存在是常态，表不存在不是探针失败）。选 rowid 而不是业务 id：
/// id 是文本、非单调，作变更信号不可靠。探针值与本轮库集完全一致 → 复用上轮记录
/// （FilesChanged=0 驱动管道短路，FilesRead=0）。
/// 探针只是优化信号：打不开 / 查询失败 → 保守全量，绝不影响扫描结果的正确性。已知边界：行原地改值
/// 且 count 与 max(rowid) 不动（如 fork 边界元数据原地修正且无新拷贝行）不会被发现——usage 行
/// 实务上只增不改（与 ZCodeSqliteAdapter 登记的同一边界）。
/// <para>
/// <b>持久只读连接 + 连接级 32MB 页缓存</b>（PRAGMA 是连接设置，不动库文件）：库路径 → 连接缓存，
/// 每轮复用前校验库文件身份 (size, mtime)，不符（检查点回写 / 被替换 / 被删除）即弃旧开新——
/// POSIX 上未链接文件的 fd 仍可读，没有身份校验，被替换/删除库的贡献会在旧 inode 句柄上
/// "永远探针成功"、永驻零变更短路。已知残留边界：mtime 粒度粗的文件系统（FAT32 2s / exFAT 10ms）上
/// 落在同粒度时间桶且长度不变的写可能延后一轮拾取（下轮任何写即带回）；桌面数据目录（APFS /
/// NTFS / ext4，粒度 ≤100ns）不受影响。
/// </para>
/// 全量读取面（I-009 口径不变）：v1 `message` 表与 v2 `session_message` 表（type='assistant'）
/// 两轮扫描 → 载荷映射（<see cref="OpenCodeMessageParser"/>，lenient JSON 口径）→ fork 拷贝行剔除
/// （session_v2.fork_session_id/fork_boundary + seq 边界解析，继承历史不计）→ 按 message id 去重
/// （映射成功之后，跨库共用一个集合）→ 按时间排序。
/// 记忆化准入（P1 审查修复，与 HEAD FileStampMemo 的"全部库读成功才落账"全或无准入等价）：仅当本轮
/// 库集全部"探针成功 + 读取成功"才整体落账（探针/干净指纹/记录/诊断四件套）；部分读取轮（有库读失败
/// 或探针失败）整体作废记忆——否则下轮删掉失败库、库集收缩后 探针集 == 库集 == 记忆集，零变更短路会
/// 把混有已删库残行的部分记录集当缓存继续服务（违反"库集收缩必判变化"不变式）。探针失败的库不进
/// 探针集，覆盖率守卫（探针集 ≠ 本轮库集即判变化）使下一轮必然全量重读（保守自愈——ZCode 无此守卫
/// 仍正确，因其探针失败 ⇒ 同库读取必失败；OpenCode 存在"探针失败但读取成功"的形态如 WITHOUT ROWID
/// 表）；频道库集变化（选中的库路径清单变化）时探针集合随之收缩，不复用陈旧记录。
/// 只读打开不改 journal mode，兼容 OpenCode 运行中的 WAL。
/// 零变更轮诊断与 FileStampMemo 时代逐位一致：FilesRead=0、内容诊断（LinesSkipped / Malformed /
/// Invalid）归零（本轮没有解析事件就没有新的坏行）、累计四项（Duplicate / Missing / Unknown）与
/// Status 沿用上轮。
/// 诊断口径沿用 <see cref="JsonlScanDiagnostics"/>（ZCodeSqlite 同款映射）：FilesDiscovered = 选中的库数、
/// FilesRead = 打开且查询完成的库数（零变更轮为 0）、LinesSkipped = 无 usage 载荷的行（user 消息等）+
/// 非 assistant 的 v2 行 + fork 拷贝行、MalformedLines = data 载荷非法 JSON 的行、InvalidRecords =
/// 身份列不可读 / 有载荷但缺 modelID/providerID / 时间戳越界的行、DuplicateRecords = 按 message id
/// 折叠的重复记录。状态：有记录且无问题 = Ok，有问题 = Partial，无记录 = NoData。
/// 蓝本的 storage/message legacy JSON 回退、会话累计表（session/session_v2）聚合回退（仅 session 报表
/// 使用）、since/until 窗口与 SQL pushdown 不在本卡范围（统一记录全量出账，见卡片偏差清单）。
/// 实例持有探针/记录记忆与持久只读连接（单例接入 App DI），并发由 LocalIngestionDataSource 串行化。
/// </summary>
public class OpenCodeSqliteAdapter : IOpenCodeSqliteAdapter
{
    /// <summary>默认库文件名（蓝本 db_path）。</summary>
    public const string DefaultDatabaseFileName = "opencode.db";

    /// <summary>v2 session_message 必需列（蓝本 prepare_session_message_query 的门槛）。</summary>
    private static readonly string[] RequiredSessionMessageColumns = ["id", "session_id", "type", "data"];

    /// <summary>v1 message 必需列（蓝本 prepare_message_query 的隐式门槛）。</summary>
    private static readonly string[] RequiredMessageColumns = ["id", "session_id", "data"];

    /// <summary>fork 边界解析必需的 session_v2 列（蓝本 fork_copy_cutoffs）。</summary>
    private static readonly string[] RequiredForkV2Columns = ["id", "fork_session_id", "fork_boundary"];

    /// <summary>fork 边界解析必需的 session_message 列（蓝本 fork_copy_cutoffs）。</summary>
    private static readonly string[] RequiredForkMessageColumns = ["id", "session_id", "seq"];

    private readonly string? _configuredDataDir;
    private readonly OpenCodeDataLocator _locator;

    // I-049 两级探针记忆（与 ZCodeSqliteAdapter 的探针记忆同构）：_lastProbes = 上轮成功读取时逐库
    // 探针快照（库路径 → 两表 count + max(rowid)）；_lastRecords / _lastDiagnostics = 上轮成功读取的
    // 记录集与诊断。探针集与库集一致且逐库探针完全一致才复用上轮记录（零读取零映射）；探针失败 /
    // 库集变化 → 保守全量读取。已知边界：行原地改值且 count 与 max(rowid) 不动不会被发现。
    private Dictionary<string, DatabaseProbe> _lastProbes = new(StringComparer.Ordinal);
    private IReadOnlyList<UsageRecord>? _lastRecords;
    private JsonlScanDiagnostics? _lastDiagnostics;

    /// <summary>与 <see cref="_lastProbes"/> 同生命周期的"干净文件指纹"账（键集恒等）：
    /// 记录 = 上轮成功读取时（或本轮探针前抓取、探针确认与记忆一致时）各库的文件状态。指纹未动
    /// ⇒ 账面未动，是探针快路径的信任依据；方向性约束：只记"早于或等于探针所见数据态"的指纹，
    /// 绝不把晚于探针数据的指纹记成干净（否则下轮快路径会发出陈旧探针值）。</summary>
    private Dictionary<string, FileFingerprint> _cleanFileFingerprints = new(StringComparer.Ordinal);

    /// <summary>持久只读连接缓存（库路径 → 连接）：连接级 32MB 页缓存使活跃轮探针免于被 OS 页缓存
    /// 逐出后重读盘（I-049）。与身份账 <see cref="_connectionIdentities"/> 同步增删，并发由
    /// LocalIngestionDataSource 串行化。生命周期：随适配器单例自持，<b>不实现 IDisposable</b>——
    /// 被删库的缓存连接不主动清理（库从发现清单除名后没有 Drop 路径），依赖同路径重现（身份不符
    /// 弃旧开新）或进程退出回收；DI 单例语义下句柄数 ≤ 历史库路径数，可接受（ZCodeSqliteAdapter
    /// 同款缺口，I-049 审查登记不修）。</summary>
    private readonly Dictionary<string, SqliteConnection> _readOnlyConnections = new(StringComparer.Ordinal);

    /// <summary>持久连接建连时的 db 文件身份（size, mtime）：每轮复用前校验，不符（检查点回写 /
    /// 被替换 / 被删除）即弃旧开新，杜绝旧 inode 句柄上的陈旧读。</summary>
    private readonly Dictionary<string, ConnectionIdentity> _connectionIdentities = new(StringComparer.Ordinal);

    /// <summary>I-049 探针实跑计数（Infrastructure.Tests 经 InternalsVisibleTo）：完整成功的 SQL 探针
    /// 次数，用于断言文件指纹快路径确实把空闲轮的探针查询省掉了（与 ZCodeSqliteAdapter 同名先例；
    /// 探针中途失败不计入——失败走保守全量，不参与快路径断言）。</summary>
    internal int ProbeQueryCountForTests { get; private set; }

    /// <summary>db 文件身份：持久连接复用的最小同一性证据（size + mtime）。</summary>
    private readonly record struct ConnectionIdentity(long DbLength, DateTime DbWriteUtc);

    /// <summary>账本文件指纹：库文件 + 其 -wal 的 (size, mtime)。WAL 追加写必动 -wal，
    /// 检点/改值/重写必动库文件——四元组未动 ⇒ 账面未动。-wal 不存在（写端干净关闭或
    /// 回滚日志模式）以零值参与比较，同为"未动"。</summary>
    private readonly record struct FileFingerprint(long DbLength, DateTime DbWriteUtc, long WalLength, DateTime WalWriteUtc);

    /// <summary>单库探针值：v2 session_message 与 v1 message 各自的 (COUNT, MAX(rowid))；
    /// 缺失表按 0/0 参与（表不存在是稳定态，不是探针失败）。</summary>
    private readonly record struct DatabaseProbe(
        long SessionMessageCount,
        long SessionMessageMaxRowId,
        long MessageCount,
        long MessageMaxRowId);

    public OpenCodeSqliteAdapter(string? configuredDataDir = null, OpenCodeDataLocator? locator = null)
    {
        _configuredDataDir = configuredDataDir;
        _locator = locator ?? new OpenCodeDataLocator();
    }

    public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var directories = _locator.ResolveDataDirectories(_configuredDataDir);
        // 现存库清单（与既有 FilesDiscovered 语义一致：只计选中到库的目录）。
        var databasePaths = directories
            .Select(SelectDatabase)
            .Where(databasePath => databasePath is not null)
            .Select(databasePath => databasePath!)
            .ToList();
        var diagnostics = new JsonlScanDiagnostics { FilesDiscovered = databasePaths.Count };

        // 两级探针前置（与 ZCodeSqliteAdapter.ScanAsync 的探针段同构）：先文件指纹快路径——四元组
        // 与"干净指纹"一致即账面必然未动，探针值直接复用记忆，连 SQL 都不发（空闲轮 IO 降为两次
        // stat）；未命中（文件动过 / 首轮 / stat 失败）才做轻量聚合查询。stat 先于探针抓取：本轮要
        // 记的指纹只可能早于或等于探针所见的数据态（见 _cleanFileFingerprints 方向性约束）。
        // stat 失败（文件消失 / 不可读）同时弃用持久连接——连接可能指向已解除链接的旧 inode
        // （POSIX 未链接文件的 fd 仍可读），不弃会让删除的库在陈旧句柄上"永远探针成功"。
        var probes = new Dictionary<string, DatabaseProbe>(StringComparer.Ordinal);
        var roundFingerprints = new Dictionary<string, FileFingerprint>(StringComparer.Ordinal);
        foreach (var databasePath in databasePaths)
        {
            if (TryGetFileFingerprint(databasePath, out var fingerprint))
            {
                if (_cleanFileFingerprints.TryGetValue(databasePath, out var clean)
                    && clean == fingerprint
                    && _lastProbes.TryGetValue(databasePath, out var memoProbe))
                {
                    probes[databasePath] = memoProbe;
                    continue;
                }

                roundFingerprints[databasePath] = fingerprint;
            }
            else
            {
                DropReadOnlyConnection(databasePath);
            }

            if (TryProbe(databasePath) is { } probe)
            {
                probes[databasePath] = probe;
            }
        }

        // 零变更轮（探针集覆盖本轮库集且逐库探针与上轮成功读取完全一致）：复用上轮记录（同实例
        // 列表，消费方视为只读），零读取零映射；FilesChanged=0 驱动采集管道短路。本轮诊断与
        // FileStampMemo 时代的零变更轮逐位一致：内容诊断归零（本轮没有解析事件就没有新的坏行），
        // 累计四项（Duplicate / Missing / Unknown）与 Status 沿用上轮。
        if (_lastRecords is { } cachedRecords
            && _lastDiagnostics is { } lastDiagnostics
            && ProbesUnchanged(probes, databasePaths.Count))
        {
            // 本轮重新探针过的库：把探针前抓的文件指纹升为"干净指纹"（探针已确认数据与记忆一致，
            // 指纹态对应的就是记忆记录）。快路径命中的库指纹本来就在账且本轮刚验证一致，不动。
            foreach (var (databasePath, fingerprint) in roundFingerprints)
            {
                _cleanFileFingerprints[databasePath] = fingerprint;
            }

            return Task.FromResult(new JsonlScanResult
            {
                Roots = directories,
                Records = cachedRecords,
                Diagnostics = new JsonlScanDiagnostics
                {
                    Status = lastDiagnostics.Status,
                    FilesDiscovered = diagnostics.FilesDiscovered,
                    FilesRead = 0,
                    LinesSkipped = 0,
                    MalformedLines = 0,
                    InvalidRecords = 0,
                    DuplicateRecords = lastDiagnostics.DuplicateRecords,
                    MissingCostRecords = lastDiagnostics.MissingCostRecords,
                    UnknownModelRecords = lastDiagnostics.UnknownModelRecords,
                    FilesChanged = 0,
                },
            });
        }

        var records = new List<UsageRecord>();
        var seenMessageIds = new HashSet<string>(StringComparer.Ordinal);
        var problems = false;
        // 记忆按库收集"本轮探针成功且读取成功"的库（探针失败的库不进 readMemory）；是否落账由
        // 末尾的全或无准入判定（readMemory 覆盖本轮库集才落账，见落账处 P1 审查修复注释）。
        // 干净指纹与探针记忆同生命周期：只记探针前抓取、且读取成功确认的指纹。
        var readMemory = new Dictionary<string, DatabaseProbe>(StringComparer.Ordinal);
        var cleanFingerprints = new Dictionary<string, FileFingerprint>(StringComparer.Ordinal);
        foreach (var databasePath in databasePaths)
        {
            ReadDatabase(databasePath, records, seenMessageIds, diagnostics, ref problems, ct, out var readSucceeded);
            if (readSucceeded && probes.TryGetValue(databasePath, out var probe))
            {
                readMemory[databasePath] = probe;
                if (roundFingerprints.TryGetValue(databasePath, out var fingerprint))
                {
                    cleanFingerprints[databasePath] = fingerprint;
                }
            }
        }

        // 稳定排序（同时间戳保持库内读取顺序，与蓝本 stable sort_by_key 一致）。
        var sorted = records.OrderBy(static record => record.Timestamp).ToList();
        diagnostics.MissingCostRecords = sorted.Count(static record => record.CostUsd is null);
        diagnostics.UnknownModelRecords = sorted.Count(static record => record.Model is null);
        diagnostics.Status = sorted.Count == 0
            ? JsonlScanStatus.NoData
            : problems || diagnostics.InvalidRecords > 0 || diagnostics.MalformedLines > 0
                ? JsonlScanStatus.Partial
                : JsonlScanStatus.Ok;
        // FilesChanged：走到全量读取即本轮账面有变化或探针失败保守处理（有库填 1）；无库不设值，
        // 由管道按"本轮发现即空"规则处理（与既有口径一致）。
        diagnostics.FilesChanged = databasePaths.Count > 0 ? 1 : null;

        // 记忆落账（P1 审查修复）：仅当本轮库集全部"探针成功 + 读取成功"（readMemory 覆盖本轮库集）
        // 才整体落账——与 HEAD FileStampMemo 的"全部库读成功才落账"（FilesRead == FilesDiscovered）
        // 全或无准入等价。部分读取轮（有库读失败 / 探针失败）整体作废记忆（探针、干净指纹、记录、
        // 诊断全清）：否则下轮删掉失败库、库集收缩后 探针集 == 库集 == 记忆集，零变更短路会把
        // 混有已删库残行的部分记录集当缓存继续服务（违反"库集收缩必判变化"不变式，HEAD 全或无
        // 准入在同序列下必然全量重读）。健康路径（全库读成功）行为不变。
        if (readMemory.Count == databasePaths.Count)
        {
            _lastProbes = readMemory;
            _cleanFileFingerprints = cleanFingerprints;
            _lastRecords = sorted;
            _lastDiagnostics = diagnostics;
        }
        else
        {
            _lastProbes = new Dictionary<string, DatabaseProbe>(StringComparer.Ordinal);
            _cleanFileFingerprints = new Dictionary<string, FileFingerprint>(StringComparer.Ordinal);
            _lastRecords = null;
            _lastDiagnostics = null;
        }

        return Task.FromResult(new JsonlScanResult
        {
            Roots = directories,
            Records = sorted,
            Diagnostics = diagnostics,
        });
    }

    /// <summary>
    /// 每目录选一个库（蓝本 db_path）：opencode.db 存在即用它；否则取文件名匹配频道库模式
    /// （opencode-*.db，中段仅字母数字/_/-）的文件里按完整路径排序的第一个。公开为纯函数供测试对齐。
    /// </summary>
    public static string? SelectDatabase(string directory)
    {
        var defaultPath = Path.Combine(directory, DefaultDatabaseFileName);
        if (File.Exists(defaultPath))
        {
            return defaultPath;
        }

        return Directory.EnumerateFiles(directory)
            .Where(path => IsChannelDatabaseName(Path.GetFileName(path)) && File.Exists(path))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    internal static bool IsChannelDatabaseName(string fileName)
    {
        const string prefix = "opencode-";
        const string suffix = ".db";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(suffix, StringComparison.Ordinal)
            || fileName.Length <= prefix.Length + suffix.Length - 1)
        {
            return false;
        }

        // 蓝本允许中段为空（"opencode-.db" 前缀 + 后缀即过）：start..end 为空串时 all() 为 true。
        var middle = fileName[prefix.Length..^suffix.Length];
        return middle.All(IsChannelNameCharacter);
    }

    private static bool IsChannelNameCharacter(char character)
        => char.IsAsciiLetterOrDigit(character) || character is '_' or '-';

    /// <summary>
    /// 打开单个库并读出 usage 行。打开失败 / 查询失败记为问题（状态降级 Partial）并返回已读记录，
    /// 与蓝本"读失败即断、保留已读条目"一致；行级拒绝按诊断口径计数后继续。
    /// 连接经 <see cref="GetReadOnlyConnection"/> 复用持久只读连接（页缓存常驻，I-049）；
    /// <paramref name="readSucceeded"/> = 打开与查询全部成功——只有成功读取的库才允许进入探针记忆
    /// （失败的库下一轮必须重新读取，ZCodeSqliteAdapter 同款）。
    /// </summary>
    private void ReadDatabase(
        string databasePath,
        List<UsageRecord> records,
        HashSet<string> seenMessageIds,
        JsonlScanDiagnostics diagnostics,
        ref bool problems,
        CancellationToken ct,
        out bool readSucceeded)
    {
        readSucceeded = false;
        SqliteConnection connection;
        try
        {
            connection = GetReadOnlyConnection(databasePath);
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            problems = true;
            return;
        }

        try
        {
            if (SqliteTableSchema.TableExists(connection, "message"))
            {
                ScanMessageTable(connection, records, seenMessageIds, diagnostics, ct);
            }

            var hasSessionMessages = SqliteTableSchema.TableExists(connection, "session_message");
            if (hasSessionMessages)
            {
                var forkCutoffs = ForkCopyCutoffs(connection);
                ScanSessionMessageTable(connection, forkCutoffs, records, seenMessageIds, diagnostics, ct);
            }

            diagnostics.FilesRead++;
            readSucceeded = true;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            problems = true;
            // 查询中途失败即弃连：连接可能处于坏状态，下轮重开自愈（ZCodeSqliteAdapter 同款，
            // 不弃会让坏连接的缓存页继续参与后续轮次）。
            DropReadOnlyConnection(databasePath);
        }
    }

    /// <summary>
    /// I-049 探针（前置）：v2 session_message 与（存在时的）v1 message 各取 COUNT + MAX(rowid)。
    /// 打不开 / 查询失败返回 null（调用方保守按"有变化"全量读取）——探针是优化信号，绝不影响扫描
    /// 结果的正确性。表不存在按 0/0 参与元组（<see cref="DatabaseProbe"/>，不是探针失败）；
    /// 探针表达式对 WITHOUT ROWID 表等形态不合法时同样返回 null → 保守全量。
    /// 连接经 <see cref="GetReadOnlyConnection"/> 复用持久只读连接（页缓存常驻）；
    /// 失败即弃连，下轮重开自愈。
    /// </summary>
    private DatabaseProbe? TryProbe(string databasePath)
    {
        try
        {
            var connection = GetReadOnlyConnection(databasePath);
            var sessionMessage = ProbeTable(connection, "session_message");
            var message = ProbeTable(connection, "message");
            ProbeQueryCountForTests++;
            return new DatabaseProbe(
                SessionMessageCount: sessionMessage?.Count ?? 0,
                SessionMessageMaxRowId: sessionMessage?.MaxRowId ?? 0,
                MessageCount: message?.Count ?? 0,
                MessageMaxRowId: message?.MaxRowId ?? 0);
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            DropReadOnlyConnection(databasePath);
            return null;
        }
    }

    /// <summary>单表探针聚合；表不存在返回 null（按 0/0 参与元组）。MAX(rowid) 在 WITHOUT ROWID
    /// 表上会抛 SqliteException（无 rowid 列），由 <see cref="TryProbe"/> 统一按探针失败处理。</summary>
    private static (long Count, long MaxRowId)? ProbeTable(SqliteConnection connection, string table)
    {
        if (!SqliteTableSchema.TableExists(connection, table))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*), COALESCE(MAX(rowid), 0) FROM {table}";
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.GetInt64(0), reader.GetInt64(1))
            : null;
    }

    /// <summary>
    /// 取库的持久只读连接：命中缓存且 db 文件身份（size, mtime）与建连时一致才复用；
    /// 否则弃旧开新。身份校验是删除/替换库不被陈旧句柄掩盖的守门员——POSIX 上未链接文件的
    /// fd 仍可读，没有这一步，被替换/删除库的贡献会在旧 inode 上"永远探针成功"。
    /// 打开即设连接级页缓存 32MB（I-049：把活跃轮 29 MB 载荷重读变成页缓存命中后再解析，
    /// 省的是磁盘读；实测依据见 ZCodeSqliteAdapter 同款注释）。
    /// </summary>
    private SqliteConnection GetReadOnlyConnection(string databasePath)
    {
        if (_readOnlyConnections.TryGetValue(databasePath, out var existing))
        {
            if (TryGetDatabaseIdentity(databasePath, out var identity)
                && _connectionIdentities.TryGetValue(databasePath, out var known)
                && identity == known)
            {
                return existing;
            }

            DropReadOnlyConnection(databasePath);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false, // 句柄由适配器自持（单例生命周期），不走连接池重复持有
        }.ToString());
        connection.Open();
        if (!TryGetDatabaseIdentity(databasePath, out var openedIdentity))
        {
            connection.Dispose();
            throw new IOException($"OpenCode 会话库身份读取失败（建连后文件即不可达）：{databasePath}");
        }

        ExecutePragma(connection, "PRAGMA cache_size=-32768");
        _readOnlyConnections[databasePath] = connection;
        _connectionIdentities[databasePath] = openedIdentity;
        return connection;
    }

    /// <summary>弃用持久连接并同步清身份账；释放失败不冒泡（下轮重开自愈）。</summary>
    private void DropReadOnlyConnection(string databasePath)
    {
        if (_readOnlyConnections.Remove(databasePath, out var connection))
        {
            _connectionIdentities.Remove(databasePath);
            try
            {
                connection.Dispose();
            }
            catch (Exception exception) when (exception is SqliteException or IOException)
            {
                // 释放失败不影响保守路径（本轮按打不开处理 / 下轮重开）。
            }
        }
    }

    private static void ExecutePragma(SqliteConnection connection, string pragma)
    {
        using var command = connection.CreateCommand();
        command.CommandText = pragma;
        command.ExecuteNonQuery();
    }

    /// <summary>db 文件身份（size + mtime）；文件消失 / stat 失败返回 false（身份校验必不过 → 弃连）。</summary>
    private static bool TryGetDatabaseIdentity(string databasePath, out ConnectionIdentity identity)
    {
        try
        {
            var database = new FileInfo(databasePath);
            if (!database.Exists)
            {
                identity = default;
                return false;
            }

            identity = new ConnectionIdentity(database.Length, database.LastWriteTimeUtc);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or System.Security.SecurityException)
        {
            identity = default;
            return false;
        }
    }

    /// <summary>
    /// 账本文件指纹（db + -wal 的 size/mtime）。-wal 不存在按零值参与比较（写端干净关闭或
    /// 回滚日志模式都是稳定态）；new FileInfo 每次现查（FileSystemInfo 实例有缓存，不可复用）。
    /// </summary>
    private static bool TryGetFileFingerprint(string databasePath, out FileFingerprint fingerprint)
    {
        try
        {
            var database = new FileInfo(databasePath);
            if (!database.Exists)
            {
                fingerprint = default;
                return false;
            }

            var wal = new FileInfo(databasePath + "-wal");
            fingerprint = new FileFingerprint(
                database.Length,
                database.LastWriteTimeUtc,
                wal.Exists ? wal.Length : 0,
                wal.Exists ? wal.LastWriteTimeUtc : DateTime.MinValue);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or System.Security.SecurityException)
        {
            fingerprint = default;
            return false;
        }
    }

    /// <summary>
    /// 零变更判定：本轮探针集必须覆盖本轮库集（覆盖率守卫，与 ZCodeSqliteAdapter 的已知差异——
    /// 探针失败的库不进探针集，探针集小于库集即有库账面未确认，保守按有变化全量读取；ZCode 无此
    /// 守卫仍正确，因其探针失败 ⇒ 同库读取必失败 ⇒ 不产出记录；OpenCode 的 WITHOUT ROWID 等形态
    /// 可以"探针失败但读取成功"，无守卫会让这类库首轮之后永驻缓存），且逐库探针与上轮成功读取的
    /// 记忆完全一致（含库集收缩——消失的库不进本轮探针，集合比对必然判为变化）。
    /// </summary>
    private bool ProbesUnchanged(Dictionary<string, DatabaseProbe> probes, int discoveredCount)
    {
        if (probes.Count != discoveredCount)
        {
            return false;
        }

        if (probes.Count != _lastProbes.Count)
        {
            return false;
        }

        foreach (var (databasePath, probe) in probes)
        {
            if (!_lastProbes.TryGetValue(databasePath, out var known) || known != probe)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// v1 `message` 表扫描（蓝本 prepare_message_query 的无窗口形态：SELECT id, session_id, data）。
    /// 列集门槛与 v2 对称：id/session_id/data 缺失时整表**静默**跳过（蓝本 prepare 失败 = 跳过 v1、
    /// 继续扫 v2、不标问题），不像打开/查询失败那样把库标为 problems。
    /// </summary>
    private static void ScanMessageTable(
        SqliteConnection connection,
        List<UsageRecord> records,
        HashSet<string> seenMessageIds,
        JsonlScanDiagnostics diagnostics,
        CancellationToken ct)
    {
        if (!SqliteTableSchema.ContainsAll(
                SqliteTableSchema.ReadColumns(connection, "message"), RequiredMessageColumns))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, session_id, data FROM message";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (TryReadText(reader, 0) is not { } id
                || TryReadText(reader, 1) is not { } sessionId
                || TryReadText(reader, 2) is not { } data)
            {
                diagnostics.InvalidRecords++;
                continue;
            }

            IngestRow(() => OpenCodeMessageParser.MapMessage(data, id, sessionId), records, seenMessageIds, diagnostics);
        }
    }

    /// <summary>
    /// v2 `session_message` 表扫描（蓝本 prepare_session_message_query + is_fork_copy）：
    /// 必需列缺失整表跳过（蓝本 prepare 失败静默）；time_created / seq 可选（缺 seq 时 fork 分类
    /// 无从判定、行保留——蓝本 read 失败 keep 同口径）；仅消费 type='assistant' 行。
    /// </summary>
    private static void ScanSessionMessageTable(
        SqliteConnection connection,
        Dictionary<string, long> forkCutoffs,
        List<UsageRecord> records,
        HashSet<string> seenMessageIds,
        JsonlScanDiagnostics diagnostics,
        CancellationToken ct)
    {
        var columns = SqliteTableSchema.ReadColumns(connection, "session_message");
        if (!SqliteTableSchema.ContainsAll(columns, RequiredSessionMessageColumns))
        {
            return;
        }

        var hasTimeCreated = columns.Contains("time_created");
        var hasSeq = columns.Contains("seq");
        var timeCreatedColumn = hasTimeCreated ? "time_created" : "NULL";
        var seqColumn = hasSeq ? ", seq" : string.Empty;

        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT id, session_id, type, data, {timeCreatedColumn}{seqColumn} FROM session_message";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            // 蓝本先读 id/session_id/type、非 assistant 行在**读 data 之前**静默 continue——
            // 非 assistant 行的 data 不可读是无声非事件（计 skipped），不计 InvalidRecords。
            if (TryReadText(reader, 0) is not { } id
                || TryReadText(reader, 1) is not { } sessionId
                || TryReadText(reader, 2) is not { } messageType)
            {
                diagnostics.InvalidRecords++;
                continue;
            }

            if (!string.Equals(messageType, "assistant", StringComparison.Ordinal))
            {
                diagnostics.LinesSkipped++;
                continue;
            }

            if (TryReadText(reader, 3) is not { } data)
            {
                diagnostics.InvalidRecords++;
                continue;
            }

            var created = hasTimeCreated ? ReadCreatedColumn(reader, 4) : 0;

            // fork 拷贝行不参与映射也不占用去重名额（蓝本 is_fork_copy 在解析前剔除）；
            // 无 seq 列或 seq 不可读时行保留（蓝本 read 失败 → 非 copy）。
            if (hasSeq
                && forkCutoffs.TryGetValue(sessionId, out var cutoff)
                && ReadSeqColumn(reader, 5) is { } seq
                && seq <= cutoff)
            {
                diagnostics.LinesSkipped++;
                continue;
            }

            IngestRow(() => OpenCodeMessageParser.MapSessionMessage(data, id, sessionId, created), records, seenMessageIds, diagnostics);
        }
    }

    /// <summary>行映射产物的统一入口：拒绝原因计数 + message id 去重（映射成功之后，I-006 口径）。</summary>
    private static void IngestRow(
        Func<(UsageRecord? Record, OpenCodeRowRejection Rejection)> map,
        List<UsageRecord> records,
        HashSet<string> seenMessageIds,
        JsonlScanDiagnostics diagnostics)
    {
        UsageRecord? record;
        OpenCodeRowRejection rejection;
        try
        {
            (record, rejection) = map();
        }
        catch (JsonException)
        {
            diagnostics.MalformedLines++;
            return;
        }

        switch (rejection)
        {
            case OpenCodeRowRejection.NoUsagePayload:
                diagnostics.LinesSkipped++;
                return;
            case OpenCodeRowRejection.MissingIdentity:
            case OpenCodeRowRejection.TimestampOutOfRange:
                diagnostics.InvalidRecords++;
                return;
        }

        // 去重发生在映射成功之后；无 message id 的记录不去重直接保留（蓝本 entry_id None → push）。
        if (record!.MessageId is { } messageId
            && !seenMessageIds.Add(messageId))
        {
            diagnostics.DuplicateRecords++;
            return;
        }

        records.Add(record!);
    }

    /// <summary>
    /// fork 会话的继承历史截断点（蓝本 fork_copy_cutoffs）：session_v2 携带 fork 链接与边界消息，
    /// session_message 携带拷贝行保留的 seq。无法解析边界（缺父行 / 缺边界行 / 载荷不可解析）时不
    /// 记录截断点——fork 保留全部行（蓝本"keeps every row"口径）。
    /// </summary>
    private static Dictionary<string, long> ForkCopyCutoffs(SqliteConnection connection)
    {
        var cutoffs = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!SqliteTableSchema.TableExists(connection, "session_v2")
            || !SqliteTableSchema.TableExists(connection, "session_message"))
        {
            return cutoffs;
        }

        if (!SqliteTableSchema.ContainsAll(SqliteTableSchema.ReadColumns(connection, "session_v2"), RequiredForkV2Columns)
            || !SqliteTableSchema.ContainsAll(SqliteTableSchema.ReadColumns(connection, "session_message"), RequiredForkMessageColumns))
        {
            return cutoffs;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, fork_session_id, fork_boundary FROM session_v2 "
            + "WHERE fork_session_id IS NOT NULL AND fork_boundary IS NOT NULL";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (TryReadText(reader, 0) is not { } forkId
                || TryReadText(reader, 1) is not { } parentId
                || TryReadText(reader, 2) is not { } boundary)
            {
                continue;
            }

            if (forkId.Length == 0 || parentId.Length == 0)
            {
                continue;
            }

            if (TryResolveForkCopyCutoff(connection, parentId, boundary, out var cutoff))
            {
                cutoffs[forkId] = cutoff;
            }
        }

        return cutoffs;
    }

    /// <summary>
    /// 单个 fork 的最大拷贝 seq（蓝本 fork_copy_cutoff）：boundary.type='through' → 父边界消息的 seq；
    /// 'before' → 父会话中小于边界 seq 的最大 seq（seq 稀疏时 fork 自有行紧接最后一条拷贝行）。
    /// </summary>
    private static bool TryResolveForkCopyCutoff(SqliteConnection connection, string parentId, string boundary, out long cutoff)
    {
        cutoff = 0;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(boundary);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || LenientJson.GetNonEmptyString(root, "type") is not { } boundaryType
                || LenientJson.GetNonEmptyString(root, "messageID") is not { } parentMessageId)
            {
                return false;
            }

            var boundarySeq = QueryScalarLong(
                connection,
                "SELECT seq FROM session_message WHERE session_id = $parentId AND id = $messageId LIMIT 1",
                ("$parentId", parentId),
                ("$messageId", parentMessageId));
            if (boundarySeq is not { } seq)
            {
                return false;
            }

            switch (boundaryType)
            {
                case "through":
                    cutoff = seq;
                    return true;
                case "before":
                    return TryLastParentSeqBefore(connection, parentId, seq, out cutoff);
                default:
                    return false;
            }
        }
    }

    /// <summary>before 边界的拷贝上界（蓝本 last_parent_seq_before）：MAX(seq) WHERE seq &lt; boundarySeq；
    /// 空集返回 None（没有拷贝行，全保留）。</summary>
    private static bool TryLastParentSeqBefore(SqliteConnection connection, string parentId, long boundarySeq, out long cutoff)
    {
        cutoff = 0;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(seq) FROM session_message WHERE session_id = $parentId AND seq < $boundarySeq";
        command.Parameters.AddWithValue("$parentId", parentId);
        command.Parameters.AddWithValue("$boundarySeq", boundarySeq);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
        {
            return false;
        }

        try
        {
            cutoff = reader.GetInt64(0);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException)
        {
            return false;
        }
    }

    /// <summary>单值 long 查询；无行 / NULL / 类型不可读返回 null（蓝本 read::&lt;i64&gt;.ok()? 口径）。</summary>
    private static long? QueryScalarLong(SqliteConnection connection, string sql, params (string Name, string Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
        {
            return null;
        }

        try
        {
            return reader.GetInt64(0);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>身份/文本列：NULL 或类型异常视为不可读（返回 null，行按蓝本静默跳过、此处计数）。</summary>
    private static string? TryReadText(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        try
        {
            return reader.GetString(ordinal);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// v2 created 列（蓝本 read i64 → f64 截断 → 其余 0）：NULL / 文本 / 类型异常按 0；
    /// f64 溢出按 Rust as-cast 饱和语义钳到 long 边界。
    /// </summary>
    private static long ReadCreatedColumn(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return 0;
        }

        try
        {
            return reader.GetInt64(ordinal);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException)
        {
            try
            {
                var value = reader.GetDouble(ordinal);
                if (!double.IsFinite(value))
                {
                    return 0;
                }

                return value switch
                {
                    >= 9223372036854775808.0 => long.MaxValue,
                    <= -9223372036854775808.0 => long.MinValue,
                    _ => (long)value,
                };
            }
            catch (Exception fallbackException) when (fallbackException is FormatException or InvalidCastException)
            {
                return 0;
            }
        }
    }

    /// <summary>fork 分类的 seq 列：i64 直读；NULL / 类型异常返回 null（行保留，蓝本 read 失败同口径）。</summary>
    private static long? ReadSeqColumn(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        try
        {
            return reader.GetInt64(ordinal);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException)
        {
            return null;
        }
    }
}
