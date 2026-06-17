# 审查范围

**关联计划**: .docs/plans/2026-06-17-ccusage-gui-avalonia-implementation.md
**开始时间**: 2026-06-17 23:41:21 +08:00

## 预期修改文件（全部在新仓库 ../ccusage-gui 中）

### 解决方案和项目文件
- CCUsage.sln
- src/CCUsage.Core/CCUsage.Core.csproj
- src/CCUsage.Infrastructure/CCUsage.Infrastructure.csproj
- src/CCUsage.App/CCUsage.App.csproj
- tests/CCUsage.Core.Tests/CCUsage.Core.Tests.csproj
- tests/CCUsage.Infrastructure.Tests/CCUsage.Infrastructure.Tests.csproj

### Core 层
- src/CCUsage.Core/Models/DailyUsageSummary.cs
- src/CCUsage.Core/Models/DashboardSummary.cs
- src/CCUsage.Core/Models/AnomalyNotice.cs
- src/CCUsage.Core/Models/AppSettings.cs
- src/CCUsage.Core/Abstractions/IUsageDataSource.cs
- src/CCUsage.Core/Abstractions/IUsageCache.cs
- src/CCUsage.Core/Abstractions/ISystemNotification.cs
- src/CCUsage.Core/Services/DateRangeService.cs
- src/CCUsage.Core/Services/DashboardService.cs
- src/CCUsage.Core/Services/AnomalyDetector.cs
- src/CCUsage.Core/Services/CacheService.cs

### Infrastructure 层
- src/CCUsage.Infrastructure/CCUsageCli/CCUsageCliParser.cs
- src/CCUsage.Infrastructure/CCUsageCli/CCUsageCliRunner.cs
- src/CCUsage.Infrastructure/CCUsageCli/CCUsageCliDataSource.cs
- src/CCUsage.Infrastructure/Cache/SqliteUsageCache.cs
- src/CCUsage.Infrastructure/Notifications/NullSystemNotification.cs
- tests/CCUsage.Infrastructure.Tests/fixtures/sample-daily.json

### Avalonia App 层
- src/CCUsage.App/App.axaml
- src/CCUsage.App/App.axaml.cs
- src/CCUsage.App/ViewModels/ViewModelBase.cs
- src/CCUsage.App/ViewModels/MainWindowViewModel.cs
- src/CCUsage.App/ViewModels/DashboardViewModel.cs
- src/CCUsage.App/Views/MainWindow.axaml
- src/CCUsage.App/Views/MainWindow.axaml.cs
- src/CCUsage.App/Views/DashboardView.axaml
- src/CCUsage.App/Views/EmptyStateView.axaml
- src/CCUsage.App/DependencyInjection/AppServices.cs
- src/CCUsage.App/Tray/TrayService.cs
- src/CCUsage.App/Services/BackgroundRefreshService.cs

### 测试文件
- tests/CCUsage.Core.Tests/Usings.cs
- tests/CCUsage.Core.Tests/Models/DailyUsageSummaryTests.cs
- tests/CCUsage.Core.Tests/Models/DashboardSummaryTests.cs
- tests/CCUsage.Core.Tests/Services/DateRangeServiceTests.cs
- tests/CCUsage.Core.Tests/Services/DashboardServiceTests.cs
- tests/CCUsage.Core.Tests/Services/AnomalyDetectorTests.cs
- tests/CCUsage.Core.Tests/Services/CacheServiceTests.cs
- tests/CCUsage.Core.Tests/Notifications/NullSystemNotificationTests.cs
- tests/CCUsage.Infrastructure.Tests/Usings.cs
- tests/CCUsage.Infrastructure.Tests/CCUsageCli/CCUsageCliParserTests.cs
- tests/CCUsage.Infrastructure.Tests/CCUsageCli/CCUsageCliRunnerTests.cs
- tests/CCUsage.Infrastructure.Tests/Cache/SqliteUsageCacheTests.cs

## 设计文档

- .docs/plans/2026-06-17-ccusage-gui-design.md

---

**注意：** 本项目是独立仓库，不在当前 `ccusage` monorepo 内。所有代码都应该在平行目录 `../ccusage-gui` 中创建。
