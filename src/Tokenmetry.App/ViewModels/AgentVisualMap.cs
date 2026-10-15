using System;

namespace Tokenmetry.App.ViewModels;

/// <summary>Agent 图标信息：品牌 logo 变体名（驱动 <c>Tokenmetry.App.Controls.AgentChip</c> 附加属性
/// 翻译成 agent-* 伪类，选中 SharedStyles 里的行内官方 logo）+ 兜底字形。不可变，行 VM 持有。</summary>
/// <param name="LogoVendor">品牌变体名（claudecode/codex/…/dsh；未识别落 generic）。</param>
/// <param name="Glyph">无 lobe 对应 Agent（未识别 / 命名存储）的字形兜底；有 logo 的变体由样式隐藏该字形。</param>
public sealed record AgentVisual(string LogoVendor, char Glyph);

/// <summary>
/// Agent 名 → 图标的稳定映射（I-034，Agent 统计页 KPI 卡 / 会话明细 Agent 列 / 右栏最近会话行共用）。
/// 与 <see cref="ModelVisualMap"/> 完全同构：品牌判定产出<b>语义变体名</b>（AgentChip.Logo 附加属性
/// 翻译成 agent-* 伪类），logo 本体是 lobe-icons 静态 SVG 生成的 AppAgentLogo* DrawingImage
/// （见 Styles/AgentLogos.axaml）——VM 不解析画刷/资源（D-008 审查修复轮 P1 的教训）。
///
/// 历史：D-008 起 Agent 图标是「软色底芯片 + 散列字形」（AgentVisualMap 按名字散列取字形与色变体），
/// 颜色对一个 Agent 是随机的、无语义；I-034 改为行内官方品牌 logo（去色块），与模型图标一致。
/// 散列字形降级为<b>兜底</b>：lobe-icons 1.95.1 无对应标的 Agent（Codebuff / Droid / pi-agent 及
/// 命名存储——pi 适配器把 store 名作为独立 agent 出账）落 generic，仍给散列字形，列表里多个未识别
/// Agent 之间保持可区分（比单一 ◇ 更有信息量；诚实回退，不伪造品牌标）。
///
/// 匹配只看传入的 Agent 展示名（采集层 AgentName 常量：Claude Code / Codex / Gemini CLI / DSH / …）：
/// 大小写不敏感的包含规则按序判定。规则顺序有讲究——openclaw 在 opencode 前（互不为子串，顺序仅在
/// 新增规则时防误伤），copilot 不含 pi 规则故无碍；zcode 不含 codex 子串。
/// </summary>
internal static class AgentVisualMap
{
    /// <summary>附加属性伪类要逐一 Set 的品牌名单（顺序 = 生成器 AGENT_VENDORS 顺序 + generic 兜底）。</summary>
    internal static readonly string[] Vendors =
    [
        "claudecode", "codex", "geminicli", "amp", "openclaw", "qwen", "kimi", "goose",
        "opencode", "kilo", "hermes", "grok", "antigravity", "copilot", "zai", "dsh", "generic",
    ];

    private static readonly char[] Glyphs = ['✳', '◈', '⬡', '◉', '⬟'];

    /// <summary>解析 Agent 名 → 图标信息；任何名字都返回非 null（未识别落 generic，行不该没有图标）。</summary>
    public static AgentVisual Resolve(string agent)
    {
        var name = agent ?? string.Empty;
        return new AgentVisual(ResolveVendor(name), Glyph(name));
    }

    internal static string ResolveVendor(string agent)
    {
        var name = agent.Trim();
        if (name.Length == 0)
        {
            return "generic";
        }

        if (Contains(name, "claude"))
        {
            return "claudecode";
        }

        if (Contains(name, "gemini"))
        {
            return "geminicli";
        }

        if (Contains(name, "openclaw"))
        {
            return "openclaw";
        }

        if (Contains(name, "opencode"))
        {
            return "opencode";
        }

        if (Contains(name, "codex"))
        {
            return "codex";
        }

        if (Contains(name, "antigravity"))
        {
            return "antigravity";
        }

        if (Contains(name, "copilot"))
        {
            return "copilot";
        }

        if (Contains(name, "kimi"))
        {
            return "kimi";
        }

        if (Contains(name, "qwen"))
        {
            return "qwen";
        }

        if (Contains(name, "kilo"))
        {
            return "kilo";
        }

        if (Contains(name, "hermes"))
        {
            return "hermes";
        }

        if (Contains(name, "goose"))
        {
            return "goose";
        }

        if (Contains(name, "grok"))
        {
            return "grok";
        }

        // ZCode = Z.ai 的编程 CLI（用 Z.ai 三块 "Z" 标）与 DSH（DeepSeek 同形状、官方黑色）
        // 放在最后：都是短名，避免与其他名称的子串判定互扰。
        if (Contains(name, "zcode", "z.ai"))
        {
            return "zai";
        }

        if (Contains(name, "dsh", "deepseek"))
        {
            return "dsh";
        }

        if (Contains(name, "amp"))
        {
            return "amp";
        }

        return "generic";
    }

    /// <summary>兜底字形：按名字稳定散列取 5 字形之一（同一 Agent 全应用一致）。</summary>
    public static char Glyph(string agent) =>
        agent.Length == 0 ? Glyphs[0] : Glyphs[Index(agent, Glyphs.Length)];

    private static int Index(string agent, int mod) =>
        agent.Length == 0 ? 0 : Math.Abs(StringComparer.Ordinal.GetHashCode(agent)) % mod;

    private static bool Contains(string name, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (name.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
