namespace Tokenmetry.Core.Models;

/// <summary>
/// 模型头像的来源方式（模型目录条目 <c>AvatarKind</c> 字段）：UI 图标槽（model-chip）按它决定
/// 渲染哪一层。Auto 是缺省——按模型 ID 走内置厂商推断（<c>ModelVisualMap</c> 既有口径）；
/// 其余四种由用户在模型编辑弹层的图标选择器显式设置。
/// </summary>
public enum ModelAvatarKind
{
    /// <summary>自动：按模型 ID 推断厂商；兼容语义——条目同时带 <c>LogoVendor</c>（旧字段手写）
    /// 时按 Vendor 呈现（不改写数据，解析层兼容）。</summary>
    Auto,

    /// <summary>内置厂商 logo（<c>LogoVendor</c> 为载荷，取值即 ModelVisualMap.Vendors 变体名）。</summary>
    Vendor,

    /// <summary>单字母头像（<c>AvatarLetter</c>，1 个字符，中性芯片字形观感）。</summary>
    Letter,

    /// <summary>纯色块头像（<c>AvatarColor</c>，#RRGGBB）。</summary>
    Color,

    /// <summary>用户上传图片（<c>AvatarImage</c>，data:image/png;base64 data URI；采集侧已
    /// 居中裁方并压缩到 64×64——隐私红线不受影响：图片是用户显式提供的图标，非会话内容）。</summary>
    Image,
}
