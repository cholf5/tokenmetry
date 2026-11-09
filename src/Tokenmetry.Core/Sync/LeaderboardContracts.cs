namespace Tokenmetry.Core.Sync;

/// <summary>
/// 排行榜线协议契约（leaderboard 设计 §4.3）：客户端与中继共享的线形状——
/// 偏好读写（既有 Bearer 中间件）与公开榜单读（匿名 CORS 端点）。
/// </summary>

/// <summary>偏好提交（PUT /api/me/leaderboard）：displayName trim 后 ≤64 字符，空串/缺省 = null（用默认名）。</summary>
public sealed record LeaderboardPreferenceRequest(bool OptIn, string? DisplayName);

/// <summary>偏好回读（GET /api/me/leaderboard）：displayName = 生效名（自定义 ?? 默认）。</summary>
public sealed record LeaderboardPreferenceResponse(bool OptIn, string? DisplayName);

/// <summary>榜单单行：rank = 行号；avatarUrl 可为 null（前端渲染首字母占位）。</summary>
public sealed record LeaderboardEntry(int Rank, string DisplayName, string? AvatarUrl, long TotalTokens, decimal TotalCostUsd);

/// <summary>公开榜单读响应（GET /api/leaderboard）：month = YYYY-MM（UTC），board = cost|tokens。</summary>
public sealed record LeaderboardResponse(string Month, string Board, int Participants, IReadOnlyList<LeaderboardEntry> Entries);
