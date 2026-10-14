# GUI 版 ccusage (Avalonia .NET) 实施计划

> **给 Claude：** 必需的子技能：使用 com-executing-plans 逐任务实施此计划。

**目标：** 构建一个开源、本地优先、常驻托盘的个人 AI 编程成本监控器，使用 Avalonia .NET 跨平台桌面框架，MVP 支持 Claude Code + Codex，默认离线定价，强调稳定和长期可维护性。

**架构：** 三层清晰隔离。Core 层纯 .NET Standard 业务逻辑（数据模型、聚合计算、异常检测、定价、缓存接口），无任何 UI 和外部依赖。Infrastructure 层封装外部依赖（调用 ccusage CLI、SQLite 缓存实现、系统通知）。App 层 Avalonia UI + MVVM，只依赖 Core 接口，通过 DI 注入实现。

**技术栈：** .NET 8 / .NET 9、Avalonia 11.x、CommunityToolkit.Mvvm、Microsoft.Data.Sqlite、System.Text.Json、xUnit + FluentAssertions、CommunityToolkit.Diagnostics、可选 LiveCharts2 for Avalonia。

---

## 重要前置约束

- 项目根目录：`ccusage-gui`（与当前上游 ccusage repo 平级）
- 不修改上游 `ccusage` repo 代码；上游只读参考
- Core 层绝对不能引用任何 Avalonia 或 UI 相关包
- 默认离线定价，MVP 阶段不做联网刷新
- MVP 数据源：先调用 `ccusage --json --offline` CLI 获取数据，稳定后再考虑 Rust FFI
- TDD：每个任务先写测试，观察失败，再写实现
- 每个任务独立、小步、可测试
- 提交信息要清晰：task-N: 描述

---

## 阶段 0：项目骨架和基础建设

### 任务 0.1：创建解决方案和项目结构

**文件：**
- 创建：`ccusage-gui/TokenPulse.sln`
- 创建：`ccusage-gui/src/TokenPulse.Core/TokenPulse.Core.csproj`
- 创建：`ccusage-gui/src/TokenPulse.Infrastructure/TokenPulse.Infrastructure.csproj`
- 创建：`ccusage-gui/src/TokenPulse.App/TokenPulse.App.csproj`
- 创建：`ccusage-gui/tests/TokenPulse.Core.Tests/TokenPulse.Core.Tests.csproj`
- 创建：`ccusage-gui/tests/TokenPulse.Infrastructure.Tests/TokenPulse.Infrastructure.Tests.csproj`

**步骤 1：创建解决方案和项目文件**

在 `ccusage-gui` 目录运行：

```powershell
dotnet new sln -n CCUsage

dotnet new classlib -n TokenPulse.Core -o src/TokenPulse.Core -f net8.0
dotnet new classlib -n TokenPulse.Infrastructure -o src/TokenPulse.Infrastructure -f net8.0
dotnet new avalonia.app -n TokenPulse.App -o src/TokenPulse.App -f net8.0

dotnet new xunit -n TokenPulse.Core.Tests -o tests/TokenPulse.Core.Tests -f net8.0
dotnet new xunit -n TokenPulse.Infrastructure.Tests -o tests/TokenPulse.Infrastructure.Tests -f net8.0
```

**步骤 2：添加项目引用关系**

```powershell
# Infrastructure 引用 Core
dotnet add src/TokenPulse.Infrastructure reference src/TokenPulse.Core

# App 引用 Core 和 Infrastructure
dotnet add src/TokenPulse.App reference src/TokenPulse.Core
dotnet add src/TokenPulse.App reference src/TokenPulse.Infrastructure

# 测试项目引用
dotnet add tests/TokenPulse.Core.Tests reference src/TokenPulse.Core
dotnet add tests/TokenPulse.Infrastructure.Tests reference src/TokenPulse.Core
dotnet add tests/TokenPulse.Infrastructure.Tests reference src/TokenPulse.Infrastructure
```

**步骤 3：将项目加入解决方案**

```powershell
dotnet sln add src/TokenPulse.Core
dotnet sln add src/TokenPulse.Infrastructure
dotnet sln add src/TokenPulse.App
dotnet sln add tests/TokenPulse.Core.Tests
dotnet sln add tests/TokenPulse.Infrastructure.Tests
```

**步骤 4：验证构建**

```powershell
dotnet build
```

预期：构建成功，0 错误 0 警告。

**步骤 5：提交**

---

### 任务 0.2：配置测试框架和基础包

**文件：**
- 修改：`ccusage-gui/tests/TokenPulse.Core.Tests/TokenPulse.Core.Tests.csproj`
- 修改：`ccusage-gui/tests/TokenPulse.Infrastructure.Tests/TokenPulse.Infrastructure.Tests.csproj`
- 创建：`ccusage-gui/tests/TokenPulse.Core.Tests/Usings.cs`
- 创建：`ccusage-gui/tests/TokenPulse.Infrastructure.Tests/Usings.cs`

**步骤 1：添加 FluentAssertions 包**

```powershell
dotnet add tests/TokenPulse.Core.Tests package FluentAssertions
dotnet add tests/TokenPulse.Infrastructure.Tests package FluentAssertions
```

**步骤 2：编写 Usings.cs 全局导入**

`TokenPulse.Core.Tests/Usings.cs`：

```csharp
global using Xunit;
global using FluentAssertions;
```

`TokenPulse.Infrastructure.Tests/Usings.cs`：

```csharp
global using Xunit;
global using FluentAssertions;
```

**步骤 3：运行空测试验证**

```powershell
dotnet test
```

预期：0 通过，0 失败，0 跳过（没有测试的正常状态）。

**步骤 4：提交**

---

### 任务 0.3：Core 层第一个失败测试和最小模型

**文件：**
- 创建：`ccusage-gui/tests/TokenPulse.Core.Tests/Models/DailyUsageSummaryTests.cs`
- 创建：`ccusage-gui/src/TokenPulse.Core/Models/DailyUsageSummary.cs`

**步骤 1：编写失败的测试**

```csharp
namespace TokenPulse.Core.Tests.Models;

public class DailyUsageSummaryTests
{
    [Fact]
    public void Default_values_are_sane()
    {
        var summary = new DailyUsageSummary();

        summary.Date.Should().BeEmpty();
        summary.TotalCostUsd.Should().Be(0);
        summary.TotalTokens.Should().Be(0);
        summary.InputTokens.Should().Be(0);
        summary.OutputTokens.Should().Be(0);
        summary.CacheCreationTokens.Should().Be(0);
        summary.CacheReadTokens.Should().Be(0);
        summary.Agents.Should().BeEmpty();
        summary.Models.Should().BeEmpty();
        summary.Projects.Should().BeEmpty();
    }

    [Fact]
    public void Can_set_all_properties()
    {
        var agentBreakdown = new AgentBreakdown { Agent = "claude", CostUsd = 1.23m, TotalTokens = 1000 };
        var modelBreakdown = new ModelBreakdown { Model = "claude-sonnet-4-20250514", CostUsd = 1.23m, TotalTokens = 1000 };
        var projectBreakdown = new ProjectBreakdown { Project = "project-alpha", DisplayName = "project-alpha", CostUsd = 1.23m, TotalTokens = 1000 };

        var summary = new DailyUsageSummary
        {
            Date = "2026-01-09",
            TotalCostUsd = 1.23m,
            TotalTokens = 1000,
            InputTokens = 500,
            OutputTokens = 300,
            CacheCreationTokens = 50,
            CacheReadTokens = 150,
            Agents = new List<AgentBreakdown> { agentBreakdown },
            Models = new List<ModelBreakdown> { modelBreakdown },
            Projects = new List<ProjectBreakdown> { projectBreakdown }
        };

        summary.Date.Should().Be("2026-01-09");
        summary.TotalCostUsd.Should().Be(1.23m);
        summary.TotalTokens.Should().Be(1000);
        summary.Agents.Single().Agent.Should().Be("claude");
    }
}
```

**步骤 2：运行测试以验证它失败**

```powershell
dotnet test tests/TokenPulse.Core.Tests
```

预期：编译错误，类型未定义。

**步骤 3：编写最少实现**

`TokenPulse.Core/Models/DailyUsageSummary.cs`：

```csharp
namespace TokenPulse.Core.Models;

public class DailyUsageSummary
{
    public string Date { get; set; } = string.Empty;
    public decimal TotalCostUsd { get; set; }
    public long TotalTokens { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheCreationTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public List<AgentBreakdown> Agents { get; set; } = new();
    public List<ModelBreakdown> Models { get; set; } = new();
    public List<ProjectBreakdown> Projects { get; set; } = new();
}

public class AgentBreakdown
{
    public string Agent { get; set; } = string.Empty;
    public decimal CostUsd { get; set; }
    public long TotalTokens { get; set; }
}

public class ModelBreakdown
{
    public string Model { get; set; } = string.Empty;
    public decimal CostUsd { get; set; }
    public long TotalTokens { get; set; }
}

public class ProjectBreakdown
{
    public string Project { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public decimal CostUsd { get; set; }
    public long TotalTokens { get; set; }
}
```

**步骤 4：运行测试以验证它通过**

```powershell
dotnet test tests/TokenPulse.Core.Tests
```

预期：2 个测试全部通过。

**步骤 5：提交**

---

### 任务 0.4：Core 层剩余基础数据模型

**文件：**
- 创建：`ccusage-gui/src/TokenPulse.Core/Models/DashboardSummary.cs`
- 创建：`ccusage-gui/src/TokenPulse.Core/Models/AppSettings.cs`
- 创建：`ccusage-gui/src/TokenPulse.Core/Models/AnomalyNotice.cs`
- 测试：`ccusage-gui/tests/TokenPulse.Core.Tests/Models/DashboardSummaryTests.cs`

**步骤 1：编写失败的测试**

```csharp
namespace TokenPulse.Core.Tests.Models;

public class DashboardSummaryTests
{
    [Fact]
    public void Default_status_is_ok()
    {
        var dashboard = new DashboardSummary();
        dashboard.Status.Should().Be("ok");
        dashboard.TodayCostUsd.Should().Be(0);
        dashboard.MonthCostUsd.Should().Be(0);
    }

    [Fact]
    public void Anomaly_notice_has_required_properties()
    {
        var notice = new AnomalyNotice
        {
            Kind = "daily_cost_spike",
            Message = "今日成本是过去 7 日均值的 2.3 倍",
            Ratio = 2.3m
        };

        notice.Kind.Should().Be("daily_cost_spike");
        notice.Ratio.Should().Be(2.3m);
    }

    [Fact]
    public void AppSettings_defaults_are_quiet()
    {
        var settings = new AppSettings();
        settings.OfflinePricing.Should().BeTrue();
        settings.AnomalyNotificationsEnabled.Should().BeTrue();
        settings.BudgetNotificationsEnabled.Should().BeFalse();
        settings.RefreshMinutes.Should().Be(15);
    }
}
```

**步骤 2：运行测试以验证它失败**

```powershell
dotnet test tests/TokenPulse.Core.Tests
```

预期：编译错误，类型未定义。

**步骤 3：编写最少实现**

`DashboardSummary.cs`：

```csharp
namespace TokenPulse.Core.Models;

public class DashboardSummary
{
    public decimal TodayCostUsd { get; set; }
    public decimal WeekCostUsd { get; set; }
    public decimal MonthCostUsd { get; set; }
    public long TodayTokens { get; set; }
    public string Status { get; set; } = "ok";
    public List<AnomalyNotice> Anomalies { get; set; } = new();
    public DateTime LastRefreshedAt { get; set; } = DateTime.UtcNow;
}
```

`AnomalyNotice.cs`：

```csharp
namespace TokenPulse.Core.Models;

public class AnomalyNotice
{
    public string Kind { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public decimal Ratio { get; set; }
}
```

`AppSettings.cs`：

```csharp
namespace TokenPulse.Core.Models;

public class AppSettings
{
    public bool OfflinePricing { get; set; } = true;
    public bool AnomalyNotificationsEnabled { get; set; } = true;
    public bool BudgetNotificationsEnabled { get; set; } = false;
    public bool ActiveSessionNotificationsEnabled { get; set; } = false;
    public int RefreshMinutes { get; set; } = 15;
    public decimal? DailyBudgetUsd { get; set; }
    public decimal? MonthlyBudgetUsd { get; set; }
}
```

**步骤 4：运行测试以验证它通过**

```powershell
dotnet test tests/TokenPulse.Core.Tests
```

预期：5 个测试全部通过。

**步骤 5：提交**

---

## 阶段 1：Core 层业务逻辑闭环

### 任务 1.1：定义数据源接口和测试用数据

**文件：**
- 创建：`ccusage-gui/src/TokenPulse.Core/Abstractions/IUsageDataSource.cs`
- 创建：`ccusage-gui/src/TokenPulse.Core/Services/DateRangeService.cs`
- 测试：`ccusage-gui/tests/TokenPulse.Core.Tests/Services/DateRangeServiceTests.cs`

**步骤 1：编写失败的测试**

```csharp
namespace TokenPulse.Core.Tests.Services;

public class DateRangeServiceTests
{
    private readonly DateRangeService _service = new();

    [Fact]
    public void Today_returns_local_date_iso_format()
    {
        var today = _service.Today();
        today.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}$");
    }

    [Fact]
    public void GetMonthPrefix_returns_yyyy_mm()
    {
        var prefix = _service.GetMonthPrefix("2026-01-09");
        prefix.Should().Be("2026-01");
    }

    [Fact]
    public void GetLastNDays_includes_today()
    {
        var dates = _service.GetLastNDays(7).ToList();
        dates.Count.Should().Be(7);
        dates.First().Should().Be(_service.Today());
    }
}
```

**步骤 2：运行测试以验证它失败**

```powershell
dotnet test tests/TokenPulse.Core.Tests
```

预期：编译错误。

**步骤 3：编写最少实现**

`IUsageDataSource.cs`：

```csharp
using TokenPulse.Core.Models;

namespace TokenPulse.Core.Abstractions;

public interface IUsageDataSource
{
    Task<List<DailyUsageSummary>> GetDailyUsageAsync(CancellationToken ct = default);
}
```

`DateRangeService.cs`：

```csharp
namespace TokenPulse.Core.Services;

public class DateRangeService
{
    public virtual string Today() => DateTime.Now.ToString("yyyy-MM-dd");

    public string GetMonthPrefix(string date) => date[..7];

    public IEnumerable<string> GetLastNDays(int days)
    {
        for (var i = 0; i < days; i++)
        {
            yield return DateTime.Now.AddDays(-i).ToString("yyyy-MM-dd");
        }
    }
}
```

**步骤 4：运行测试以验证它通过**

```powershell
dotnet test tests/TokenPulse.Core.Tests
```

预期：测试全部通过。

**步骤 5：提交**

---

### 任务 1.2：实现 Dashboard 汇总逻辑

**文件：**
- 创建：`ccusage-gui/src/TokenPulse.Core/Services/DashboardService.cs`
- 测试：`ccusage-gui/tests/TokenPulse.Core.Tests/Services/DashboardServiceTests.cs`

**步骤 1：编写失败的测试**

```csharp
namespace TokenPulse.Core.Tests.Services;

public class DashboardServiceTests
{
    private readonly DashboardService _service;
    private readonly TestDateRangeService _dateService;

    public DashboardServiceTests()
    {
        _dateService = new TestDateRangeService { Today = "2026-01-10" };
        _service = new DashboardService(_dateService);
    }

    [Fact]
    public void Empty_data_returns_zero_dashboard()
    {
        var rows = new List<DailyUsageSummary>();
        var dashboard = _service.BuildDashboard(rows);

        dashboard.TodayCostUsd.Should().Be(0);
        dashboard.WeekCostUsd.Should().Be(0);
        dashboard.MonthCostUsd.Should().Be(0);
        dashboard.TodayTokens.Should().Be(0);
        dashboard.Status.Should().Be("ok");
    }

    [Fact]
    public void Sums_today_week_and_month_cost_correctly()
    {
        var rows = new List<DailyUsageSummary>
        {
            new() { Date = "2026-01-09", TotalCostUsd = 1.0m, TotalTokens = 100 },
            new() { Date = "2026-01-10", TotalCostUsd = 2.0m, TotalTokens = 200 },
            new() { Date = "2026-01-08", TotalCostUsd = 0.5m, TotalTokens = 50 },
            new() { Date = "2025-12-31", TotalCostUsd = 100m, TotalTokens = 10000 }
        };

        var dashboard = _service.BuildDashboard(rows);

        dashboard.TodayCostUsd.Should().Be(2.0m);
        dashboard.MonthCostUsd.Should().Be(3.5m);
        dashboard.TodayTokens.Should().Be(200);
    }

    private class TestDateRangeService : DateRangeService
    {
        public string Today { get; set; } = string.Empty;
        public override string Today() => Today;
    }
}
```

**步骤 2：运行测试以验证它失败**

```powershell
dotnet test tests/TokenPulse.Core.Tests
```

预期：编译错误。

**步骤 3：编写最少实现**

```csharp
using TokenPulse.Core.Abstractions;
using TokenPulse.Core.Models;

namespace TokenPulse.Core.Services;

public class DashboardService
{
    private readonly DateRangeService _dateService;

    public DashboardService(DateRangeService dateService)
    {
        _dateService = dateService;
    }

    public DashboardSummary BuildDashboard(List<DailyUsageSummary> rows)
    {
        var today = _dateService.Today();
        var monthPrefix = _dateService.GetMonthPrefix(today);
        var last7Days = _dateService.GetLastNDays(7).ToHashSet();

        var todayRow = rows.FirstOrDefault(r => r.Date == today);

        return new DashboardSummary
        {
            TodayCostUsd = todayRow?.TotalCostUsd ?? 0,
            TodayTokens = todayRow?.TotalTokens ?? 0,
            WeekCostUsd = rows.Where(r => last7Days.Contains(r.Date)).Sum(r => r.TotalCostUsd),
            MonthCostUsd = rows.Where(r => r.Date.StartsWith(monthPrefix)).Sum(r => r.TotalCostUsd),
            Status = "ok",
            LastRefreshedAt = DateTime.UtcNow
        };
    }
}
```

**步骤 4：运行测试以验证它通过**

```powershell
dotnet test tests/TokenPulse.Core.Tests
```

预期：测试全部通过。

**步骤 5：提交**

---

### 任务 1.3：实现异常检测规则引擎

**文件：**
- 创建：`ccusage-gui/src/TokenPulse.Core/Services/AnomalyDetector.cs`
- 测试：`ccusage-gui/tests/TokenPulse.Core.Tests/Services/AnomalyDetectorTests.cs`

**步骤 1：编写失败的测试**

```csharp
namespace TokenPulse.Core.Tests.Services;

public class AnomalyDetectorTests
{
    private readonly AnomalyDetector _detector = new();
    private const decimal MinAbsoluteCost = 0.05m;

    [Fact]
    public void No_history_returns_no_anomalies()
    {
        var history = new List<DailyUsageSummary>();
        var today = new DailyUsageSummary { Date = "2026-01-10", TotalCostUsd = 1.0m };

        var anomalies = _detector.DetectAnomalies(today, history, MinAbsoluteCost);

        anomalies.Should().BeEmpty();
    }

    [Fact]
    public void Cost_below_min_absolute_threshold_is_not_anomaly_even_when_high_ratio()
    {
        var history = Enumerable.Range(1, 7).Select(i =>
            new DailyUsageSummary { Date = $"2026-01-0{i}", TotalCostUsd = 0.01m }).ToList();
        var today = new DailyUsageSummary { Date = "2026-01-10", TotalCostUsd = 0.03m };

        var anomalies = _detector.DetectAnomalies(today, history, MinAbsoluteCost);

        anomalies.Should().BeEmpty();
    }

    [Fact]
    public void Cost_above_2x_average_is_daily_cost_spike()
    {
        var history = Enumerable.Range(1, 7).Select(i =>
            new DailyUsageSummary { Date = $"2026-01-0{i}", TotalCostUsd = 1.0m }).ToList();
        var today = new DailyUsageSummary { Date = "2026-01-10", TotalCostUsd = 2.5m };

        var anomalies = _detector.DetectAnomalies(today, history, MinAbsoluteCost);

        var spike = anomalies.Single();
        spike.Kind.Should().Be("daily_cost_spike");
        spike.Message.Should().Contain("2.5 倍");
    }

    [Fact]
    public void Cost_below_2x_average_is_not_anomaly()
    {
        var history = Enumerable.Range(1, 7).Select(i =>
            new DailyUsageSummary { Date = $"2026-01-0{i}", TotalCostUsd = 1.0m }).ToList();
        var today = new DailyUsageSummary { Date = "2026-01-10", TotalCostUsd = 1.9m };

        var anomalies = _detector.DetectAnomalies(today, history, MinAbsoluteCost);

        anomalies.Should().BeEmpty();
    }
}
```

**步骤 2：运行测试以验证它失败**

```powershell
dotnet test tests/TokenPulse.Core.Tests
```

预期：编译错误。

**步骤 3：编写最少实现**

```csharp
using TokenPulse.Core.Models;

namespace TokenPulse.Core.Services;

public class AnomalyDetector
{
    private const decimal AnomalyRatioThreshold = 2.0m;

    public List<AnomalyNotice> DetectAnomalies(
        DailyUsageSummary today,
        List<DailyUsageSummary> history,
        decimal minAbsoluteCost)
    {
        var result = new List<AnomalyNotice>();

        if (!history.Any() || today.TotalCostUsd < minAbsoluteCost)
        {
            return result;
        }

        var average = history.Average(h => h.TotalCostUsd);
        if (average <= 0)
        {
            return result;
        }

        var ratio = today.TotalCostUsd / average;
        if (ratio >= AnomalyRatioThreshold)
        {
            result.Add(new AnomalyNotice
            {
                Kind = "daily_cost_spike",
                Message = $"今日成本是过去 {history.Count} 日均值的 {ratio:0.0} 倍",
                Ratio = ratio
            });
        }

        return result;
    }
}
```

**步骤 4：运行测试以验证它通过**

```powershell
dotnet test tests/TokenPulse.Core.Tests
```

预期：测试全部通过。

**步骤 5：提交**

---

### 任务 1.4：实现本地缓存接口

**文件：**
- 创建：`ccusage-gui/src/TokenPulse.Core/Abstractions/IUsageCache.cs`
- 创建：`ccusage-gui/src/TokenPulse.Core/Services/CacheService.cs`
- 测试：`ccusage-gui/tests/TokenPulse.Core.Tests/Services/CacheServiceTests.cs`

**步骤 1：编写失败的测试**

```csharp
namespace TokenPulse.Core.Tests.Services;

public class CacheServiceTests
{
    private readonly InMemoryUsageCache _cache;
    private readonly CacheService _service;

    public CacheServiceTests()
    {
        _cache = new InMemoryUsageCache();
        _service = new CacheService(_cache);
    }

    [Fact]
    public async Task Empty_cache_returns_null_dashboard()
    {
        var dashboard = await _service.GetLatestDashboardAsync();
        dashboard.Should().BeNull();
    }

    [Fact]
    public async Task Save_and_get_roundtrip()
    {
        var summary = new DashboardSummary { TodayCostUsd = 1.23m, TodayTokens = 1000 };

        await _service.SaveDashboardAsync(summary);
        var result = await _service.GetLatestDashboardAsync();

        result.Should().NotBeNull();
        result!.TodayCostUsd.Should().Be(1.23m);
        result.TodayTokens.Should().Be(1000);
    }
}

public class InMemoryUsageCache : IUsageCache
{
    private DashboardSummary? _dashboard;
    private List<DailyUsageSummary> _dailyRows = new();

    public Task SaveDashboardAsync(DashboardSummary summary, CancellationToken ct = default)
    {
        _dashboard = summary;
        return Task.CompletedTask;
    }

    public Task<DashboardSummary?> GetLatestDashboardAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_dashboard);
    }

    public Task SaveDailyRowsAsync(List<DailyUsageSummary> rows, CancellationToken ct = default)
    {
        _dailyRows = rows;
        return Task.CompletedTask;
    }

    public Task<List<DailyUsageSummary>> GetDailyRowsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_dailyRows);
    }
}
```

**步骤 2：运行测试以验证它失败**

```powershell
dotnet test tests/TokenPulse.Core.Tests
```

预期：编译错误，接口未定义。

**步骤 3：编写最少实现**

`IUsageCache.cs`：

```csharp
using TokenPulse.Core.Models;

namespace TokenPulse.Core.Abstractions;

public interface IUsageCache
{
    Task SaveDashboardAsync(DashboardSummary summary, CancellationToken ct = default);
    Task<DashboardSummary?> GetLatestDashboardAsync(CancellationToken ct = default);
    Task SaveDailyRowsAsync(List<DailyUsageSummary> rows, CancellationToken ct = default);
    Task<List<DailyUsageSummary>> GetDailyRowsAsync(CancellationToken ct = default);
}
```

`CacheService.cs`：

```csharp
using TokenPulse.Core.Abstractions;
using TokenPulse.Core.Models;

namespace TokenPulse.Core.Services;

public class CacheService
{
    private readonly IUsageCache _cache;

    public CacheService(IUsageCache cache)
    {
        _cache = cache;
    }

    public Task SaveDashboardAsync(DashboardSummary summary, CancellationToken ct = default)
    {
        return _cache.SaveDashboardAsync(summary, ct);
    }

    public Task<DashboardSummary?> GetLatestDashboardAsync(CancellationToken ct = default)
    {
        return _cache.GetLatestDashboardAsync(ct);
    }

    public Task SaveDailyRowsAsync(List<DailyUsageSummary> rows, CancellationToken ct = default)
    {
        return _cache.SaveDailyRowsAsync(rows, ct);
    }

    public Task<List<DailyUsageSummary>> GetDailyRowsAsync(CancellationToken ct = default)
    {
        return _cache.GetDailyRowsAsync(ct);
    }
}
```

**步骤 4：运行测试以验证它通过**

```powershell
dotnet test tests/TokenPulse.Core.Tests
```

预期：测试全部通过。

**步骤 5：提交**

---

## 阶段 2：Infrastructure 层外部依赖封装

### 任务 2.1：实现 ccusage CLI JSON 输出解析器

**文件：**
- 创建：`ccusage-gui/src/TokenPulse.Infrastructure/CCUsageCli/CCUsageCliParser.cs`
- 创建：`ccusage-gui/tests/TokenPulse.Infrastructure.Tests/CCUsageCliParserTests.cs`
- 测试数据：`ccusage-gui/tests/TokenPulse.Infrastructure.Tests/fixtures/sample-daily.json`

**步骤 1：准备测试 JSON fixture**

从现有 ccusage 测试提取一个 sample daily JSON，保存到 `fixtures/sample-daily.json`：

```json
[
  {
    "date": "2026-01-09",
    "totalCostUsd": 1.23,
    "totalTokens": 1000,
    "inputTokens": 500,
    "outputTokens": 300,
    "cacheCreationTokens": 50,
    "cacheReadTokens": 150,
    "agents": [{"agent": "claude", "costUsd": 1.23, "totalTokens": 1000}],
    "models": [{"model": "claude-sonnet-4-20250514", "costUsd": 1.23, "totalTokens": 1000}],
    "projects": [{"project": "project-alpha", "displayName": "project-alpha", "costUsd": 1.23, "totalTokens": 1000}]
  }
]
```

**步骤 2：编写失败的测试**

```csharp
namespace TokenPulse.Infrastructure.Tests.CCUsageCli;

public class CCUsageCliParserTests
{
    private readonly CCUsageCliParser _parser = new();

    [Fact]
    public async Task Parses_sample_daily_json_correctly()
    {
        var json = await File.ReadAllTextAsync("fixtures/sample-daily.json");
        var rows = _parser.ParseDailyJson(json);

        rows.Count.Should().Be(1);
        var row = rows.Single();
        row.Date.Should().Be("2026-01-09");
        row.TotalCostUsd.Should().Be(1.23m);
        row.TotalTokens.Should().Be(1000);
        row.Agents.Single().Agent.Should().Be("claude");
    }

    [Fact]
    public void Empty_json_returns_empty_list()
    {
        var rows = _parser.ParseDailyJson("[]");
        rows.Should().BeEmpty();
    }

    [Fact]
    public void Malformed_json_throws_parse_exception()
    {
        var action = () => _parser.ParseDailyJson("{invalid json}");
        action.Should().Throw<Exception>();
    }
}
```

**步骤 3：运行测试以验证它失败**

```powershell
dotnet test tests/TokenPulse.Infrastructure.Tests
```

预期：编译错误。

**步骤 4：编写最少实现**

首先给 Infrastructure 项目添加 System.Text.Json 包：

```powershell
dotnet add src/TokenPulse.Infrastructure package System.Text.Json
```

然后实现 parser：

```csharp
using System.Text.Json;
using TokenPulse.Core.Models;

namespace TokenPulse.Infrastructure.CCUsageCli;

public class CCUsageCliParser
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public List<DailyUsageSummary> ParseDailyJson(string json)
    {
        return JsonSerializer.Deserialize<List<DailyUsageSummary>>(json, _options)
            ?? throw new InvalidOperationException("Failed to parse daily JSON: result was null");
    }
}
```

**步骤 5：运行测试以验证它通过**

```powershell
dotnet test tests/TokenPulse.Infrastructure.Tests
```

预期：3 个测试全部通过。

**步骤 6：提交**

---

### 任务 2.2：实现 CLI 进程调用包装器

**文件：**
- 创建：`ccusage-gui/src/TokenPulse.Infrastructure/CCUsageCli/CCUsageCliRunner.cs`
- 创建：`ccusage-gui/src/TokenPulse.Infrastructure/CCUsageCli/CCUsageCliDataSource.cs`
- 测试：`ccusage-gui/tests/TokenPulse.Infrastructure.Tests/CCUsageCliRunnerTests.cs`

**步骤 1：编写失败的测试**

```csharp
namespace TokenPulse.Infrastructure.Tests.CCUsageCli;

public class CCUsageCliRunnerTests
{
    [Fact]
    public async Task Echo_output_is_captured_correctly()
    {
        var runner = new CCUsageCliRunner("echo", "hello world");
        var output = await runner.GetOutputAsync();

        output.Trim().Should().Be("hello world");
    }

    [Fact]
    public async Task Non_zero_exit_code_throws_exception()
    {
        var runner = new CCUsageCliRunner("cmd", "/c exit 1");
        var action = () => runner.GetOutputAsync();
        await action.Should().ThrowAsync<Exception>();
    }
}
```

**步骤 2：运行测试以验证它失败**

```powershell
dotnet test tests/TokenPulse.Infrastructure.Tests
```

预期：编译错误。

**步骤 3：编写最少实现**

`CCUsageCliRunner.cs`：

```csharp
using System.Diagnostics;

namespace TokenPulse.Infrastructure.CCUsageCli;

public class CCUsageCliRunner
{
    private readonly string _command;
    private readonly string _arguments;

    public CCUsageCliRunner(string command, string arguments)
    {
        _command = command;
        _arguments = arguments;
    }

    public async Task<string> GetOutputAsync(CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _command,
            Arguments = _arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start process");
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Process exited with code {process.ExitCode}: {error}");
        }

        return output;
    }
}
```

`CCUsageCliDataSource.cs`（实现 Core 层接口）：

```csharp
using TokenPulse.Core.Abstractions;
using TokenPulse.Core.Models;

namespace TokenPulse.Infrastructure.CCUsageCli;

public class CCUsageCliDataSource : IUsageDataSource
{
    private readonly CCUsageCliParser _parser = new();
    private readonly string _cliPath;

    public CCUsageCliDataSource(string cliPath = "npx")
    {
        _cliPath = cliPath;
    }

    public async Task<List<DailyUsageSummary>> GetDailyUsageAsync(CancellationToken ct = default)
    {
        var runner = new CCUsageCliRunner(_cliPath, "--yes ccusage@latest --json --offline daily");
        var json = await runner.GetOutputAsync(ct);
        return _parser.ParseDailyJson(json);
    }
}
```

**步骤 4：运行测试以验证它通过**

```powershell
dotnet test tests/TokenPulse.Infrastructure.Tests
```

预期：测试全部通过（echo 测试在 Windows 上可能需要调整为 cmd /c echo，根据平台调整）。

**注意：** 如果 echo 测试在你的平台上不稳定，可以改为调用 `dotnet --version` 这类更稳定的命令。

**步骤 5：提交**

---

### 任务 2.3：实现 SQLite 持久化缓存

**文件：**
- 修改：`ccusage-gui/src/TokenPulse.Infrastructure/TokenPulse.Infrastructure.csproj`
- 创建：`ccusage-gui/src/TokenPulse.Infrastructure/Cache/SqliteUsageCache.cs`
- 测试：`ccusage-gui/tests/TokenPulse.Infrastructure.Tests/Cache/SqliteUsageCacheTests.cs`

**步骤 1：添加 SQLite 包**

```powershell
dotnet add src/TokenPulse.Infrastructure package Microsoft.Data.Sqlite
dotnet add src/TokenPulse.Infrastructure package System.Text.Json
```

**步骤 2：编写失败的测试**

```csharp
namespace TokenPulse.Infrastructure.Tests.Cache;

public class SqliteUsageCacheTests : IAsyncLifetime
{
    private string _tempDbPath = null!;
    private SqliteUsageCache _cache = null!;

    public async Task InitializeAsync()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"ccusage-test-{Guid.NewGuid()}.db");
        _cache = new SqliteUsageCache(_tempDbPath);
        await _cache.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_tempDbPath))
        {
            File.Delete(_tempDbPath);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Empty_db_returns_null_dashboard()
    {
        var result = await _cache.GetLatestDashboardAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task Dashboard_roundtrip_preserves_values()
    {
        var summary = new DashboardSummary
        {
            TodayCostUsd = 1.23m,
            TodayTokens = 1000,
            Status = "ok"
        };

        await _cache.SaveDashboardAsync(summary);
        var result = await _cache.GetLatestDashboardAsync();

        result.Should().NotBeNull();
        result!.TodayCostUsd.Should().Be(1.23m);
        result.TodayTokens.Should().Be(1000);
        result.Status.Should().Be("ok");
    }

    [Fact]
    public async Task Daily_rows_roundtrip_preserves_values()
    {
        var rows = new List<DailyUsageSummary>
        {
            new() { Date = "2026-01-09", TotalCostUsd = 1.23m, TotalTokens = 1000 },
            new() { Date = "2026-01-10", TotalCostUsd = 2.34m, TotalTokens = 2000 }
        };

        await _cache.SaveDailyRowsAsync(rows);
        var result = await _cache.GetDailyRowsAsync();

        result.Count.Should().Be(2);
        result.First(r => r.Date == "2026-01-09").TotalCostUsd.Should().Be(1.23m);
    }
}
```

**步骤 3：运行测试以验证它失败**

```powershell
dotnet test tests/TokenPulse.Infrastructure.Tests
```

预期：编译错误。

**步骤 4：编写最少实现**

```csharp
using Microsoft.Data.Sqlite;
using System.Text.Json;
using TokenPulse.Core.Abstractions;
using TokenPulse.Core.Models;

namespace TokenPulse.Infrastructure.Cache;

public class SqliteUsageCache : IUsageCache
{
    private readonly string _dbPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public SqliteUsageCache(string dbPath)
    {
        _dbPath = dbPath;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(ct);

        const string createDashboardTableSql = """
            CREATE TABLE IF NOT EXISTS dashboard_cache (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                json TEXT NOT NULL,
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """;

        const string createDailyRowsTableSql = """
            CREATE TABLE IF NOT EXISTS daily_rows_cache (
                date TEXT PRIMARY KEY,
                json TEXT NOT NULL,
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = createDashboardTableSql;
        await cmd.ExecuteNonQueryAsync(ct);

        cmd.CommandText = createDailyRowsTableSql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveDashboardAsync(DashboardSummary summary, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(summary, _jsonOptions);
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO dashboard_cache (id, json)
            VALUES (1, $json)
            """;
        cmd.Parameters.AddWithValue("$json", json);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DashboardSummary?> GetLatestDashboardAsync(CancellationToken ct = default)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT json FROM dashboard_cache WHERE id = 1 LIMIT 1";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var json = reader.GetString(0);
        return JsonSerializer.Deserialize<DashboardSummary>(json, _jsonOptions);
    }

    public async Task SaveDailyRowsAsync(List<DailyUsageSummary> rows, CancellationToken ct = default)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        foreach (var row in rows)
        {
            var json = JsonSerializer.Serialize(row, _jsonOptions);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO daily_rows_cache (date, json)
                VALUES ($date, $json)
                """;
            cmd.Parameters.AddWithValue("$date", row.Date);
            cmd.Parameters.AddWithValue("$json", json);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        transaction.Commit();
    }

    public async Task<List<DailyUsageSummary>> GetDailyRowsAsync(CancellationToken ct = default)
    {
        var result = new List<DailyUsageSummary>();
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT json FROM daily_rows_cache ORDER BY date DESC";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var json = reader.GetString(0);
            var row = JsonSerializer.Deserialize<DailyUsageSummary>(json, _jsonOptions);
            if (row != null)
            {
                result.Add(row);
            }
        }

        return result;
    }
}
```

**步骤 5：运行测试以验证它通过**

```powershell
dotnet test tests/TokenPulse.Infrastructure.Tests
```

预期：测试全部通过。

**步骤 6：提交**

---

### 任务 2.4：实现跨平台系统通知抽象

**文件：**
- 创建：`ccusage-gui/src/TokenPulse.Core/Abstractions/ISystemNotification.cs`
- 创建：`ccusage-gui/src/TokenPulse.Infrastructure/Notifications/NullSystemNotification.cs`
- 测试：`ccusage-gui/tests/TokenPulse.Core.Tests/Notifications/NullSystemNotificationTests.cs`

**步骤 1：编写失败的测试**

```csharp
namespace TokenPulse.Core.Tests.Notifications;

public class NullSystemNotificationTests
{
    [Fact]
    public async Task Show_notification_completes_without_error()
    {
        var notification = new NullSystemNotification();
        var action = () => notification.ShowAsync("Test Title", "Test Message");
        await action.Should().NotThrowAsync();
    }
}
```

**步骤 2：运行测试以验证它失败**

```powershell
dotnet test tests/TokenPulse.Core.Tests
```

预期：编译错误。

**步骤 3：编写最少实现**

`ISystemNotification.cs`（Core 层）：

```csharp
namespace TokenPulse.Core.Abstractions;

public interface ISystemNotification
{
    Task ShowAsync(string title, string message, CancellationToken ct = default);
}
```

`NullSystemNotification.cs`（Infrastructure 层）：

```csharp
using TokenPulse.Core.Abstractions;

namespace TokenPulse.Infrastructure.Notifications;

public class NullSystemNotification : ISystemNotification
{
    public Task ShowAsync(string title, string message, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
```

**步骤 4：运行测试以验证它通过**

```powershell
dotnet test tests/TokenPulse.Core.Tests
```

预期：测试通过。

**步骤 5：提交**

**后续：** 真正的平台通知（Windows 11 Toast / macOS NSUserNotificationCenter）会在 Avalonia App 层实现，MVP 阶段先用 Null 实现。

---

## 阶段 3：Avalonia App 层基础框架

### 任务 3.1：初始化 Avalonia App 基础结构

**文件：**
- 修改：`ccusage-gui/src/TokenPulse.App/TokenPulse.App.csproj`
- 创建：`ccusage-gui/src/TokenPulse.App/App.axaml`
- 创建：`ccusage-gui/src/TokenPulse.App/App.axaml.cs`
- 创建：`ccusage-gui/src/TokenPulse.App/ViewModels/ViewModelBase.cs`
- 创建：`ccusage-gui/src/TokenPulse.App/ViewModels/MainWindowViewModel.cs`
- 创建：`ccusage-gui/src/TokenPulse.App/Views/MainWindow.axaml`
- 创建：`ccusage-gui/src/TokenPulse.App/Views/MainWindow.axaml.cs`

**步骤 1：添加必要的 NuGet 包**

```powershell
dotnet add src/TokenPulse.App package Avalonia.Desktop
dotnet add src/TokenPulse.App package CommunityToolkit.Mvvm
dotnet add src/TokenPulse.App package Microsoft.Extensions.DependencyInjection
```

**步骤 2：创建 ViewModel 基类**

`ViewModelBase.cs`：

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace TokenPulse.App.ViewModels;

public class ViewModelBase : ObservableObject
{
}
```

`MainWindowViewModel.cs`：

```csharp
namespace TokenPulse.App.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private string _title = "AI Coding Cost Monitor";
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }
}
```

**步骤 3：创建 App.axaml**

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="using:TokenPulse.App"
             RequestedThemeVariant="Default">
    <Application.Resources>
    </Application.Resources>
</Application>
```

`App.axaml.cs`：

```csharp
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TokenPulse.App.ViewModels;
using TokenPulse.App.Views;

namespace TokenPulse.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
```

**步骤 4：创建 MainWindow.axaml**

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
        xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
        mc:Ignorable="d"
        Title="{Binding Title}"
        Width="800" Height="600">
    <Grid>
        <TextBlock Text="AI Coding Cost Monitor"
                   FontSize="24"
                   HorizontalAlignment="Center"
                   VerticalAlignment="Center"/>
    </Grid>
</Window>
```

`MainWindow.axaml.cs`：

```csharp
using Avalonia.Controls;

namespace TokenPulse.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
```

**步骤 5：验证构建**

```powershell
dotnet build src/TokenPulse.App
```

预期：构建成功。

**步骤 6：手动验证**

```powershell
dotnet run --project src/TokenPulse.App
```

预期：窗口正常打开，标题显示 "AI Coding Cost Monitor"，中央有文字。

**步骤 7：提交**

---

### 任务 3.2：实现依赖注入配置和服务定位器

**文件：**
- 创建：`ccusage-gui/src/TokenPulse.App/DependencyInjection/AppServices.cs`
- 修改：`ccusage-gui/src/TokenPulse.App/App.axaml.cs`

**步骤 1：编写最少实现**

`AppServices.cs`：

```csharp
using Microsoft.Extensions.DependencyInjection;
using TokenPulse.Core.Abstractions;
using TokenPulse.Core.Services;
using TokenPulse.Infrastructure.CCUsageCli;
using TokenPulse.Infrastructure.Cache;
using TokenPulse.Infrastructure.Notifications;

namespace TokenPulse.App.DependencyInjection;

public static class AppServices
{
    private static IServiceProvider? _provider;

    public static void Initialize()
    {
        var services = new ServiceCollection();

        // Core services
        services.AddSingleton<DateRangeService>();
        services.AddSingleton<DashboardService>();
        services.AddSingleton<AnomalyDetector>();

        // Data sources
        services.AddSingleton<IUsageDataSource, CCUsageCliDataSource>();

        // Cache
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CCUsage",
            "ccusage.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var cache = new SqliteUsageCache(dbPath);
        cache.InitializeAsync().GetAwaiter().GetResult();
        services.AddSingleton<IUsageCache>(cache);
        services.AddSingleton<CacheService>();

        // Notifications
        services.AddSingleton<ISystemNotification, NullSystemNotification>();

        _provider = services.BuildServiceProvider();
    }

    public static T Get<T>() where T : notnull
    {
        if (_provider == null)
        {
            throw new InvalidOperationException("AppServices not initialized. Call Initialize() first.");
        }
        return _provider.GetRequiredService<T>();
    }
}
```

修改 `App.axaml.cs`：

```csharp
public override void OnFrameworkInitializationCompleted()
{
    AppServices.Initialize(); // 添加这行

    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
    {
        desktop.MainWindow = new MainWindow
        {
            DataContext = new MainWindowViewModel(),
        };
    }

    base.OnFrameworkInitializationCompleted();
}
```

**步骤 2：验证构建**

```powershell
dotnet build src/TokenPulse.App
```

预期：构建成功。

**步骤 3：运行并验证 DI 初始化不崩溃**

```powershell
dotnet run --project src/TokenPulse.App
```

预期：App 正常启动，无异常。

**步骤 4：提交**

---

### 任务 3.3：实现 Dashboard ViewModel 绑定

**文件：**
- 修改：`ccusage-gui/src/TokenPulse.App/ViewModels/MainWindowViewModel.cs`
- 创建：`ccusage-gui/src/TokenPulse.App/ViewModels/DashboardViewModel.cs`
- 修改：`ccusage-gui/src/TokenPulse.App/Views/MainWindow.axaml`
- 创建：`ccusage-gui/src/TokenPulse.App/Views/DashboardView.axaml`

**步骤 1：创建 DashboardViewModel**

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TokenPulse.Core.Models;
using TokenPulse.Core.Services;

namespace TokenPulse.App.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly DashboardService _dashboardService;
    private readonly CacheService _cacheService;

    [ObservableProperty]
    private decimal _todayCostUsd;

    [ObservableProperty]
    private decimal _weekCostUsd;

    [ObservableProperty]
    private decimal _monthCostUsd;

    [ObservableProperty]
    private long _todayTokens;

    [ObservableProperty]
    private string _status = "loading";

    [ObservableProperty]
    private bool _isLoading;

    public DashboardViewModel(DashboardService dashboardService, CacheService cacheService)
    {
        _dashboardService = dashboardService;
        _cacheService = cacheService;
        _ = LoadFromCacheAsync();
    }

    private async Task LoadFromCacheAsync()
    {
        var dashboard = await _cacheService.GetLatestDashboardAsync();
        if (dashboard != null)
        {
            UpdateFromDashboard(dashboard);
        }
    }

    private void UpdateFromDashboard(DashboardSummary summary)
    {
        TodayCostUsd = summary.TodayCostUsd;
        WeekCostUsd = summary.WeekCostUsd;
        MonthCostUsd = summary.MonthCostUsd;
        TodayTokens = summary.TodayTokens;
        Status = summary.Status;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        Status = "refreshing";
        try
        {
            // MVP: 先从 cache 读，后续接入真正的 data source
            var dashboard = await _cacheService.GetLatestDashboardAsync();
            if (dashboard != null)
            {
                UpdateFromDashboard(dashboard);
            }
            else
            {
                Status = "no data";
            }
        }
        catch (Exception ex)
        {
            Status = "error";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
```

**步骤 2：创建 DashboardView.axaml**

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid RowDefinitions="Auto, Auto, *" Margin="20">
        <TextBlock Grid.Row="0" Text="Dashboard" FontSize="24" FontWeight="Bold" Margin="0 0 0 20"/>

        <Grid Grid.Row="1" ColumnDefinitions="*, *, *, *" Margin="0 0 0 20">
            <Border Grid.Column="0" Background="#f8f9fa" CornerRadius="8" Padding="15" Margin="0 0 10 0">
                <StackPanel>
                    <TextBlock Text="今日成本" Foreground="#666" FontSize="12"/>
                    <TextBlock Text="{Binding TodayCostUsd, StringFormat=${0:F2}}" FontSize="24" FontWeight="Bold" Margin="0 5 0 0"/>
                </StackPanel>
            </Border>
            <Border Grid.Column="1" Background="#f8f9fa" CornerRadius="8" Padding="15" Margin="0 0 10 0">
                <StackPanel>
                    <TextBlock Text="本周成本" Foreground="#666" FontSize="12"/>
                    <TextBlock Text="{Binding WeekCostUsd, StringFormat=${0:F2}}" FontSize="24" FontWeight="Bold" Margin="0 5 0 0"/>
                </StackPanel>
            </Border>
            <Border Grid.Column="2" Background="#f8f9fa" CornerRadius="8" Padding="15" Margin="0 0 10 0">
                <StackPanel>
                    <TextBlock Text="本月成本" Foreground="#666" FontSize="12"/>
                    <TextBlock Text="{Binding MonthCostUsd, StringFormat=${0:F2}}" FontSize="24" FontWeight="Bold" Margin="0 5 0 0"/>
                </StackPanel>
            </Border>
            <Border Grid.Column="3" Background="#f8f9fa" CornerRadius="8" Padding="15">
                <StackPanel>
                    <TextBlock Text="今日 Tokens" Foreground="#666" FontSize="12"/>
                    <TextBlock Text="{Binding TodayTokens, StringFormat={}{0:N0}}" FontSize="24" FontWeight="Bold" Margin="0 5 0 0"/>
                </StackPanel>
            </Border>
        </Grid>

        <Button Grid.Row="2" Command="{Binding RefreshCommand}" Content="Refresh" HorizontalAlignment="Left"/>
    </Grid>
</UserControl>
```

**步骤 3：更新 MainWindow 集成 DashboardView**

修改 `MainWindow.axaml`：

```xml
<Window ...>
    <Grid RowDefinitions="Auto, *">
        <TabControl Grid.Row="1" Margin="10">
            <TabItem Header="Dashboard">
                <local:DashboardView DataContext="{Binding Dashboard}"/>
            </TabItem>
            <TabItem Header="Settings">
                <TextBlock Text="Settings page coming soon" HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </TabItem>
        </TabControl>
    </Grid>
</Window>
```

修改 `MainWindowViewModel.cs`：

```csharp
public partial class MainWindowViewModel : ViewModelBase
{
    public DashboardViewModel Dashboard { get; }

    public MainWindowViewModel()
    {
        Dashboard = AppServices.Get<DashboardViewModel>();
    }
}
```

同时在 `AppServices.cs` 注册 ViewModel：

```csharp
// ViewModels
services.AddTransient<DashboardViewModel>();
```

**步骤 4：验证构建和运行**

```powershell
dotnet run --project src/TokenPulse.App
```

预期：窗口显示 Dashboard Tab，有 4 个卡片和 Refresh 按钮，点击按钮不崩溃。

**步骤 5：提交**

---

### 任务 3.4：实现托盘图标和菜单

**文件：**
- 创建：`ccusage-gui/src/TokenPulse.App/Tray/TrayService.cs`
- 修改：`ccusage-gui/src/TokenPulse.App/App.axaml.cs`

**步骤 1：创建 TrayService**

```csharp
using Avalonia.Controls;
using TokenPulse.App.ViewModels;

namespace TokenPulse.App.Tray;

public class TrayService
{
    private readonly Window _mainWindow;
    private TrayIcon? _trayIcon;

    public TrayService(Window mainWindow)
    {
        _mainWindow = mainWindow;
    }

    public void Initialize()
    {
        _trayIcon = new TrayIcon
        {
            ToolTipText = "AI Coding Cost Monitor",
            Menu = new NativeMenu
            {
                Items =
                {
                    new NativeMenuItem { Header = "Open", Command = new RelayCommand(ShowWindow) },
                    new NativeMenuItem { Header = "Exit", Command = new RelayCommand(ExitApp) }
                }
            }
        };

        _trayIcon.Clicked += (_, _) => ShowWindow();
    }

    public void UpdateToolTip(string tooltip)
    {
        if (_trayIcon != null)
        {
            _trayIcon.ToolTipText = tooltip;
        }
    }

    private void ShowWindow()
    {
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    private void ExitApp()
    {
        _mainWindow.Close();
        Environment.Exit(0);
    }
}
```

**步骤 2：在 App 启动时初始化托盘**

修改 `App.axaml.cs`：

```csharp
public override void OnFrameworkInitializationCompleted()
{
    AppServices.Initialize();

    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
    {
        var mainWindow = new MainWindow
        {
            DataContext = new MainWindowViewModel(),
        };
        desktop.MainWindow = mainWindow;

        var trayService = new TrayService(mainWindow);
        trayService.Initialize();
    }

    base.OnFrameworkInitializationCompleted();
}
```

**步骤 3：验证构建和运行**

```powershell
dotnet run --project src/TokenPulse.App
```

预期：系统托盘出现图标，右键有 Open/Exit 菜单，点击可以打开窗口。

**注意：** macOS 可能需要额外配置才能在菜单栏显示图标，Windows 通常直接可见。

**步骤 4：提交**

---

## 阶段 4：数据流闭环和 MVP 完成

### 任务 4.1：连接 CLI 数据源到 Dashboard 刷新

**文件：**
- 修改：`ccusage-gui/src/TokenPulse.App/ViewModels/DashboardViewModel.cs`
- 修改：`ccusage-gui/src/TokenPulse.App/DependencyInjection/AppServices.cs`

**步骤 1：修改 DashboardViewModel 接入真实数据源**

```csharp
private readonly IUsageDataSource _dataSource; // 添加这个字段

public DashboardViewModel(
    DashboardService dashboardService,
    CacheService cacheService,
    IUsageDataSource dataSource) // 注入
{
    _dashboardService = dashboardService;
    _cacheService = cacheService;
    _dataSource = dataSource;
    _ = LoadFromCacheAsync();
}

[RelayCommand]
public async Task RefreshAsync()
{
    IsLoading = true;
    Status = "refreshing";
    try
    {
        var rows = await _dataSource.GetDailyUsageAsync();
        if (rows.Any())
        {
            var dashboard = _dashboardService.BuildDashboard(rows);
            await _cacheService.SaveDashboardAsync(dashboard);
            await _cacheService.SaveDailyRowsAsync(rows);
            UpdateFromDashboard(dashboard);
        }
        else
        {
            Status = "no data";
        }
    }
    catch (Exception ex)
    {
        Status = "error: " + ex.Message;
    }
    finally
    {
        IsLoading = false;
    }
}
```

**步骤 2：验证构建和运行**

```powershell
dotnet run --project src/TokenPulse.App
```

预期：点击 Refresh 按钮会调用 CLI 并更新数据（如果本地有 ccusage 数据的话）。

**步骤 3：提交**

---

### 任务 4.2：实现后台定时刷新服务

**文件：**
- 创建：`ccusage-gui/src/TokenPulse.App/Services/BackgroundRefreshService.cs`
- 修改：`ccusage-gui/src/TokenPulse.App/App.axaml.cs`

**步骤 1：实现后台刷新服务**

```csharp
namespace TokenPulse.App.Services;

public class BackgroundRefreshService
{
    private readonly Func<Task> _refreshAction;
    private readonly int _refreshMinutes;
    private Timer? _timer;
    private bool _isRunning;

    public BackgroundRefreshService(Func<Task> refreshAction, int refreshMinutes = 15)
    {
        _refreshAction = refreshAction;
        _refreshMinutes = refreshMinutes;
    }

    public void Start()
    {
        if (_timer != null) return;
        var interval = TimeSpan.FromMinutes(_refreshMinutes);
        _timer = new Timer(OnTimerTick, null, interval, interval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private async void OnTimerTick(object? state)
    {
        if (_isRunning) return;
        _isRunning = true;
        try
        {
            await _refreshAction();
        }
        catch
        {
            // 静默失败，不打断用户
        }
        finally
        {
            _isRunning = false;
        }
    }
}
```

**步骤 2：在 App 启动时初始化并启动**

修改 `App.axaml.cs` 在 OnFrameworkInitializationCompleted 末尾：

```csharp
var vm = (MainWindowViewModel)desktop.MainWindow.DataContext;
var refreshService = new BackgroundRefreshService(
    () => vm.Dashboard.RefreshCommand.ExecuteAsync(null),
    15);
refreshService.Start();
```

**步骤 3：验证构建和运行**

```powershell
dotnet run --project src/TokenPulse.App
```

预期：App 启动后 15 分钟会自动刷新（可以临时改为 1 分钟测试）。

**步骤 4：提交**

---

### 任务 4.3：实现无数据引导和空状态

**文件：**
- 创建：`ccusage-gui/src/TokenPulse.App/Views/EmptyStateView.axaml`
- 修改：`ccusage-gui/src/TokenPulse.App/ViewModels/DashboardViewModel.cs`
- 修改：`ccusage-gui/src/TokenPulse.App/Views/DashboardView.axaml`

**步骤 1：创建 EmptyStateView**

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid Margin="40" HorizontalAlignment="Center" VerticalAlignment="Center">
        <StackPanel Spacing="20" HorizontalAlignment="Center">
            <TextBlock Text="📊" FontSize="64" HorizontalAlignment="Center"/>
            <TextBlock Text="尚未找到 AI 编程使用记录" FontSize="20" HorizontalAlignment="Center"/>
            <TextBlock Text="请确保已安装 ccusage CLI，并且本地有 Claude Code 或 Codex 数据"
                       Foreground="#666" TextWrapping="Wrap" MaxWidth="400" HorizontalAlignment="Center"/>
            <Button Content="重新扫描" Command="{Binding RefreshCommand}" HorizontalAlignment="Center"/>
        </StackPanel>
    </Grid>
</UserControl>
```

**步骤 2：在 DashboardView 中集成空状态切换**

使用 Converter 或简单的 DataTrigger 来切换有数据/无数据视图。

**步骤 3：验证构建和运行**

```powershell
dotnet run --project src/TokenPulse.App
```

预期：无数据时显示友好引导页面。

**步骤 4：提交**

---

### 任务 4.4：实现异常通知触发

**文件：**
- 修改：`ccusage-gui/src/TokenPulse.App/ViewModels/DashboardViewModel.cs`
- 修改：`ccusage-gui/src/TokenPulse.App/DependencyInjection/AppServices.cs`

**步骤 1：在 Refresh 后检测异常并通知**

```csharp
// 在 RefreshAsync 方法中检测异常
var dashboard = _dashboardService.BuildDashboard(rows);
var anomalies = _anomalyDetector.DetectAnomalies(
    rows.FirstOrDefault(r => r.Date == _dateService.Today()) ?? new DailyUsageSummary(),
    rows.Take(7).ToList(),
    minAbsoluteCost: 0.05m);

if (anomalies.Any())
{
    foreach (var anomaly in anomalies)
    {
        await _notification.ShowAsync("AI 成本异常提醒", anomaly.Message);
    }
}
```

需要注入 `AnomalyDetector`、`DateRangeService` 和 `ISystemNotification`。

**步骤 2：验证构建和运行**

```powershell
dotnet run --project src/TokenPulse.App
```

预期：检测到异常时触发系统通知。

**步骤 3：提交**

---

## 阶段 5：Breakdown / Settings / Reports 页面

（这 3 个页面每个拆 2-3 个任务，每个任务 1-2 小时工作量，模式和 Dashboard 一样：ViewModel 测试 → 界面 → 绑定）

### 任务 5.1：Breakdown 页面 - 按 Agent/Model/Project 拆分列表

### 任务 5.2：Settings 页面 - 可编辑配置和保存

### 任务 5.3：Reports 页面 - Daily/Monthly 表格视图

### 任务 5.4：趋势图（可选，使用 LiveCharts2）

---

## 阶段 6：打包和发布

### 任务 6.1：配置 Windows 打包

### 任务 6.2：配置 macOS 打包

### 任务 6.3：创建 GitHub Release 流程

---

## 审查范围

详见：`.docs/plans/2026-06-17-ccusage-gui-avalonia-review-scope.md`

---

## 计划完成后下一步

- 修复 `App.axaml.cs` 中 DI 初始化的异步问题（不要用 .GetAwaiter().GetResult()）
- 为 `CCUsageCliDataSource` 添加超时和错误重试
- 考虑将 CLI 路径配置化
- 实现真正的平台通知（Windows Toast / macOS NSUserNotification）
- 添加托盘菜单显示今日成本的 tooltip 动态更新
- 异常检测规则扩展到模型和项目维度
