# 审查范围

**关联计划**: .docs/plans/2026-06-17-ccusage-gui-avalonia-implementation.md
**开始时间**: 2026-06-17 23:41:21 +08:00

## 预期修改文件（全部在新仓库 ccusage-gui 中）

### 解决方案和项目文件
- Tokenmetry.sln
- src/Tokenmetry.Core/Tokenmetry.Core.csproj
- src/Tokenmetry.Infrastructure/Tokenmetry.Infrastructure.csproj
- src/Tokenmetry.App/Tokenmetry.App.csproj
- tests/Tokenmetry.Core.Tests/Tokenmetry.Core.Tests.csproj
- tests/Tokenmetry.Infrastructure.Tests/Tokenmetry.Infrastructure.Tests.csproj

### Core 层
- src/Tokenmetry.Core/Models/DailyUsageSummary.cs
- src/Tokenmetry.Core/Models/DashboardSummary.cs
- src/Tokenmetry.Core/Models/AnomalyNotice.cs
- src/Tokenmetry.Core/Models/AppSettings.cs
- src/Tokenmetry.Core/Abstractions/IUsageDataSource.cs
- src/Tokenmetry.Core/Abstractions/IUsageCache.cs
- src/Tokenmetry.Core/Abstractions/ISystemNotification.cs
- src/Tokenmetry.Core/Services/DateRangeService.cs
- src/Tokenmetry.Core/Services/DashboardService.cs
- src/Tokenmetry.Core/Services/AnomalyDetector.cs
- src/Tokenmetry.Core/Services/CacheService.cs

### Infrastructure 层
- src/Tokenmetry.Infrastructure/CCUsageCli/CCUsageCliParser.cs
- src/Tokenmetry.Infrastructure/CCUsageCli/CCUsageCliRunner.cs
- src/Tokenmetry.Infrastructure/CCUsageCli/CCUsageCliDataSource.cs
- src/Tokenmetry.Infrastructure/Cache/SqliteUsageCache.cs
- src/Tokenmetry.Infrastructure/Notifications/NullSystemNotification.cs
- tests/Tokenmetry.Infrastructure.Tests/fixtures/sample-daily.json

### Avalonia App 层
- src/Tokenmetry.App/App.axaml
- src/Tokenmetry.App/App.axaml.cs
- src/Tokenmetry.App/ViewModels/ViewModelBase.cs
- src/Tokenmetry.App/ViewModels/MainWindowViewModel.cs
- src/Tokenmetry.App/ViewModels/DashboardViewModel.cs
- src/Tokenmetry.App/Views/MainWindow.axaml
- src/Tokenmetry.App/Views/MainWindow.axaml.cs
- src/Tokenmetry.App/Views/DashboardView.axaml
- src/Tokenmetry.App/Views/EmptyStateView.axaml
- src/Tokenmetry.App/DependencyInjection/AppServices.cs
- src/Tokenmetry.App/Tray/TrayService.cs
- src/Tokenmetry.App/Services/BackgroundRefreshService.cs

### 测试文件
- tests/Tokenmetry.Core.Tests/Usings.cs
- tests/Tokenmetry.Core.Tests/Models/DailyUsageSummaryTests.cs
- tests/Tokenmetry.Core.Tests/Models/DashboardSummaryTests.cs
- tests/Tokenmetry.Core.Tests/Services/DateRangeServiceTests.cs
- tests/Tokenmetry.Core.Tests/Services/DashboardServiceTests.cs
- tests/Tokenmetry.Core.Tests/Services/AnomalyDetectorTests.cs
- tests/Tokenmetry.Core.Tests/Services/CacheServiceTests.cs
- tests/Tokenmetry.Core.Tests/Notifications/NullSystemNotificationTests.cs
- tests/Tokenmetry.Infrastructure.Tests/Usings.cs
- tests/Tokenmetry.Infrastructure.Tests/CCUsageCli/CCUsageCliParserTests.cs
- tests/Tokenmetry.Infrastructure.Tests/CCUsageCli/CCUsageCliRunnerTests.cs
- tests/Tokenmetry.Infrastructure.Tests/Cache/SqliteUsageCacheTests.cs

## 设计文档

- .docs/plans/2026-06-17-ccusage-gui-design.md

---

**注意：** 本项目是独立仓库，不在当前 `ccusage` monorepo 内。所有代码都应该在平行目录 `ccusage-gui` 中创建。
