using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

namespace Tokenmetry.App.Tests.ViewModels;

/// <summary>
/// 数据与计价页 VM 测试（S-004 自 SettingsViewModelTests 拆出）：数据源表、重扫、统计口径、
/// 模型目录与别名映射。应用偏好（主题/语言/刷新间隔）在 SettingsViewModelTests。
/// </summary>
public class DataPricingViewModelTests
{
    // ---- S-002 数据源表 ----

    [Fact]
    public void Constructor_BuildsSourceRowsFromScanRegistry()
    {
        var reader = new FakeScanReader();
        reader.Register("Claude Code", new IngestionSourceScanSnapshot("Claude Code", JsonlScanStatus.Ok, 2184, Now));
        reader.Register("Copilot CLI", new IngestionSourceScanSnapshot("Copilot CLI", JsonlScanStatus.Partial, 128, Now));
        reader.Register("Gemini CLI", new IngestionSourceScanSnapshot("Gemini CLI", JsonlScanStatus.NoData, 0, Now));
        reader.Registered.Add("ZCode"); // 已注册但本轮尚未扫描

        var viewModel = CreateViewModel(new AppSettings(), new FakeSettingsStore(), reader: reader);

        var rows = viewModel.SourceRows;
        rows.Select(r => r.Agent).Should().Equal("Claude Code", "Copilot CLI", "Gemini CLI", "ZCode");

        var ok = rows[0];
        ok.StatusText.Should().Be("● 已连接");
        ok.IsOk.Should().BeTrue();
        ok.RecordCountText.Should().Be("2,184");
        ok.LastScanText.Should().NotBe("—");

        rows[1].StatusText.Should().Be("● 部分数据");
        rows[1].IsPartial.Should().BeTrue();

        rows[2].StatusText.Should().Be("● 未检出");
        rows[2].IsNoData.Should().BeTrue();
        rows[2].RecordCountText.Should().Be("0");

        rows[3].StatusText.Should().Be("● 尚未扫描");
        rows[3].IsPending.Should().BeTrue();
        rows[3].LastScanText.Should().Be("—");
        rows[3].RecordCountText.Should().Be("—");
    }

    [Fact]
    public void OnNavigatedTo_RebuildsSourceRowsFromLatestSnapshot()
    {
        // 单例页重进刷新（INavigationAware）：进入页面按最新快照重建
        var reader = new FakeScanReader();
        var viewModel = CreateViewModel(new AppSettings(), new FakeSettingsStore(), reader: reader);

        viewModel.SourceRows.Should().BeEmpty();

        reader.Registered.Add("Claude Code");
        reader.Register("Claude Code", new IngestionSourceScanSnapshot("Claude Code", JsonlScanStatus.Ok, 7, Now));
        viewModel.OnNavigatedTo();

        viewModel.SourceRows.Should().HaveCount(1);
        viewModel.SourceRows[0].IsOk.Should().BeTrue();
        viewModel.SourceRows[0].RecordCountText.Should().Be("7");

        // 再次进入时快照已更新（如后台刷新发生）→ 行刷新
        reader.Register("Claude Code", new IngestionSourceScanSnapshot("Claude Code", JsonlScanStatus.Partial, 9, Now));
        viewModel.OnNavigatedTo();

        viewModel.SourceRows[0].IsPartial.Should().BeTrue();
        viewModel.SourceRows[0].RecordCountText.Should().Be("9");
    }

    [Fact]
    public async Task RescanAsync_CompletesReloadsRowsAndShowsToast()
    {
        // TC-001（全局重扫定案）：按钮扫描态 → 完成后行重建 + toast（成功路径）
        var reader = new FakeScanReader();
        reader.Registered.Add("Claude Code");
        var toast = new FakeToastService();
        var gate = new TaskCompletionSource();
        var refresh = new BackgroundRefreshService(async () =>
        {
            await gate.Task; // 模拟扫描进行中
            reader.Register("Claude Code", new IngestionSourceScanSnapshot("Claude Code", JsonlScanStatus.Ok, 42, DateTimeOffset.Now));
            return true;
        });
        var viewModel = CreateViewModel(new AppSettings(), new FakeSettingsStore(),
            reader: reader, toast: toast, refreshService: refresh);

        var run = viewModel.RescanAsync();
        viewModel.IsScanning.Should().BeTrue();
        viewModel.ScanButtonText.Should().Be(Translations.Instance.ScanRunning);
        viewModel.SourceMetaText.Should().Be(Translations.Instance.SourceMetaScanning);

        gate.SetResult();
        await run;

        viewModel.IsScanning.Should().BeFalse();
        viewModel.ScanButtonText.Should().Be(Translations.Instance.ScanIdle);
        viewModel.SourceMetaText.Should().Be(Translations.Instance.SourceMetaIdle);
        viewModel.SourceRows.Single().RecordCountText.Should().Be("42");
        toast.Messages.Should().Contain(Translations.Instance.ToastScanDone);
        viewModel.Status.Should().Be("scan finished");
    }

    [Fact]
    public async Task RescanAsync_WhenRefreshFails_ShowsFailureInsteadOfFakeSuccess()
    {
        // P2-1：刷新路径可报告成败——失败如实「扫描失败」+ 数据表面板行级提示，不做假成功
        var reader = new FakeScanReader();
        reader.Registered.Add("Claude Code");
        reader.Register("Claude Code", new IngestionSourceScanSnapshot("Claude Code", JsonlScanStatus.Ok, 7, DateTimeOffset.Now));
        var toast = new FakeToastService();
        var refresh = new BackgroundRefreshService(() => Task.FromResult(false)); // 采集线报错（如适配器异常）
        var viewModel = CreateViewModel(new AppSettings(), new FakeSettingsStore(),
            reader: reader, toast: toast, refreshService: refresh);

        await viewModel.RescanAsync();

        viewModel.Status.Should().Be("scan error");
        viewModel.IsScanning.Should().BeFalse();
        viewModel.SourceMetaText.Should().Be(Translations.Instance.SourceMetaScanFailed);
        toast.Messages.Should().Contain(Translations.Instance.ToastScanFailed);
        toast.Messages.Should().NotContain(Translations.Instance.ToastScanDone);
        // 行仍为最近一次成功快照（如实旧数据 + 面板提示"可能非本轮"）
        viewModel.SourceRows.Single().RecordCountText.Should().Be("7");
    }

    [Fact]
    public async Task RescanAsync_WhenServiceUnwired_FailsHonestlyInsteadOfFakeSuccess()
    {
        // P0 语义：未接线（Headless 测试宿主等无 App 启动序列的环境）重扫 → 接线层抛错 →
        // 服务层转失败结果 → VM 如实「扫描失败」，绝不空转假成功
        var toast = new FakeToastService();
        var refresh = new BackgroundRefreshService(new BackgroundRefreshWiring().RunRefreshAsync);
        var viewModel = CreateViewModel(new AppSettings(), new FakeSettingsStore(),
            toast: toast, refreshService: refresh);

        await viewModel.RescanAsync();

        viewModel.Status.Should().Be("scan error");
        toast.Messages.Should().Contain(Translations.Instance.ToastScanFailed);
    }

    [Fact]
    public async Task RescanAsync_MergesIntoInFlightRefresh_WithoutExtraRun()
    {
        // 与定时器刷新共用同一单飞槽（审查修复轮 P2-2 合并语义）：已有刷新在跑时，
        // 重扫并入该轮等待其真实结果，不追加第二轮
        var ticks = 0;
        var gate = new TaskCompletionSource();
        var refresh = new BackgroundRefreshService(async () =>
        {
            Interlocked.Increment(ref ticks);
            await gate.Task;
            return true;
        });

        var first = refresh.TriggerRefreshAsync(); // 占住单飞槽（模拟定时器刷新在跑）
        var viewModel = CreateViewModel(new AppSettings(), new FakeSettingsStore(), refreshService: refresh);

        var rescan = viewModel.RescanAsync(); // 并入在跑的那轮
        await Task.Delay(50);
        ticks.Should().Be(1, "合并语义下重扫并入在跑的一轮，不得追加第二轮");

        gate.SetResult();
        (await first).Should().BeTrue();
        await rescan;

        ticks.Should().Be(1, "重扫应并入在跑的一轮，而不是再跑一遍");
        viewModel.IsScanning.Should().BeFalse();
        viewModel.Status.Should().Be("scan finished");
    }

    // ---- S-002 统计口径区定案 ----

    [Fact]
    public void CaliberItems_AreAllExplanatoryRowsWithoutFakeSwitches()
    {
        // 原型 5 个复选项逐项定案：4 项始终生效（管道内建行为）+ 1 项未提供（未实现），
        // 全部为说明样式（CaliberItem 记录，非布尔开关），不落假开关进 AppSettings。
        var items = DataPricingViewModel.CaliberItems;

        items.Should().HaveCount(5);
        items.Should().OnlyContain(i => i.Badge == "始终生效" || i.Badge == "未提供");
        items.Count(i => i.Badge == "始终生效").Should().Be(4);
        items.Count(i => i.Badge == "未提供").Should().Be(1);
        items.Should().Contain(i => i.Text.Contains("按 Agent + Session ID 去重"));
        items.Should().Contain(i => i.Text.Contains("区分输入、输出、缓存读取与缓存写入"));
        items.Should().Contain(i => i.Text.Contains("未知模型价格显示为「未知」"));
        items.Should().Contain(i => i.Text.Contains("自动合并") && i.Badge == "未提供");
        items.Should().Contain(i => i.Text.Contains("仅在本机处理"));
    }

    [Fact]
    public void Page_DescriptorMatchesDataPricingNavigationEntry()
    {
        var viewModel = CreateViewModel(new AppSettings(), new FakeSettingsStore());

        viewModel.Page.Id.Should().Be(PageId.DataPricing);
        viewModel.Page.Title.Should().Be("数据与计价");
        viewModel.Page.Subtitle.Should().Be("多 Agent 统一统计的关键是来源适配、去重和口径透明");
    }

    [Fact]
    public void LanguageSwitch_RetranslatesImperativeTextsAndRows()
    {
        // 数据与计价页无语言下拉：重译由 Translations 信号驱动（DI 单例直订阅）——
        // 命令式文案（重扫按钮）与快照行（状态列）在语言切换后重取串。
        var originalLanguage = Translations.Instance.CurrentLanguage;
        try
        {
            var reader = new FakeScanReader();
            reader.Register("Claude Code", new IngestionSourceScanSnapshot("Claude Code", JsonlScanStatus.Ok, 7, Now));
            var viewModel = CreateViewModel(new AppSettings(), new FakeSettingsStore(), reader: reader);

            Translations.Instance.SetLanguage(AppLanguage.English);

            viewModel.ScanButtonText.Should().Be(Translations.Instance.ScanIdle);
            Translations.Instance.ScanIdle.Should().Be("Rescan (all sources)");
            viewModel.SourceRows.Single().StatusText.Should().Be(Translations.Instance.StatusOk);
            viewModel.CaliberRows.Should().HaveCount(5);
        }
        finally
        {
            Translations.Instance.SetLanguage(originalLanguage);
        }
    }

    // ---- 模型聚合（别名映射）：规则增删即存即热更新 ----

    [Fact]
    public void Constructor_LoadsExistingAliasRulesAndSeedsResolver()
    {
        var settings = new AppSettings();
        settings.ModelAliases["ark/glm-5.3-flash"] = "glm-5.3-flash";
        var catalog = new ModelCatalog();

        var viewModel = CreateViewModel(settings, new FakeSettingsStore(), catalog: catalog);

        viewModel.AliasRules.Should().HaveCount(1);
        viewModel.AliasRules[0].Alias.Should().Be("ark/glm-5.3-flash");
        viewModel.AliasRules[0].Target.Should().Be("glm-5.3-flash");
        viewModel.AliasRules[0].Avatar.Vendor.Should().Be("glm", "规则行头像跟随规范名（统计里显示的是规范名）");
        viewModel.HasNoAliasRules.Should().BeFalse();
    }

    [Fact]
    public void AddAliasRule_PersistsAndHotUpdatesCatalog()
    {
        var settings = new AppSettings();
        var store = new FakeSettingsStore();
        var catalog = new ModelCatalog();
        var viewModel = CreateViewModel(settings, store, catalog: catalog);

        viewModel.NewAliasSource = "  ark/glm-5.3-flash ";
        viewModel.NewAliasTarget = "glm-5.3-flash";
        viewModel.AddAliasRuleCommand.Execute(null);

        settings.ModelAliases.Should().ContainKey("ark/glm-5.3-flash").WhoseValue.Should().Be("glm-5.3-flash");
        store.SaveCalls.Should().Be(1, "改完即落盘（全自动保存纪律）");
        // I-035：规则命中后聚合身份是目录条目 ID（glm-5.3-flash 是快照里的 zai 模型），
        // 关联方式如实记为「别名」——这条规则同时让计价引擎按该模型计价（此前只影响聚合）。
        var identity = catalog.ResolveIdentity("ark/glm-5.3-flash");
        identity.Kind.Should().Be(ModelMatchKind.Alias, "目录热更新，统计页查询即时按新映射聚合");
        identity.Id.Should().Be("glm-5-3-flash");
        viewModel.AliasRules.Should().HaveCount(1);
        viewModel.AliasRules[0].Avatar.Vendor.Should().Be("glm");
        viewModel.NewAliasSource.Should().BeEmpty("添加成功后清空输入框");
        viewModel.NewAliasTarget.Should().BeEmpty();
        viewModel.AliasError.Should().BeNull();
    }

    [Fact]
    public void AddAliasRule_WithBlankFields_ShowsInlineErrorAndDoesNotPersist()
    {
        var settings = new AppSettings();
        var store = new FakeSettingsStore();
        var viewModel = CreateViewModel(settings, store);

        viewModel.NewAliasSource = "ark/glm-5.3-flash";
        viewModel.AddAliasRuleCommand.Execute(null);

        viewModel.AliasError.Should().Be(Translations.Instance.AliasErrorRequired);
        store.SaveCalls.Should().Be(0);
        settings.ModelAliases.Should().BeEmpty();
    }

    [Fact]
    public void AddAliasRule_WithSelfMapping_ShowsInlineErrorAndDoesNotPersist()
    {
        // 解析器按原始名精确匹配，字面相同的规则无效果，录入侧劝阻
        // （"GLM-5.3-Flash" → "glm-5.3-flash" 是大小写变体映射，不受此限，属核心场景）。
        var settings = new AppSettings();
        var store = new FakeSettingsStore();
        var viewModel = CreateViewModel(settings, store);

        viewModel.NewAliasSource = "glm-5.3-flash";
        viewModel.NewAliasTarget = "glm-5.3-flash";
        viewModel.AddAliasRuleCommand.Execute(null);

        viewModel.AliasError.Should().Be(Translations.Instance.AliasErrorSame);
        store.SaveCalls.Should().Be(0);
        settings.ModelAliases.Should().BeEmpty();
    }

    [Fact]
    public void AddAliasRule_WithUnknownTarget_ShowsInlineErrorAndDoesNotPersist()
    {
        // 落空的重定向会把身份解析带偏（悬空目标压掉原始名命中），录入侧直接劝阻。
        var settings = new AppSettings();
        var store = new FakeSettingsStore();
        var viewModel = CreateViewModel(settings, store);

        viewModel.NewAliasSource = "some-raw-name";
        viewModel.NewAliasTarget = "totally-unknown-target-x";
        viewModel.AddAliasRuleCommand.Execute(null);

        viewModel.AliasError.Should().Be(Translations.Instance.AliasErrorTargetUnknown);
        store.SaveCalls.Should().Be(0);
        settings.ModelAliases.Should().BeEmpty();
    }

    [Fact]
    public void AddAliasRule_WithCaseVariantMapping_PersistsAndResolves()
    {
        // 大小写变体（GLM-5.3-Flash → glm-5.3-flash）是用户场景的核心：精确匹配的合法规则。
        var settings = new AppSettings();
        var catalog = new ModelCatalog();
        var viewModel = CreateViewModel(settings, new FakeSettingsStore(), catalog: catalog);

        viewModel.NewAliasSource = "GLM-5.3-Flash";
        viewModel.NewAliasTarget = "glm-5.3-flash";
        viewModel.AddAliasRuleCommand.Execute(null);

        settings.ModelAliases.Should().ContainKey("GLM-5.3-Flash").WhoseValue.Should().Be("glm-5.3-flash");
        catalog.ResolveIdentity("GLM-5.3-Flash").Kind.Should().Be(ModelMatchKind.Alias);
        catalog.ResolveIdentity("GLM-5.3-Flash").Id.Should().Be("glm-5-3-flash");
        viewModel.AliasError.Should().BeNull();
    }

    [Fact]
    public void AddAliasRule_WithDuplicateSource_ReplacesTarget()
    {
        var settings = new AppSettings();
        var catalog = new ModelCatalog();
        var viewModel = CreateViewModel(settings, new FakeSettingsStore(), catalog: catalog);

        viewModel.NewAliasSource = "ark/glm-5.3-flash";
        viewModel.NewAliasTarget = "GLM-5.3";
        viewModel.AddAliasRuleCommand.Execute(null);
        viewModel.NewAliasSource = "ark/glm-5.3-flash";
        viewModel.NewAliasTarget = "glm-5.3-flash";
        viewModel.AddAliasRuleCommand.Execute(null);

        settings.ModelAliases.Should().HaveCount(1, "同原始名覆盖旧规则（字典语义）");
        settings.ModelAliases["ark/glm-5.3-flash"].Should().Be("glm-5.3-flash");
        catalog.ResolveIdentity("ark/glm-5.3-flash").Kind.Should().Be(ModelMatchKind.Alias);
        viewModel.AliasRules.Should().HaveCount(1);
    }

    [Fact]
    public void RemoveAliasRule_PersistsAndHotUpdatesCatalog()
    {
        // 用目录自己关联不上的原始名——否则删掉规则后名字仍会被目录自动关联，
        // 测不出"规则移除"这件事（自动关联不是规则）。
        var settings = new AppSettings();
        settings.ModelAliases["glm46-preview-build"] = "glm-4.6";
        var store = new FakeSettingsStore();
        var catalog = new ModelCatalog();
        var viewModel = CreateViewModel(settings, store, catalog: catalog);
        catalog.ResolveIdentity("glm46-preview-build").Kind.Should().Be(ModelMatchKind.Alias, "规则已在构造时装入目录");

        viewModel.RemoveAliasRuleCommand.Execute("glm46-preview-build");

        settings.ModelAliases.Should().BeEmpty();
        store.SaveCalls.Should().Be(1);
        var identity = catalog.ResolveIdentity("glm46-preview-build");
        identity.Kind.Should().Be(ModelMatchKind.None);
        identity.Id.Should().Be("glm46-preview-build");
        viewModel.HasNoAliasRules.Should().BeTrue();
    }

    [Fact]
    public void RemoveAliasRule_WithUnknownAlias_IsNoOp()
    {
        var settings = new AppSettings();
        settings.ModelAliases["ark/glm-5.3-flash"] = "glm-5.3-flash";
        var store = new FakeSettingsStore();
        var viewModel = CreateViewModel(settings, store);

        viewModel.RemoveAliasRuleCommand.Execute("not-exists");

        store.SaveCalls.Should().Be(0);
        settings.ModelAliases.Should().HaveCount(1);
    }

    [Fact]
    public async Task AliasCandidates_ListUnlinkedRawNamesFirst()
    {
        // I-035：目录自动关联得上的名字不需要人管，候选清单把"关联不上的"排前面——
        // 这一栏的用途是处理待关联的名字（已关联的仍在清单里，用户可能想覆盖自动关联结果）。
        var query = new FakeQueryService(["glm-5.3-flash", "ark/glm-5.3-flash", "glm46-preview-build"]);
        var viewModel = CreateViewModel(new AppSettings(), new FakeSettingsStore(), query: query);

        await viewModel.LoadAliasCandidatesAsync(); // 构造路径 fire-and-forget，测试显式等待

        viewModel.AliasCandidates.Should().Equal(
            "glm46-preview-build",
            "ark/glm-5.3-flash",
            "glm-5.3-flash");
    }

    // ---- 模型目录（I-035）：条目增删改 + 保存即重算 ----

    private static (DataPricingViewModel ViewModel, int[] RefreshCalls) CreateCatalogViewModel(
        AppSettings settings,
        FakeSettingsStore store,
        ModelCatalog catalog)
    {
        var refreshCalls = new int[1];
        var viewModel = CreateViewModel(
            settings,
            store,
            refreshService: new BackgroundRefreshService(() =>
            {
                refreshCalls[0]++;
                return Task.FromResult(true);
            }),
            catalog: catalog);
        return (viewModel, refreshCalls);
    }

    [Fact]
    public void SaveModelDefinition_CreatesEntryPersistsAndTriggersReprice()
    {
        var settings = new AppSettings();
        var store = new FakeSettingsStore();
        var catalog = new ModelCatalog();
        var (viewModel, refreshCalls) = CreateCatalogViewModel(settings, store, catalog);

        viewModel.ModelFormId = "GLM-4.6"; // 大写 + 点号：按约定归一，不算错误
        viewModel.ModelFormVendor = "zai";
        viewModel.ModelFormDisplayName = "GLM 4.6 特价";
        viewModel.ModelFormInput = "1";
        viewModel.ModelFormOutput = "2";
        viewModel.SaveModelDefinitionCommand.Execute(null);

        settings.ModelDefinitions.Should().ContainSingle();
        settings.ModelDefinitions[0].Id.Should().Be("glm-4-6", "ID 归一到小写连字符约定");
        store.SaveCalls.Should().Be(1, "改完即落盘（全自动保存纪律）");
        catalog.FindById("glm-4-6")!.IsPriceOverridden.Should().BeTrue("目录热更新，统计与计价即时生效");
        catalog.FindById("glm-4-6")!.InputPerMillion.Should().Be(1m);
        refreshCalls[0].Should().Be(1, "价格改完必须触发一轮重算，否则数字不会变");
        viewModel.ModelFormError.Should().BeNull();
        viewModel.EditingModelId.Should().BeNull("保存后回到新建态");
    }

    [Fact]
    public void SaveModelDefinition_WithInvalidId_ShowsInlineErrorAndDoesNotPersist()
    {
        var settings = new AppSettings();
        var store = new FakeSettingsStore();
        var (viewModel, refreshCalls) = CreateCatalogViewModel(settings, store, new ModelCatalog());

        viewModel.ModelFormId = "bad#id";
        viewModel.ModelFormInput = "1";
        viewModel.ModelFormOutput = "2";
        viewModel.SaveModelDefinitionCommand.Execute(null);

        viewModel.ModelFormError.Should().Be(Translations.Instance.ModelFormErrorIdInvalid);
        settings.ModelDefinitions.Should().BeEmpty();
        store.SaveCalls.Should().Be(0);
        refreshCalls[0].Should().Be(0);
    }

    [Fact]
    public void SaveModelDefinition_WithHalfPrice_ShowsInlineError()
    {
        var settings = new AppSettings();
        var (viewModel, _) = CreateCatalogViewModel(settings, new FakeSettingsStore(), new ModelCatalog());

        viewModel.ModelFormId = "glm-4-6";
        viewModel.ModelFormInput = "1";
        viewModel.SaveModelDefinitionCommand.Execute(null);

        viewModel.ModelFormError.Should().Be(Translations.Instance.ModelFormErrorPricePair);
        settings.ModelDefinitions.Should().BeEmpty();
    }

    [Fact]
    public void SaveModelDefinition_WithoutPrice_RegistersIdentityOnly()
    {
        var settings = new AppSettings();
        var catalog = new ModelCatalog();
        var (viewModel, _) = CreateCatalogViewModel(settings, new FakeSettingsStore(), catalog);

        viewModel.ModelFormId = "my-local-model";
        viewModel.ModelFormVendor = "local";
        viewModel.SaveModelDefinitionCommand.Execute(null);

        catalog.FindById("my-local-model").Should().NotBeNull();
        catalog.FindById("my-local-model")!.HasPrice.Should().BeFalse("只登记身份，价格留空");
        catalog.UserEntries.Should().BeEmpty("没有价就不参与计价（绝不折 0）");
    }

    [Fact]
    public void SaveModelDefinition_WithProviderScope_PersistsScopeAndKeepsSiblingEntries()
    {
        // P-001 T5：作用域条目与裸名条目按 (ID, scope) 共存——保存作用域条目不得清掉同 ID 裸名条目。
        var settings = new AppSettings();
        settings.ModelDefinitions.Add(new ModelDefinitionOverride
        {
            Id = "glm-5-3-flash",
            InputPerMillion = 0.2m,
            OutputPerMillion = 0.6m,
        });
        var (viewModel, _) = CreateCatalogViewModel(settings, new FakeSettingsStore(), new ModelCatalog());

        viewModel.ModelFormId = "glm-5-3-flash";
        viewModel.ModelFormScope = "openrouter/z-ai";
        viewModel.ModelFormInput = "0.047";
        viewModel.ModelFormOutput = "0.12";
        viewModel.SaveModelDefinitionCommand.Execute(null);

        settings.ModelDefinitions.Should().HaveCount(2, "同 ID 不同作用域是两条独立条目（Lua 局部变量语义）");
        settings.ModelDefinitions.Should().Contain(entry =>
            entry.ProviderScope == "openrouter/z-ai" && entry.InputPerMillion == 0.047m);
        settings.ModelDefinitions.Should().Contain(entry =>
            string.IsNullOrWhiteSpace(entry.ProviderScope) && entry.InputPerMillion == 0.2m,
            "保存作用域条目不得清掉裸名条目");
        viewModel.ModelFormScope.Should().BeEmpty("保存后表单复位");
    }

    [Fact]
    public void EditModelDefinition_PrefillsScopeFromUserEntry()
    {
        var settings = new AppSettings();
        settings.ModelDefinitions.Add(new ModelDefinitionOverride
        {
            Id = "glm-5-3-flash",
            ProviderScope = "z-ai",
            InputPerMillion = 0.047m,
            OutputPerMillion = 0.12m,
        });
        var (viewModel, _) = CreateCatalogViewModel(settings, new FakeSettingsStore(), new ModelCatalog());

        viewModel.EditModelDefinitionCommand.Execute("glm-5-3-flash");

        viewModel.ModelFormScope.Should().Be("z-ai");
        viewModel.ModelFormInput.Should().Be("0.047");
    }

    [Fact]
    public void EditModelDefinition_LoadsEffectiveValuesAndRemoveRevertsToImported()
    {
        var settings = new AppSettings();
        settings.ModelDefinitions.Add(new ModelDefinitionOverride
        {
            Id = "glm-4.6",
            InputPerMillion = 9m,
            OutputPerMillion = 90m,
        });
        var catalog = new ModelCatalog(userDefinitions: settings.ModelDefinitions);
        var (viewModel, _) = CreateCatalogViewModel(settings, new FakeSettingsStore(), catalog);

        viewModel.EditModelDefinitionCommand.Execute("glm-4-6");

        viewModel.EditingModelId.Should().Be("glm-4-6");
        viewModel.ModelFormInput.Should().Be("9");
        viewModel.ModelFormOutput.Should().Be("90");
        viewModel.ModelFormVendor.Should().Be("zai", "未覆盖的字段回落导入层");

        viewModel.RemoveModelDefinitionCommand.Execute("glm-4-6");

        settings.ModelDefinitions.Should().BeEmpty();
        var reverted = catalog.FindById("glm-4-6")!;
        reverted.IsPriceOverridden.Should().BeFalse();
        reverted.InputPerMillion.Should().NotBe(9m, "移除覆盖后回到内置快照的价");
    }

    [Fact]
    public void CatalogRows_ComeFromTheCatalogAndExposeIdCandidates()
    {
        var settings = new AppSettings();
        var catalog = new ModelCatalog();
        var (viewModel, _) = CreateCatalogViewModel(settings, new FakeSettingsStore(), catalog);

        viewModel.ModelRows.Should().NotBeEmpty("内置快照条目也在目录里");
        viewModel.HasNoModelRows.Should().BeFalse();
        viewModel.ModelIdCandidates.Should().Contain("glm-4-6", "关联目标侧从目录的模型 ID 里挑");

        var row = viewModel.ModelRows.Single(item => item.Id == "glm-4-6");
        row.Vendor.Should().Be("zai");
        row.OriginText.Should().Be(Translations.Instance.ModelOriginBuiltin);
        row.HasPrice.Should().BeTrue();
        row.IsOverridden.Should().BeFalse();
    }

    [Fact]
    public void DataPricingViewModelConstruction_KeepsLegacyPricingOverridesPriced()
    {
        // 装配层回归钉（I-038 TC-001 的 App 缝）：AppServices 已把旧 PricingOverrides 折算进目录，
        // 本页 VM 构造（随外壳启动发生）此前用裸 ModelDefinitions 热更新，把旧配置从目录里冲掉——
        // 用户的旧价格覆盖启动即静默失效。合并口径收敛到 ModelCatalog.MergeUserDefinitions 后，
        // 装配与热更新必须同源。
        var settings = new AppSettings();
        settings.PricingOverrides.Add(new ModelPricingOverride { Model = "glm-5.3-flash", InputPerMillion = 9m, OutputPerMillion = 90m });
        var catalog = new ModelCatalog(userDefinitions: ModelCatalog.MergeUserDefinitions(settings));
        var (viewModel, _) = CreateCatalogViewModel(settings, new FakeSettingsStore(), catalog);

        viewModel.ModelRows.Should().Contain(row => row.Id == "glm-5-3-flash", "旧字段条目在目录表里可见");
        catalog.FindById("glm-5-3-flash")!.IsPriceOverridden.Should().BeTrue("VM 构造后旧价仍是用户覆盖");
        catalog.UserEntries.Should().Contain(entry => entry.ModelId == "glm-5-3-flash" && entry.InputPerMillion == 9m);
    }

    [Fact]
    public void RemoveModelDefinition_AlsoRemovesLegacyOnlyRule()
    {
        var settings = new AppSettings();
        settings.PricingOverrides.Add(new ModelPricingOverride { Model = "my-legacy-model", InputPerMillion = 3m, OutputPerMillion = 4m });
        var catalog = new ModelCatalog(userDefinitions: ModelCatalog.MergeUserDefinitions(settings));
        var (viewModel, _) = CreateCatalogViewModel(settings, new FakeSettingsStore(), catalog);

        viewModel.RemoveModelDefinitionCommand.Execute("my-legacy-model");

        settings.PricingOverrides.Should().BeEmpty("纯旧字段条目显式移除时连旧规则一起删（此前该行移除静默无效）");
        catalog.UserEntries.Should().NotContain(entry => entry.ModelId == "my-legacy-model");
    }

    [Fact]
    public void SaveModelDefinition_ClearsShadowedLegacyRule()
    {
        var settings = new AppSettings();
        settings.PricingOverrides.Add(new ModelPricingOverride { Model = "glm-4.6", InputPerMillion = 3m, OutputPerMillion = 30m });
        var catalog = new ModelCatalog(userDefinitions: ModelCatalog.MergeUserDefinitions(settings));
        var (viewModel, _) = CreateCatalogViewModel(settings, new FakeSettingsStore(), catalog);

        viewModel.ModelFormId = "glm-4-6";
        viewModel.ModelFormInput = "1";
        viewModel.ModelFormOutput = "2";
        viewModel.SaveModelDefinitionCommand.Execute(null);

        settings.PricingOverrides.Should().BeEmpty("新字段接管该 ID 后清掉被遮蔽的旧规则，防止删除新条目时旧价复活");
        settings.ModelDefinitions.Should().ContainSingle();
        catalog.FindById("glm-4-6")!.InputPerMillion.Should().Be(1m, "生效价以新字段为准");
    }

    [Fact]
    public void SaveModelDefinition_WithUnchangedImportedPrice_DoesNotPinPriceOverride()
    {
        var settings = new AppSettings();
        var catalog = new ModelCatalog();
        var (viewModel, _) = CreateCatalogViewModel(settings, new FakeSettingsStore(), catalog);

        viewModel.EditModelDefinitionCommand.Execute("glm-4-6"); // 表单预填导入层生效价
        viewModel.ModelFormDisplayName = "改名不改价";
        viewModel.SaveModelDefinitionCommand.Execute(null);

        var saved = settings.ModelDefinitions.Single();
        saved.InputPerMillion.Should().BeNull("未改动的生效价不落用户覆盖——models.dev 刷新继续更新该价");
        saved.DisplayName.Should().Be("改名不改价");
        catalog.FindById("glm-4-6")!.HasPrice.Should().BeTrue("元数据覆盖后价仍由导入层供给");
        catalog.FindById("glm-4-6")!.IsPriceOverridden.Should().BeFalse();
    }

    [Fact]
    public void SaveModelDefinition_WithChangedPrice_PinsPriceOverride()
    {
        var settings = new AppSettings();
        var catalog = new ModelCatalog();
        var (viewModel, _) = CreateCatalogViewModel(settings, new FakeSettingsStore(), catalog);

        viewModel.EditModelDefinitionCommand.Execute("glm-4-6");
        viewModel.ModelFormInput = "9";
        viewModel.ModelFormOutput = "90";
        viewModel.SaveModelDefinitionCommand.Execute(null);

        settings.ModelDefinitions.Single().InputPerMillion.Should().Be(9m, "改动过的价就是用户覆盖");
        catalog.FindById("glm-4-6")!.IsPriceOverridden.Should().BeTrue();
    }

    [Fact]
    public void SaveModelDefinition_EditingUserCreatedModel_KeepsItsPrice()
    {
        // 用户自建条目的价格只存在新字段里：编辑元数据时预填的是用户自己的价，
        // 必须原样保留——若按生效价比较清空，Bind 找不到导入层，重启后价格就丢了。
        var settings = new AppSettings();
        settings.ModelDefinitions.Add(new ModelDefinitionOverride
        {
            Id = "my-local-model",
            DisplayName = "本地模型",
            InputPerMillion = 7m,
            OutputPerMillion = 70m,
        });
        var catalog = new ModelCatalog(userDefinitions: ModelCatalog.MergeUserDefinitions(settings));
        var (viewModel, _) = CreateCatalogViewModel(settings, new FakeSettingsStore(), catalog);

        viewModel.EditModelDefinitionCommand.Execute("my-local-model");
        viewModel.ModelFormDisplayName = "本地模型改";
        viewModel.SaveModelDefinitionCommand.Execute(null);

        var saved = settings.ModelDefinitions.Single();
        saved.InputPerMillion.Should().Be(7m, "自建条目的价必须落盘保留");
        saved.DisplayName.Should().Be("本地模型改");
        catalog.FindById("my-local-model")!.HasPrice.Should().BeTrue();
    }

    [Fact]
    public void ModelRows_CanRemoveOnlyForUserLayerEntries()
    {
        var settings = new AppSettings();
        settings.ModelDefinitions.Add(new ModelDefinitionOverride { Id = "glm-4.6", InputPerMillion = 1m, OutputPerMillion = 3m });
        settings.ModelDefinitions.Add(new ModelDefinitionOverride { Id = "my-own-model", InputPerMillion = 2m, OutputPerMillion = 8m });
        var viewModel = CreateViewModel(settings, new FakeSettingsStore(), catalog: new ModelCatalog());

        var overridden = viewModel.ModelRows.Single(r => r.Id == "glm-4-6");
        var userCreated = viewModel.ModelRows.Single(r => r.Id == "my-own-model");
        var untouched = viewModel.ModelRows.First(r => r.Origin == ModelDefinitionOrigin.Snapshot && !r.IsOverridden);

        overridden.CanRemove.Should().BeTrue("被覆盖的导入条目可移除覆盖");
        overridden.IsOverridden.Should().BeTrue();
        overridden.IsUserCreated.Should().BeFalse();
        userCreated.CanRemove.Should().BeTrue("自建条目可整条删除");
        userCreated.IsOverridden.Should().BeFalse("自建条目没有覆盖任何导入条目，不挂已改徽标");
        userCreated.IsUserCreated.Should().BeTrue();
        untouched.CanRemove.Should().BeFalse("从未覆盖过的条目不该出现假按钮");
        untouched.IsOverridden.Should().BeFalse();
    }

    [Fact]
    public void ModelRows_SearchTextFiltersByIdLabelAndVendor()
    {
        var viewModel = CreateViewModel(new AppSettings(), new FakeSettingsStore(), catalog: new ModelCatalog());

        viewModel.ModelRows.Should().NotBeEmpty("内置快照应有条目");
        viewModel.ModelSearchText = "glm";
        viewModel.ModelRows.Should().OnlyContain(r =>
            r.Id.Contains("glm", StringComparison.OrdinalIgnoreCase)
            || r.Label.Contains("glm", StringComparison.OrdinalIgnoreCase)
            || r.Vendor.Contains("glm", StringComparison.OrdinalIgnoreCase));

        viewModel.ModelSearchText = "no-such-model-anywhere-xz";
        viewModel.HasNoMatchingRows.Should().BeTrue("目录非空但无匹配 → 无匹配空态");
        viewModel.HasNoModelRows.Should().BeFalse("目录真空空态不受过滤影响");

        viewModel.ModelSearchText = string.Empty;
        viewModel.HasNoMatchingRows.Should().BeFalse();
        viewModel.ModelRows.Should().NotBeEmpty();
    }

    [Fact]
    public void ModelRows_FilterChipsUnionThenIntersectWithText()
    {
        var settings = new AppSettings();
        settings.ModelDefinitions.Add(new ModelDefinitionOverride { Id = "glm-4.6", InputPerMillion = 1m, OutputPerMillion = 3m });
        settings.ModelDefinitions.Add(new ModelDefinitionOverride { Id = "my-own-model", InputPerMillion = 2m, OutputPerMillion = 8m });
        var viewModel = CreateViewModel(settings, new FakeSettingsStore(), catalog: new ModelCatalog());
        var unfilteredCount = viewModel.ModelRows.Count; // 全不选 = 不过滤（快照层全集 + 用户自建条目，无在线层）

        viewModel.FilterUser = true;
        viewModel.ModelRows.Should().ContainSingle("只选自定义 chip → 只剩用户自建条目（覆盖导入层不改 Origin，见 Bind）")
            .Which.Id.Should().Be("my-own-model");

        viewModel.FilterBuiltin = true;
        viewModel.ModelRows.Should().HaveCount(unfilteredCount, "内置 ∪ 自定义 = 全集（目录无在线层；不钉死快照规模）");

        viewModel.FilterOverridden = true;
        viewModel.ModelRows.Select(r => r.Id).Should().BeEquivalentTo(
            new[] { "glm-4-6" },
            "叠加已覆盖 chip 取交集：只有覆盖了导入层的条目算「已覆盖」——自建条目下面没有导入层，不在此列（去自定义 chip 找）");

        viewModel.ModelSearchText = "gpt";
        viewModel.ModelRows.Should().BeEmpty("chip 与文本取交集");

        // 候选清单（别名目标下拉）不受浏览过滤影响。
        viewModel.ModelIdCandidates.Should().NotBeEmpty();
    }

    [Fact]
    public void ModelEditDialog_OpensForEditAndCreate_ClosesOnSaveAndCancel()
    {
        var settings = new AppSettings();
        var store = new FakeSettingsStore();
        var viewModel = CreateViewModel(settings, store, catalog: new ModelCatalog());

        viewModel.IsModelEditOpen.Should().BeFalse();

        // 新建入口：清空表单 + 开弹层。
        viewModel.AddModelDefinitionCommand.Execute(null);
        viewModel.IsModelEditOpen.Should().BeTrue();
        viewModel.EditingModelId.Should().BeNull();
        viewModel.ModelFormId.Should().BeEmpty();

        // 取消：关弹层 + 清表单。
        viewModel.CancelModelEditCommand.Execute(null);
        viewModel.IsModelEditOpen.Should().BeFalse();
        viewModel.ModelFormId.Should().BeEmpty();

        // 编辑入口：预填 + 开弹层；校验失败不关弹层，保存成功才关。
        viewModel.EditModelDefinitionCommand.Execute("glm-4.6");
        viewModel.IsModelEditOpen.Should().BeTrue();
        viewModel.ModelFormId.Should().Be("glm-4-6");

        viewModel.ModelFormInput = "not-a-number";
        viewModel.SaveModelDefinitionCommand.Execute(null);
        viewModel.IsModelEditOpen.Should().BeTrue("校验失败弹层不关");
        viewModel.ModelFormError.Should().NotBeNull();

        viewModel.ModelFormInput = "1";
        viewModel.ModelFormOutput = "3";
        viewModel.SaveModelDefinitionCommand.Execute(null);
        viewModel.IsModelEditOpen.Should().BeFalse("保存成功关弹层");
        store.SaveCalls.Should().Be(1);
    }

    [Fact]
    public void SaveModelDefinition_PersistsOptionalCachePrices()
    {
        var settings = new AppSettings();
        var store = new FakeSettingsStore();
        var viewModel = CreateViewModel(settings, store, catalog: new ModelCatalog());

        viewModel.AddModelDefinitionCommand.Execute(null);
        viewModel.ModelFormId = "my-own-model";
        viewModel.ModelFormInput = "0.06";
        viewModel.ModelFormOutput = "0.21";
        viewModel.ModelFormCacheWrite = "0.075";
        viewModel.ModelFormCacheRead = "0.006";
        viewModel.SaveModelDefinitionCommand.Execute(null);

        viewModel.ModelFormError.Should().BeNull();
        var saved = settings.ModelDefinitions.Single(d => ModelId.Normalize(d.Id) == "my-own-model");
        saved.CacheWritePerMillion.Should().Be(0.075m);
        saved.CacheReadPerMillion.Should().Be(0.006m);
    }

    [Fact]
    public void SaveModelDefinition_WithInvalidCachePrice_ShowsInlineError()
    {
        var settings = new AppSettings();
        var store = new FakeSettingsStore();
        var viewModel = CreateViewModel(settings, store, catalog: new ModelCatalog());

        viewModel.AddModelDefinitionCommand.Execute(null);
        viewModel.ModelFormId = "my-own-model";
        viewModel.ModelFormInput = "0.06";
        viewModel.ModelFormOutput = "0.21";
        viewModel.ModelFormCacheWrite = "abc";
        viewModel.SaveModelDefinitionCommand.Execute(null);

        viewModel.ModelFormError.Should().Be(Translations.Instance.ModelFormErrorPriceNumber);
        store.SaveCalls.Should().Be(0);
    }

    [Fact]
    public void SaveModelDefinition_WithUnchangedImportedCachePrices_DoesNotPinCacheOverride()
    {
        // 元数据级编辑不钉缓存价（与 in/out 同口径）：预填的生效缓存价是按 input 推导的数
        // （×1.25 / ×0.1），照抄保存会把推导结果冻结成用户覆盖，models.dev 刷新后与 input 脱钩。
        var settings = new AppSettings();
        var catalog = new ModelCatalog();
        var (viewModel, _) = CreateCatalogViewModel(settings, new FakeSettingsStore(), catalog);

        viewModel.EditModelDefinitionCommand.Execute("glm-4-6"); // 表单预填导入层生效价与推导缓存价
        viewModel.ModelFormDisplayName = "改名不改缓存价";
        viewModel.SaveModelDefinitionCommand.Execute(null);

        var saved = settings.ModelDefinitions.Single();
        saved.InputPerMillion.Should().BeNull("未改动的生效价不落用户覆盖（既有口径）");
        saved.CacheWritePerMillion.Should().BeNull("未改动的推导缓存价同样不落用户覆盖");
        saved.CacheReadPerMillion.Should().BeNull();
        catalog.FindById("glm-4-6")!.HasPrice.Should().BeTrue("元数据覆盖后价仍由导入层供给");
    }

    [Fact]
    public void SaveModelDefinition_WithChangedCachePrice_PinsCacheOverride()
    {
        // 用户改过的缓存价就是价格覆盖：断言到目录层（计价真正读的地方），settings.json 落值
        // 但装订层丢弃 = 静默无效（Bind 的 HasPriceOverride 要求 in/out 成对，故缓存价有改动时
        // 整组价格覆盖照写，in/out 连同钉住）。
        var settings = new AppSettings();
        var catalog = new ModelCatalog();
        var (viewModel, _) = CreateCatalogViewModel(settings, new FakeSettingsStore(), catalog);

        viewModel.EditModelDefinitionCommand.Execute("glm-4-6");
        viewModel.ModelFormCacheWrite = "0.5";
        viewModel.ModelFormCacheRead = "0.05";
        viewModel.SaveModelDefinitionCommand.Execute(null);

        var saved = settings.ModelDefinitions.Single();
        saved.CacheWritePerMillion.Should().Be(0.5m, "改动的缓存价是显式覆盖");
        saved.CacheReadPerMillion.Should().Be(0.05m);
        saved.InputPerMillion.Should().NotBeNull("Bind 的价格覆盖整组生效：缓存价有改动时 in/out 一并钉住，否则缓存价被静默丢弃");
        catalog.FindById("glm-4-6")!.CacheWritePerMillion.Should().Be(0.5m, "计价层必须真的读到用户的缓存价");
        catalog.FindById("glm-4-6")!.CacheReadPerMillion.Should().Be(0.05m);
        catalog.FindById("glm-4-6")!.IsPriceOverridden.Should().BeTrue();
    }

    [Fact]
    public void SaveModelDefinition_MetadataOnlyEdit_KeepsExplicitZeroCacheWrite()
    {
        // 特性化锁（glm-4.6 快照 cache_write 显式为 0，非推导缺省）：元数据级编辑后全部留空，
        // 导入层原样回供缓存价（0 / 0.11），绝不能落到"表单显示 0、落盘变推导 0.75"的静默偷换。
        var settings = new AppSettings();
        var catalog = new ModelCatalog();
        var (viewModel, _) = CreateCatalogViewModel(settings, new FakeSettingsStore(), catalog);
        var effective = catalog.FindById("glm-4-6")!;
        effective.CacheWritePerMillion.Should().Be(0m, "前置：快照对 glm-4.6 显式标 0 缓存写价");

        viewModel.EditModelDefinitionCommand.Execute("glm-4-6");
        viewModel.ModelFormDisplayName = "只改显示名";
        viewModel.SaveModelDefinitionCommand.Execute(null);

        settings.ModelDefinitions.Single().InputPerMillion.Should().BeNull("元数据级编辑不落价格覆盖");
        var merged = catalog.FindById("glm-4-6")!;
        merged.CacheWritePerMillion.Should().Be(0m, "显式 0 缓存写价不得被推导缺省（×1.25）静默替换");
        merged.CacheReadPerMillion.Should().Be(effective.CacheReadPerMillion);
    }

    [Fact]
    public void SaveModelDefinition_WithCachePriceWithoutInput_ShowsInlineError()
    {
        // 自建无价条目只填缓存价：缓存价按输入价体系推导，没有 input 就没有可计价的锚，
        // 落盘只会得到一条被装订层忽略的死数据——录入侧直接劝阻。
        var settings = new AppSettings();
        var store = new FakeSettingsStore();
        var viewModel = CreateViewModel(settings, store, catalog: new ModelCatalog());

        viewModel.AddModelDefinitionCommand.Execute(null);
        viewModel.ModelFormId = "my-own-model";
        viewModel.ModelFormCacheWrite = "0.075";
        viewModel.SaveModelDefinitionCommand.Execute(null);

        viewModel.ModelFormError.Should().Be(Translations.Instance.ModelFormErrorCacheNeedsInputPair);
        store.SaveCalls.Should().Be(0);
        settings.ModelDefinitions.Should().BeEmpty();
    }

    // ---- helpers ----

    private static readonly DateTimeOffset Now = new(2026, 10, 13, 10, 30, 0, TimeSpan.Zero);

    private static DataPricingViewModel CreateViewModel(
        AppSettings settings,
        FakeSettingsStore store,
        FakeScanReader? reader = null,
        FakeToastService? toast = null,
        BackgroundRefreshService? refreshService = null,
        IUsageQueryService? query = null,
        ModelCatalog? catalog = null)
    {
        return new DataPricingViewModel(
            settings,
            store,
            reader ?? new FakeScanReader(),
            refreshService ?? new BackgroundRefreshService(() => Task.FromResult(true)),
            toast ?? new FakeToastService(),
            query,
            catalog);
    }

    /// <summary>扫描注册表读取面桩：Registered/快照可变，供重进刷新与重扫完成断言。
    /// Register 与真实注册表一致（登记扫描即视为该源已注册）。</summary>
    private sealed class FakeScanReader : IIngestionScanReader
    {
        public List<string> Registered { get; } = new();
        private readonly Dictionary<string, IngestionSourceScanSnapshot> _scans = new();

        public IReadOnlyList<string> RegisteredSources => Registered;

        public IngestionSourceScanSnapshot? GetLastScan(string agent) =>
            _scans.GetValueOrDefault(agent);

        public void Register(string agent, IngestionSourceScanSnapshot snapshot)
        {
            if (!Registered.Contains(agent))
            {
                Registered.Add(agent);
            }

            _scans[agent] = snapshot;
        }
    }

    private sealed class FakeToastService : IToastService
    {
        public List<string> Messages { get; } = new();

        public void Show(string message, Avalonia.Visual? host = null)
        {
            Messages.Add(message);
        }
    }

    /// <summary>查询服务桩（模型聚合卡候选清单）：只真实返回原始模型名，其余成员不参与断言。</summary>
    private sealed class FakeQueryService(IReadOnlyList<string> rawModelNames) : IUsageQueryService
    {
        public Task<IReadOnlyList<DailyUsagePoint>> GetDailySeriesAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DailyUsagePoint>>([]);

        public Task<UsageKpiResult> GetKpiAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(new UsageKpiResult());

        public Task<IReadOnlyList<DimensionUsageRow>> GetAgentBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>([]);

        public Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>([]);

        public Task<IReadOnlyList<DimensionUsageRow>> GetProjectBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>([]);

        public Task<IReadOnlyList<string>> GetRawModelNamesAsync(CancellationToken ct = default) =>
            Task.FromResult(rawModelNames);

        public Task<SessionListResult> GetSessionsAsync(SessionListQuery query, CancellationToken ct = default) =>
            Task.FromResult(new SessionListResult());

        public Task<HourlyDistributionResult> GetHourlyDistributionAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(new HourlyDistributionResult());
    }

    // ---- Q-012 模型头像（图标选择器四模式）----

    [Fact]
    public void AvatarPicker_LetterRoundTrip_SavesAndLoads()
    {
        var settings = new AppSettings();
        var store = new FakeSettingsStore();
        var catalog = new ModelCatalog();
        var viewModel = CreateViewModel(settings, store, catalog: catalog);

        viewModel.AddModelDefinitionCommand.Execute(null);
        viewModel.ModelFormId = "my-own-model";
        viewModel.OpenAvatarPickerCommand.Execute(null);
        viewModel.IsAvatarPickerOpen.Should().BeTrue();
        viewModel.AvatarPicker.Mode.Should().Be(ModelAvatarKind.Auto, "新建态草稿从表单载入 = 自动");
        viewModel.AvatarPicker.CanConfirm.Should().BeTrue("自动模式恒可确认");

        viewModel.AvatarPicker.Letter = "G";
        viewModel.AvatarPicker.Mode = ModelAvatarKind.Letter;
        viewModel.AvatarPicker.CanConfirm.Should().BeTrue("字母载荷已就绪");

        viewModel.ConfirmAvatarPickerCommand.Execute(null);
        viewModel.IsAvatarPickerOpen.Should().BeFalse();
        viewModel.ModelFormAvatarKind.Should().Be(ModelAvatarKind.Letter);
        viewModel.ModelFormAvatarLetter.Should().Be("G");

        viewModel.SaveModelDefinitionCommand.Execute(null);
        viewModel.ModelFormError.Should().BeNull();
        var saved = settings.ModelDefinitions.Single(d => ModelId.Normalize(d.Id) == "my-own-model");
        saved.AvatarKind.Should().Be(ModelAvatarKind.Letter);
        saved.AvatarLetter.Should().Be("G");
        saved.LogoVendor.Should().BeNull();
        store.SaveCalls.Should().Be(1, "改完即落盘（全自动保存纪律）");

        // 目录行头像跟随条目（显示统一）
        viewModel.ModelRows.Single(r => r.Id == "my-own-model").Avatar.GlyphText.Should().Be("G");

        // 编辑装载：用户层值回填表单
        viewModel.EditModelDefinitionCommand.Execute("my-own-model");
        viewModel.ModelFormAvatarKind.Should().Be(ModelAvatarKind.Letter);
        viewModel.ModelFormAvatarLetter.Should().Be("G");
        viewModel.IsModelEditOpen.Should().BeTrue();
    }

    [Fact]
    public void AvatarPicker_ConfirmWritesSelectedModePayloadOnly()
    {
        var settings = new AppSettings();
        var viewModel = CreateViewModel(settings, new FakeSettingsStore(), catalog: new ModelCatalog());

        viewModel.AddModelDefinitionCommand.Execute(null);
        viewModel.ModelFormId = "my-own-model";
        viewModel.OpenAvatarPickerCommand.Execute(null);

        // 选色（点格子 = 切模式 + 选载荷）
        var swatch = viewModel.AvatarPicker.ColorTiles[0];
        viewModel.AvatarPicker.SelectColorCommand.Execute(swatch);
        viewModel.AvatarPicker.Mode.Should().Be(ModelAvatarKind.Color, "点色块即表达颜色意图");
        viewModel.AvatarPicker.ColorHex.Should().Be(swatch.Hex);
        viewModel.ConfirmAvatarPickerCommand.Execute(null);
        viewModel.ModelFormAvatarKind.Should().Be(ModelAvatarKind.Color);
        viewModel.ModelFormAvatarColor.Should().Be(swatch.Hex);
        viewModel.ModelFormAvatarLetter.Should().BeEmpty("其余模式载荷清空，不留中间态");

        // 再选厂商标（vendor 格子 = Vendor 模式）
        viewModel.OpenAvatarPickerCommand.Execute(null);
        var tile = viewModel.AvatarPicker.VendorTiles.Single(t => t.Key == "claude");
        viewModel.AvatarPicker.SelectVendorCommand.Execute(tile);
        viewModel.ConfirmAvatarPickerCommand.Execute(null);
        viewModel.ModelFormAvatarKind.Should().Be(ModelAvatarKind.Vendor);
        viewModel.ModelFormAvatarVendor.Should().Be("claude");
        viewModel.ModelFormAvatarColor.Should().BeNull();

        viewModel.SaveModelDefinitionCommand.Execute(null);
        var saved = settings.ModelDefinitions.Single(d => ModelId.Normalize(d.Id) == "my-own-model");
        saved.LogoVendor.Should().Be("claude");
        saved.AvatarKind.Should().Be(ModelAvatarKind.Vendor);
        saved.AvatarColor.Should().BeNull("非选中模式不落盘");
    }

    [Fact]
    public void AvatarPicker_EmptyLetter_CannotConfirm()
    {
        var settings = new AppSettings();
        var viewModel = CreateViewModel(settings, new FakeSettingsStore(), catalog: new ModelCatalog());

        viewModel.AddModelDefinitionCommand.Execute(null);
        viewModel.OpenAvatarPickerCommand.Execute(null);
        viewModel.AvatarPicker.Mode = ModelAvatarKind.Letter;
        viewModel.AvatarPicker.CanConfirm.Should().BeFalse("空字母不让静默落成自动");

        viewModel.ConfirmAvatarPickerCommand.Execute(null);
        viewModel.IsAvatarPickerOpen.Should().BeTrue("确认被门禁拦下，弹层不关");
        viewModel.ModelFormAvatarKind.Should().Be(ModelAvatarKind.Auto, "表单字段不被空草稿污染");
    }

    [Fact]
    public void AvatarPicker_Cancel_DiscardsDraft()
    {
        var settings = new AppSettings();
        var viewModel = CreateViewModel(settings, new FakeSettingsStore(), catalog: new ModelCatalog());

        viewModel.AddModelDefinitionCommand.Execute(null);
        viewModel.OpenAvatarPickerCommand.Execute(null);
        viewModel.AvatarPicker.Mode = ModelAvatarKind.Color;
        viewModel.AvatarPicker.SelectColorCommand.Execute(viewModel.AvatarPicker.ColorTiles[2]);
        viewModel.CancelAvatarPickerCommand.Execute(null);

        viewModel.IsAvatarPickerOpen.Should().BeFalse();
        viewModel.ModelFormAvatarKind.Should().Be(ModelAvatarKind.Auto, "草稿丢弃");
        viewModel.ModelFormAvatarColor.Should().BeNull();
    }

    [Fact]
    public void SaveModelDefinition_LetterAvatarClearsLegacyLogoVendor()
    {
        // 旧手写 LogoVendor（Auto 兼容）→ 用户改选字母头像：LogoVendor 必须清掉，
        // 否则用户日后切回自动时旧厂商会"复活"（同旧字段清理先例）。
        var settings = new AppSettings();
        settings.ModelDefinitions.Add(new ModelDefinitionOverride { Id = "my-own-model", LogoVendor = "qwen" });
        var viewModel = CreateViewModel(settings, new FakeSettingsStore(), catalog: new ModelCatalog());

        viewModel.EditModelDefinitionCommand.Execute("my-own-model");
        viewModel.ModelFormAvatarKind.Should().Be(ModelAvatarKind.Vendor, "旧 Auto + LogoVendor 在表单里显式化为 Vendor，所见即所存");
        viewModel.ModelFormAvatarVendor.Should().Be("qwen");

        viewModel.OpenAvatarPickerCommand.Execute(null);
        viewModel.AvatarPicker.Letter = "M";
        viewModel.AvatarPicker.Mode = ModelAvatarKind.Letter;
        viewModel.ConfirmAvatarPickerCommand.Execute(null);
        viewModel.SaveModelDefinitionCommand.Execute(null);

        var saved = settings.ModelDefinitions.Single(d => ModelId.Normalize(d.Id) == "my-own-model");
        saved.AvatarKind.Should().Be(ModelAvatarKind.Letter);
        saved.AvatarLetter.Should().Be("M");
        saved.LogoVendor.Should().BeNull("旧手写厂商标随之清理，防止切回自动时复活");
    }

    [Fact]
    public void AvatarPicker_ImageMode_RequiresUploadBeforeConfirm()
    {
        const string dataUri = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUg==";
        var settings = new AppSettings();
        var viewModel = CreateViewModel(settings, new FakeSettingsStore(), catalog: new ModelCatalog());

        viewModel.AddModelDefinitionCommand.Execute(null);
        viewModel.ModelFormId = "my-own-model";
        viewModel.OpenAvatarPickerCommand.Execute(null);
        viewModel.AvatarPicker.Mode = ModelAvatarKind.Image;
        viewModel.AvatarPicker.CanConfirm.Should().BeFalse("未上传图片不可确认");

        viewModel.AvatarPicker.ImageDataUri = dataUri;
        viewModel.AvatarPicker.CanConfirm.Should().BeTrue();
        viewModel.ConfirmAvatarPickerCommand.Execute(null);
        viewModel.ModelFormAvatarImageData.Should().Be(dataUri);

        viewModel.SaveModelDefinitionCommand.Execute(null);
        settings.ModelDefinitions.Single(d => ModelId.Normalize(d.Id) == "my-own-model")
            .AvatarImage.Should().Be(dataUri);
    }

    private sealed class FakeSettingsStore : IAppSettingsStore
    {
        public int SaveCalls { get; private set; }
        public AppSettings? SavedSettings { get; private set; }
        public Exception? SaveException { get; init; }

        public Task<AppSettings> LoadAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new AppSettings());
        }

        public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
        {
            SaveCalls++;
            if (SaveException != null)
            {
                throw SaveException;
            }

            SavedSettings = settings;
            return Task.CompletedTask;
        }
    }
}
