using System;
using System.Collections.Generic;

namespace Tokenmetry.App.ViewModels;

/// <summary>模型图标信息：厂商变体名（驱动 <c>Tokenmetry.App.Controls.ModelChip</c> 附加属性翻译成
/// model-* 伪类，选中 SharedStyles 里的行内官方 logo）+ 兜底字形。不可变，行 VM 持有。</summary>
/// <param name="Vendor">厂商变体名（claude/openai/…/generic；对应 SharedStyles 的 model-* 样式组）。</param>
/// <param name="Glyph">无 logo 厂商的字形兜底（phi 显示 Φ）；generic 的 UI 兜底是 circle-help
/// 矢量图标（Q-009，SharedStyles 的 model-fallback 样式），字形仅保留数据层语义。</param>
public sealed record ModelVisual(string Vendor, char Glyph);

/// <summary>
/// 模型名 → 内置图标的稳定映射（行内官方 logo 变体 + 兜底字形）。与 <see cref="AgentVisualMap"/>
/// 同一机制：厂商判定走 SharedStyles 的 model-* 伪类（VM 不解析画刷/资源——明暗翻转纪律同
/// AgentVisualMap 审查修复轮 P1 的教训）；logo 本体是 lobe-icons 静态 SVG 生成的 AppModelLogo*
/// DrawingImage（见 Styles/ModelLogos.axaml），由伪类样式经 DynamicResource 给出。
/// 内置规则覆盖主流厂商（Anthropic / OpenAI / Google / 智谱 GLM / DeepSeek / 通义 Qwen /
/// Moonshot Kimi / Meta Llama / xAI Grok / Mistral / 字节 Doubao / MiniMax / Cohere / 文心 /
/// 腾讯混元），其余（含 phi）一律落到 generic/字形兜底，与截图「其他」行的兜底图标同语义。
/// 匹配只看传入名（模型统计页给的是聚合后的规范名）：大小写不敏感的包含/前缀规则按序判定，
/// 具体厂商在前、openai 的 o1/o3/o4 前缀判定最后（避免 oxx 误命中）。
/// </summary>
internal static class ModelVisualMap
{
    /// <summary>附加属性伪类要逐一 Set 的厂商名单（顺序即判定优先序，见 <see cref="Resolve"/>）。</summary>
    internal static readonly string[] Vendors =
    [
        "claude", "openai", "gemini", "glm", "deepseek", "qwen", "kimi", "llama",
        "grok", "mistral", "doubao", "minimax", "cohere", "ernie", "hunyuan", "phi", "generic",
    ];

    /// <summary>解析模型名 → 图标信息；任何名字都返回非 null（未识别厂商落 generic，统计行不该没有图标）。</summary>
    public static ModelVisual Resolve(string modelName)
    {
        var vendor = ResolveVendor(modelName ?? string.Empty);
        return new ModelVisual(vendor, GlyphOf(vendor));
    }

    internal static string ResolveVendor(string modelName)
    {
        var name = modelName.Trim();
        if (name.Length == 0)
        {
            return "generic";
        }

        // 包含判定（OrdinalIgnoreCase）。规则顺序：具体厂商名在前，泛词在后。
        if (Contains(name, "claude", "anthropic"))
        {
            return "claude";
        }

        if (Contains(name, "gemini"))
        {
            return "gemini";
        }

        if (Contains(name, "deepseek"))
        {
            return "deepseek";
        }

        if (Contains(name, "kimi", "moonshot"))
        {
            return "kimi";
        }

        if (Contains(name, "qwen", "qwq", "qvq", "tongyi"))
        {
            return "qwen";
        }

        if (Contains(name, "glm", "chatglm", "zhipu", "z.ai"))
        {
            return "glm";
        }

        if (Contains(name, "llama"))
        {
            return "llama";
        }

        if (Contains(name, "grok"))
        {
            return "grok";
        }

        if (Contains(name, "mistral", "mixtral", "codestral", "ministral", "pixtral", "magistral", "voxtral"))
        {
            return "mistral";
        }

        if (Contains(name, "doubao"))
        {
            return "doubao";
        }

        if (Contains(name, "minimax", "abab"))
        {
            return "minimax";
        }

        if (Contains(name, "cohere", "command"))
        {
            return "cohere";
        }

        if (Contains(name, "ernie", "wenxin"))
        {
            return "ernie";
        }

        if (Contains(name, "hunyuan"))
        {
            return "hunyuan";
        }

        if (Contains(name, "phi-"))
        {
            return "phi";
        }

        // OpenAI 家族放最后：gpt / codex / davinci / chatgpt 子串，与 o1/o3/o4 系列的
        // 「段首前缀」判定（o3-mini 命中、mor-3 不命中）。
        if (Contains(name, "gpt", "codex", "chatgpt", "davinci")
            || HasOpenAiReasoningPrefix(name)
            || name.Contains("openai", StringComparison.OrdinalIgnoreCase))
        {
            return "openai";
        }

        return "generic";
    }

    private static char GlyphOf(string vendor) => vendor switch
    {
        "claude" => '✳',     // Claude 星芒
        "openai" => '⬡',     // 六边形结花（App 在 Agent 芯片已有同字形先例）
        "gemini" => '✦',     // Gemini 四角星光
        "glm" => 'Z',        // Z.ai / 智谱
        "deepseek" => 'D',
        "qwen" => 'Q',
        "kimi" => 'K',
        "llama" => 'L',
        "grok" => 'X',
        "mistral" => 'M',
        "doubao" => '豆',    // 豆包（品牌名本字）
        "minimax" => 'M',
        "cohere" => 'C',
        "ernie" => 'E',
        "hunyuan" => 'H',
        "phi" => 'Φ',
        _ => '?',            // 未识别（含「其他」聚合行）：数据层兜底语义；UI 端 generic 显示
                             // AppIconCircleHelp 矢量问号（Q-009，字形墨迹太小退役）
    };

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

    /// <summary>o1 / o3 / o4 系列（o1、o3-mini、o4-mini-high…）的段首判定：名字以系列号开头，
    /// 紧跟串尾或分隔符（'-' / '.' / '_' / '/'）才算——"mor3"、"gto1" 这类不误命中。</summary>
    private static bool HasOpenAiReasoningPrefix(string name)
    {
        foreach (var prefix in (string[])["o1", "o3", "o4"])
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.Length == prefix.Length)
            {
                return true;
            }

            var next = name[prefix.Length];
            if (next is '-' or '.' or '_' or '/')
            {
                return true;
            }
        }

        return false;
    }
}
