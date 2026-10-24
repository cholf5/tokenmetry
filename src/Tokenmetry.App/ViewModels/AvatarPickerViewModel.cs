using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.ViewModels;

/// <summary>图标选择器的厂商格子（ModelVisualMap.Vendors 全变体 + generic 兜底，可选不加限制）。</summary>
public sealed partial class AvatarVendorTileViewModel : ObservableObject, IAvatarTile
{
    public AvatarVendorTileViewModel(string key)
    {
        Key = key;
        Visual = ModelAvatarVisual.FromVendor(key);
    }

    /// <summary>厂商变体名（ModelVisualMap.Vendors 规范小写名）。</summary>
    public string Key { get; }

    /// <summary>格子的预览视觉（Vendor 模式，logo / Φ / 兜底问号照伪类规则渲染）。</summary>
    public ModelAvatarVisual Visual { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>图标选择器色盘的色块格子（常量调色板——用户可选的「内容」色，非主题令牌，不随明暗翻转）。</summary>
public sealed partial class AvatarColorTileViewModel : ObservableObject, IAvatarTile
{
    public AvatarColorTileViewModel(string hex)
    {
        Hex = hex;
    }

    /// <summary>#RRGGBB（大写规范形）。</summary>
    public string Hex { get; }

    /// <summary>格子的选中标识键（即色值本身）。</summary>
    public string Key => Hex;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// 模型头像图标选择器（模型编辑弹层「设置头像」入口打开的草稿编辑器）：模式段选（自动 /
/// 内置图标 / 字母 / 颜色 / 图片）+ 各模式载荷 + 顶部实时预览。<b>草稿语义</b>——确定才写回
/// <see cref="DataPricingViewModel"/> 的表单字段，取消 / 遮罩 / Esc 全部丢弃；各模式载荷在
/// 确定时按选中模式整组写入（其余模式载荷清空，不留中间态）。
/// </summary>
public partial class AvatarPickerViewModel : ObservableObject
{
    /// <summary>
    /// 色盘（20 色常量）：前 7 色与 Tokens.axaml 的 AppSeries0Color–6Color 同源（镜像注明，
    /// 与图表系列色观感统一），其余为与应用强调色协调的补档（含明暗两侧中性色）。
    /// </summary>
    internal static readonly string[] Palette =
    [
        "#4C8DFF", "#12B886", "#FFAD4D", "#A779FF", "#48BDE3", "#8394AE", "#F06BC4",
        "#E5484D", "#FF6900", "#FFC53D", "#7FBF26", "#08979C", "#3E63DD", "#7C66DC",
        "#D6409F", "#6E56CF", "#CE7E00", "#2AA198", "#E8ECF2", "#263143",
    ];

    public AvatarPickerViewModel()
    {
        VendorTiles = ModelVisualMap.Vendors.Select(key => new AvatarVendorTileViewModel(key)).ToList();
        ColorTiles = Palette.Select(hex => new AvatarColorTileViewModel(hex)).ToList();
    }

    /// <summary>厂商格子（顺序 = ModelVisualMap.Vendors 判定序，claude…generic）。</summary>
    public IReadOnlyList<AvatarVendorTileViewModel> VendorTiles { get; }

    /// <summary>色盘格子（<see cref="Palette"/> 序）。</summary>
    public IReadOnlyList<AvatarColorTileViewModel> ColorTiles { get; }

    [ObservableProperty]
    private ModelAvatarKind _mode = ModelAvatarKind.Auto;

    [ObservableProperty]
    private string? _vendor;

    [ObservableProperty]
    private string _letter = string.Empty;

    [ObservableProperty]
    private string? _colorHex;

    [ObservableProperty]
    private string? _imageDataUri;

    /// <summary>上传图片解析失败（非图片 / 超限）——行内提示「换一张」，不阻塞其它模式。</summary>
    [ObservableProperty]
    private bool _isImageDecodeFailed;

    /// <summary>自动模式的预览视觉（打开选择器时按表单当前 ID 推断；Auto 段选下展示它）。</summary>
    public ModelAvatarVisual AutoPreview { get; private set; } = ModelAvatarVisual.FromName(string.Empty);

    /// <summary>「确定」是否可用：自动段恒可确认；其余段需载荷就绪（字母非空 / 已选色 /
    /// 图片已上传成功），不让空载荷静默落成自动。</summary>
    public bool CanConfirm => Mode switch
    {
        ModelAvatarKind.Letter => Letter.Trim().Length > 0,
        ModelAvatarKind.Color => ColorHex is not null,
        ModelAvatarKind.Image => ImageDataUri is not null,
        _ => true,
    };

    // 模式面板显隐（视图段选内容区）；HasImage 驱动「移除图片」按钮。
    public bool IsModeAuto => Mode == ModelAvatarKind.Auto;

    public bool IsModeVendor => Mode == ModelAvatarKind.Vendor;

    public bool IsModeLetter => Mode == ModelAvatarKind.Letter;

    public bool IsModeColor => Mode == ModelAvatarKind.Color;

    public bool IsModeImage => Mode == ModelAvatarKind.Image;

    public bool HasImage => ImageDataUri is not null;

    partial void OnModeChanged(ModelAvatarKind value)
    {
        OnPropertyChanged(nameof(IsModeAuto));
        OnPropertyChanged(nameof(IsModeVendor));
        OnPropertyChanged(nameof(IsModeLetter));
        OnPropertyChanged(nameof(IsModeColor));
        OnPropertyChanged(nameof(IsModeImage));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(CanConfirm));
    }

    /// <summary>顶部预览（实时所见即所得；图片模式未上传时为空芯片，诚实无内容）。</summary>
    public ModelAvatarVisual Preview => Mode switch
    {
        ModelAvatarKind.Vendor => ModelAvatarVisual.FromVendor(Vendor),
        ModelAvatarKind.Letter => ModelAvatarVisual.FromLetter(Letter),
        ModelAvatarKind.Color => ModelAvatarVisual.FromColorHex(ColorHex) ?? ModelAvatarVisual.Empty,
        ModelAvatarKind.Image => ModelAvatarVisual.FromImageData(ImageDataUri) ?? ModelAvatarVisual.Empty,
        _ => AutoPreview,
    };

    partial void OnVendorChanged(string? value) => OnPropertyChanged(nameof(Preview));

    partial void OnLetterChanged(string value)
    {
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(CanConfirm));
    }

    partial void OnColorHexChanged(string? value)
    {
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(CanConfirm));
    }

    partial void OnImageDataUriChanged(string? value)
    {
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(HasImage));
    }

    /// <summary>按表单当前头像载入草稿（打开选择器时调用一次）。</summary>
    public void Load(
        ModelAvatarKind kind,
        string? vendor,
        string letter,
        string? colorHex,
        string? imageDataUri,
        ModelAvatarVisual autoPreview)
    {
        AutoPreview = autoPreview;
        Mode = kind;
        Vendor = kind == ModelAvatarKind.Vendor ? vendor : null;
        Letter = letter ?? string.Empty;
        ColorHex = kind == ModelAvatarKind.Color ? colorHex : null;
        ImageDataUri = kind == ModelAvatarKind.Image ? imageDataUri : null;
        IsImageDecodeFailed = false;
        ApplySelection(VendorTiles, Vendor);
        ApplySelection(ColorTiles, ColorHex);
        // AutoPreview / Preview 通知已被上面的属性变更覆盖；同为 Auto 时显式补一次。
        OnPropertyChanged(nameof(Preview));
    }

    /// <summary>选中厂商格子（同时切到 Vendor 模式——点击格子即表达意图，不要求先切段选）。</summary>
    [RelayCommand]
    private void SelectVendor(AvatarVendorTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        Mode = ModelAvatarKind.Vendor;
        Vendor = tile.Key;
        ApplySelection(VendorTiles, tile.Key);
    }

    /// <summary>选中色块（同时切到 Color 模式，同上）。</summary>
    [RelayCommand]
    private void SelectColor(AvatarColorTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        Mode = ModelAvatarKind.Color;
        ColorHex = tile.Hex;
        ApplySelection(ColorTiles, tile.Hex);
    }

    /// <summary>移除已上传图片（回空芯片，须重新上传才能确认）。</summary>
    [RelayCommand]
    private void ClearImage()
    {
        ImageDataUri = null;
        IsImageDecodeFailed = false;
    }

    private static void ApplySelection<TTile>(IEnumerable<TTile> tiles, string? selectedKey)
        where TTile : ObservableObject, IAvatarTile
    {
        foreach (var tile in tiles)
        {
            tile.IsSelected = string.Equals(tile.Key, selectedKey, StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>两类格子（厂商 / 色块）的选中态公共口径（Key + IsSelected）。</summary>
public interface IAvatarTile
{
    /// <summary>格子的标识键（厂商变体名 / #RRGGBB）。</summary>
    string Key { get; }

    bool IsSelected { get; set; }
}
