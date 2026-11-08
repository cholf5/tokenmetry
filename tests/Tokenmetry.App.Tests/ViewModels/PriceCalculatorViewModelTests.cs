namespace Tokenmetry.App.Tests.ViewModels;

using System;
using System.Linq;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

/// <summary>
/// 模型计价器 VM（I-CALC）：模型清单装配与过滤、实时重算、手动填价兜底、无价模型自动进手动、
/// 非法输入挂起与恢复、语言切换重算。断言语言无关：期望串一律经 UsdCostFormatter /
/// Translations.Instance 现取，与被测代码同一出处。
/// </summary>
public class PriceCalculatorViewModelTests : IDisposable
{
    // 干净数字：输入 $3/M、输出 $15/M、缓存读 $0.3/M；1 亿输入 @80% + 1000 万输出
    // → 命中 $24 + 未命中 $60 + 输出 $150 = $234。
    private const decimal InputPrice = 3m;
    private const decimal OutputPrice = 15m;
    private const decimal CacheReadPrice = 0.3m;

    private readonly ModelCatalog _catalog = new(
        remoteSource: null,
        userDefinitions: new[]
        {
            // 无价用户条目：目录里存在但没有价格数据（计价器自动进手动填价的路径）。
            new ModelDefinitionOverride { Id = "mystery-model" },
        },
        snapshotEntries: new[]
        {
            new ModelPriceEntry
            {
                ModelId = "model-a",
                Provider = "vendor-x",
                InputPerMillion = InputPrice,
                OutputPerMillion = OutputPrice,
                CacheReadPerMillion = CacheReadPrice,
            },
            new ModelPriceEntry
            {
                ModelId = "model-b",
                Provider = "vendor-y",
                InputPerMillion = 1m,
                OutputPerMillion = 4m,
                CacheReadPerMillion = 0.1m,
            },
        });

    private readonly PriceCalculatorViewModel _vm;

    public PriceCalculatorViewModelTests()
    {
        _vm = new PriceCalculatorViewModel(_catalog);
    }

    public void Dispose()
    {
        _vm.Detach();
    }

    [Fact]
    public void ModelOptions_come_from_catalog_with_priced_first()
    {
        _vm.ModelOptions.Select(o => o.Id).Should().Equal("model-a", "model-b", "mystery-model");
        _vm.ModelOptions.Single(o => o.Id == "mystery-model").HasPrice.Should().BeFalse();
    }

    [Fact]
    public void Search_filters_by_id_and_vendor()
    {
        _vm.SearchText = "model-a";
        _vm.FilteredModelOptions.Select(o => o.Id).Should().Equal("model-a");

        _vm.SearchText = "vendor-y";
        _vm.FilteredModelOptions.Select(o => o.Id).Should().Equal("model-b");

        _vm.SearchText = string.Empty;
        _vm.FilteredModelOptions.Select(o => o.Id).Should().Equal("model-a", "model-b", "mystery-model");
    }

    [Fact]
    public void Live_estimate_splits_input_by_hit_rate()
    {
        _vm.SelectedModel = _vm.ModelOptions.Single(o => o.Id == "model-a");
        _vm.InputTokensText = "1亿";
        _vm.CacheHitRateText = "80";
        _vm.OutputTokensText = "1000万";

        _vm.HasResult.Should().BeTrue();
        _vm.HasHint.Should().BeFalse();
        _vm.TotalCostText.Should().Be(UsdCostFormatter.Format(234m));
        _vm.CacheReadCostText.Should().Be(UsdCostFormatter.Format(24m));
        _vm.FreshInputCostText.Should().Be(UsdCostFormatter.Format(60m));
        _vm.OutputCostText.Should().Be(UsdCostFormatter.Format(150m));
        _vm.BlendedCostText.Should().Be(UsdCostFormatter.FormatPerMillion(234m / 110m));

        // 明细行带 tokens 与单价（"80,000,000 × $0.3000/M" 形态）；单价来自同一格式器。
        _vm.CacheReadDetailText.Should().Contain(
            $"{80_000_000:N0} × {UsdCostFormatter.FormatPerMillion(CacheReadPrice)}");
        _vm.FreshInputDetailText.Should().Contain(
            $"{20_000_000:N0} × {UsdCostFormatter.FormatPerMillion(InputPrice)}");
    }

    [Fact]
    public void Manual_prices_price_the_same_way_with_cache_read_default()
    {
        _vm.ManualPrices = true;
        _vm.ManualInputPriceText = "3";
        _vm.ManualOutputPriceText = "15";
        _vm.InputTokensText = "1,000,000";

        // 缓存读留空 → 输入价 × 0.1（快照解析同规则）；无命中 → 全按输入价。
        _vm.HasResult.Should().BeTrue();
        _vm.TotalCostText.Should().Be(UsdCostFormatter.Format(3m));
        _vm.CacheReadCostText.Should().Be(UsdCostFormatter.Format(0m));

        _vm.CacheHitRateText = "100";
        _vm.TotalCostText.Should().Be(UsdCostFormatter.Format(0.3m));
        _vm.FreshInputCostText.Should().Be(UsdCostFormatter.Format(0m));
    }

    [Fact]
    public void Half_filled_manual_prices_wait_quietly()
    {
        _vm.ManualPrices = true;
        _vm.ManualCacheReadPriceText = "0.3";

        _vm.HasResult.Should().BeFalse();
        _vm.HasHint.Should().BeFalse(); // 没填齐不算错
    }

    [Fact]
    public void Priceless_model_auto_switches_to_manual_and_hints_when_off()
    {
        _vm.SelectedModel = _vm.ModelOptions.Single(o => o.Id == "mystery-model");
        _vm.ManualPrices.Should().BeTrue(); // 自动进手动填价

        _vm.ManualPrices = false; // 用户关掉 → 如实提示无价格数据
        _vm.HasResult.Should().BeFalse();
        _vm.HasHint.Should().BeTrue();
        _vm.HintText.Should().Be(Translations.Instance.PriceCalculatorNoPriceHint);
    }

    [Fact]
    public void Invalid_input_pauses_calculation_then_recovers()
    {
        _vm.SelectedModel = _vm.ModelOptions.Single(o => o.Id == "model-a");
        _vm.InputTokensText = "1,000,000";

        _vm.HasResult.Should().BeTrue();

        _vm.OutputTokensText = "abc";
        _vm.HasResult.Should().BeFalse();
        _vm.HasHint.Should().BeTrue();
        _vm.HintText.Should().Be(Translations.Instance.PriceCalculatorInvalidNumber);

        _vm.OutputTokensText = "5M";
        _vm.HasHint.Should().BeFalse();
        _vm.HasResult.Should().BeTrue();
    }

    [Fact]
    public void Language_switch_recomputes_hint_strings()
    {
        var original = Translations.Instance.CurrentLanguage;
        try
        {
            Translations.Instance.SetLanguage(AppLanguage.English);
            _vm.SelectedModel = _vm.ModelOptions.Single(o => o.Id == "mystery-model");
            _vm.ManualPrices = false;

            _vm.HintText.Should().Be(Translations.Instance.PriceCalculatorNoPriceHint);

            Translations.Instance.SetLanguage(AppLanguage.Japanese);
            _vm.HintText.Should().Be(Translations.Instance.PriceCalculatorNoPriceHint);
        }
        finally
        {
            Translations.Instance.SetLanguage(original);
        }
    }
}
