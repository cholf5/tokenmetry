# GUI 版 ccusage 实施计划

> **给 Claude：** 必需的子技能：使用 com-executing-plans 逐任务实施此计划。

**目标：** 构建一个开源、本地优先、常驻提醒优先的 GUI 版个人 AI 编程成本监控器，MVP 支持 Claude Code + Codex。

**架构：** 在现有 monorepo 中新增 `apps/ccusage-gui` Tauri 2 桌面应用，并先把 `rust/crates/ccusage` 的核心统计能力抽成可复用 library API。GUI 后端通过 Rust adapter 调用 ccusage core，默认 offline pricing，使用 SQLite 缓存聚合数据，前端展示 Dashboard/Breakdown/Reports/Settings，托盘和通知由 Tauri 后端负责。

**技术栈：** Tauri 2、React、TypeScript、Vite、Rust、serde、SQLite、Tauri notification plugin、Tauri tray/menu API、pnpm、cargo test、Node test runner。

---

## 重要前置约束

- 这个计划假设在专用工作树中执行。
- 该 GUI 是独立产品，放在 `apps/ccusage-gui` 只是为了复用当前 repo 的 Rust crate 和 fixture；产品命名后续可独立。
- 不要实现 SaaS、账号、云同步、团队、多 agent 全覆盖。
- MVP 只承诺 Claude Code + Codex。
- 默认 offline pricing，禁止 GUI 主路径同步请求 LiteLLM 或 models.dev。
- 不要保存 prompt、原始消息、完整敏感路径到 GUI 缓存。
- 每个任务完成后运行对应测试。实施计划中不包含 `git add` 和 `git commit` 命令步骤。

---

### 任务 1：建立 ccusage core library 入口

**文件：**
- 创建：`rust/crates/ccusage/src/lib.rs`
- 修改：`rust/crates/ccusage/src/main.rs`
- 测试：`rust/crates/ccusage/src/lib.rs` 内单元测试

**步骤 1：编写失败的测试**

在 `rust/crates/ccusage/src/lib.rs` 创建最小 library 入口和测试，先写测试引用即将暴露的 API：

```rust
pub mod gui;

#[cfg(test)]
mod tests {
    use super::gui::{GuiAgent, GuiReportRequest};

    #[test]
    fn gui_request_defaults_to_offline_pricing() {
        let request = GuiReportRequest::default();
        assert!(request.offline);
        assert_eq!(request.agents, vec![GuiAgent::Claude, GuiAgent::Codex]);
    }
}
```

同时创建空模块文件 `rust/crates/ccusage/src/gui.rs`：

```rust
// Temporary stub; task 2 fills this module.
```

**步骤 2：运行测试以验证它失败**

运行：

```bash
cargo test -p ccusage gui_request_defaults_to_offline_pricing
```

预期：FAIL，错误类似：

```text
unresolved imports super::gui::GuiAgent, super::gui::GuiReportRequest
```

**步骤 3：编写最少实现**

在 `rust/crates/ccusage/src/gui.rs` 添加：

```rust
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum GuiAgent {
    Claude,
    Codex,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct GuiReportRequest {
    pub agents: Vec<GuiAgent>,
    pub offline: bool,
}

impl Default for GuiReportRequest {
    fn default() -> Self {
        Self {
            agents: vec![GuiAgent::Claude, GuiAgent::Codex],
            offline: true,
        }
    }
}
```

**步骤 4：运行测试以验证它通过**

运行：

```bash
cargo test -p ccusage gui_request_defaults_to_offline_pricing
```

预期：PASS。

**步骤 5：确保 CLI 仍能编译**

运行：

```bash
cargo test -p ccusage --no-run
```

预期：编译通过。若 `main.rs` 与 `lib.rs` 出现模块重复问题，保持 `main.rs` 原有结构不动，先让 library 独立暴露 `gui` 模块；后续任务再逐步复用现有内部模块。

---

### 任务 2：定义 GUI 稳定数据模型

**文件：**
- 创建：`rust/crates/ccusage/src/gui/model.rs`
- 修改：`rust/crates/ccusage/src/gui.rs`
- 测试：`rust/crates/ccusage/src/gui/model.rs` 内单元测试

**步骤 1：编写失败的测试**

在 `rust/crates/ccusage/src/gui/model.rs` 写：

```rust
use serde_json::json;

#[test]
fn serializes_daily_usage_summary_as_camel_case() {
    let row = DailyUsageSummary {
        date: "2026-01-09".to_string(),
        total_cost_usd: 1.23,
        total_tokens: 456,
        input_tokens: 100,
        output_tokens: 50,
        cache_creation_tokens: 25,
        cache_read_tokens: 281,
        agents: vec![AgentBreakdown {
            agent: GuiAgent::Claude,
            cost_usd: 1.23,
            total_tokens: 456,
        }],
        models: vec![ModelBreakdown {
            model: "claude-sonnet-4-20250514".to_string(),
            cost_usd: 1.23,
            total_tokens: 456,
        }],
        projects: vec![ProjectBreakdown {
            project: "project-alpha".to_string(),
            display_name: "project-alpha".to_string(),
            cost_usd: 1.23,
            total_tokens: 456,
        }],
    };

    assert_eq!(
        serde_json::to_value(row).unwrap(),
        json!({
            "date": "2026-01-09",
            "totalCostUsd": 1.23,
            "totalTokens": 456,
            "inputTokens": 100,
            "outputTokens": 50,
            "cacheCreationTokens": 25,
            "cacheReadTokens": 281,
            "agents": [{"agent": "claude", "costUsd": 1.23, "totalTokens": 456}],
            "models": [{"model": "claude-sonnet-4-20250514", "costUsd": 1.23, "totalTokens": 456}],
            "projects": [{"project": "project-alpha", "displayName": "project-alpha", "costUsd": 1.23, "totalTokens": 456}]
        })
    );
}
```

**步骤 2：运行测试以验证它失败**

运行：

```bash
cargo test -p ccusage serializes_daily_usage_summary_as_camel_case
```

预期：FAIL，类型未定义。

**步骤 3：编写最少实现**

在 `rust/crates/ccusage/src/gui.rs` 改为：

```rust
mod model;

pub use model::{
    AgentBreakdown, DailyUsageSummary, GuiAgent, GuiReportRequest, ModelBreakdown,
    ProjectBreakdown,
};
```

在 `rust/crates/ccusage/src/gui/model.rs` 写：

```rust
use serde::Serialize;

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum GuiAgent {
    #[serde(rename = "claude")]
    Claude,
    #[serde(rename = "codex")]
    Codex,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct GuiReportRequest {
    pub agents: Vec<GuiAgent>,
    pub offline: bool,
}

impl Default for GuiReportRequest {
    fn default() -> Self {
        Self {
            agents: vec![GuiAgent::Claude, GuiAgent::Codex],
            offline: true,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DailyUsageSummary {
    pub date: String,
    pub total_cost_usd: f64,
    pub total_tokens: u64,
    pub input_tokens: u64,
    pub output_tokens: u64,
    pub cache_creation_tokens: u64,
    pub cache_read_tokens: u64,
    pub agents: Vec<AgentBreakdown>,
    pub models: Vec<ModelBreakdown>,
    pub projects: Vec<ProjectBreakdown>,
}

#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentBreakdown {
    pub agent: GuiAgent,
    pub cost_usd: f64,
    pub total_tokens: u64,
}

#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ModelBreakdown {
    pub model: String,
    pub cost_usd: f64,
    pub total_tokens: u64,
}

#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProjectBreakdown {
    pub project: String,
    pub display_name: String,
    pub cost_usd: f64,
    pub total_tokens: u64,
}
```

**步骤 4：运行测试以验证它通过**

运行：

```bash
cargo test -p ccusage serializes_daily_usage_summary_as_camel_case
```

预期：PASS。

**步骤 5：运行相关测试**

运行：

```bash
cargo test -p ccusage gui
```

预期：PASS。

---

### 任务 3：抽出 GUI report service 的 trait 和 fake 实现

**文件：**
- 创建：`rust/crates/ccusage/src/gui/service.rs`
- 修改：`rust/crates/ccusage/src/gui.rs`
- 测试：`rust/crates/ccusage/src/gui/service.rs` 内单元测试

**步骤 1：编写失败的测试**

在 `service.rs` 写：

```rust
#[test]
fn fake_service_returns_deterministic_dashboard_data() {
    let service = FakeGuiUsageService::new(vec![DailyUsageSummary {
        date: "2026-01-09".to_string(),
        total_cost_usd: 1.0,
        total_tokens: 100,
        input_tokens: 40,
        output_tokens: 20,
        cache_creation_tokens: 10,
        cache_read_tokens: 30,
        agents: vec![],
        models: vec![],
        projects: vec![],
    }]);

    let rows = service.load_daily(&GuiReportRequest::default()).unwrap();
    assert_eq!(rows.len(), 1);
    assert_eq!(rows[0].date, "2026-01-09");
}
```

**步骤 2：运行测试以验证它失败**

运行：

```bash
cargo test -p ccusage fake_service_returns_deterministic_dashboard_data
```

预期：FAIL，`FakeGuiUsageService` 未定义。

**步骤 3：编写最少实现**

在 `gui.rs` 导出：

```rust
mod service;

pub use service::{FakeGuiUsageService, GuiUsageService};
```

在 `service.rs` 添加：

```rust
use super::{DailyUsageSummary, GuiReportRequest};

pub type GuiResult<T> = Result<T, String>;

pub trait GuiUsageService {
    fn load_daily(&self, request: &GuiReportRequest) -> GuiResult<Vec<DailyUsageSummary>>;
}

pub struct FakeGuiUsageService {
    rows: Vec<DailyUsageSummary>,
}

impl FakeGuiUsageService {
    pub fn new(rows: Vec<DailyUsageSummary>) -> Self {
        Self { rows }
    }
}

impl GuiUsageService for FakeGuiUsageService {
    fn load_daily(&self, _request: &GuiReportRequest) -> GuiResult<Vec<DailyUsageSummary>> {
        Ok(self.rows.clone())
    }
}
```

**步骤 4：运行测试以验证它通过**

运行：

```bash
cargo test -p ccusage fake_service_returns_deterministic_dashboard_data
```

预期：PASS。

**步骤 5：运行 GUI 模块测试**

运行：

```bash
cargo test -p ccusage gui
```

预期：PASS。

---

### 任务 4：实现 Claude/Codex fixture 到 GUI daily summary 的 adapter 原型

**文件：**
- 创建：`rust/crates/ccusage/src/gui/adapter.rs`
- 修改：`rust/crates/ccusage/src/gui.rs`
- 可能修改：`rust/crates/ccusage/src/adapter/claude/mod.rs`
- 可能修改：`rust/crates/ccusage/src/adapter/codex/loader.rs`
- 测试：`rust/crates/ccusage/src/gui/adapter.rs` 内 fixture 测试

**步骤 1：编写失败的测试**

使用已有 fixture：

- `apps/ccusage/test/fixtures/claude/projects/project-alpha/session-alpha/chat.jsonl`
- `apps/ccusage/test/fixtures/codex/sessions/project-alpha/session-alpha.jsonl`

测试先只要求能从 fixture 聚合出日期和 agent breakdown，不要求完整 UI 字段完美：

```rust
#[test]
fn loads_claude_and_codex_fixture_daily_rows() {
    let root = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../..");
    let request = GuiReportRequest {
        offline: true,
        agents: vec![GuiAgent::Claude, GuiAgent::Codex],
    };
    let service = CcusageGuiUsageService::from_fixture_root(root);

    let rows = service.load_daily(&request).unwrap();

    assert!(rows.iter().any(|row| row.date == "2026-01-09"));
    assert!(rows.iter().any(|row| row.date == "2026-05-13"));
    assert!(rows.iter().flat_map(|row| &row.agents).any(|agent| agent.agent == GuiAgent::Claude));
    assert!(rows.iter().flat_map(|row| &row.agents).any(|agent| agent.agent == GuiAgent::Codex));
}
```

**步骤 2：运行测试以验证它失败**

运行：

```bash
cargo test -p ccusage loads_claude_and_codex_fixture_daily_rows
```

预期：FAIL，`CcusageGuiUsageService` 未定义。

**步骤 3：编写最少实现**

在 `gui.rs` 导出：

```rust
mod adapter;

pub use adapter::CcusageGuiUsageService;
```

在 `adapter.rs` 先实现 fixture-oriented 原型，允许后续替换为真实 path discovery：

```rust
use std::{collections::BTreeMap, fs, path::PathBuf};

use super::{AgentBreakdown, DailyUsageSummary, GuiAgent, GuiReportRequest, GuiResult, GuiUsageService};

pub struct CcusageGuiUsageService {
    fixture_root: Option<PathBuf>,
}

impl CcusageGuiUsageService {
    pub fn new() -> Self {
        Self { fixture_root: None }
    }

    #[cfg(test)]
    pub fn from_fixture_root(root: PathBuf) -> Self {
        Self { fixture_root: Some(root) }
    }
}

impl GuiUsageService for CcusageGuiUsageService {
    fn load_daily(&self, request: &GuiReportRequest) -> GuiResult<Vec<DailyUsageSummary>> {
        debug_assert!(request.offline, "GUI service must default to offline pricing");
        let Some(root) = &self.fixture_root else {
            return Ok(Vec::new());
        };

        let mut by_date: BTreeMap<String, DailyUsageSummary> = BTreeMap::new();
        if request.agents.contains(&GuiAgent::Claude) {
            load_fixture_agent(root, "claude", GuiAgent::Claude, &mut by_date)?;
        }
        if request.agents.contains(&GuiAgent::Codex) {
            load_fixture_agent(root, "codex", GuiAgent::Codex, &mut by_date)?;
        }
        Ok(by_date.into_values().collect())
    }
}

fn load_fixture_agent(
    root: &PathBuf,
    agent_name: &str,
    agent: GuiAgent,
    by_date: &mut BTreeMap<String, DailyUsageSummary>,
) -> GuiResult<()> {
    let base = root.join("apps/ccusage/test/fixtures").join(agent_name);
    if !base.exists() {
        return Ok(());
    }
    let mut stack = vec![base];
    while let Some(path) = stack.pop() {
        for entry in fs::read_dir(&path).map_err(|error| error.to_string())? {
            let entry = entry.map_err(|error| error.to_string())?;
            let path = entry.path();
            if path.is_dir() {
                stack.push(path);
                continue;
            }
            if path.extension().and_then(|value| value.to_str()) != Some("jsonl") {
                continue;
            }
            let content = fs::read_to_string(&path).map_err(|error| error.to_string())?;
            for line in content.lines().filter(|line| !line.trim().is_empty()) {
                let value: serde_json::Value = serde_json::from_str(line).map_err(|error| error.to_string())?;
                let Some(timestamp) = value.get("timestamp").and_then(|value| value.as_str()) else {
                    continue;
                };
                let date = timestamp.chars().take(10).collect::<String>();
                let row = by_date.entry(date.clone()).or_insert_with(|| DailyUsageSummary {
                    date,
                    total_cost_usd: 0.0,
                    total_tokens: 0,
                    input_tokens: 0,
                    output_tokens: 0,
                    cache_creation_tokens: 0,
                    cache_read_tokens: 0,
                    agents: Vec::new(),
                    models: Vec::new(),
                    projects: Vec::new(),
                });
                row.agents.push(AgentBreakdown { agent: agent.clone(), cost_usd: 0.0, total_tokens: 0 });
            }
        }
    }
    Ok(())
}
```

**步骤 4：运行测试以验证它通过**

运行：

```bash
cargo test -p ccusage loads_claude_and_codex_fixture_daily_rows
```

预期：PASS。

**步骤 5：标记后续重构点**

在 `adapter.rs` 顶部加注释：

```rust
// TODO(gui): Replace fixture-oriented prototype with calls into ccusage Claude/Codex loaders.
```

下一任务再用现有 ccusage loader 替换原型，不要在本任务扩大范围。

---

### 任务 5：初始化 Tauri 2 GUI app 骨架

**文件：**
- 创建：`apps/ccusage-gui/package.json`
- 创建：`apps/ccusage-gui/index.html`
- 创建：`apps/ccusage-gui/src/main.tsx`
- 创建：`apps/ccusage-gui/src/App.tsx`
- 创建：`apps/ccusage-gui/src/styles.css`
- 创建：`apps/ccusage-gui/src-tauri/Cargo.toml`
- 创建：`apps/ccusage-gui/src-tauri/tauri.conf.json`
- 创建：`apps/ccusage-gui/src-tauri/src/main.rs`
- 创建：`apps/ccusage-gui/src-tauri/src/lib.rs`
- 修改：根 `package.json` 不需要新增 workspace，因为已有 `apps/*`

**步骤 1：编写失败的 smoke 测试**

创建 `apps/ccusage-gui/src/App.test.ts`：

```ts
import test from 'node:test';
import assert from 'node:assert/strict';
import { appTitle } from './app-title.js';

test('app title describes local-first cost monitor', () => {
  assert.equal(appTitle, 'AI Coding Cost Monitor');
});
```

创建空文件 `apps/ccusage-gui/src/app-title.ts`，先不导出。

**步骤 2：运行测试以验证它失败**

运行：

```bash
pnpm --dir apps/ccusage-gui test
```

预期：FAIL，当前 package/script 尚不存在。这个失败证明 app 还未初始化。

**步骤 3：创建最少 Tauri/Vite/React app**

参考 Tauri 2 文档命令：

```bash
pnpm create tauri-app@2 apps/ccusage-gui
```

如果交互式脚手架不适合自动执行，手动创建上述文件。`apps/ccusage-gui/package.json` 至少包含：

```json
{
  "name": "ccusage-gui",
  "version": "0.0.0",
  "private": true,
  "type": "module",
  "scripts": {
    "dev": "vite",
    "build": "tsc && vite build",
    "test": "tsx --test src/**/*.test.ts",
    "tauri": "tauri"
  },
  "dependencies": {
    "@tauri-apps/api": "^2.0.0",
    "@vitejs/plugin-react": "^5.0.0",
    "vite": "^7.0.0",
    "react": "^19.0.0",
    "react-dom": "^19.0.0"
  },
  "devDependencies": {
    "@types/node": "catalog:types",
    "@types/react": "^19.0.0",
    "@types/react-dom": "^19.0.0",
    "tsx": "^4.20.0",
    "typescript": "^5.0.0"
  }
}
```

`apps/ccusage-gui/src/app-title.ts`：

```ts
export const appTitle = 'AI Coding Cost Monitor';
```

`App.tsx`：

```tsx
import { appTitle } from './app-title';

export function App() {
  return <main><h1>{appTitle}</h1><p>Local-first AI coding usage monitor.</p></main>;
}
```

`src-tauri/Cargo.toml` 至少包含：

```toml
[package]
name = "ccusage-gui"
version = "0.0.0"
edition = "2024"

[lib]
name = "ccusage_gui_lib"
crate-type = ["staticlib", "cdylib", "rlib"]

[dependencies]
tauri = { version = "2", features = ["tray-icon"] }
tauri-plugin-notification = "2"
serde = { version = "1", features = ["derive"] }
serde_json = "1"
ccusage = { path = "../../../rust/crates/ccusage" }
```

**步骤 4：运行测试以验证它通过**

运行：

```bash
pnpm install
pnpm --dir apps/ccusage-gui test
```

预期：PASS。

**步骤 5：验证 Tauri 编译配置**

运行：

```bash
pnpm --dir apps/ccusage-gui build
cargo check --manifest-path apps/ccusage-gui/src-tauri/Cargo.toml
```

预期：均通过。

---

### 任务 6：实现 Tauri 命令 `get_dashboard` 返回 fake 数据

**文件：**
- 修改：`apps/ccusage-gui/src-tauri/src/lib.rs`
- 创建：`apps/ccusage-gui/src-tauri/src/dashboard.rs`
- 测试：`apps/ccusage-gui/src-tauri/src/dashboard.rs` 内单元测试

**步骤 1：编写失败的测试**

`dashboard.rs`：

```rust
#[test]
fn fake_dashboard_contains_today_and_month_costs() {
    let dashboard = fake_dashboard();
    assert_eq!(dashboard.today_cost_usd, 1.23);
    assert_eq!(dashboard.month_cost_usd, 12.34);
    assert_eq!(dashboard.status, "ok");
}
```

**步骤 2：运行测试以验证它失败**

运行：

```bash
cargo test --manifest-path apps/ccusage-gui/src-tauri/Cargo.toml fake_dashboard_contains_today_and_month_costs
```

预期：FAIL，`fake_dashboard` 未定义。

**步骤 3：编写最少实现**

`dashboard.rs`：

```rust
use serde::Serialize;

#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DashboardSummary {
    pub today_cost_usd: f64,
    pub week_cost_usd: f64,
    pub month_cost_usd: f64,
    pub today_tokens: u64,
    pub status: String,
}

pub fn fake_dashboard() -> DashboardSummary {
    DashboardSummary {
        today_cost_usd: 1.23,
        week_cost_usd: 5.67,
        month_cost_usd: 12.34,
        today_tokens: 4567,
        status: "ok".to_string(),
    }
}

#[tauri::command]
pub fn get_dashboard() -> DashboardSummary {
    fake_dashboard()
}
```

`lib.rs`：

```rust
mod dashboard;

pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_notification::init())
        .invoke_handler(tauri::generate_handler![dashboard::get_dashboard])
        .run(tauri::generate_context!())
        .expect("error while running ccusage gui");
}
```

**步骤 4：运行测试以验证它通过**

运行：

```bash
cargo test --manifest-path apps/ccusage-gui/src-tauri/Cargo.toml fake_dashboard_contains_today_and_month_costs
```

预期：PASS。

**步骤 5：运行 Tauri 后端检查**

运行：

```bash
cargo check --manifest-path apps/ccusage-gui/src-tauri/Cargo.toml
```

预期：PASS。

---

### 任务 7：前端 Dashboard 调用 Tauri 命令并展示总览

**文件：**
- 创建：`apps/ccusage-gui/src/api.ts`
- 创建：`apps/ccusage-gui/src/types.ts`
- 修改：`apps/ccusage-gui/src/App.tsx`
- 创建：`apps/ccusage-gui/src/Dashboard.tsx`
- 测试：`apps/ccusage-gui/src/dashboard-view.test.ts`

**步骤 1：编写失败的测试**

为了避免引入复杂 DOM test runner，先测试纯格式化函数。创建 `src/dashboard-format.ts` 和测试：

```ts
import test from 'node:test';
import assert from 'node:assert/strict';
import { formatUsd, dashboardStatusLabel } from './dashboard-format.js';

test('formats dashboard cost and status', () => {
  assert.equal(formatUsd(1.234), '$1.23');
  assert.equal(dashboardStatusLabel('ok'), '正常');
  assert.equal(dashboardStatusLabel('anomaly'), '异常');
});
```

**步骤 2：运行测试以验证它失败**

运行：

```bash
pnpm --dir apps/ccusage-gui test
```

预期：FAIL，`dashboard-format` 未导出函数。

**步骤 3：编写最少实现**

`dashboard-format.ts`：

```ts
export function formatUsd(value: number): string {
  return `$${value.toFixed(2)}`;
}

export function dashboardStatusLabel(status: string): string {
  return status === 'anomaly' ? '异常' : '正常';
}
```

`types.ts`：

```ts
export interface DashboardSummary {
  todayCostUsd: number;
  weekCostUsd: number;
  monthCostUsd: number;
  todayTokens: number;
  status: string;
}
```

`api.ts`：

```ts
import { invoke } from '@tauri-apps/api/core';
import type { DashboardSummary } from './types';

export function getDashboard(): Promise<DashboardSummary> {
  return invoke<DashboardSummary>('get_dashboard');
}
```

`Dashboard.tsx`：

```tsx
import type { DashboardSummary } from './types';
import { dashboardStatusLabel, formatUsd } from './dashboard-format';

export function Dashboard({ summary }: { summary: DashboardSummary }) {
  return (
    <section>
      <h2>总览</h2>
      <div>今日成本：{formatUsd(summary.todayCostUsd)}</div>
      <div>本周成本：{formatUsd(summary.weekCostUsd)}</div>
      <div>本月成本：{formatUsd(summary.monthCostUsd)}</div>
      <div>今日 tokens：{summary.todayTokens.toLocaleString()}</div>
      <div>状态：{dashboardStatusLabel(summary.status)}</div>
    </section>
  );
}
```

`App.tsx` 用 `useEffect` 调用 `getDashboard`，加载中显示“正在读取本地使用数据...”。

**步骤 4：运行测试以验证它通过**

运行：

```bash
pnpm --dir apps/ccusage-gui test
pnpm --dir apps/ccusage-gui build
```

预期：PASS。

**步骤 5：手动运行 GUI**

运行：

```bash
pnpm --dir apps/ccusage-gui tauri dev
```

预期：窗口显示 Dashboard 卡片。若本地缺少 WebView/Tauri 依赖，记录系统依赖缺失，不要改业务代码绕过。

---

### 任务 8：实现本地 SQLite 聚合缓存层

**文件：**
- 修改：`apps/ccusage-gui/src-tauri/Cargo.toml`
- 创建：`apps/ccusage-gui/src-tauri/src/cache.rs`
- 修改：`apps/ccusage-gui/src-tauri/src/lib.rs`
- 测试：`apps/ccusage-gui/src-tauri/src/cache.rs` 内单元测试

**步骤 1：编写失败的测试**

```rust
#[test]
fn cache_round_trips_dashboard_summary() {
    let cache = UsageCache::open_in_memory().unwrap();
    let summary = DashboardSummary {
        today_cost_usd: 1.23,
        week_cost_usd: 5.0,
        month_cost_usd: 9.0,
        today_tokens: 1000,
        status: "ok".to_string(),
    };

    cache.save_dashboard(&summary).unwrap();
    assert_eq!(cache.load_dashboard().unwrap(), Some(summary));
}
```

**步骤 2：运行测试以验证它失败**

运行：

```bash
cargo test --manifest-path apps/ccusage-gui/src-tauri/Cargo.toml cache_round_trips_dashboard_summary
```

预期：FAIL，缓存类型未定义。

**步骤 3：编写最少实现**

`Cargo.toml` 增加：

```toml
sqlite = { version = "0.37.0", default-features = false, features = ["bundled", "linkage"] }
```

`cache.rs` 实现内存库、表结构和 JSON 存储：

```rust
use crate::dashboard::DashboardSummary;

pub struct UsageCache {
    connection: sqlite::Connection,
}

impl UsageCache {
    pub fn open_in_memory() -> Result<Self, String> {
        let connection = sqlite::open(":memory:").map_err(|error| error.to_string())?;
        let cache = Self { connection };
        cache.migrate()?;
        Ok(cache)
    }

    fn migrate(&self) -> Result<(), String> {
        self.connection.execute(
            "CREATE TABLE IF NOT EXISTS dashboard_cache (id INTEGER PRIMARY KEY CHECK (id = 1), json TEXT NOT NULL)",
        ).map_err(|error| error.to_string())
    }

    pub fn save_dashboard(&self, summary: &DashboardSummary) -> Result<(), String> {
        let json = serde_json::to_string(summary).map_err(|error| error.to_string())?;
        let query = format!(
            "INSERT OR REPLACE INTO dashboard_cache (id, json) VALUES (1, '{}')",
            json.replace('\'', "''")
        );
        self.connection.execute(query).map_err(|error| error.to_string())
    }

    pub fn load_dashboard(&self) -> Result<Option<DashboardSummary>, String> {
        let mut statement = self.connection.prepare("SELECT json FROM dashboard_cache WHERE id = 1")
            .map_err(|error| error.to_string())?;
        if let Ok(sqlite::State::Row) = statement.next() {
            let json: String = statement.read(0).map_err(|error| error.to_string())?;
            return serde_json::from_str(&json).map(Some).map_err(|error| error.to_string());
        }
        Ok(None)
    }
}
```

**步骤 4：运行测试以验证它通过**

运行：

```bash
cargo test --manifest-path apps/ccusage-gui/src-tauri/Cargo.toml cache_round_trips_dashboard_summary
```

预期：PASS。

**步骤 5：运行后端测试**

运行：

```bash
cargo test --manifest-path apps/ccusage-gui/src-tauri/Cargo.toml
```

预期：PASS。

---

### 任务 9：实现异常检测规则

**文件：**
- 创建：`apps/ccusage-gui/src-tauri/src/anomaly.rs`
- 修改：`apps/ccusage-gui/src-tauri/src/lib.rs`
- 测试：`apps/ccusage-gui/src-tauri/src/anomaly.rs` 内单元测试

**步骤 1：编写失败的测试**

```rust
#[test]
fn detects_today_cost_above_seven_day_average() {
    let historical = vec![1.0, 1.2, 0.8, 1.1, 1.0, 0.9, 1.0];
    let anomaly = detect_cost_anomaly(3.0, &historical, 0.5).unwrap();

    assert_eq!(anomaly.kind, "daily_cost_spike");
    assert!(anomaly.message.contains("7 日均值"));
}

#[test]
fn ignores_tiny_absolute_cost_increase() {
    let historical = vec![0.01, 0.01, 0.01, 0.01, 0.01, 0.01, 0.01];
    assert!(detect_cost_anomaly(0.03, &historical, 0.5).is_none());
}
```

**步骤 2：运行测试以验证它失败**

运行：

```bash
cargo test --manifest-path apps/ccusage-gui/src-tauri/Cargo.toml detects_today_cost_above_seven_day_average
```

预期：FAIL，函数未定义。

**步骤 3：编写最少实现**

```rust
use serde::Serialize;

#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AnomalyNotice {
    pub kind: String,
    pub message: String,
    pub ratio: f64,
}

pub fn detect_cost_anomaly(today: f64, historical: &[f64], min_absolute_cost: f64) -> Option<AnomalyNotice> {
    if today < min_absolute_cost || historical.is_empty() {
        return None;
    }
    let average = historical.iter().sum::<f64>() / historical.len() as f64;
    if average <= 0.0 {
        return None;
    }
    let ratio = today / average;
    (ratio >= 2.0).then(|| AnomalyNotice {
        kind: "daily_cost_spike".to_string(),
        message: format!("今日成本是过去 7 日均值的 {:.1} 倍", ratio),
        ratio,
    })
}
```

**步骤 4：运行测试以验证它通过**

运行：

```bash
cargo test --manifest-path apps/ccusage-gui/src-tauri/Cargo.toml anomaly
```

预期：PASS。

**步骤 5：把 anomaly 接入 dashboard 状态**

修改 `dashboard.rs`，当 `detect_cost_anomaly` 返回 Some 时，`status = "anomaly"`，否则 `status = "ok"`。新增一个小测试验证状态变化。

---

### 任务 10：实现后台刷新服务的纯 Rust 核心

**文件：**
- 创建：`apps/ccusage-gui/src-tauri/src/refresh.rs`
- 修改：`apps/ccusage-gui/src-tauri/src/lib.rs`
- 测试：`apps/ccusage-gui/src-tauri/src/refresh.rs` 内单元测试

**步骤 1：编写失败的测试**

```rust
#[test]
fn refresh_writes_latest_dashboard_to_cache() {
    let cache = UsageCache::open_in_memory().unwrap();
    let summary = fake_dashboard();

    refresh_dashboard_cache(&cache, summary.clone()).unwrap();

    assert_eq!(cache.load_dashboard().unwrap(), Some(summary));
}
```

**步骤 2：运行测试以验证它失败**

运行：

```bash
cargo test --manifest-path apps/ccusage-gui/src-tauri/Cargo.toml refresh_writes_latest_dashboard_to_cache
```

预期：FAIL。

**步骤 3：编写最少实现**

```rust
use crate::{cache::UsageCache, dashboard::DashboardSummary};

pub fn refresh_dashboard_cache(cache: &UsageCache, summary: DashboardSummary) -> Result<(), String> {
    cache.save_dashboard(&summary)
}
```

**步骤 4：运行测试以验证它通过**

运行：

```bash
cargo test --manifest-path apps/ccusage-gui/src-tauri/Cargo.toml refresh
```

预期：PASS。

**步骤 5：接入 app state**

在 `lib.rs` 中添加 Tauri managed state，先只在命令调用时读取缓存；如果缓存为空返回 fake dashboard。不要在本任务实现真实定时器。

---

### 任务 11：实现托盘和通知最小闭环

**文件：**
- 修改：`apps/ccusage-gui/src-tauri/src/lib.rs`
- 创建：`apps/ccusage-gui/src-tauri/src/tray.rs`
- 创建：`apps/ccusage-gui/src-tauri/src/notification.rs`
- 测试：`apps/ccusage-gui/src-tauri/src/tray.rs` 和 `notification.rs` 的纯函数测试

**步骤 1：编写失败的测试**

`tray.rs`：

```rust
#[test]
fn tray_title_contains_today_cost() {
    assert_eq!(tray_title(1.23, "ok"), "今日 $1.23");
    assert_eq!(tray_title(3.45, "anomaly"), "异常 $3.45");
}
```

`notification.rs`：

```rust
#[test]
fn anomaly_notification_explains_reason() {
    let text = anomaly_notification_body("今日成本是过去 7 日均值的 2.3 倍");
    assert!(text.contains("2.3 倍"));
}
```

**步骤 2：运行测试以验证它失败**

运行：

```bash
cargo test --manifest-path apps/ccusage-gui/src-tauri/Cargo.toml tray_title_contains_today_cost anomaly_notification_explains_reason
```

预期：FAIL。

**步骤 3：编写最少实现**

`tray.rs`：

```rust
pub fn tray_title(today_cost_usd: f64, status: &str) -> String {
    if status == "anomaly" {
        format!("异常 ${today_cost_usd:.2}")
    } else {
        format!("今日 ${today_cost_usd:.2}")
    }
}
```

`notification.rs`：

```rust
pub fn anomaly_notification_body(reason: &str) -> String {
    format!("AI 编程成本异常：{reason}")
}
```

在 `lib.rs` 使用 Tauri 2 tray/menu API 初始化托盘；notification 使用：

```rust
use tauri_plugin_notification::NotificationExt;

app.notification()
    .builder()
    .title("AI Coding Cost Monitor")
    .body("AI 编程成本异常：今日成本明显高于历史均值")
    .show()?;
```

**步骤 4：运行测试以验证它通过**

运行：

```bash
cargo test --manifest-path apps/ccusage-gui/src-tauri/Cargo.toml tray notification
```

预期：PASS。

**步骤 5：手动验证**

运行：

```bash
pnpm --dir apps/ccusage-gui tauri dev
```

预期：macOS 菜单栏或 Windows 托盘出现应用入口；点击可打开主窗口。通知权限缺失时，UI 不崩溃。

---

### 任务 12：实现 Settings 数据模型和本地配置

**文件：**
- 创建：`apps/ccusage-gui/src-tauri/src/settings.rs`
- 修改：`apps/ccusage-gui/src-tauri/src/lib.rs`
- 创建：`apps/ccusage-gui/src/settings-format.ts`
- 创建：`apps/ccusage-gui/src/Settings.tsx`
- 测试：Rust settings 测试，TS settings 测试

**步骤 1：编写失败的测试**

Rust：

```rust
#[test]
fn default_settings_are_quiet_and_offline() {
    let settings = AppSettings::default();
    assert!(settings.offline_pricing);
    assert!(settings.anomaly_notifications_enabled);
    assert!(!settings.budget_notifications_enabled);
    assert_eq!(settings.refresh_minutes, 15);
}
```

TypeScript：

```ts
import test from 'node:test';
import assert from 'node:assert/strict';
import { refreshLabel } from './settings-format.js';

test('formats refresh interval', () => {
  assert.equal(refreshLabel(15), '每 15 分钟');
});
```

**步骤 2：运行测试以验证它失败**

运行：

```bash
cargo test --manifest-path apps/ccusage-gui/src-tauri/Cargo.toml default_settings_are_quiet_and_offline
pnpm --dir apps/ccusage-gui test
```

预期：FAIL。

**步骤 3：编写最少实现**

`settings.rs`：

```rust
use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppSettings {
    pub offline_pricing: bool,
    pub anomaly_notifications_enabled: bool,
    pub budget_notifications_enabled: bool,
    pub active_session_notifications_enabled: bool,
    pub refresh_minutes: u64,
    pub daily_budget_usd: Option<f64>,
    pub monthly_budget_usd: Option<f64>,
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            offline_pricing: true,
            anomaly_notifications_enabled: true,
            budget_notifications_enabled: false,
            active_session_notifications_enabled: false,
            refresh_minutes: 15,
            daily_budget_usd: None,
            monthly_budget_usd: None,
        }
    }
}

#[tauri::command]
pub fn get_settings() -> AppSettings {
    AppSettings::default()
}
```

`settings-format.ts`：

```ts
export function refreshLabel(minutes: number): string {
  return `每 ${minutes} 分钟`;
}
```

`Settings.tsx` 展示 refresh、offline pricing 和三个通知开关的只读状态；保存功能后续任务再做。

**步骤 4：运行测试以验证它通过**

运行：

```bash
cargo test --manifest-path apps/ccusage-gui/src-tauri/Cargo.toml settings
pnpm --dir apps/ccusage-gui test
```

预期：PASS。

**步骤 5：把 Settings 页面接入 UI**

在 `App.tsx` 增加简单 tab 状态：Dashboard / Settings。运行 `pnpm --dir apps/ccusage-gui build` 预期通过。

---

### 任务 13：实现 Breakdown 和 Reports 的前端骨架

**文件：**
- 创建：`apps/ccusage-gui/src/Breakdown.tsx`
- 创建：`apps/ccusage-gui/src/Reports.tsx`
- 创建：`apps/ccusage-gui/src/breakdown-format.ts`
- 修改：`apps/ccusage-gui/src/App.tsx`
- 测试：`apps/ccusage-gui/src/breakdown-format.test.ts`

**步骤 1：编写失败的测试**

```ts
import test from 'node:test';
import assert from 'node:assert/strict';
import { percentLabel } from './breakdown-format.js';

test('formats breakdown percentages', () => {
  assert.equal(percentLabel(0.612), '61.2%');
});
```

**步骤 2：运行测试以验证它失败**

运行：

```bash
pnpm --dir apps/ccusage-gui test
```

预期：FAIL。

**步骤 3：编写最少实现**

`breakdown-format.ts`：

```ts
export function percentLabel(value: number): string {
  return `${(value * 100).toFixed(1)}%`;
}
```

`Breakdown.tsx`：展示“按项目 / 按模型 / 按 Agent”三个 section，先使用 Dashboard fake 数据或空状态。

`Reports.tsx`：展示 Daily / Monthly / Session tabs 的空状态：“明细报表将在接入 ccusage core 后显示”。

**步骤 4：运行测试以验证它通过**

运行：

```bash
pnpm --dir apps/ccusage-gui test
pnpm --dir apps/ccusage-gui build
```

预期：PASS。

**步骤 5：手动验证导航**

运行：

```bash
pnpm --dir apps/ccusage-gui tauri dev
```

预期：Dashboard、Breakdown、Reports、Settings 可切换，空状态文案清晰。

---

### 任务 14：替换 fake dashboard 为 ccusage GUI service 数据

**文件：**
- 修改：`apps/ccusage-gui/src-tauri/src/dashboard.rs`
- 修改：`apps/ccusage-gui/src-tauri/Cargo.toml`
- 修改：`rust/crates/ccusage/src/gui/adapter.rs`
- 测试：Rust 后端集成测试

**步骤 1：编写失败的测试**

在 `dashboard.rs` 添加：

```rust
#[test]
fn dashboard_from_daily_rows_sums_today_week_and_month() {
    let rows = vec![
        ccusage::gui::DailyUsageSummary {
            date: "2026-01-09".to_string(),
            total_cost_usd: 1.0,
            total_tokens: 100,
            input_tokens: 0,
            output_tokens: 0,
            cache_creation_tokens: 0,
            cache_read_tokens: 0,
            agents: vec![],
            models: vec![],
            projects: vec![],
        },
        ccusage::gui::DailyUsageSummary {
            date: "2026-01-10".to_string(),
            total_cost_usd: 2.0,
            total_tokens: 200,
            input_tokens: 0,
            output_tokens: 0,
            cache_creation_tokens: 0,
            cache_read_tokens: 0,
            agents: vec![],
            models: vec![],
            projects: vec![],
        },
    ];
    let dashboard = dashboard_from_rows(&rows, "2026-01-10");
    assert_eq!(dashboard.today_cost_usd, 2.0);
    assert_eq!(dashboard.month_cost_usd, 3.0);
    assert_eq!(dashboard.today_tokens, 200);
}
```

**步骤 2：运行测试以验证它失败**

运行：

```bash
cargo test --manifest-path apps/ccusage-gui/src-tauri/Cargo.toml dashboard_from_daily_rows_sums_today_week_and_month
```

预期：FAIL。

**步骤 3：编写最少实现**

`dashboard.rs` 添加：

```rust
pub fn dashboard_from_rows(rows: &[ccusage::gui::DailyUsageSummary], today: &str) -> DashboardSummary {
    let today_row = rows.iter().find(|row| row.date == today);
    let month_prefix = today.get(0..7).unwrap_or(today);
    DashboardSummary {
        today_cost_usd: today_row.map(|row| row.total_cost_usd).unwrap_or_default(),
        week_cost_usd: rows.iter().rev().take(7).map(|row| row.total_cost_usd).sum(),
        month_cost_usd: rows.iter().filter(|row| row.date.starts_with(month_prefix)).map(|row| row.total_cost_usd).sum(),
        today_tokens: today_row.map(|row| row.total_tokens).unwrap_or_default(),
        status: "ok".to_string(),
    }
}
```

然后让 `get_dashboard` 调用 `CcusageGuiUsageService::new().load_daily(&GuiReportRequest::default())`，失败时返回用户可理解错误或 cached/fake fallback。不要在前端暴露 panic。

**步骤 4：运行测试以验证它通过**

运行：

```bash
cargo test --manifest-path apps/ccusage-gui/src-tauri/Cargo.toml dashboard
```

预期：PASS。

**步骤 5：端到端手动验证**

运行：

```bash
pnpm --dir apps/ccusage-gui tauri dev
```

预期：Dashboard 不再只显示 fake 固定值；无本地日志时展示空状态，不崩溃。

---

### 任务 15：首次启动和无数据/无权限状态

**文件：**
- 创建：`apps/ccusage-gui/src/EmptyState.tsx`
- 修改：`apps/ccusage-gui/src/App.tsx`
- 修改：`apps/ccusage-gui/src/api.ts`
- 修改：`apps/ccusage-gui/src-tauri/src/dashboard.rs`
- 测试：`apps/ccusage-gui/src/empty-state-format.test.ts`

**步骤 1：编写失败的测试**

```ts
import test from 'node:test';
import assert from 'node:assert/strict';
import { emptyStateMessage } from './empty-state-format.js';

test('explains missing supported data source', () => {
  assert.equal(
    emptyStateMessage('noData'),
    '未检测到 Claude Code 或 Codex 使用记录'
  );
});
```

**步骤 2：运行测试以验证它失败**

运行：

```bash
pnpm --dir apps/ccusage-gui test
```

预期：FAIL。

**步骤 3：编写最少实现**

`empty-state-format.ts`：

```ts
export type EmptyStateReason = 'noData' | 'permissionDenied' | 'refreshFailed';

export function emptyStateMessage(reason: EmptyStateReason): string {
  switch (reason) {
    case 'permissionDenied':
      return '无法读取本地使用日志，请检查目录权限';
    case 'refreshFailed':
      return '刷新失败，正在显示最近一次成功结果';
    case 'noData':
    default:
      return '未检测到 Claude Code 或 Codex 使用记录';
  }
}
```

`EmptyState.tsx` 提供“重新扫描”“手动选择日志目录”“查看支持的数据源”按钮，按钮可以先禁用或只显示 toast。

**步骤 4：运行测试以验证它通过**

运行：

```bash
pnpm --dir apps/ccusage-gui test
pnpm --dir apps/ccusage-gui build
```

预期：PASS。

**步骤 5：手动验证空状态**

临时让后端返回 noData 状态，运行：

```bash
pnpm --dir apps/ccusage-gui tauri dev
```

预期：用户看到可理解引导，没有 stack trace、stderr 或技术参数。

---

### 任务 16：最终验证和文档

**文件：**
- 创建：`apps/ccusage-gui/README.md`
- 修改：`.docs/plans/2026-06-17-ccusage-gui-design.md`（如实现偏离设计，补充说明）
- 测试：全量 Rust/前端检查

**步骤 1：编写 README 草稿**

`apps/ccusage-gui/README.md`：

```markdown
# AI Coding Cost Monitor

Local-first desktop monitor for personal AI coding usage and cost.

- No account
- No cloud upload
- Reads local Claude Code and Codex usage logs
- Uses offline/embedded pricing by default
- Shows dashboard, breakdowns, reports, tray status, and anomaly notifications

## Development

```bash
pnpm install
pnpm --dir apps/ccusage-gui test
pnpm --dir apps/ccusage-gui build
cargo test -p ccusage gui
cargo test --manifest-path apps/ccusage-gui/src-tauri/Cargo.toml
pnpm --dir apps/ccusage-gui tauri dev
```
```

**步骤 2：运行全量检查**

运行：

```bash
cargo test -p ccusage gui
cargo test --manifest-path apps/ccusage-gui/src-tauri/Cargo.toml
pnpm --dir apps/ccusage-gui test
pnpm --dir apps/ccusage-gui build
```

预期：全部 PASS。

**步骤 3：运行现有 CLI 编译检查**

运行：

```bash
cargo test -p ccusage --no-run
```

预期：PASS，GUI core API 抽取没有破坏现有 CLI。

**步骤 4：手动跨平台检查清单**

macOS：

- App 窗口能打开；
- 菜单栏图标出现；
- 通知权限请求合理；
- 无数据状态清晰；
- Dashboard 可显示数据或空状态。

Windows：

- App 窗口能打开；
- 系统托盘图标出现；
- 通知不崩溃；
- 本地路径权限错误可理解；
- Dashboard 可显示数据或空状态。

**步骤 5：更新审查范围**

确认实际修改文件是否仍在 `.docs/plans/2026-06-17-ccusage-gui-review-scope.md` 的预期范围内。如新增文件，补充到审查范围文件。

---

## 审查范围

详见：`.docs/plans/2026-06-17-ccusage-gui-review-scope.md`
