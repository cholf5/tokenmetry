#!/usr/bin/env python3
"""生成应用内界面图标资源（lucide 图标 SVG → Avalonia StreamGeometry）：

- src/Tokenmetry.App/Styles/Icons.axaml —— 界面 chrome 图标（AppIcon*，2026-10-24 起
  替换顶栏/侧栏/KPI chip 里的 Unicode 字形图标——字形回退到 Apple Symbols 类符号字体
  墨迹只占 em 的四到五成，34px 按钮里 14pt 字形实际墨迹仅 6~8px；矢量图标尺寸跨平台稳定）。

与 scripts/generate-brand-logos.py（品牌 logo，DrawingImage 彩标）的分工：
品牌 logo 是彩色/官方标语义，走 DrawingImage；界面图标是单色描边语义，走 StreamGeometry +
Path.app-icon 统一描边样式（颜色一律使用处给令牌画刷，随明暗翻转）。

数据来源：npm 包 lucide-static（版本见 LUCIDE_VERSION，pin 死不许浮动），
ISC License, Copyright (c) lucide authors，https://github.com/lucide-icons/lucide。

转换规则（lucide 是 stroke 语义：fill=none + stroke=currentColor @ 24×24 视框）：
- <path d> 原样保留；
- <circle> 转成两段 arc 的整圆路径；
- <line>/<polyline>/<polygon> 转 M/L(/Z) 序列；
- <rect> 转闭合路径（rx/ry 圆角用 arc 拼接）；
- 合并各子元素为单个 path 串进 StreamGeometry。描边参数（粗细 1.5 / 圆角端点 / 颜色）
  不进资源，由 SharedStyles.axaml 的 Path.app-icon 统一给出。
- 遇到 fill 声明（lucide 少数实心子形）、transform、mask、<g> 等不支持结构：整体失败、
  不写文件——宁可构建失败也不输出失真图标。

用法：python3 scripts/generate-ui-icons.py（--out 可改输出路径）。
"""

from __future__ import annotations

import argparse
import re
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

LUCIDE_VERSION = "1.61.0"
UNPKG = f"https://unpkg.com/lucide-static@{LUCIDE_VERSION}/icons"

# 图标键（= AppIcon{键} 资源键，顺序即生成顺序）→ lucide 图标 id。
# 引用点（勿删注释，键名变更需同步）：
#   House/Table/Bot/Cpu/Folder/Clock/DollarCircle/Database/Settings —— 侧栏导航九页
#     （NavigationItemViewModel.ShellPages 的 Icon 键 + Controls/PageIcon 翻译成 pi-* 伪类）；
#   Settings —— 兼顶栏设置入口；RotateCw —— 顶栏手动刷新；LoaderCircle —— 顶栏刷新进行中
#     菊花与侧栏品牌标；Coins/DollarCircle/Table/Zap —— 总览 KPI 四卡与样式预览页。
#   不收裸 dollar-sign（2026-10-24 真机目检）：竖杆长长伸出 S 上下、横端子向两侧大敞，
#     15px 下读作「歪」；KPI 成本卡改用 DollarCircle（与导航成本图标一致，圆圈锚定字形）。
#   HardDriveUpload/HardDriveDownload —— 成本分析页缓存读取/写占比卡（Q-007）：箭头方向
#     相对盘体（读=数据出盘 upload、写=数据入盘 download），与页内输入 ↓ / 输出 ↑ 的方向语言同构。
#   CircleHelp —— 模型芯片 generic 兜底（Q-009）：未识别厂商的模型显示「问号」图标，
#     取代原 ◇ 文本字形（墨迹太小、疑似山寨品牌标）；phi 的 Φ 文本字形不受影响。
#   ArrowUp/ArrowDown —— 成本分析页输入/输出成本占比卡（原 ↓ / ↑ 文本字形，I-040 同源问题）。
#   Close —— 顶栏时间范围弹层「我的预设」行删除按钮（D-020）。
ICONS: list[tuple[str, str]] = [
    ("House", "house"),
    ("Table", "table"),
    ("Bot", "bot"),
    ("Cpu", "cpu"),
    ("Folder", "folder"),
    ("Clock", "clock"),
    ("DollarCircle", "circle-dollar-sign"),
    ("Database", "database"),
    ("Settings", "settings"),
    ("RotateCw", "rotate-cw"),
    ("LoaderCircle", "loader-circle"),
    ("Coins", "coins"),
    ("Zap", "zap"),
    ("HardDriveUpload", "hard-drive-upload"),
    ("HardDriveDownload", "hard-drive-download"),
    ("CircleHelp", "circle-help"),
    ("ArrowUp", "arrow-up"),
    ("ArrowDown", "arrow-down"),
    ("Close", "x"),
]

SVG_NS = "{http://www.w3.org/2000/svg}"

# path 数据里的一个数（整数 / 小数 / 科学计数，含正负号）
_NUM_RE = re.compile(r"[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?")


def _num(value: str | float) -> str:
    """数字规范化：去掉多余的 .0，保持整数原样。"""
    f = float(value)
    if f.is_integer():
        return str(int(f))
    return repr(round(f, 4))


def _circle_to_path(el: ET.Element) -> str:
    cx = float(el.attrib["cx"])
    cy = float(el.attrib["cy"])
    r = float(el.attrib["r"])
    return _oval_path(cx, cy, r, r)


def _ellipse_to_path(el: ET.Element) -> str:
    cx = float(el.attrib["cx"])
    cy = float(el.attrib["cy"])
    rx = float(el.attrib["rx"])
    ry = float(el.attrib["ry"])
    return _oval_path(cx, cy, rx, ry)


def _oval_path(cx: float, cy: float, rx: float, ry: float) -> str:
    return (f"M {_num(cx - rx)} {_num(cy)} "
            f"a {_num(rx)} {_num(ry)} 0 1 0 {_num(2 * rx)} 0 "
            f"a {_num(rx)} {_num(ry)} 0 1 0 {_num(-2 * rx)} 0")


def _line_to_path(el: ET.Element) -> str:
    a = el.attrib
    return f"M {a['x1']} {a['y1']} L {a['x2']} {a['y2']}"


def _points_to_path(points: str, close: bool) -> str:
    pairs = [p for p in re.split(r"[,\s]+", points.strip()) if p]
    if len(pairs) % 2 != 0 or len(pairs) < 4:
        raise ValueError(f"points 坐标数量不合法: {points!r}")
    coords = list(zip(pairs[0::2], pairs[1::2]))
    d = f"M {coords[0][0]} {coords[0][1]}"
    d += "".join(f" L {x} {y}" for x, y in coords[1:])
    return d + (" Z" if close else "")


def _rect_to_path(el: ET.Element) -> str:
    x = float(el.attrib.get("x", 0))
    y = float(el.attrib.get("y", 0))
    w = float(el.attrib["width"])
    h = float(el.attrib["height"])
    rx = float(el.attrib.get("rx", el.attrib.get("ry", 0)))
    ry = float(el.attrib.get("ry", el.attrib.get("rx", 0)))
    if w <= 0 or h <= 0:
        raise ValueError("rect 宽高必须为正")
    if rx <= 0 and ry <= 0:
        return (f"M {_num(x)} {_num(y)} H {_num(x + w)} V {_num(y + h)} "
                f"H {_num(x)} Z")
    rx, ry = rx or ry, ry or rx
    if 2 * rx > w or 2 * ry > h:
        raise ValueError("rect 圆角超过边长")
    return (f"M {_num(x + rx)} {_num(y)} "
            f"H {_num(x + w - rx)} A {_num(rx)} {_num(ry)} 0 0 1 {_num(x + w)} {_num(y + ry)} "
            f"V {_num(y + h - ry)} A {_num(rx)} {_num(ry)} 0 0 1 {_num(x + w - rx)} {_num(y + h)} "
            f"H {_num(x + rx)} A {_num(rx)} {_num(ry)} 0 0 1 {_num(x)} {_num(y + h - ry)} "
            f"V {_num(y + ry)} A {_num(rx)} {_num(ry)} 0 0 1 {_num(x + rx)} {_num(y)} Z")


def _child_to_path(el: ET.Element) -> str:
    if el.attrib.get("fill", "none") not in ("none",):
        raise ValueError(f"不支持带 fill 的子元素（lucide 应全描边）: {el.tag} {el.attrib}")
    for attr in ("transform", "mask", "clip-path"):
        if attr in el.attrib:
            raise ValueError(f"不支持的属性 {attr}: {el.attrib}")
    tag = el.tag
    if tag == SVG_NS + "path":
        d = el.attrib["d"].strip()
        # SVG 规范：相对 moveto 出现在 path 首位时按绝对解释。本生成器把多个子路径拼进
        # 同一个 d 串——拼接后行首 m 不再是 path 首命令，会相对上一子路径终点漂移。
        # 规范化成 M 时只拆首对坐标：moveto 后若还跟着隐式线段对，它们继承 moveto 的
        # 大小写（M → 隐式绝对 L），须显式补小写 l 保住原本的相对语义。
        if d[0] == "m":
            body = d[1:].lstrip()
            spans = list(_NUM_RE.finditer(body))
            if len(spans) < 2:
                raise ValueError(f"path 首个 moveto 缺坐标对: {d!r}")
            head = body[:spans[1].end()]
            rest = body[spans[1].end():].lstrip()
            d = "M" + head + (" l" + rest if rest and _NUM_RE.match(rest) else rest)
        return d
    if tag == SVG_NS + "circle":
        return _circle_to_path(el)
    if tag == SVG_NS + "ellipse":
        return _ellipse_to_path(el)
    if tag == SVG_NS + "line":
        return _line_to_path(el)
    if tag == SVG_NS + "polyline":
        return _points_to_path(el.attrib["points"], close=False)
    if tag == SVG_NS + "polygon":
        return _points_to_path(el.attrib["points"], close=True)
    if tag == SVG_NS + "rect":
        return _rect_to_path(el)
    raise ValueError(f"不支持的子元素: {tag}")


def convert_icon(svg_text: str) -> str:
    """一个 lucide SVG → 单个 path d 串（24×24 视框，stroke 语义由消费端样式给出）。"""
    root = ET.fromstring(svg_text)
    if root.tag != SVG_NS + "svg":
        raise ValueError("根元素必须是 svg")
    vb = root.attrib.get("viewBox")
    if vb != "0 0 24 24":
        raise ValueError(f"viewBox 非预期: {vb!r}")
    for attr in ("transform", "mask"):
        if attr in root.attrib:
            raise ValueError(f"根元素不支持 {attr}")
    parts = [_child_to_path(el) for el in root]
    if not parts:
        raise ValueError("SVG 无可转换子元素")
    merged = " ".join(parts)
    if not merged.startswith("M"):
        raise ValueError("合并后的 path 串必须以绝对 moveto 开头")
    return merged


def fetch_icon(lucide_id: str) -> str:
    req = urllib.request.Request(
        f"{UNPKG}/{lucide_id}.svg",
        headers={"User-Agent": "tokenmetry-generate-ui-icons"},
    )
    with urllib.request.urlopen(req, timeout=30) as resp:
        return resp.read().decode("utf-8")


def build_axaml() -> str:
    entries = []
    for key, lucide_id in ICONS:
        d = convert_icon(fetch_icon(lucide_id))
        entries.append((key, lucide_id, d))

    lines = [
        '<ResourceDictionary xmlns="https://github.com/avaloniaui"',
        '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
        "",
        "    <!--",
        "        界面图标（脚本生成，勿手改）：lucide-static@" + LUCIDE_VERSION + " → StreamGeometry。",
        "        再生：python3 scripts/generate-ui-icons.py（图标名单与版本 pin 在脚本 ICONS / LUCIDE_VERSION）。",
        "        来源：lucide（https://github.com/lucide-icons/lucide，ISC License, Copyright (c) lucide authors）。",
        "",
        "        lucide 为 stroke 语义（24×24 视框、fill=none、stroke-width 2、圆角端点）：本文件同时给出",
        "        PathIcon 的隐式 ControlTheme 与 AppIcon* 几何。渲染经 PathIcon（内层 Path 自适应墨迹尺寸、",
        "        由布局居中于定尺寸槽位）——不用裸 Path + Stretch=Uniform：Avalonia 的 Shape.Stretch",
        "        只缩放适配并钉在元素左上角、没有 WPF 的居中项（Shape.CalculateSizeAndTransform 查证），",
        "        墨迹在槽内偏移 (槽−墨)/2，$ 左偏 3px、zap 左偏 1.2px（真机目检「歪/不居中」实锤）。",
        "        描边粗细/端点在 ControlTheme 模板定值（1.5 / 圆角），颜色走 Foreground 继承",
        "        （模板 TemplateBinding Foreground——导航选中、按钮禁用等状态色随祖先 Foreground 生效）。",
        "        ControlTheme 显式 ClipToBounds=False：Avalonia 12 起 TemplatedControl（IconElement/",
        "        PathIcon 继承）把 ClipToBounds 元数据默认改 true，而内层 Path 按几何 bounds 缩放填槽、",
        "        居中描边的外半环（0.75px）溢出控件边界——默认整圈被削，大圆类图标（clock/dollar-circle/",
        "        database/house）左右弧读成直边（真机目检实锤，headless 像素量测复现）。裸 Path（Shape）",
        "        的 ClipToBounds 元数据仍默认 false、不受影响——此 setter 只为对冲 PathIcon 的默认值。",
        "",
        "        替换的 Unicode 字形图标见 git 历史（⟳ ⚙ ◌ ⌂ ▤ ◉ ⬡ ▣ ◷ ＄ ⌗ ◈ ▣ ↻）：字形经系统回退",
        "        字体（macOS = Apple Symbols）渲染，墨迹只占 em 四到五成且跨平台不可控。",
        "",
        "        消费：静态处 <PathIcon Classes=\"app-icon\" Data=\"{StaticResource AppIcon*}\"",
        "        Width=\"..\" Height=\"..\" Foreground=\"..\"/>；数据驱动处（导航项）经 Controls/PageIcon.Icon",
        "        把键名翻译成 pi-* 伪类 → 本文件 pi-* 样式给 Data。名单变更需同步：",
        "        ShellPages（NavigationItemViewModel.cs）与 DesignTokenTests.AllTokenKeys。",
        "    -->",
        "",
        '    <ControlTheme x:Key="{x:Type PathIcon}" TargetType="PathIcon">',
        '        <Setter Property="ClipToBounds" Value="False"/>',
        "        <Setter Property=\"Template\">",
        "            <ControlTemplate>",
        "                <Path Data=\"{TemplateBinding Data}\"",
        "                      Stretch=\"Uniform\"",
        "                      Stroke=\"{TemplateBinding Foreground}\"",
        "                      StrokeThickness=\"1.5\"",
        "                      StrokeLineCap=\"Round\"",
        "                      StrokeJoin=\"Round\"",
        '                      HorizontalAlignment="Center"',
        '                      VerticalAlignment="Center"/>',
        "            </ControlTemplate>",
        "        </Setter>",
        "    </ControlTheme>",
    ]
    for key, lucide_id, d in entries:
        lines.append(f"    <!-- {lucide_id} -->")
        lines.append(f'    <StreamGeometry x:Key="AppIcon{key}">{d}</StreamGeometry>')
    lines.append("")
    lines.append("</ResourceDictionary>")
    return "\n".join(lines) + "\n"


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="src/Tokenmetry.App/Styles/Icons.axaml")
    args = ap.parse_args()

    axaml = build_axaml()
    out = Path(args.out)
    out.write_text(axaml, encoding="utf-8")
    print(f"已生成 {out}（{len(ICONS)} 个图标，lucide-static@{LUCIDE_VERSION}）")


if __name__ == "__main__":
    main()
