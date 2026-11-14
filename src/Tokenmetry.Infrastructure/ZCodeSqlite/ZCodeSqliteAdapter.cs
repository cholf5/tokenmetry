using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Microsoft.Data.Sqlite;

namespace Tokenmetry.Infrastructure.ZCodeSqlite;

/// <summary>
/// ZCode SQLite 账本直读适配器（I-006，蓝本 ../ccusage/rust/adapters/zcode/）：
/// 库定位（<see cref="ZCodeLogLocator"/>）→ 逐库探针（文件指纹快路径 → completed 行 count + max(id)）
/// → 账面未动直接复用上轮记录（零读取零映射）→ 否则逐库只读打开 → schema 检查（<see cref="ZCodeSchemaInspector"/>，
/// 按可选列拼查询）→ 读 completed 行 → 行映射（<see cref="ZCodeRecordMapper"/>）→ 按 usage id 去重 →
/// 按时间排序 → 统一记录 + 诊断计数。
/// 账本是随使用持续变化的单一库文件，mtime 增量不适用；以 I-042 轻量探针 + 零变更轮结果记忆化
/// （与 <see cref="Infrastructure.Jsonl.IncrementalJsonlScanner"/> 的零变更缓存同构）避免每轮全表
/// 重读重映射：探针快照 = 上轮<b>成功读取</b>时各库的 (count, max(id))，本轮逐库探针与库集完全一致
/// 才复用；探针失败 / 库集变化（新增、消失）→ 保守全量读取。已知边界：行原地改值（token 修正且
/// count 与 max(id) 不动）不会被发现——usage 行实务上只增不改（账本写入方语义）。
/// <para>
/// <b>探针成本两级（IOPS 修复）</b>：探针查询 <c>WHERE status='completed'</c> 在 ZCode 账本上无索引
/// 可走（status 不在其二级索引内），每轮全表 SCAN model_usage——生产实测 b-tree ~25MB，OS 页缓存
/// 被逐出后每轮重读盘（app.log 5087 轮 p50=1415ms / max=63s；刷新间隔 1 分钟时 ≈ 每天 34 分钟盘上扫，
/// 即用户观测到的持续高 IOPS）。两级缓解：
/// ① <b>文件指纹快路径</b>——db 与 -wal 的 (size, mtime) 与上轮记录的"干净指纹"一致即账面必然未动
/// （WAL 追加写必动 -wal，检查点/改值必动库文件），探针值直接复用记忆、连 SQL 都不发（空闲轮 IO
/// 降为两次 stat）；② <b>持久只读连接 + 连接级 32MB 页缓存</b>（PRAGMA 是连接设置，不动库文件）——
/// b-tree 常驻进程内存，writer 活跃期（指纹必变、探针必跑）暖态探针实测 3-10ms。
/// </para>
/// <para>
/// <b>持久连接的身份校验</b>：每轮 stat db 文件的 (size, mtime) 与建连时不符（检查点回写 / 被替换 /
/// 被删除）即弃旧开新——否则在旧 inode 的句柄上会"永远探针成功"（POSIX 未链接文件的 fd 仍可读），
/// 删除库的贡献将永驻零变更短路。已知残留边界：mtime 粒度粗的文件系统（FAT32 2s / exFAT 10ms）上
/// 落在同粒度时间桶且长度不变的写可能延后一轮拾取（下轮任何写即带回）；桌面数据目录（APFS /
/// NTFS / ext4，粒度 ≤100ns）不受影响。
/// </para>
/// 只读模式打开且不改 journal mode，兼容 ZCode 运行中的 WAL。
/// 读取层的类型容错与蓝本 read_token_column / read_timestamp_ms 一致：token 列 i64 钳 0 →
/// f64 兜底（有限正数四舍五入）→ 其余按 0，行永保留；身份列读取失败才整行静默跳过；
/// provider / directory 列类型异常按"缺失"处理保留行。去重只对映射成功的记录生效
/// （与蓝本"仅对成功条目按 message id 去重"一致）。
/// 诊断字段沿用 <see cref="JsonlScanDiagnostics"/> 的最近口径：FilesDiscovered = 发现的库数、
/// FilesRead = 成功打开且 schema 支持并完成查询的库数（零变更轮为 0）、InvalidRecords =
/// 语义校验拒绝的行数（身份无效 / 时间无效 / 全零，经 <see cref="ZCodeRecordMapper.Map"/>）、
/// DuplicateRecords = 按 id 折叠的重复记录；行级 malformed 概念不存在（Malformed/LinesSkipped 恒 0）。
/// 实例持有探针/记录记忆与持久只读连接（单例接入 App DI），并发由 LocalIngestionDataSource 串行化。
/// 采集成本审计：本源判定（两级探针为何是当前最优、为何不做行级增量）与六源矩阵的唯一出处见
/// docs/perf-investigation-2026-11-14-sqlite-sources.md（I-051，数字留在文档、注释不复制防漂移）。
/// </summary>
public class ZCodeSqliteAdapter : IZCodeSqliteAdapter
{
    /// <summary>严格小于 2^63 的 double 才可安全 cast 到 long（2^63 本身可被 double 精确表示，cast 回绕）。</summary>
    private const double MaxRepresentableDoubleForLong = 9223372036854775808.0;

    private readonly string? _configuredHome;
    private readonly ZCodeLogLocator _locator;

    // I-042 延伸（探针前置 + 零变更轮结果记忆化）：_lastCompletedProbe = 上轮成功读取时逐库
    // 探针快照（库路径 → completed 行 count + max(id)）；_lastRecords / _lastDiagnostics = 上轮
    // 成功读取的记录集与累计诊断。账本库无 mtime 增量语义（单文件持续变化），以轻量聚合查询
    // 判断"自上轮成功读取以来账面是否动过"——库集与逐库探针完全一致才复用上轮记录（零读取
    // 零映射）。已知边界：行原地改值（token 修正且 count 与 max(id) 不动）不会被发现——usage 行
    // 实务上只增不改（账本写入方语义），且任何新行/删除/库集变化都会触发全量。
    private Dictionary<string, (int Count, string MaxId)> _lastCompletedProbe = new(StringComparer.Ordinal);
    private IReadOnlyList<UsageRecord>? _lastRecords;
    private JsonlScanDiagnostics? _lastDiagnostics;

    /// <summary>与 <see cref="_lastCompletedProbe"/> 同生命周期的"干净文件指纹"账（键集恒等）：
    /// 记录 = 上轮成功读取时（或本轮探针前抓取、探针确认与记忆一致时）各库的文件状态。指纹未动
    /// ⇒ 账面未动，是探针快路径的信任依据；方向性约束：只记"早于或等于探针所见数据态"的指纹，
    /// 绝不把晚于探针数据的指纹记成干净（否则下轮快路径会发出陈旧探针值）。</summary>
    private Dictionary<string, FileFingerprint> _cleanFileFingerprints = new(StringComparer.Ordinal);

    /// <summary>持久只读连接缓存（库路径 → 连接，IOPS 修复）：连接级 32MB 页缓存使 model_usage
    /// b-tree 常驻进程内存，免于每轮探针被 OS 页缓存逐出后重读盘。与身份账
    /// <see cref="_connectionIdentities"/> 同步增删，并发由 LocalIngestionDataSource 串行化。</summary>
    private readonly Dictionary<string, SqliteConnection> _readOnlyConnections = new(StringComparer.Ordinal);

    /// <summary>持久连接建连时的 db 文件身份（size, mtime）：每轮复用前校验，不符（检查点回写 /
    /// 被替换 / 被删除）即弃旧开新，杜绝旧 inode 句柄上的陈旧读。</summary>
    private readonly Dictionary<string, ConnectionIdentity> _connectionIdentities = new(StringComparer.Ordinal);

    /// <summary>IOPS 修复验证计数（Infrastructure.Tests 经 InternalsVisibleTo）：SQL 探针实跑次数，
    /// 用于断言文件指纹快路径确实把空闲轮的探针查询省掉了。</summary>
    internal int ProbeQueryCountForTests { get; private set; }

    /// <summary>db 文件身份：持久连接复用的最小同一性证据（size + mtime）。</summary>
    private readonly record struct ConnectionIdentity(long DbLength, DateTime DbWriteUtc);

    /// <summary>账本文件指纹：库文件 + 其 -wal 的 (size, mtime)。WAL 追加写必动 -wal，
    /// 检点/改值/重写必动库文件——四元组未动 ⇒ 账面未动。-wal 不存在（写端干净关闭或
    /// 回滚日志模式）以零值参与比较，同为"未动"。</summary>
    private readonly record struct FileFingerprint(long DbLength, DateTime DbWriteUtc, long WalLength, DateTime WalWriteUtc);

    public ZCodeSqliteAdapter(string? configuredHome = null, ZCodeLogLocator? locator = null)
    {
        _configuredHome = configuredHome;
        _locator = locator ?? new ZCodeLogLocator();
    }

    public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var resolved = _locator.ResolveDatabases(_configuredHome);
        var diagnostics = new JsonlScanDiagnostics
        {
            FilesDiscovered = resolved.Databases.Count,
        };

        // 探针前置（两级）：先文件指纹快路径——四元组与"干净指纹"一致即账面必然未动，探针值
        // 直接复用记忆，连 SQL 都不发（空闲轮 IO 降为两次 stat）；未命中（文件动过 / 首轮 /
        // stat 失败）才做轻量聚合查询。stat 先于探针抓取：本轮要记的指纹只可能早于或等于探针
        // 所见的数据态（见 _cleanFileFingerprints 方向性约束）。stat 失败（文件消失 / 不可读）
        // 同时弃用持久连接——连接可能指向已解除链接的旧 inode（POSIX 未链接文件的 fd 仍可读），
        // 不弃会让删除的库在陈旧句柄上"永远探针成功"。
        var probes = new Dictionary<string, (int Count, string MaxId)>(StringComparer.Ordinal);
        var roundFingerprints = new Dictionary<string, FileFingerprint>(StringComparer.Ordinal);
        foreach (var database in resolved.Databases)
        {
            if (TryGetFileFingerprint(database, out var fingerprint))
            {
                if (_cleanFileFingerprints.TryGetValue(database, out var clean)
                    && clean == fingerprint
                    && _lastCompletedProbe.TryGetValue(database, out var memoProbe))
                {
                    probes[database] = memoProbe;
                    continue;
                }

                roundFingerprints[database] = fingerprint;
            }
            else
            {
                DropReadOnlyConnection(database);
            }

            if (TryProbe(database) is { } probe)
            {
                probes[database] = probe;
            }
        }

        // 零变更轮（库集与逐库探针与上轮成功读取完全一致）：复用上轮记录（同实例列表，消费方
        // 视为只读），零读取零映射；FilesChanged=0 驱动采集管道短路。本轮诊断只反映"未读取"
        // （FilesRead=0、无解析事件），累计口径（Status / Invalid / Duplicate / Missing / Unknown）
        // 沿用上轮——与"重建后必然得到的结果"一致（IncrementalJsonlScanner 零变更缓存同款）。
        if (_lastRecords is { } cachedRecords && _lastDiagnostics is { } lastDiagnostics && ProbesUnchanged(probes))
        {
            // 本轮重新探针过的库：把探针前抓的文件指纹升为"干净指纹"（探针已确认数据与记忆一致，
            // 指纹态对应的就是记忆记录）。快路径命中的库指纹本来就在账且本轮刚验证一致，不动。
            foreach (var (database, fingerprint) in roundFingerprints)
            {
                _cleanFileFingerprints[database] = fingerprint;
            }

            return Task.FromResult(new JsonlScanResult
            {
                Roots = resolved.Roots,
                Records = cachedRecords,
                Diagnostics = new JsonlScanDiagnostics
                {
                    Status = lastDiagnostics.Status,
                    FilesDiscovered = diagnostics.FilesDiscovered,
                    FilesRead = 0,
                    InvalidRecords = lastDiagnostics.InvalidRecords,
                    DuplicateRecords = lastDiagnostics.DuplicateRecords,
                    MissingCostRecords = lastDiagnostics.MissingCostRecords,
                    UnknownModelRecords = lastDiagnostics.UnknownModelRecords,
                    FilesChanged = 0,
                },
            });
        }

        var records = new List<UsageRecord>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var problems = false;
        // 记忆只收"本轮成功读取且探针成功"的库：读取失败的库不进记忆，下一轮库集比对必然
        // 判为变化、重新读取（保守自愈，与探针后置时代"只在成功读取后落账"一致）。
        // 干净指纹与探针记忆同生命周期（键集恒等）：只记探针前抓取、且读取成功确认的指纹。
        var readMemory = new Dictionary<string, (int Count, string MaxId)>(StringComparer.Ordinal);
        var cleanFingerprints = new Dictionary<string, FileFingerprint>(StringComparer.Ordinal);
        foreach (var database in resolved.Databases)
        {
            var rows = ReadDatabase(database, diagnostics, ref problems, ct, out var readSucceeded);
            if (readSucceeded && probes.TryGetValue(database, out var probe))
            {
                readMemory[database] = probe;
                if (roundFingerprints.TryGetValue(database, out var fingerprint))
                {
                    cleanFingerprints[database] = fingerprint;
                }
            }

            foreach (var row in rows)
            {
                var record = ZCodeRecordMapper.Map(row);
                if (record is null)
                {
                    diagnostics.InvalidRecords++;
                    continue;
                }

                // 去重发生在映射成功之后（蓝本先 row_to_entry 再按 id 去重）：
                // 同 id 的无效首现行不占用去重名额，另一库的有效行得以保留。
                // MessageId 取自已通过身份校验的 row.Id（Map 保证非 null），此处非空断言仅为可空标注。
                if (!seenIds.Add(record.MessageId!))
                {
                    diagnostics.DuplicateRecords++;
                    continue;
                }

                records.Add(record);
            }
        }

        // 稳定排序（同时间戳保持库内读取顺序，与蓝本 stable sort_by_key 一致）。
        var sorted = records.OrderBy(static record => record.Timestamp).ToList();
        diagnostics.MissingCostRecords = sorted.Count(static record => record.CostUsd is null);
        diagnostics.UnknownModelRecords = sorted.Count(static record => record.Model is null);
        diagnostics.Status = sorted.Count == 0
            ? JsonlScanStatus.NoData
            : problems || diagnostics.InvalidRecords > 0
                ? JsonlScanStatus.Partial
                : JsonlScanStatus.Ok;
        // FilesChanged：走到全量读取即本轮账面有变化或探针失败保守处理（有库填 1）；
        // 无库不设值，由管道按"本轮发现即空"规则处理（与既有口径一致）。
        diagnostics.FilesChanged = resolved.Databases.Count > 0 ? 1 : null;

        _lastCompletedProbe = readMemory;
        _cleanFileFingerprints = cleanFingerprints;
        _lastRecords = sorted;
        _lastDiagnostics = diagnostics;

        return Task.FromResult(new JsonlScanResult
        {
            Roots = resolved.Roots,
            Records = sorted,
            Diagnostics = diagnostics,
        });
    }

    /// <summary>
    /// 打开单个库并读出 completed 行。打开失败 / schema 不支持 / 查询中断都记为问题
    /// （状态降级 Partial）并返回已读到的行，与蓝本"读失败即断、保留已读条目"的语义一致；
    /// 行级读取失败（身份列 NULL / 类型异常）按蓝本静默跳过该行、继续读取。
    /// <paramref name="readSucceeded"/> = 打开、schema 检查与查询全部成功——只有成功读取的库
    /// 才允许进入探针记忆（失败的库下一轮必须重新读取）。
    /// </summary>
    private List<ZCodeUsageRow> ReadDatabase(
        string databasePath,
        JsonlScanDiagnostics diagnostics,
        ref bool problems,
        CancellationToken ct,
        out bool readSucceeded)
    {
        var rows = new List<ZCodeUsageRow>();
        readSucceeded = false;
        SqliteConnection connection;
        try
        {
            connection = GetReadOnlyConnection(databasePath);
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            problems = true;
            return rows;
        }

        try
        {
            var schema = ZCodeSchemaInspector.Inspect(connection);
            if (!schema.IsSupported)
            {
                problems = true;
                return rows;
            }

            using var command = connection.CreateCommand();
            command.CommandText = BuildQuery(schema);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (ReadRow(reader) is { } row)
                {
                    rows.Add(row);
                }
            }

            diagnostics.FilesRead++;
            readSucceeded = true;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            problems = true;
            // 查询中途失败即弃连：连接可能处于坏状态，下轮重开自愈（不弃会让坏连接的
            // 缓存页继续参与后续轮次）。
            DropReadOnlyConnection(databasePath);
        }

        return rows;
    }

    /// <summary>
    /// I-042 探针（前置）：completed 行 count + max(id)。打不开 / 表缺失 / 查询失败返回 null
    /// （调用方保守按"有变化"全量读取）——探针是优化信号，绝不影响扫描结果的正确性。
    /// 连接经 <see cref="GetReadOnlyConnection"/> 复用持久只读连接（页缓存常驻，IOPS 修复）；
    /// 失败即弃连，下轮重开自愈。
    /// </summary>
    private (int Count, string MaxId)? TryProbe(string databasePath)
    {
        try
        {
            var connection = GetReadOnlyConnection(databasePath);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*), COALESCE(MAX(id), '') FROM model_usage WHERE status = 'completed'";
            using var reader = command.ExecuteReader();
            ProbeQueryCountForTests++;
            return reader.Read()
                ? (reader.GetInt32(0), reader.GetString(1))
                : null;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            DropReadOnlyConnection(databasePath);
            return null;
        }
    }

    /// <summary>
    /// 取库的持久只读连接：命中缓存且 db 文件身份（size, mtime）与建连时一致才复用；
    /// 否则弃旧开新。身份校验是删除/替换库不被陈旧句柄掩盖的守门员——POSIX 上未链接文件的
    /// fd 仍可读，没有这一步，被删库的贡献会在旧 inode 上"永远探针成功"。
    /// 打开即设连接级页缓存 32MB（<see cref="_readOnlyConnections"/> 注释：IOPS 修复的依据与实测）。
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
            throw new IOException($"ZCode 账本库身份读取失败（建连后文件即不可达）：{databasePath}");
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

    /// <summary>零变更判定：本轮探针的库集与逐库指纹与上轮成功读取的记忆完全一致
    /// （含库集收缩——消失的库不进本轮探针，集合比对必然判为变化）。</summary>
    private bool ProbesUnchanged(Dictionary<string, (int Count, string MaxId)> probes)
    {
        if (probes.Count != _lastCompletedProbe.Count)
        {
            return false;
        }

        foreach (var (databasePath, probe) in probes)
        {
            if (!_lastCompletedProbe.TryGetValue(databasePath, out var known) || known != probe)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 查询语句与蓝本 loader.rs 完全同构：completed 过滤 + LEFT JOIN session 取项目目录，
    /// 可选列按 schema 拼接（缺失列以常量占位，保持列位固定）。
    /// </summary>
    internal static string BuildQuery(ZCodeSchemaInspector.Schema schema)
    {
        var cacheCreation = schema.HasCacheCreation ? "m.cache_creation_input_tokens" : "0";
        var cacheRead = schema.HasCacheRead ? "m.cache_read_input_tokens" : "0";
        var computedTotal = schema.HasComputedTotal
            ? "m.computed_total_tokens"
            : "m.input_tokens + m.output_tokens";
        var providerId = schema.HasProviderId ? "m.provider_id" : "NULL";
        return
            $"SELECT m.id, m.session_id, m.started_at, m.model_id, m.input_tokens, m.output_tokens, "
            + $"{cacheCreation}, {cacheRead}, {computedTotal}, {providerId}, s.directory "
            + $"FROM model_usage m LEFT JOIN session s ON s.id = m.session_id "
            + $"WHERE m.status = 'completed'";
    }

    /// <summary>
    /// 行读取；列序与 <see cref="BuildQuery"/> 固定对应。身份列（id / session_id / model_id）
    /// 为 NULL 或类型异常时返回 null（静默跳过，蓝本 read::&lt;String&gt;.ok()? 语义）；
    /// token / 时间列按蓝本容错转换永不失败；文本元数据列（provider / directory）类型异常按缺失处理。
    /// </summary>
    private static ZCodeUsageRow? ReadRow(SqliteDataReader reader)
    {
        var id = TryReadText(reader, 0);
        var sessionId = TryReadText(reader, 1);
        var modelId = TryReadText(reader, 3);
        if (id is null || sessionId is null || modelId is null)
        {
            return null;
        }

        if (!TryReadTimestampMs(reader, 2, out var startedAt))
        {
            return null;
        }

        return new ZCodeUsageRow(
            Id: id,
            SessionId: sessionId,
            StartedAt: startedAt,
            ModelId: modelId,
            ProviderId: TryReadOptionalText(reader, 9),
            InputTokens: ReadTokenColumn(reader, 4),
            OutputTokens: ReadTokenColumn(reader, 5),
            CacheCreationInputTokens: ReadTokenColumn(reader, 6),
            CacheReadInputTokens: ReadTokenColumn(reader, 7),
            ComputedTotalTokens: ReadTokenColumn(reader, 8),
            Directory: TryReadOptionalText(reader, 10));
    }

    /// <summary>身份列：NULL 或类型异常视为整行跳过。</summary>
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

    /// <summary>元数据列：NULL 或类型异常按"缺失"处理，行保留（蓝本 read::&lt;Option&lt;String&gt;&gt;.ok().flatten()）。</summary>
    private static string? TryReadOptionalText(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : TryReadText(reader, ordinal);
    }

    /// <summary>
    /// token 列（蓝本 read_token_column）：i64 负值钳 0 → f64 有限正数四舍五入（钳 long 上限）→
    /// 其余（NULL / 非数值文本）按 0，永不抛出、行保留。
    /// </summary>
    private static long ReadTokenColumn(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return 0;
        }

        try
        {
            return Math.Max(0, reader.GetInt64(ordinal));
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException)
        {
            try
            {
                var value = reader.GetDouble(ordinal);
                if (!double.IsFinite(value) || value <= 0)
                {
                    return 0;
                }

                // double 域钳到 long 可表示范围（2^63 恰为 double 可精确表示值，直接 cast 会溢出回绕）。
                if (value >= MaxRepresentableDoubleForLong)
                {
                    return long.MaxValue;
                }

                var rounded = (long)Math.Round(value);
                return rounded < 0 ? 0 : rounded;
            }
            catch (Exception fallbackException) when (fallbackException is FormatException or InvalidCastException)
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// 时间列（蓝本 read_timestamp_ms）：i64 直读 → f64 有限且在 i64 域内四舍五入 → 其余失败
    /// （返回 false，整行跳过）。
    /// </summary>
    private static bool TryReadTimestampMs(SqliteDataReader reader, int ordinal, out long value)
    {
        value = 0;
        if (!reader.IsDBNull(ordinal))
        {
            try
            {
                value = reader.GetInt64(ordinal);
                return true;
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException)
            {
                try
                {
                    var doubleValue = reader.GetDouble(ordinal);
                    if (double.IsFinite(doubleValue)
                        && doubleValue >= long.MinValue
                        && doubleValue < MaxRepresentableDoubleForLong)
                    {
                        value = (long)Math.Round(doubleValue);
                        return true;
                    }
                }
                catch (Exception fallbackException) when (fallbackException is FormatException or InvalidCastException)
                {
                    // 落入最外层 return false：类型完全不可读，整行跳过。
                }
            }
        }

        return false;
    }
}
