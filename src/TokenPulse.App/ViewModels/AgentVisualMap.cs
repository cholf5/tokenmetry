using System;

namespace TokenPulse.App.ViewModels;

/// <summary>
/// Agent → 图标字形 / 软色底变体的稳定映射（D-008，右栏最近会话行与会话明细表共用）。
/// 原型 .agenticon 有四组底色变体（默认蓝 / green / purple / gray）与五个字形（✳◈⬡◉⬟），
/// 按示例逐会话手排；此处改为按 Agent 名稳定散列取模——同一 Agent 全应用外观一致，
/// 不再逐行手排。
/// 审查修复轮（P1）定案：本类只产出<b>语义变体名</b>（blue/green/purple/neutral），不再解析画刷——
/// 原实现用 <c>ThemeVariant.Default</c> 显式查询主题字典键（AppIconChip* 只在 Light/Dark 字典，
/// 无 Default 兜底）→ TryGetResource 软失败返回 null → 芯片在明暗两套全部不渲染。
/// 变体名经 <see cref="TokenPulse.App.Controls.AgentChip"/> 附加属性翻译成 Border 伪类，
/// 颜色一律由 SharedStyles 的 DynamicResource 提供（按控件 ActualThemeVariant 解析）：
/// 明暗翻转、窗口级 RequestedThemeVariant（明暗截图）都自动生效，无需行 VM 重建或重查。
/// </summary>
internal static class AgentVisualMap
{
    private static readonly char[] Glyphs = ['✳', '◈', '⬡', '◉', '⬟'];
    private static readonly string[] Variants = ["blue", "green", "purple", "neutral"];

    public static char Glyph(string agent) => Glyphs[Index(agent, Glyphs.Length)];

    /// <summary>软色底变体名（对应 SharedStyles 的 chip-* 伪类与 AppIconChip*Hue 令牌组）。</summary>
    public static string ChipVariant(string agent) => Variants[Index(agent, Variants.Length)];

    private static int Index(string agent, int mod) =>
        agent.Length == 0 ? 0 : Math.Abs(StringComparer.Ordinal.GetHashCode(agent)) % mod;
}
