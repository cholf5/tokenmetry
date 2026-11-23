using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Sync;

namespace Tokenmetry.App.Services;

/// <summary>
/// 一轮应用设置同步（settings-sync 设计 §5.2）：拉远端 → 应用（本地未推变更的 key 不被旧远端覆盖；
/// 首次同步按用户裁决采纳远端或覆盖远端）→ 推本地变更 → 落盘 → 返回 applied keys 供上层热应用。
/// 只在 "用量同步已启用 + 设置同步开关打开" 时由 SyncStateService 调用；关 = 零出站（S1）。
/// 服务端时间戳是唯一新旧的判据（S3）；本机账面 settings_sync_state 记录上次对齐值。
/// </summary>
public sealed class SettingsSyncRunner(
    AppSettings settings,
    ISettingsSyncStateStore stateStore,
    IAppSettingsStore settingsStore,
    Func<ISyncBackend?> backendProvider)
{
    /// <summary>
    /// 跑一轮；返回本轮从远端写回 AppSettings 的 key，以及「首同步冲突」待用户裁决的 key。
    /// 冲突时本轮不应用、不推送（避免任何一方被静默覆盖）。异常向上抛，退避归调用方。
    /// </summary>
    public async Task<SettingsSyncRunOutcome> RunOnceAsync(CancellationToken ct = default)
    {
        var backend = backendProvider();
        if (backend is null || !settings.SettingsSyncEnabled)
        {
            return SettingsSyncRunOutcome.None;
        }

        var stateRows = await stateStore.GetSettingsSyncStateAsync(ct);
        var state = stateRows.ToDictionary(row => row.Key, row => row, StringComparer.Ordinal);
        var firstSync = state.Count == 0;
        // 手改/旧版 settings.json 的未知裁决值一律按「未裁决」处理——不能因为坏值悄悄采用远端。
        var policy = settings.SettingsSyncFirstSyncPolicy is SettingsSyncFirstSyncPolicies.UseRemote
            or SettingsSyncFirstSyncPolicies.UseLocal
            ? settings.SettingsSyncFirstSyncPolicy
            : SettingsSyncFirstSyncPolicies.Unresolved;
        var remoteRows = await backend.GetSettingsAsync(ct);

        // 中继删库重建（同凭据）后远端全空：账面非空说明本机同步过，这不是「无冲突的首同步」，
        // 而是新桶——清账面按首同步全量推本机值，否则所有 key 都与账面相等、什么都推不出去。
        if (!firstSync && remoteRows.Count == 0)
        {
            await stateStore.ClearSettingsSyncStateAsync(ct);
            state.Clear();
            firstSync = true;
        }

        // 首同步冲突（审查 #3）：远端有值且与本机不同、用户尚未裁决——停下等设置页三选一，
        // 不应用也不推送，任何一方都不会被静默覆盖。
        if (firstSync && policy == SettingsSyncFirstSyncPolicies.Unresolved)
        {
            var conflictKeys = new List<string>();
            foreach (var remote in remoteRows)
            {
                var local = SettingsSyncKeys.All.Contains(remote.Key)
                    ? SettingsSyncKeys.Read(settings, remote.Key)
                    : null;
                if (local is not null && !string.Equals(local, remote.Value, StringComparison.Ordinal))
                {
                    conflictKeys.Add(remote.Key);
                }
            }

            if (conflictKeys.Count > 0)
            {
                return new SettingsSyncRunOutcome(Array.Empty<string>(), conflictKeys);
            }
        }

        // 本地未推变更集（首同步视为空：先采纳远端，远端没有的 key 随后全量推出；
        // 用户裁决「保留本机」时，首同步的全部本机 key 都算未推变更，远端值一律不采纳）。
        var localChanged = new HashSet<string>(StringComparer.Ordinal);
        if (firstSync)
        {
            if (policy == SettingsSyncFirstSyncPolicies.UseLocal)
            {
                foreach (var key in SettingsSyncKeys.All)
                {
                    if (SettingsSyncKeys.Read(settings, key) is not null)
                    {
                        localChanged.Add(key);
                    }
                }
            }
        }
        else
        {
            foreach (var key in SettingsSyncKeys.All)
            {
                var current = SettingsSyncKeys.Read(settings, key);
                if (current is null)
                {
                    continue;
                }

                if (!state.TryGetValue(key, out var synced) || !string.Equals(current, synced.Value, StringComparison.Ordinal))
                {
                    localChanged.Add(key);
                }
            }
        }

        var applied = new List<string>();
        var touched = new List<SyncedSettingState>();

        foreach (var remote in remoteRows)
        {
            if (!SettingsSyncKeys.All.Contains(remote.Key) || localChanged.Contains(remote.Key))
            {
                continue;
            }

            var current = SettingsSyncKeys.Read(settings, remote.Key);
            if (current is null)
            {
                continue;
            }

            // 已见过的版本：只刷新账面时间戳，不重复应用。
            if (state.TryGetValue(remote.Key, out var synced) && remote.UpdatedAt <= synced.SyncedAt)
            {
                continue;
            }

            if (string.Equals(current, remote.Value, StringComparison.Ordinal))
            {
                touched.Add(new SyncedSettingState(remote.Key, remote.Value, remote.UpdatedAt));
            }
            else if (SettingsSyncKeys.TryWrite(settings, remote.Key, remote.Value))
            {
                applied.Add(remote.Key);
                touched.Add(new SyncedSettingState(remote.Key, remote.Value, remote.UpdatedAt));
            }
        }

        // 先落盘、后记账（审查 #2）：账面若领先于磁盘（落盘失败/进程在两步之间中断），重启后
        // 本机旧值会被当成「本地未推变更」反推上去，覆盖更新的远端。反过来（磁盘新、账面旧）
        // 只是幂等重推一次，服务端 LWW 消化。推失败也保留已落盘的采纳值。
        if (applied.Count > 0)
        {
            await settingsStore.SaveAsync(settings, ct);
        }

        foreach (var row in touched)
        {
            state[row.Key] = row;
        }

        if (touched.Count > 0)
        {
            await stateStore.UpsertSettingsSyncStateAsync(touched, ct);
        }

        // 推本地变更（在应用远端之后重算：被采纳的 key 不再回推）。
        var push = new List<SyncedSettingUpdate>();
        foreach (var key in SettingsSyncKeys.All)
        {
            var current = SettingsSyncKeys.Read(settings, key);
            if (current is null)
            {
                continue;
            }

            if (!state.TryGetValue(key, out var synced) || !string.Equals(current, synced.Value, StringComparison.Ordinal))
            {
                push.Add(new SyncedSettingUpdate(key, current));
            }
        }

        if (push.Count > 0)
        {
            var written = await backend.PutSettingsAsync(push, ct);
            await stateStore.UpsertSettingsSyncStateAsync(
                written.Select(row => new SyncedSettingState(row.Key, row.Value, row.UpdatedAt)).ToList(), ct);
        }

        return new SettingsSyncRunOutcome(applied, Array.Empty<string>());
    }
}

/// <summary>
/// 一轮设置同步的结果：<paramref name="Applied"/> = 本轮从远端写回 AppSettings 的 key（供热应用）；
/// <paramref name="ConflictKeys"/> 非空 = 首同步需要用户裁决，本轮未应用也未推送。
/// </summary>
public sealed record SettingsSyncRunOutcome(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> ConflictKeys)
{
    public static SettingsSyncRunOutcome None { get; } = new(Array.Empty<string>(), Array.Empty<string>());

    public bool HasConflict => ConflictKeys.Count > 0;
}
