using System.Text.Json;
using Tokenmetry.Core.Models;

namespace Tokenmetry.Infrastructure.ClaudeCodeJsonl;

/// <summary>
/// Claude Code 转录的"响应链"跟踪（移植自 ccusage 20.1.0 #2341 claude response_chain）。
/// 一次 API 响应会被写成多行 assistant 记录——每个 content block（thinking / text / tool_use…）
/// 一行，共享 message.id 与同一份 usage，但 uuid、parentUuid、时间戳不同。只按 message.id 去重会
/// 误折叠网关复用占位 id 的不同响应；只按时间戳则会把同一次响应的多 block 写入反复记账。
/// 这里的判据是转录自身给出的证据：
/// <list type="bullet">
/// <item>行的 uuid/parentUuid 构成父子链；</item>
/// <item>user 的 tool_result 行只有在其引用的全部 tool_use 都属于同一响应时才是链的一环，
/// 普通用户消息与混合内容块行不建链（链在轮次边界自然断开）；</item>
/// <item>usage 行沿父链回溯到已有响应，且 message.id + input + cache_creation + cache_read
/// 签名一致时并入该响应，否则开启新响应。</item>
/// </list>
/// 拓扑按 (sessionId, id) 记账：同一文件里混入其它会话的复制行时不会跨会话串链。
/// 实例按文件创建、不跨文件共享（与上游每份转录一份状态一致）。
/// </summary>
internal sealed class ClaudeResponseChainTracker
{
    private readonly string _fileSessionId;
    private readonly Dictionary<(string Session, string Id), Node> _nodes = new();
    private readonly Dictionary<(string Session, string ToolUseId), int> _toolUseOwners = new();
    private readonly List<ResponseSignature> _responses = new();

    public ClaudeResponseChainTracker(string fileSessionId) => _fileSessionId = fileSessionId;

    /// <summary>
    /// 处理一条 usage 行：返回它所属响应的链索引；无法证明链归属（缺 uuid / message id 等）时返回 null，
    /// 该记录退回按消息 id + 时间戳的旧口径去重。
    /// </summary>
    public int? TrackUsage(JsonElement line, UsageRecord record)
    {
        if (record.MessageId is not { } messageId || line.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var uuid = GetString(line, "uuid");
        if (uuid is null)
        {
            return null;
        }

        var session = record.SessionId;
        var signature = new ResponseSignature(
            messageId,
            record.InputTokens,
            record.CacheCreationTokens,
            record.CacheReadTokens);

        var index = FindChainedResponse(session, GetString(line, "parentUuid"), signature)
                    ?? CreateResponse(signature);

        foreach (var toolUseId in EnumerateToolUseIds(line))
        {
            _toolUseOwners[(session, toolUseId)] = index;
        }

        _nodes[(session, uuid)] = Node.Block(index);
        return index;
    }

    /// <summary>
    /// 处理一条不携带 usage 的转录行：把 uuid → parentUuid 记成链上的 Link 节点，
    /// 让后续 usage 行能沿链回溯到所属响应。普通用户消息与混合内容块行不建链。
    /// </summary>
    public void TrackLink(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object || HasUsage(line))
        {
            return;
        }

        var uuid = GetString(line, "uuid");
        var parentUuid = GetString(line, "parentUuid");
        var type = GetString(line, "type");
        if (uuid is null || parentUuid is null || type is null)
        {
            return;
        }

        var session = GetString(line, "sessionId") ?? _fileSessionId;
        if (!_nodes.ContainsKey((session, parentUuid)))
        {
            // 父节点不在链上（新轮次、更早的历史行、或与 usage 行无关的记录）：不建链。
            return;
        }

        int? owner = null;
        if (type == "user")
        {
            var answers = GetToolResultAnswers(line);
            if (answers is null)
            {
                // 普通用户输入（或其 content 不是纯 tool_result）：链在轮次边界断开。
                return;
            }

            var parentResponse = FindResponseOf(session, parentUuid);
            if (parentResponse is null)
            {
                return;
            }

            foreach (var answer in answers)
            {
                if (!_toolUseOwners.TryGetValue((session, answer), out var toolOwner) || toolOwner != parentResponse)
                {
                    return;
                }
            }

            owner = parentResponse;
        }

        _nodes[(session, uuid)] = new Node(ParentUuid: parentUuid, Owner: owner, ResponseIndex: null);
    }

    private int CreateResponse(ResponseSignature signature)
    {
        _responses.Add(signature);
        return _responses.Count - 1;
    }

    /// <summary>
    /// 从 parentUuid 起沿 Link 链回溯：到达 Block 即候选响应；途中经过的 tool_result 归属
    /// 必须全部等于候选响应，且签名一致，才算同一次响应。
    /// </summary>
    private int? FindChainedResponse(string session, string? parentUuid, ResponseSignature signature)
    {
        if (parentUuid is null)
        {
            return null;
        }

        var owners = new List<int>();
        var current = parentUuid;
        var steps = 0;
        int? candidate = null;

        while (steps++ <= _nodes.Count)
        {
            if (!_nodes.TryGetValue((session, current), out var node))
            {
                break;
            }

            if (node.ResponseIndex is { } index)
            {
                candidate = index;
                break;
            }

            if (node.Owner is { } owner)
            {
                owners.Add(owner);
            }

            if (node.ParentUuid is null)
            {
                break;
            }

            current = node.ParentUuid;
        }

        if (candidate is not { } found)
        {
            return null;
        }

        foreach (var owner in owners)
        {
            if (owner != found)
            {
                return null;
            }
        }

        return _responses[found] == signature ? found : null;
    }

    /// <summary>沿 Link 链找到所属 Block 响应（用于 user tool_result 行的归属校验）。</summary>
    private int? FindResponseOf(string session, string parentUuid)
    {
        var current = parentUuid;
        var steps = 0;
        while (steps++ <= _nodes.Count)
        {
            if (!_nodes.TryGetValue((session, current), out var node))
            {
                return null;
            }

            if (node.ResponseIndex is { } index)
            {
                return index;
            }

            if (node.ParentUuid is null)
            {
                return null;
            }

            current = node.ParentUuid;
        }

        return null;
    }

    private static bool HasUsage(JsonElement line)
        => line.TryGetProperty("message", out var message)
           && message.ValueKind == JsonValueKind.Object
           && message.TryGetProperty("usage", out var usage)
           && usage.ValueKind != JsonValueKind.Null;

    /// <summary>
    /// 读取 user 行的 tool_result 引用：content 必须是非空数组，且每个块都是带字符串 tool_use_id
    /// 的 tool_result；否则返回 null（上游同口径——普通用户消息是轮次边界，不能当链节点）。
    /// </summary>
    private static List<string>? GetToolResultAnswers(JsonElement line)
    {
        if (!line.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var length = content.GetArrayLength();
        if (length == 0)
        {
            return null;
        }

        var answers = new List<string>(length);
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object
                || GetString(block, "type") != "tool_result"
                || GetString(block, "tool_use_id") is not { } toolUseId)
            {
                return null;
            }

            answers.Add(toolUseId);
        }

        return answers;
    }

    private static IEnumerable<string> EnumerateToolUseIds(JsonElement line)
    {
        if (!line.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && GetString(block, "type") == "tool_use"
                && GetString(block, "id") is { } toolUseId)
            {
                yield return toolUseId;
            }
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>链节点：要么是一次响应的一个 content block（ResponseIndex 非空），要么是普通链接行。</summary>
    private readonly record struct Node(string? ParentUuid, int? Owner, int? ResponseIndex)
    {
        public static Node Block(int index) => new(ParentUuid: null, Owner: null, ResponseIndex: index);
    }

    /// <summary>上游 signature：message id + input + cache_creation 总量 + cache_read（output 不参与）。</summary>
    private readonly record struct ResponseSignature(
        string MessageId,
        long InputTokens,
        long CacheCreationTokens,
        long CacheReadTokens);
}
