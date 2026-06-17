# 审查范围

**关联计划**: .docs/plans/2026-06-17-ccusage-gui-implementation.md
**开始时间**: 2026-06-17 09:47:01 +08:00

## 预期修改文件

- rust/crates/ccusage/src/lib.rs
- rust/crates/ccusage/src/main.rs
- rust/crates/ccusage/src/gui.rs
- rust/crates/ccusage/src/gui/model.rs
- rust/crates/ccusage/src/gui/service.rs
- rust/crates/ccusage/src/gui/adapter.rs
- rust/crates/ccusage/src/adapter/claude/mod.rs
- rust/crates/ccusage/src/adapter/codex/loader.rs
- apps/ccusage-gui/package.json
- apps/ccusage-gui/index.html
- apps/ccusage-gui/README.md
- apps/ccusage-gui/src/main.tsx
- apps/ccusage-gui/src/App.tsx
- apps/ccusage-gui/src/Dashboard.tsx
- apps/ccusage-gui/src/Breakdown.tsx
- apps/ccusage-gui/src/Reports.tsx
- apps/ccusage-gui/src/Settings.tsx
- apps/ccusage-gui/src/EmptyState.tsx
- apps/ccusage-gui/src/styles.css
- apps/ccusage-gui/src/api.ts
- apps/ccusage-gui/src/types.ts
- apps/ccusage-gui/src/app-title.ts
- apps/ccusage-gui/src/App.test.ts
- apps/ccusage-gui/src/dashboard-format.ts
- apps/ccusage-gui/src/dashboard-view.test.ts
- apps/ccusage-gui/src/settings-format.ts
- apps/ccusage-gui/src/breakdown-format.ts
- apps/ccusage-gui/src/breakdown-format.test.ts
- apps/ccusage-gui/src/empty-state-format.ts
- apps/ccusage-gui/src/empty-state-format.test.ts
- apps/ccusage-gui/src-tauri/Cargo.toml
- apps/ccusage-gui/src-tauri/tauri.conf.json
- apps/ccusage-gui/src-tauri/src/main.rs
- apps/ccusage-gui/src-tauri/src/lib.rs
- apps/ccusage-gui/src-tauri/src/dashboard.rs
- apps/ccusage-gui/src-tauri/src/cache.rs
- apps/ccusage-gui/src-tauri/src/anomaly.rs
- apps/ccusage-gui/src-tauri/src/refresh.rs
- apps/ccusage-gui/src-tauri/src/tray.rs
- apps/ccusage-gui/src-tauri/src/notification.rs
- apps/ccusage-gui/src-tauri/src/settings.rs
- .docs/plans/2026-06-17-ccusage-gui-design.md
- .docs/plans/2026-06-17-ccusage-gui-implementation.md
- .docs/plans/2026-06-17-ccusage-gui-review-scope.md

## 设计文档

- .docs/plans/2026-06-17-ccusage-gui-design.md
