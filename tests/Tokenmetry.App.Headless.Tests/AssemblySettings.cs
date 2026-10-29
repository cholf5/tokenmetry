using Xunit;

// 进程级单例纪律：Translations（界面语言）是进程全局状态，而本套件里有大量"切语言 → 断言文案"
// 的用例（TrendChart/Donut/Heatmap tooltip、表格列宽十语种 theory、页面文案）。xUnit 默认按测试类
// 并行，语言切换会在别的类里被观察到（译文断言偶发红）。
// 2026-10-29 前端修缮批次新增 40 条十语种列宽 theory 后放大成实际风险 → 与 App.Tests 同款纪律：
// 整个程序集串行，换取确定性（headless 渲染本身不慢，串行后的总时长仍在两分钟级）。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
