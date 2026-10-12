using System.Text.Json;
using CCUsage.Infrastructure.Jsonl;
using CCUsage.Infrastructure.JsonSource;
using CCUsage.Infrastructure.SqliteSource;

namespace CCUsage.Infrastructure.PiAgent;

/// <summary>pi.stores[] 命名存储条目（蓝本 ccusage-cli NamedPiStore）。</summary>
public sealed record PiNamedStore(string Name, string Path);

/// <summary>
/// pi-agent 数据目录定位（蓝本 ../ccusage/rust/adapters/pi/src/paths.rs 逐条移植，I-019；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// - 会话目录（蓝本 paths）：configuredDataDir（测试/调用方钉定，仓库共有增强，非蓝本）→
///   PI_AGENT_DIR 环境变量（<b>trim 后非空</b>才生效——与 Kimi/Amp/Codebuff 的「已设置即生效」不同，
///   pi 蓝本对空串**回退默认目录**；逗号分隔、去空白、丢空段、仅保留实际存在的目录、按序去重；
///   <b>不做 ~ 展开</b>——蓝本注释明确保留既有语义）→ ~/.pi/agent/sessions（存在才保留）；
/// - 命名存储路径（蓝本 named_store_paths）：逗号分隔清单，**做 ~ 展开**（expand_home_path：恰为
///   "~" 或 "~/前缀" 两种形态），其余与会话目录清单同规则；
/// - ccusage.json 的 pi.stores[]（蓝本 crates/ccusage-config parse_named_pi_stores 移植）：
///   配置文件发现顺序 CLAUDE_CONFIG_DIR 目录（逗号分隔）→ ~/.config/claude/ccusage.json →
///   ~/.claude/ccusage.json（首个可解析为 JSON 对象者生效；蓝本另扫 CWD/.ccusage/，桌面无 CLI 语义，
///   见卡片偏差⑦）；条目校验 name 模式 ^[a-z][a-z0-9_-]{0,31}$、保留名（all + 内置 agent 名）、
///   path 非空、name 去重；<b>任一校验失败整组丢弃</b>（蓝本命令报错终止，桌面降级为仅默认存储，
///   见卡片偏差⑥）。
/// </summary>
public class PiLogLocator
{
    /// <summary>pi 数据目录环境变量（蓝本 paths.rs PI_AGENT_DIR_ENV）。</summary>
    public const string PiAgentDirEnvVariable = "PI_AGENT_DIR";

    /// <summary>默认会话目录（蓝本 home.join(".pi/agent/sessions")）。</summary>
    public const string DefaultSessionsRelativePath = ".pi/agent/sessions";

    /// <summary>存储名模式（蓝本 config_schema NAMED_PI_STORE_NAME_PATTERN）。</summary>
    public const string NamedStoreNamePattern = "^[a-z][a-z0-9_-]{0,31}$";

    /// <summary>保留存储名（蓝本 reserved_named_pi_store_names："all" + 内置 agent 名）。</summary>
    public static readonly string[] ReservedStoreNames =
    [
        "all", "claude", "codex", "opencode", "amp", "droid", "codebuff", "hermes", "pi",
        "goose", "openclaw", "kilo", "copilot", "gemini", "antigravity", "kimi", "qwen", "grok", "zcode",
    ];

    private readonly Func<string, string?> _environmentAccessor;

    public PiLogLocator(Func<string, string?>? environmentAccessor = null)
    {
        _environmentAccessor = environmentAccessor ?? (name => Environment.GetEnvironmentVariable(name));
    }

    /// <summary>
    /// 解析默认作用域会话目录列表（蓝本 paths）：configuredDataDir 非空（trim 后）即生效（不做 ~ 展开，
    /// 仅保留存在目录）；否则 PI_AGENT_DIR 已设置且 trim 后非空才生效（同样不 ~ 展开）；否则
    /// ~/.pi/agent/sessions（存在才保留）。
    /// </summary>
    public IReadOnlyList<string> ResolveSessionDirectories(string? configuredDataDir)
    {
        if (!string.IsNullOrWhiteSpace(configuredDataDir))
        {
            return ExistingPathList(configuredDataDir, expandHome: false);
        }

        var fromEnvironment = _environmentAccessor(PiAgentDirEnvVariable);
        if (fromEnvironment is not null && !string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return ExistingPathList(fromEnvironment, expandHome: false);
        }

        var home = JsonlHomePath.GetHomeDir(_environmentAccessor);
        if (home is null)
        {
            return [];
        }

        var defaultDirectory = Path.Combine(home, ".pi", "agent", "sessions");
        return Directory.Exists(defaultDirectory) ? [defaultDirectory] : [];
    }

    /// <summary>解析命名存储路径清单（蓝本 named_store_paths）：逗号分隔、去空白、丢空段、~ 展开、
    /// 仅保留实际存在的目录、按序去重。</summary>
    public IReadOnlyList<string> ResolveNamedStoreDirectories(string raw)
    {
        return ExistingPathList(raw, expandHome: true);
    }

    /// <summary>
    /// 读取 ccusage.json 的 pi.stores[]（蓝本 parse_named_pi_stores）：数组条目须含字符串 name/path、
    /// path 非空、name 合法且未保留、name 全局唯一；任一失败返回空（蓝本命令报错，桌面降级见偏差⑥）。
    /// </summary>
    public IReadOnlyList<PiNamedStore> LoadNamedStores()
    {
        foreach (var configPath in DiscoverConfigPaths())
        {
            string content;
            try
            {
                if (!File.Exists(configPath))
                {
                    continue;
                }

                content = File.ReadAllText(configPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(content);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                return ParseNamedStores(document.RootElement);
            }
        }

        return [];
    }

    /// <summary>解析单份配置的 pi.stores[]；结构/校验失败返回空列表（偏差⑥：蓝本报错、桌面丢弃）。</summary>
    public static List<PiNamedStore> ParseNamedStores(JsonElement root)
    {
        if (LenientJson.GetObject(root, "pi") is not { } pi
            || !pi.TryGetProperty("stores", out var storesValue)
            || storesValue.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var parsed = new List<PiNamedStore>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var store in storesValue.EnumerateArray())
        {
            if (store.ValueKind != JsonValueKind.Object
                || LenientJson.GetNonEmptyString(store, "name") is not { } name
                || LenientJson.GetNonEmptyString(store, "path") is not { } path
                || string.IsNullOrWhiteSpace(path))
            {
                return [];
            }

            if (!IsValidStoreName(name) || ReservedStoreNames.Contains(name) || !names.Add(name))
            {
                return [];
            }

            parsed.Add(new PiNamedStore(name, path));
        }

        return parsed;
    }

    private static bool IsValidStoreName(string name)
    {
        if (name.Length == 0 || name.Length > 32 || name[0] is < 'a' or > 'z')
        {
            return false;
        }

        foreach (var character in name[1..])
        {
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>配置文件发现（蓝本 discover_config_paths 去 CWD 变体，偏差⑦）：CLAUDE_CONFIG_DIR
    /// 目录（逗号分隔）→ ~/.config/claude → ~/.claude，逐个 &lt;dir&gt;/ccusage.json。</summary>
    private IEnumerable<string> DiscoverConfigPaths()
    {
        var claudeConfigDir = _environmentAccessor("CLAUDE_CONFIG_DIR");
        if (claudeConfigDir is not null)
        {
            foreach (var segment in claudeConfigDir.Split(','))
            {
                var trimmed = segment.Trim();
                if (trimmed.Length > 0)
                {
                    yield return Path.Combine(trimmed, "ccusage.json");
                }
            }

            yield break;
        }

        var home = JsonlHomePath.GetHomeDir(_environmentAccessor);
        if (home is null)
        {
            yield break;
        }

        yield return Path.Combine(home, ".config", "claude", "ccusage.json");
        yield return Path.Combine(home, ".claude", "ccusage.json");
    }

    /// <summary>蓝本 existing_paths：逗号分隔、去空白、丢空段、仅保留存在目录、按序去重
    /// （路径不做规范化，文本相等即重复）。</summary>
    private List<string> ExistingPathList(string raw, bool expandHome)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in raw.Split(','))
        {
            var trimmed = segment.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var path = expandHome ? JsonlHomePath.ExpandHomePath(trimmed, _environmentAccessor) : trimmed;
            if (Directory.Exists(path) && seen.Add(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }
}
