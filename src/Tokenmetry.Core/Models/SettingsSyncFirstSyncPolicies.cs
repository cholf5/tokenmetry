namespace Tokenmetry.Core.Models;

/// <summary>
/// 首次设置同步遇到「远端与本机不同」时的用户裁决（settings-sync 审查 #3）。
/// 默认 <see cref="Unresolved"/>：runner 不应用、不推送，设置页给出三选一
/// （采用远端 / 保留本机 / 本机配置不同步 = 关闭「同步应用设置」）。
/// 只存本机、不参与同步；换账号（设置同步账面清空）时由 SyncStateService 复位。
/// </summary>
public static class SettingsSyncFirstSyncPolicies
{
    /// <summary>尚未裁决：runner 发现远端与本机不同即停下等用户选择。</summary>
    public const string Unresolved = "unresolved";

    /// <summary>采用远端：把云端值写回本机（覆盖本机偏好）。</summary>
    public const string UseRemote = "useRemote";

    /// <summary>保留本机：不采纳远端，把本机值推到云端（覆盖远端）。</summary>
    public const string UseLocal = "useLocal";
}
