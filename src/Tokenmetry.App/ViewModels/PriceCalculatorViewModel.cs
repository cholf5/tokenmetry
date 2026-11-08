using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

namespace Tokenmetry.App.ViewModels;

/// <summary>计价器模型下拉选项：包一层目录定义，供 AutoCompleteBox 搜索用；
/// ToString 即选中后输入框里的文本（Avalonia 12 AutoCompleteBox 无 TextBinding，默认走 ToString）。</summary>
public sealed record PriceCalculatorModelOption
{
    public required ModelDefinition Definition { get; init; }

    public string Id => Definition.Id;

    public string DisplayLabel => Definition.DisplayLabel;

    public string Vendor => Definition.Vendor;

    public bool HasPrice => Definition.HasPrice;

    /// <summary>下拉行的厂商头像视觉（官方 logo / 字母 / 色块 / 上传图，与统计页同源同机制）。</summary>
    public ModelAvatarVisual Avatar => ModelAvatarVisual.FromDefinition(Definition, DisplayLabel);

    public override string ToString() => DisplayLabel;
}

/// <summary>
/// 模型计价器（I-CALC 独立工具）VM：选模型 + 填用量 → 实时出费用。与统计零关系——
/// 模型与价格只读自共享模型目录（<see cref="ModelCatalog"/>，快照 + 远程 + 用户覆盖三层），
/// 计算走 <see cref="ModelCostCalculator"/>（与主管道同一计价公式），不触碰 facts 存储。
/// <para>输入语义：输入总量 + 缓存命中率（百分比）自动拆「命中按缓存读价 / 未命中按输入价」，
/// 输出独立一桶；数字框支持 20亿 / 2e9 / 3M / 1500k 后缀写法（<see cref="TokenAmountTextParser"/>）。
/// 快照里没有的模型可勾选手动填单价（缓存读可空回落 input × 0.1，与快照解析同规则）。</para>
/// <para>语言切换：静态文案由 Translations 绑定自译，本 VM 的计算串（明细/提示）在
/// PropertyChanged(CurrentLanguage) 时整体重算；订阅经 <see cref="Detach"/> 解除——
/// 计价器窗口关窗即弃、重开重建，窗口 Closed 由 <see cref="PriceCalculatorLauncher"/> 接线调用。</para>
/// </summary>
public sealed partial class PriceCalculatorViewModel : ObservableObject
{
    /// <summary>下拉候选上限：全量目录在远程刷新后可达千级，过滤结果封顶保住下拉渲染。</summary>
    private const int MaxOptions = 300;

    private readonly ModelCatalog _catalog;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private PriceCalculatorModelOption? _selectedModel;

    [ObservableProperty]
    private string _inputTokensText = string.Empty;

    [ObservableProperty]
    private string _cacheHitRateText = string.Empty;

    [ObservableProperty]
    private string _outputTokensText = string.Empty;

    [ObservableProperty]
    private bool _manualPrices;

    [ObservableProperty]
    private string _manualInputPriceText = string.Empty;

    [ObservableProperty]
    private string _manualCacheReadPriceText = string.Empty;

    [ObservableProperty]
    private string _manualOutputPriceText = string.Empty;

    // ── 计算结果（Recompute 整体重算 + 手动 raise；字符串随语言切换重建）──
    private bool _hasUnitPrices;
    private string _inputPriceText = string.Empty;
    private string _cacheReadPriceText = string.Empty;
    private string _outputPriceText = string.Empty;
    private bool _hasResult;
    private string _freshInputDetailText = string.Empty;
    private string _cacheReadDetailText = string.Empty;
    private string _outputDetailText = string.Empty;
    private string _freshInputCostText = string.Empty;
    private string _cacheReadCostText = string.Empty;
    private string _outputCostText = string.Empty;
    private string _totalCostText = string.Empty;
    private string _blendedCostText = string.Empty;
    private string _hintText = string.Empty;

    public PriceCalculatorViewModel(ModelCatalog catalog)
    {
        _catalog = catalog;
        // 有价的排前面（搜"便宜的"场景别让无价条目占满下拉），组内按 ID 序数稳定排序。
        ModelOptions = catalog.Definitions
            .Select(definition => new PriceCalculatorModelOption { Definition = definition })
            .OrderByDescending(option => option.HasPrice)
            .ThenBy(option => option.Id, StringComparer.Ordinal)
            .ToArray();
        Translations.Instance.PropertyChanged += OnTranslationsPropertyChanged;
        Recompute();
    }

    /// <summary>全量模型选项（只读快照；开窗时取一次，远程刷新不回流已开窗口——重开即见新表）。</summary>
    public IReadOnlyList<PriceCalculatorModelOption> ModelOptions { get; }

    /// <summary>搜索过滤后的下拉候选（按 ID / 显示名 / 厂商包含匹配，封顶 <see cref="MaxOptions"/>）。</summary>
    public IReadOnlyList<PriceCalculatorModelOption> FilteredModelOptions
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                return ModelOptions.Take(MaxOptions).ToArray();
            }

            return ModelOptions
                .Where(option => Matches(option, SearchText))
                .Take(MaxOptions)
                .ToArray();
        }
    }

    public bool HasUnitPrices => _hasUnitPrices;
    public string InputPriceText => _inputPriceText;
    public string CacheReadPriceText => _cacheReadPriceText;
    public string OutputPriceText => _outputPriceText;
    public bool HasResult => _hasResult;
    public string FreshInputDetailText => _freshInputDetailText;
    public string CacheReadDetailText => _cacheReadDetailText;
    public string OutputDetailText => _outputDetailText;
    public string FreshInputCostText => _freshInputCostText;
    public string CacheReadCostText => _cacheReadCostText;
    public string OutputCostText => _outputCostText;
    public string TotalCostText => _totalCostText;
    public string BlendedCostText => _blendedCostText;
    public bool HasHint => _hintText.Length > 0;
    public string HintText => _hintText;

    /// <summary>解除 Translations 订阅（窗口 Closed 时调用；否则 VM 沿事件钉死在进程里）。</summary>
    public void Detach()
    {
        Translations.Instance.PropertyChanged -= OnTranslationsPropertyChanged;
    }

    partial void OnSearchTextChanged(string value) => OnPropertyChanged(nameof(FilteredModelOptions));

    partial void OnSelectedModelChanged(PriceCalculatorModelOption? value)
    {
        // 无价模型选中即自动进手动填价（用户可再取消勾选）。
        if (value is { } selected && !selected.HasPrice && !ManualPrices)
        {
            ManualPrices = true;
            return; // ManualPrices 变更会触发 Recompute
        }

        Recompute();
    }

    partial void OnInputTokensTextChanged(string value) => Recompute();

    partial void OnCacheHitRateTextChanged(string value) => Recompute();

    partial void OnOutputTokensTextChanged(string value) => Recompute();

    partial void OnManualPricesChanged(bool value) => Recompute();

    partial void OnManualInputPriceTextChanged(string value) => Recompute();

    partial void OnManualCacheReadPriceTextChanged(string value) => Recompute();

    partial void OnManualOutputPriceTextChanged(string value) => Recompute();

    private static bool Matches(PriceCalculatorModelOption option, string search)
    {
        return option.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (option.DisplayLabel.Contains(search, StringComparison.OrdinalIgnoreCase) && !string.Equals(option.DisplayLabel, option.Id, StringComparison.Ordinal))
            || option.Vendor.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void OnTranslationsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Translations.CurrentLanguage))
        {
            Recompute();
        }
    }

    /// <summary>整体重算：解析输入 → 取价格条目（目录选中 / 手动填价）→ 走 <see cref="ModelCostCalculator"/>
    /// 出分桶估算 → 回写全部展示属性。任何一步不成立就清空结果区，错误走 <see cref="HintText"/>。</summary>
    private void Recompute()
    {
        var parseError = false;
        var input = ParseTokensOrZero(InputTokensText, ref parseError);
        var output = ParseTokensOrZero(OutputTokensText, ref parseError);
        parseError |= !TokenAmountTextParser.TryParsePercent(CacheHitRateText, out var hitRate);

        ModelPriceEntry? entry = null;
        string? modelId = null;
        string hint = string.Empty;

        if (ManualPrices)
        {
            entry = BuildManualEntry(ref parseError);
            modelId = null; // 手动价没有模型名，速度倍率表自然不命中
        }
        else if (SelectedModel is { } selected)
        {
            entry = selected.Definition.ToPriceEntry(includeMatchPattern: false);
            modelId = selected.Id;
            if (entry is null || !selected.HasPrice)
            {
                entry = null;
                hint = Translations.Instance.PriceCalculatorNoPriceHint;
            }
        }

        if (parseError)
        {
            hint = Translations.Instance.PriceCalculatorInvalidNumber;
        }

        _hasUnitPrices = entry is not null && !parseError;
        _hasResult = false;
        _inputPriceText = string.Empty;
        _cacheReadPriceText = string.Empty;
        _outputPriceText = string.Empty;
        _freshInputDetailText = string.Empty;
        _cacheReadDetailText = string.Empty;
        _outputDetailText = string.Empty;
        _freshInputCostText = string.Empty;
        _cacheReadCostText = string.Empty;
        _outputCostText = string.Empty;
        _totalCostText = string.Empty;
        _blendedCostText = string.Empty;

        if (entry is not null && !parseError)
        {
            _inputPriceText = UsdCostFormatter.FormatPerMillion(entry.InputPerMillion);
            _cacheReadPriceText = UsdCostFormatter.FormatPerMillion(entry.CacheReadPerMillion);
            _outputPriceText = UsdCostFormatter.FormatPerMillion(entry.OutputPerMillion);

            var estimate = ModelCostCalculator.Calculate(entry, input, hitRate, output, modelId);
            _hasResult = true;
            _freshInputDetailText = DetailText(estimate.FreshInputTokens, entry.InputPerMillion);
            _cacheReadDetailText = DetailText(estimate.CacheReadTokens, entry.CacheReadPerMillion);
            _outputDetailText = DetailText(estimate.OutputTokens, entry.OutputPerMillion);
            _freshInputCostText = UsdCostFormatter.Format(estimate.FreshInputCostUsd);
            _cacheReadCostText = UsdCostFormatter.Format(estimate.CacheReadCostUsd);
            _outputCostText = UsdCostFormatter.Format(estimate.OutputCostUsd);
            _totalCostText = UsdCostFormatter.Format(estimate.TotalCostUsd);
            _blendedCostText = UsdCostFormatter.FormatPerMillion(estimate.BlendedPerMillionUsd);
        }

        _hintText = hint;

        RaiseResultChanged();
    }

    /// <summary>手动填价条目：输入/输出必填；缓存读可空 → input × 0.1（快照解析同规则）。</summary>
    private ModelPriceEntry? BuildManualEntry(ref bool parseError)
    {
        var inputFilled = ManualInputPriceText.Trim().Length > 0;
        var outputFilled = ManualOutputPriceText.Trim().Length > 0;
        var cacheReadFilled = ManualCacheReadPriceText.Trim().Length > 0;

        if (!inputFilled && !outputFilled && !cacheReadFilled)
        {
            return null; // 一个都没填：安静等待，不算错
        }

        var inputPrice = 0m;
        var outputPrice = 0m;
        var cacheReadPrice = 0m;
        var okInput = !inputFilled
            || TokenAmountTextParser.TryParsePrice(ManualInputPriceText, out inputPrice);
        var okOutput = !outputFilled
            || TokenAmountTextParser.TryParsePrice(ManualOutputPriceText, out outputPrice);
        var okCacheRead = !cacheReadFilled
            || TokenAmountTextParser.TryParsePrice(ManualCacheReadPriceText, out cacheReadPrice);

        if (!okInput || !okOutput || !okCacheRead)
        {
            parseError = true;
            return null;
        }

        if (!inputFilled || !outputFilled)
        {
            return null; // 填了一半：不猜测，等输入/输出价补齐
        }

        return new ModelPriceEntry
        {
            ModelId = "manual",
            Provider = string.Empty,
            InputPerMillion = inputPrice,
            OutputPerMillion = outputPrice,
            CacheReadPerMillion = cacheReadFilled ? cacheReadPrice : inputPrice * ModelPriceEntry.DefaultCacheReadMultiplier,
        };
    }

    private static long ParseTokensOrZero(string text, ref bool parseError)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        if (TokenAmountTextParser.TryParseTokens(text, out var tokens))
        {
            return tokens;
        }

        parseError = true;
        return 0;
    }

    private static string DetailText(long tokens, decimal perMillion)
    {
        return $"{tokens.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} × {UsdCostFormatter.FormatPerMillion(perMillion)}";
    }

    private void RaiseResultChanged()
    {
        OnPropertyChanged(nameof(HasUnitPrices));
        OnPropertyChanged(nameof(InputPriceText));
        OnPropertyChanged(nameof(CacheReadPriceText));
        OnPropertyChanged(nameof(OutputPriceText));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(FreshInputDetailText));
        OnPropertyChanged(nameof(CacheReadDetailText));
        OnPropertyChanged(nameof(OutputDetailText));
        OnPropertyChanged(nameof(FreshInputCostText));
        OnPropertyChanged(nameof(CacheReadCostText));
        OnPropertyChanged(nameof(OutputCostText));
        OnPropertyChanged(nameof(TotalCostText));
        OnPropertyChanged(nameof(BlendedCostText));
        OnPropertyChanged(nameof(HasHint));
        OnPropertyChanged(nameof(HintText));
    }
}
