# 审查范围

**关联计划**: .docs/plans/2026-06-17-ccusage-gui-avalonia-implementation.md
**开始时间**: 2026-06-17 23:41:21 +08:00

## 预期修改文件（全部在新仓库 ccusage-gui 中）

### 解决方案和项目文件
- TokenPulse.sln
- src/TokenPulse.Core/TokenPulse.Core.csproj
- src/TokenPulse.Infrastructure/TokenPulse.Infrastructure.csproj
- src/TokenPulse.App/TokenPulse.App.csproj
- tests/TokenPulse.Core.Tests/TokenPulse.Core.Tests.csproj
- tests/TokenPulse.Infrastructure.Tests/TokenPulse.Infrastructure.Tests.csproj

### Core 层
- src/TokenPulse.Core/Models/DailyUsageSummary.cs
- src/TokenPulse.Core/Models/DashboardSummary.cs
- src/TokenPulse.Core/Models/AnomalyNotice.cs
- src/TokenPulse.Core/Models/AppSettings.cs
- src/TokenPulse.Core/Abstractions/IUsageDataSource.cs
- src/TokenPulse.Core/Abstractions/IUsageCache.cs
- src/TokenPulse.Core/Abstractions/ISystemNotification.cs
- src/TokenPulse.Core/Services/DateRangeService.cs
- src/TokenPulse.Core/Services/DashboardService.cs
- src/TokenPulse.Core/Services/AnomalyDetector.cs
- src/TokenPulse.Core/Services/CacheService.cs

### Infrastructure 层
- src/TokenPulse.Infrastructure/CCUsageCli/CCUsageCliParser.cs
- src/TokenPulse.Infrastructure/CCUsageCli/CCUsageCliRunner.cs
- src/TokenPulse.Infrastructure/CCUsageCli/CCUsageCliDataSource.cs
- src/TokenPulse.Infrastructure/Cache/SqliteUsageCache.cs
- src/TokenPulse.Infrastructure/Notifications/NullSystemNotification.cs
- tests/TokenPulse.Infrastructure.Tests/fixtures/sample-daily.json

### Avalonia App 层
- src/TokenPulse.App/App.axaml
- src/TokenPulse.App/App.axaml.cs
- src/TokenPulse.App/ViewModels/ViewModelBase.cs
- src/TokenPulse.App/ViewModels/MainWindowViewModel.cs
- src/TokenPulse.App/ViewModels/DashboardViewModel.cs
- src/TokenPulse.App/Views/MainWindow.axaml
- src/TokenPulse.App/Views/MainWindow.axaml.cs
- src/TokenPulse.App/Views/DashboardView.axaml
- src/TokenPulse.App/Views/EmptyStateView.axaml
- src/TokenPulse.App/DependencyInjection/AppServices.cs
- src/TokenPulse.App/Tray/TrayService.cs
- src/TokenPulse.App/Services/BackgroundRefreshService.cs

### 测试文件
- tests/TokenPulse.Core.Tests/Usings.cs
- tests/TokenPulse.Core.Tests/Models/DailyUsageSummaryTests.cs
- tests/TokenPulse.Core.Tests/Models/DashboardSummaryTests.cs
- tests/TokenPulse.Core.Tests/Services/DateRangeServiceTests.cs
- tests/TokenPulse.Core.Tests/Services/DashboardServiceTests.cs
- tests/TokenPulse.Core.Tests/Services/AnomalyDetectorTests.cs
- tests/TokenPulse.Core.Tests/Services/CacheServiceTests.cs
- tests/TokenPulse.Core.Tests/Notifications/NullSystemNotificationTests.cs
- tests/TokenPulse.Infrastructure.Tests/Usings.cs
- tests/TokenPulse.Infrastructure.Tests/CCUsageCli/CCUsageCliParserTests.cs
- tests/TokenPulse.Infrastructure.Tests/CCUsageCli/CCUsageCliRunnerTests.cs
- tests/TokenPulse.Infrastructure.Tests/Cache/SqliteUsageCacheTests.cs

## 设计文档

- .docs/plans/2026-06-17-ccusage-gui-design.md

---

**注意：** 本项目是独立仓库，不在当前 `ccusage` monorepo 内。所有代码都应该在平行目录 `ccusage-gui` 中创建。
