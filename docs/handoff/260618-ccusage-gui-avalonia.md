# 交接摘要：ccusage-gui Avalonia GUI 项目

**日期**: 2026-06-18  
**项目**: ccusage-gui - AI 编程成本桌面监控器  
**状态**: 核心功能开发完成，存在一个关键 bug 需要排查

---

## 1. 当前任务目标

### 要解决的问题
构建一个开源、本地优先、常驻托盘的个人 AI 编程成本监控器 GUI 桌面应用。

### 预期产出
- Windows 可执行文件，显示 Claude Code / Codex 使用成本 Dashboard
- 系统托盘常驻，后台自动刷新
- 异常成本检测和通知

### 完成标准
- 点击刷新按钮后能正确显示本地 ccusage 数据
- 今日/本周/本月成本卡片显示正确数值
- 所有单元测试通过
- App 能正常启动不崩溃

---

## 2. 当前进展

### ✅ 已完成
1. **项目骨架和基础建设**（任务 0.1-0.4）
   - 三层架构解决方案：`CCUsage.Core` / `CCUsage.Infrastructure` / `CCUsage.App`
   - xUnit + FluentAssertions 测试框架配置
   - 数据模型：`DailyUsageSummary`, `DashboardSummary`, `AnomalyNotice`, `AppSettings`

2. **Core 层业务逻辑闭环**（任务 1.1-1.4）
   - `DateRangeService` - 日期范围计算
   - `DashboardService` - 数据汇总计算（今日/本周/本月）
   - `AnomalyDetector` - 异常成本检测
   - `CacheService` + `IUsageCache` 接口 - 缓存抽象
   - `IUsageDataSource` 接口 - 数据源抽象
   - `MockUsageDataSource` - 模拟测试数据源

3. **Infrastructure 层外部依赖**（任务 2.1-2.4）
   - `CCUsageCliParser` - JSON 解析器，适配真实 CLI 输出格式 `{ daily: [...] }`
   - `CCUsageCliRunner` - 进程调用包装
   - `CCUsageCliDataSource` - 真实 CLI 数据源（调用 `ccusage --json --offline daily`）
   - `SqliteUsageCache` - SQLite 持久化缓存实现
   - `NullSystemNotification` - 系统通知抽象

4. **App 层 Avalonia UI**（任务 3.1-3.4）
   - `MainWindow` + `MainWindowViewModel`
   - `DashboardView` + `DashboardViewModel` - 主仪表盘 4 个统计卡片
   - `EmptyStateView` - 无数据引导页面
   - `AppServices` - Microsoft.Extensions.DependencyInjection 配置
   - `BackgroundRefreshService` - 后台定时刷新
   - 系统托盘图标 + 右键菜单

5. **所有单元测试通过**
   - Core.Tests: 16/16 通过
   - Infrastructure.Tests: 9/9 通过

---

## 3. 关键上下文

### 重要背景信息
- **项目位置**: `E:\dev\cholf5\ccusage-gui\`（与上游 ccusage repo 平级）
- **上游 ccusage**: 本地已安装 CLI，有真实使用数据
- **缓存位置**: `%LOCALAPPDATA%\CCUsage\ccusage.db`
- **技术栈**: .NET 8 + Avalonia 11.x + CommunityToolkit.Mvvm + Microsoft.Data.Sqlite

### 用户的明确要求
1. ✅ 不要用 npx，直接执行 `ccusage` 命令（已安装）
2. ✅ 用户本地有真实的 ccusage 数据
3. 需要显示真实数据，不是 Mock 数据

### 已做出的关键决定
1. 从 npx 改为直接调用 `ccusage` 命令（避免 npx 在有 node_modules 目录的执行问题）
2. 解析器已适配真实 CLI 输出格式 `{ daily: [...] }`（不是直接数组）
3. `DashboardViewModel.HasData` 判断逻辑已放宽（只要有数据就显示，不局限于今日/本周）
4. 已在 UI 中添加调试信息显示（`DebugInfo` 属性）

---

## 4. 关键发现

### 🚨 当前阻塞性问题
**症状**: 用户运行 App 后点击刷新，显示"尚未找到 AI 编程使用记录"空状态。

**已添加的调试手段**:
- `DashboardViewModel.DebugInfo` 属性：刷新后显示获取到的数据条数或错误信息
- 在 DashboardView 的空状态页面以红色显示 DebugInfo

**可能的根因方向**（尚未验证，需排查）：
1. `CCUsageCliRunner` 进程调用 `ccusage` 失败
2. `CCUsageCliParser` JSON 解析失败
3. SQLite 缓存初始化/写入有问题
4. `HasData` 判断逻辑仍有缺陷

### 已知的 CLI 输出格式
```json
{
  "daily": [
    {
      "period": "2026-06-18",
      "totalCost": 1.23,
      "totalTokens": 1000,
      "inputTokens": 500,
      "outputTokens": 300,
      "cacheReadTokens": 200,
      "modelBreakdowns": [...],
      "metadata": { "agents": ["codex"] }
    }
  ]
}
```
注意字段名：`period`（不是 date）, `totalCost`（不是 totalCostUsd）

---

## 5. 未完成事项

### 🔴 最高优先级
1. **排查并修复数据刷新问题** - 为什么点击刷新后没有数据显示
2. **验证真实数据流** - 确保从 CLI → 解析 → Dashboard 全链路正常

### 🟡 高优先级（阶段 4 收尾）
3. 任务 4.3 - 优化空状态引导页面
4. 任务 4.4 - 完善异常通知触发逻辑（目前已有基础实现）

### 🟢 中优先级（后续阶段）
5. 阶段 5 - Breakdown 明细页面（按 Agent/Model/Project 拆分）
6. 阶段 5 - Settings 设置页面
7. 阶段 5 - Reports 历史数据表格
8. 阶段 6 - Windows 打包和发布

---

## 6. 建议接手路径

### 第一步：复现和定位问题
1. 清理缓存：删除 `%LOCALAPPDATA%\CCUsage\ccusage.db`
2. 运行 App: `cd E:\dev\cholf5\ccusage-gui; dotnet run --project src/CCUsage.App`
3. 点击"刷新数据"按钮
4. **观察空状态页面显示的红色调试信息** - 这会告诉你：
   - 获取到了多少条数据？
   - 还是有具体的错误信息？

### 第二步：根据调试信息定位
**如果显示错误信息**：
- 检查 `CCUsageCliDataSource.cs` - 确保 ccusage 在 PATH 中
- 检查 `CCUsageCliRunner.cs` - 进程调用参数

**如果显示 0 条数据**：
- 检查 `CCUsageCliParser.cs` - JSON 解析逻辑
- 断点调试看 CLI 输出原始字符串

**如果显示 X 条数据但仍显示空状态**：
- 检查 `DashboardViewModel.HasData` 属性设置
- 检查 `DashboardService.BuildDashboard()` 返回值

### 第三步：验证全链路
1. 在 `CCUsageCliDataSource.GetDailyUsageAsync()` 加断点
2. 确认返回的 `List<DailyUsageSummary>` 有数据
3. 确认 `DashboardService.BuildDashboard()` 正确汇总
4. 确认 `HasData` 被设置为 `true`

### 关键文件清单
- 数据源: `src/CCUsage.Infrastructure/CCUsageCli/CCUsageCliDataSource.cs`
- 解析器: `src/CCUsage.Infrastructure/CCUsageCli/CCUsageCliParser.cs`
- ViewModel: `src/CCUsage.App/ViewModels/DashboardViewModel.cs`
- Dashboard 服务: `src/CCUsage.Core/Services/DashboardService.cs`

---

## 7. 风险与注意事项

### ❌ 已验证不建议的方向
1. **不要用 npx** - 用户明确要求直接调用 `ccusage`，npx 在有 node_modules 的目录会有模块加载问题
2. **不要切回 Mock 数据** - 用户要看真实数据，Mock 只是开发用

### ⚠️ 容易踩的坑
1. **CLI 输出格式** - 不是直接数组，是 `{ daily: [...] }` 包裹对象
2. **字段名大小写敏感** - Newtonsoft.Json 是 camelCase，System.Text.Json 默认区分大小写
3. **日期格式** - CLI 输出 `"period": "2026-06-18"`，已映射到 `DailyUsageSummary.Date`
4. **SQLite 并发** - 注意 SQLite 文件锁，运行多个实例可能冲突
5. **ccusage PATH** - 确保 ccusage 命令在系统 PATH 中可用

### 🔍 容易误判的点
- "今日没有数据 ≠ 没有任何数据" - 用户可能历史有数据但今天没有，Dashboard 应该显示历史累计
- 不要只看 `TodayCostUsd` 为 0 就认为没数据，应该看数据列表是否有记录

---

## 下一位 Agent 的第一步建议

**1 分钟快速定位**：
1. 打开 PowerShell，运行：
   ```powershell
   cd E:\dev\cholf5\ccusage-gui
   del $env:LOCALAPPDATA\CCUsage\ccusage.db -ErrorAction SilentlyContinue
   dotnet run --project src/CCUsage.App
   ```
2. 在 App 中点击"刷新数据"按钮
3. **观察红色调试信息**，根据显示内容判断问题所在

根据调试信息，你会知道是：
- `错误: xxx` → 进程调用或解析异常
- `获取到 0 条数据记录` → CLI 输出为空或解析失败
- `获取到 N 条数据记录` → 数据获取成功，但 HasData 判断或 Dashboard 汇总有问题

**祝你顺利！** 🚀
