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
/// 首次同步先把远端全量采纳）→ 推本地变更 → 落盘 → 返回 applied keys 供上层热应用。
/// 只在 "用量同步已启用 + 设置同步开关打开" 时由 SyncStateService 调用；关 = 零出站（S1）。
/// 服务端时间戳是唯一新旧的判据（S3）；本机账面 settings_sync_state 记录上次对齐值。
/// </summary>
public sealed class SettingsSyncRunner(
    AppSettings settings,
    ISettingsSyncStateStore stateStore,
    IAppSettingsStore settingsStore,
    Func<ISyncBackend?> backendProvider)
{
    /// <summary>跑一轮；返回本轮从远端写回 AppSettings 的 key（空 = 无变化/未启用）。异常向上抛，退避归调用方。</summary>
    public async Task<IReadOnlyList<string>> RunOnceAsync(CancellationToken ct = default)
    {
        var backend = backendProvider();
        if (backend is null || !settings.SettingsSyncEnabled)
        {
            return Array.Empty<string>();
        }

        var stateRows = await stateStore.GetSettingsSyncStateAsync(ct);
        var state = stateRows.ToDictionary(row => row.Key, row => row, StringComparer.Ordinal);
        var firstSync = state.Count == 0;

        // 本地未推变更集（首同步视为空：先采纳远端，远端没有的 key 随后全量推出）。
        var localChanged = new HashSet<string>(StringComparer.Ordinal);
        if (!firstSync)
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

        foreach (var remote in await backend.GetSettingsAsync(ct))
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

        return applied;
    }
}
