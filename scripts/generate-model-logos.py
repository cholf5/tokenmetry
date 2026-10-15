#!/usr/bin/env python3
"""生成 src/Tokenmetry.App/Styles/ModelLogos.axaml —— lobe-icons 静态 SVG → Avalonia DrawingImage。

数据来源：npm 包 @lobehub/icons-static-svg（版本见 LOBE_VERSION，pin 死不许浮动），
MIT License, Copyright (c) 2023 LobeHub，https://github.com/lobehub/lobe-icons。

转换规则（与 SVG 语义一一对应）：
- <path d> 原样保留，前面拼 F0(evenodd)/F1(nonzero) 填充规则前缀（SVG 默认 nonzero；
  lobe 的 mono 版在根节点声明 evenodd，color 版逐 path 自己声明）。
- <circle> 转成两段 arc 命令的闭合路径。
- <linearGradient gradientUnits="userSpaceOnUse"> → 坐标直译（纯数字，Avalonia RelativePoint
  语法把无 % 坐标解析为绝对值）；默认 objectBoundingBox（含 % 坐标）→ % 直译（Avalonia 解析为
  相对包围盒）。Avalonia 12 起渐变没有 MappingMode 属性，映射完全由坐标写法决定。
- stop 的 offset（小数或 %）折算成 0..1；stop-opacity / fill-opacity 折算进颜色 alpha（#AARRGGBB）。
- 命名色仅映射常见一小撮（mistral 用到 gold），未知命名色直接报错。
- 遇到 transform / stroke / mask / <g> / radialGradient 等不支持结构：整体失败、不写文件——
  宁可该厂商回退字形，也不输出失真的 logo。

用法：python3 scripts/generate-model-logos.py
输出默认写到 src/Tokenmetry.App/Styles/ModelLogos.axaml（--out 可改）。
"""

from __future__ import annotations

import argparse
import tempfile
import urllib.request
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path

LOBE_VERSION = "1.95.1"
UNPKG = f"https://unpkg.com/@lobehub/icons-static-svg@{LOBE_VERSION}/icons"

# vendor（= ModelVisualMap.Vendors 里的名字）→ (mono 版 lobe id | None, 彩色版 lobe id | None)。
# 顺序即生成顺序。三種主题策略：
#   只有彩色版            → 明暗同用 color；
#   只有单色版（官方标无彩色形态）→ Light=黑 / Dark=白（lobe 静态 PNG 惯例）；
#   两者都有且彩标以白为主体（暗底设计，kimi）→ Light=单色黑 / Dark=彩色。
VENDORS: list[tuple[str, str | None, str | None]] = [
    ("claude", None, "claude-color"),
    ("openai", "openai", None),
    ("gemini", None, "gemini-color"),
    ("glm", None, "zhipu-color"),  # lobe 无 glm.svg，用智谱官方标
    ("deepseek", None, "deepseek-color"),
    ("qwen", None, "qwen-color"),
    ("kimi", "kimi", "kimi-color"),  # kimi 彩标主体为白色 K，浅色底不可读 → 浅色用单色标
    ("llama", None, "meta-color"),  # lobe 无 llama.svg，用 Meta 官方标
    ("grok", "grok", None),
    ("mistral", None, "mistral-color"),
    ("doubao", None, "doubao-color"),
    ("minimax", None, "minimax-color"),
    ("cohere", None, "cohere-color"),
    ("ernie", None, "wenxin-color"),  # lobe 无 ernie.svg，用百度文心官方标
    ("hunyuan", None, "hunyuan-color"),
]

# vendor → 资源键的 Pascal 段（与 Tokens.axaml 旧命名习惯一致：OpenAI / DeepSeek / MiniMax）
VENDOR_PASCAL = {
    "claude": "Claude", "openai": "OpenAI", "gemini": "Gemini", "glm": "Glm",
    "deepseek": "DeepSeek", "qwen": "Qwen", "kimi": "Kimi", "llama": "Llama",
    "grok": "Grok", "mistral": "Mistral", "doubao": "Doubao", "minimax": "MiniMax",
    "cohere": "Cohere", "ernie": "Ernie", "hunyuan": "Hunyuan",
    "phi": "Phi", "generic": "Generic",
}

MONO_LIGHT = "#000000"
MONO_DARK = "#FFFFFF"

NAMED_COLORS = {
    "gold": "#FFD700", "white": "#FFFFFF", "black": "#000000", "silver": "#C0C0C0",
    "gray": "#808080", "red": "#FF0000", "green": "#008000", "blue": "#0000FF",
    "yellow": "#FFFF00", "orange": "#FFA500", "purple": "#800080",
}

REJECTED_ATTRS = ("transform", "stroke", "stroke-width", "mask", "clip-path",
                  "filter", "opacity", "stroke-opacity", "stroke-dasharray")


def die(msg: str) -> None:
    raise SystemExit(f"generate-model-logos: {msg}")


def fetch(lobe_id: str, cache_dir: Path) -> Path:
    cache = cache_dir / f"{lobe_id}.svg"
    if not cache.exists():
        req = urllib.request.Request(
            f"{UNPKG}/{lobe_id}.svg", headers={"User-Agent": "tokenmetry-model-logos/1.0"})
        with urllib.request.urlopen(req, timeout=30) as resp:
            cache.write_bytes(resp.read())
    return cache


def parse_color(text: str, opacity: str | None, where: str) -> str:
    """SVG 颜色 + 可选透明度 → Avalonia #RRGGBB / #AARRGGBB。"""
    value = text.strip()
    if value.startswith("#"):
        hexpart = value[1:]
        if len(hexpart) == 3:
            hexpart = "".join(ch * 2 for ch in hexpart)
        if len(hexpart) != 6:
            die(f"{where}: 不支持的十六进制颜色 {value!r}")
        rgb = hexpart.upper()
    else:
        named = NAMED_COLORS.get(value.lower())
        if named is None:
            die(f"{where}: 未映射的命名颜色 {value!r}（请在 NAMED_COLORS 补充后重跑）")
        rgb = named[1:]
    if opacity is not None:
        alpha = round(float(opacity) * 255)
        if not 0 <= alpha <= 255:
            die(f"{where}: opacity 越界 {opacity!r}")
        return f"#{alpha:02X}{rgb}"
    return f"#{rgb}"


def fmt_number(value: float) -> str:
    text = f"{value:.6f}".rstrip("0").rstrip(".")
    return text if text not in ("-0", "") else "0"


def circle_to_path(cx: float, cy: float, r: float) -> str:
    # 两段半圆弧拼整圆（单闭合轮廓，nonzero/evenodd 下形状一致）
    return (f"M{fmt_number(cx - r)},{fmt_number(cy)} "
            f"a{fmt_number(r)},{fmt_number(r)} 0 1,0 {fmt_number(2 * r)},0 "
            f"a{fmt_number(r)},{fmt_number(r)} 0 1,0 {fmt_number(-2 * r)},0 Z")


@dataclass
class Gradient:
    absolute: bool                       # userSpaceOnUse → True（仅用于断言写法，不出现在 XAML）
    start: str                           # "7,15.5"（绝对）或 "75.897%,89.199%"（相对）
    end: str
    stops: list[tuple[str, float]]       # (颜色含 alpha, offset 0..1)


def parse_gradient(node: ET.Element, where: str) -> Gradient:
    units = node.get("gradientUnits", "objectBoundingBox")
    if units not in ("userSpaceOnUse", "objectBoundingBox"):
        die(f"{where}: 不支持的 gradientUnits={units!r}")
    raw = {k: node.get(k) for k in ("x1", "y1", "x2", "y2")}

    def coord(name: str) -> str:
        value = raw[name]
        if value is None:
            die(f"{where}: linearGradient 缺 {name}")
        value = value.strip()
        if units == "userSpaceOnUse":
            if "%" in value:
                die(f"{where}: userSpaceOnUse 渐变不应使用百分比坐标：{name}={value!r}")
            return value
        if not value.endswith("%"):
            die(f"{where}: objectBoundingBox 渐变坐标应为百分比：{name}={value!r}")
        return value

    stops: list[tuple[str, float]] = []
    for stop in node:
        if stop.tag.split("}")[-1] != "stop":
            die(f"{where}: linearGradient 内不支持的子元素 <{stop.tag}>")
        color = parse_color(stop.get("stop-color", "#000000"), stop.get("stop-opacity"), where)
        raw_offset = stop.get("offset", "0").strip()
        offset = float(raw_offset.removesuffix("%")) / 100 if raw_offset.endswith("%") else float(raw_offset)
        if not 0 <= offset <= 1:
            die(f"{where}: stop offset 越界 {raw_offset!r}")
        stops.append((color, offset))
    if not stops:
        die(f"{where}: linearGradient 没有 stop")

    return Gradient(absolute=units == "userSpaceOnUse",
                    start=f"{coord('x1')},{coord('y1')}",
                    end=f"{coord('x2')},{coord('y2')}",
                    stops=stops)


def shape_drawing(node: ET.Element, default_fill: str, default_fill_rule: str,
                  gradients: dict[str, Gradient], where: str, indent: str) -> str:
    """单个形状 → 一段 GeometryDrawing XAML（indent 为 GeometryDrawing 的缩进）。"""
    tag = node.tag.split("}")[-1]
    for attr in REJECTED_ATTRS:
        if node.get(attr) is not None:
            die(f"{where}: <{tag}> 带不支持的 {attr} 属性")
    # clip-rule 只在配合 clip-path 时才有语义（cohere 等出现），直接忽略。

    if tag == "path":
        d = node.get("d")
        if not d:
            die(f"{where}: <path> 缺 d")
        geometry = d.strip()
    elif tag == "circle":
        try:
            cx, cy, r = float(node.get("cx")), float(node.get("cy")), float(node.get("r"))
        except (TypeError, ValueError):
            die(f"{where}: <circle> 的 cx/cy/r 缺失或非法")
        geometry = circle_to_path(cx, cy, r)
    else:
        die(f"{where}: 不支持的形状 <{tag}>")

    fill_rule = node.get("fill-rule", default_fill_rule)
    if fill_rule == "evenodd":
        prefix = "F0"
    elif fill_rule == "nonzero":
        prefix = "F1"
    else:
        die(f"{where}: 不支持的 fill-rule={fill_rule!r}")
    geometry = f"{prefix} {geometry}"

    fill = node.get("fill", default_fill)
    if fill is None:
        die(f"{where}: 无法确定填充色（形状与根节点都没有 fill）")
    fill_opacity = node.get("fill-opacity")

    if fill.startswith("url(#"):
        gradient_id = fill[5:fill.index(")")]
        gradient = gradients.get(gradient_id)
        if gradient is None:
            die(f"{where}: 引用了未定义的渐变 #{gradient_id}")
        if fill_opacity is not None and float(fill_opacity) != 1:
            die(f"{where}: 渐变填充叠 fill-opacity 尚不支持")
        brush_indent = indent + "    "
        brush_tag_indent = brush_indent + "    "
        stop_indent = brush_tag_indent + "    "
        stops = "".join(
            f'\n{stop_indent}<GradientStop Color="{color}" Offset="{fmt_number(offset)}"/>'
            for color, offset in gradient.stops)
        mapping = ''  # Avalonia 12 无 MappingMode：纯数字=绝对、%=相对，由坐标写法本身决定
        return (f'{indent}<GeometryDrawing Geometry="{geometry}">\n'
                f'{brush_indent}<GeometryDrawing.Brush>\n'
                f'{brush_tag_indent}<LinearGradientBrush StartPoint="{gradient.start}" '
                f'EndPoint="{gradient.end}"{mapping}>{stops}\n'
                f'{brush_tag_indent}</LinearGradientBrush>\n'
                f'{brush_indent}</GeometryDrawing.Brush>\n'
                f'{indent}</GeometryDrawing>')

    color = parse_color(fill, fill_opacity, where)
    return f'{indent}<GeometryDrawing Brush="{color}" Geometry="{geometry}"/>'


def convert_svg(path: Path, flavor: str, theme_color: str, indent: str = "            ") -> str:
    """单个 SVG → DrawingImage.Drawing 的内部 XML（统一 DrawingGroup 包裹）。

    indent = DrawingGroup 的缩进（形状再退一层）。
    """
    where = path.name
    root = ET.parse(path).getroot()

    def local(tag: str) -> str:
        return tag.split("}")[-1]

    if local(root.tag) != "svg":
        die(f"{where}: 根元素不是 <svg>")
    if root.get("viewBox") != "0 0 24 24":
        die(f"{where}: viewBox 不是 0 0 24 24（{root.get('viewBox')!r}）")
    for attr in REJECTED_ATTRS:
        if root.get(attr) is not None:
            die(f"{where}: 根节点带不支持的 {attr} 属性")
    if root.get("style") not in (None, "flex:none;line-height:1"):
        die(f"{where}: 根节点 style 不是预期值：{root.get('style')!r}")

    default_fill = root.get("fill")
    if flavor == "mono":
        if default_fill != "currentColor":
            die(f"{where}: mono 版根节点 fill 应为 currentColor（实际 {default_fill!r}）")
        default_fill = theme_color
    elif default_fill is not None:
        die(f"{where}: color 版根节点不应有 fill")

    default_fill_rule = root.get("fill-rule", "nonzero")

    gradients: dict[str, Gradient] = {}
    for child in root:
        if local(child.tag) == "defs":
            for sub in child:
                if local(sub.tag) == "linearGradient":
                    gid = sub.get("id")
                    if not gid:
                        die(f"{where}: linearGradient 缺 id")
                    gradients[gid] = parse_gradient(sub, where)
                elif local(sub.tag) != "title":
                    die(f"{where}: defs 内不支持 <{local(sub.tag)}>")

    drawings: list[str] = []
    for child in root:
        tag = local(child.tag)
        if tag in ("title", "defs"):
            continue
        drawings.append(shape_drawing(child, default_fill, default_fill_rule,
                                      gradients, where, indent + "    "))

    if not drawings:
        die(f"{where}: 没有可转换的形状")
    inner = "\n".join(drawings)
    return f"{indent}<DrawingGroup>\n{inner}\n{indent}</DrawingGroup>"


def drawing_image(vendor: str, inner: str, indent: str = "    ") -> str:
    key = f"AppModelLogo{VENDOR_PASCAL[vendor]}"
    pad = indent + "    "
    return (f'{indent}<DrawingImage x:Key="{key}">\n'
            f'{pad}<DrawingImage.Drawing>\n'
            f'{inner}\n'
            f'{pad}</DrawingImage.Drawing>\n'
            f'{indent}</DrawingImage>')


def build_xaml(cache_dir: Path) -> str:
    color_entries: list[str] = []       # 明暗同用的彩色版 → 字典级普通资源
    light_entries: list[str] = []       # ThemeDictionaries Light（单色黑）
    dark_entries: list[str] = []        # ThemeDictionaries Dark（单色白 / kimi 彩色）

    for vendor, mono_id, color_id in VENDORS:
        if mono_id and color_id:        # 双版本：Light=单色黑 / Dark=彩色
            light_entries.append(drawing_image(vendor, convert_svg(
                fetch(mono_id, cache_dir), "mono", MONO_LIGHT, indent="                "), indent="        "))
            dark_entries.append(drawing_image(vendor, convert_svg(
                fetch(color_id, cache_dir), "color", "", indent="                "), indent="        "))
        elif mono_id:                   # 只有单色版：Light=黑 / Dark=白
            light_entries.append(drawing_image(vendor, convert_svg(
                fetch(mono_id, cache_dir), "mono", MONO_LIGHT, indent="                "), indent="        "))
            dark_entries.append(drawing_image(vendor, convert_svg(
                fetch(mono_id, cache_dir), "mono", MONO_DARK, indent="                "), indent="        "))
        else:
            assert color_id
            color_entries.append(drawing_image(vendor, convert_svg(fetch(color_id, cache_dir), "color", "")))

    theme_block = ""
    if light_entries:
        theme_block = (
            "    <ResourceDictionary.ThemeDictionaries>\n"
            "        <ResourceDictionary x:Key=\"Light\">\n"
            + "\n".join(light_entries) + "\n"
            "        </ResourceDictionary>\n"
            "        <ResourceDictionary x:Key=\"Dark\">\n"
            + "\n".join(dark_entries) + "\n"
            "        </ResourceDictionary>\n"
            "    </ResourceDictionary.ThemeDictionaries>\n\n")

    keys = ", ".join(f"AppModelLogo{VENDOR_PASCAL[v]}" for v, _, _ in VENDORS)
    header = (
        f"<!--\n"
        f"        模型厂商 logo 资源（本文件由 scripts/generate-model-logos.py 生成，勿手改）。\n"
        f"        来源：@lobehub/icons-static-svg@{LOBE_VERSION}（https://github.com/lobehub/lobe-icons，\n"
        f"        MIT License, Copyright (c) 2023 LobeHub）。主题策略：官方标无彩色形态\n"
        f"        （openai/grok）给 Light=黑 / Dark=白 两套单色；kimi 彩标以白为主体（暗底设计），\n"
        f"        Light 用单色标、Dark 用彩标；其余明暗同用 color 版官方配色。\n"
        f"        phi / generic 无 lobe 对应，视图层回退字形（不在此文件）。\n"
        f"        再生：python3 scripts/generate-model-logos.py\n"
        f"        键清单（App* 前缀，与 Semi 避让）：{keys}\n"
        f"    -->")

    body = "\n\n".join(color_entries)
    return ('<ResourceDictionary xmlns="https://github.com/avaloniaui"\n'
            '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">\n\n'
            f"    {header}\n\n"
            + theme_block
            + body + "\n"
            "</ResourceDictionary>\n")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", default="src/Tokenmetry.App/Styles/ModelLogos.axaml")
    parser.add_argument("--cache-dir", default=None, help="SVG 下载缓存目录（默认临时目录）")
    args = parser.parse_args()

    cache_dir = Path(args.cache_dir) if args.cache_dir else Path(tempfile.mkdtemp(prefix="lobe-svg-"))
    cache_dir.mkdir(parents=True, exist_ok=True)

    xaml = build_xaml(cache_dir)
    out = Path(args.out)
    out.write_text(xaml, encoding="utf-8")
    print(f"OK：写出 {out}（{len(xaml)} 字符，{len(VENDORS)} 个厂商）")


if __name__ == "__main__":
    main()
