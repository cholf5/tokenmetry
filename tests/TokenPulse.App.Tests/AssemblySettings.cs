using Xunit;

// 进程级单例纪律：Translations（界面语言）与 BackgroundRefreshService 的单飞槽是进程全局状态，
// 语言切换用例与文案断言用例并行跑会互相污染（如重扫 toast 在英文窗口期被断言成中文）。
// 本套件很小（<200ms），整体串行换取确定性——与仓库既有"serial test collections"纪律一致。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
