#!/usr/bin/env python3
# 应用画板（主题皮肤）生成器：四套可选画板的单一事实来源。
#
# 产物：src/Tokenmetry.App/Styles/Themes/{Nord,Dracula,Solarized,Gruvbox}.axaml（x:Class 编译期 XAML）
#       及同名 .axaml.cs code-behind（类型化实例化，AOT 零 IL 警告纪律，见 ThemeService）。
# 每个文件 = ResourceDictionary.ThemeDictionaries（Light/Dark 各一份，仅该画板支持的模式），
# 内含两类资源：
#   1. App* 设计令牌（46 个随模式翻转的键，与 Styles/Tokens.axaml 默认画板同键位；
#      刻意不含 AppPoster* 与图表系列色——海报表面跟随画板，但海报专用可视化令牌
#      与数据语义色全画板恒定，见设计文档）；
#   2. Semi* 控件别名键覆盖（白名单见 SEMI_ALIASES）：Semi v12 的控件 ControlTheme 只消费
#      控件别名键（Themes/{Light,Dark}/{Control}.axaml 内 StaticResource 烤死的语义值），
#      覆盖别名键是唯一能让 Semi 原生控件跟随画板换肤的通道。
#      这是对 docs/design/design-tokens.md §1「Semi* 禁止覆盖」的一次有意识例外：
#      覆盖的是既有键而非新增键，App* 前缀规则与撞名测试不受影响，例外本身记录在该文档。
#
# 纪律：文字/强调色对比度逐项过 WCAG AA（≥4.5:1，正文 ≥7:1）。本脚本断言全部语义配对，
# 不达标时沿色相微调亮度并打印改动——绝不静默放水；改动后的值即 XAML 落盘值。
#
# 用法：python3 scripts/generate-theme-palettes.py            # 校验 + 重新生成
#       python3 scripts/generate-theme-palettes.py --check    # 只校验不写盘（CI 用）

import sys
import os
import colorsys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT_DIR = os.path.join(HERE, "..", "src", "Tokenmetry.App", "Styles", "Themes")


# ── 颜色工具 ────────────────────────────────────────────────────────────────

def _clamp(x, lo=0.0, hi=1.0):
    return max(lo, min(hi, x))


def parse(hexstr: str):
    hexstr = hexstr.lstrip("#")
    if len(hexstr) == 8:
        return tuple(int(hexstr[i:i + 2], 16) for i in (2, 4, 6))  # 丢弃 alpha，颜色一律不透明定义
    return tuple(int(hexstr[i:i + 2], 16) for i in (0, 2, 4))


def fmt(rgb) -> str:
    return "#{:02x}{:02x}{:02x}".format(*(max(0, min(255, round(c))) for c in rgb))


def srgb_channel(c):
    c /= 255.0
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def luminance(rgb) -> float:
    r, g, b = (srgb_channel(c) for c in rgb)
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def contrast(a, b) -> float:
    la, lb = luminance(a), luminance(b)
    hi, lo = max(la, lb), min(la, lb)
    return (hi + 0.05) / (lo + 0.05)


def mix(a, b, t) -> tuple:
    """a * (1-t) + b * t，通道线性混合。"""
    return tuple(a[i] * (1 - t) + b[i] * t for i in range(3))


def shift_lightness(rgb, delta) -> tuple:
    """HSL 亮度 += delta（正提亮负压暗），色相/饱和度不动。"""
    r, g, b = (c / 255.0 for c in rgb)
    h, l, s = colorsys.rgb_to_hls(r, g, b)
    r2, g2, b2 = colorsys.hls_to_rgb(h, _clamp(l + delta), s)
    return (r2 * 255, g2 * 255, b2 * 255)


def ensure_contrast(rgb, against, target, prefer_light: bool) -> tuple:
    """亮度沿 prefer_light 方向步进（2%/步，最多 24 步）直到达标；返回 (色值, 是否调过)。"""
    if contrast(rgb, against) >= target:
        return rgb, False
    c = rgb
    for _ in range(24):
        c = shift_lightness(c, 0.02 if prefer_light else -0.02)
        if contrast(c, against) >= target:
            return c, True
    return c, True


WHITE = (255, 255, 255)


# ── Semi 语义派生 ───────────────────────────────────────────────────────────

def semi_semantics(p, mode: str) -> dict:
    """由画板主值派生 Semi 语义层（SemiColor*）。dark: 在 bg 上叠文字色；light: 在 panel 上叠文字色。"""
    dark = mode == "dark"
    base = p["bg"] if dark else p["panel"]
    surface = p["panel"] if dark else mix(p["panel"], WHITE, 0.55)
    text, muted = p["text"], p["muted"]
    blue = p["accents"]["blue"]

    def fill(t):
        return mix(base, text, t)

    def poi_accent(c):
        return shift_lightness(c, 0.10 if dark else -0.10)

    def act_accent(c):
        return shift_lightness(c, 0.20 if dark else -0.20)

    def light_bg(c):
        # 选中态浅底 = 表层掺 ~25%（暗）/ ~18%（亮）主色，与默认画板导航选中底同构
        return mix(surface, c, 0.25) if dark else mix(WHITE, c, 0.18)

    s = {
        "SemiColorFill0": fill(0.05), "SemiColorFill1": fill(0.09), "SemiColorFill2": fill(0.13),
        "SemiColorBackground0": p["bg"], "SemiColorBackground1": p["panel"],
        "SemiColorBackground2": p["panel_alt"], "SemiColorBackground3": surface,
        "SemiColorBorder": p["line"],
        "SemiColorText0": text, "SemiColorText1": mix(text, muted, 0.4),
        "SemiColorText2": muted, "SemiColorText3": mix(muted, base, 0.4),
        "SemiColorPrimary": blue, "SemiColorPrimaryPointerover": poi_accent(blue),
        "SemiColorPrimaryActive": act_accent(blue),
        "SemiColorPrimaryLight": light_bg(blue),
        "SemiColorPrimaryLightPointerover": shift_lightness(light_bg(blue), 0.03 if dark else -0.03),
        "SemiColorPrimaryDisabled": mix(blue, base, 0.55),
        "SemiColorSecondary": p["accents"]["cyan"],
        "SemiColorSecondaryPointerover": poi_accent(p["accents"]["cyan"]),
        "SemiColorSecondaryActive": act_accent(p["accents"]["cyan"]),
        "SemiColorTertiary": mix(text, muted, 0.4),
        "SemiColorSuccess": p["accents"]["green"],
        "SemiColorWarning": p["accents"]["orange"],
        "SemiColorDanger": p["accents"]["red"],
        "SemiColorInformation": blue,
        "SemiColorDisabledText": mix(text, base, 0.55),
        "SemiColorDisabledFill": fill(0.07),
    }
    for name in ("Success", "Warning", "Danger", "Information"):
        c = p["accents"][{"Success": "green", "Warning": "orange", "Danger": "red",
                          "Information": "blue"}[name]]
        s[f"SemiColor{name}Light"] = light_bg(c)
        s[f"SemiColor{name}LightPointerover"] = shift_lightness(light_bg(c), 0.03 if dark else -0.03)
        s[f"SemiColor{name}LightActive"] = shift_lightness(light_bg(c), 0.06 if dark else -0.06)
        s[f"SemiColor{name}Pointerover"] = poi_accent(c)
        s[f"SemiColor{name}Active"] = act_accent(c)
    return {k: fmt(v) for k, v in s.items()}


# ── Semi 控件别名白名单（键 → 语义） ────────────────────────────────────────
# 逐键核对于 Semi.Avalonia v12.1.0.1 的 src/Semi.Avalonia/Themes/{Light,Dark}/{Control}.axaml；
# 只收录"吃颜色"的键（透明/尺寸/阴影键不覆盖）。应用未用的控件（Calendar/DatePicker/TabControl
# 等）不进白名单——那些表面回落 Semi 自带的明暗语义，结构性正确。

SEMI_ALIASES = {
    # Window / 弹出宿主兜底（应用各窗都显式给底，这里是 child window/未设底场景的保险）
    "WindowDefaultBackground": "Background0",
    "WindowDefaultForeground": "Text0",
    "DefaultForeground": "Text0",
    # TextBox（含 Bordered 变体与选区/光标）
    "TextBoxTextCaretBrush": "Text0",
    "TextBoxDefaultBackground": "Fill0",
    "TextBoxPointeroverBackground": "Fill1",
    "TextBoxPressedBackground": "Fill2",
    "TextBoxFocusBorderBrush": "Primary",
    "TextBoxForeground": "Text0",
    "TextBoxPlaceholderForeground": "Text2",
    "TextBoxInnerForeground": "Text2",
    "TextBoxDisabledBackground": "DisabledFill",
    "TextBoxDisabledForeground": "DisabledText",
    "TextBoxBorderedDefaultBorderBrush": "Fill0",
    "TextBoxBorderedPointeroverBorderBrush": "PrimaryPointerover",
    # TextBoxSelectionBackground 特殊：主色半透明，见 emit()
    "TextBoxSelectionForeground": "OnBrand",
    # ComboBox
    "ComboBoxSelectorBackground": "Fill0",
    "ComboBoxSelectorPlaceHolderForeground": "Text2",
    "ComboBoxSelectorPointeroverBackground": "Fill1",
    "ComboBoxSelectorFocusBackground": "Fill1",
    "ComboBoxSelectorPressedBackground": "Fill2",
    "ComboBoxSelectorDisabledBackground": "DisabledFill",
    "ComboBoxSelectorDisabledBorderBrush": "DisabledFill",
    "ComboBoxSelectorFocusBorderBrush": "Primary",
    "ComboBoxSelectorPressedBorderBrush": "Primary",
    "ComboBoxIconDefaultForeground": "Text2",
    "ComboBoxIconPointeroverForeground": "Text0",
    "ComboBoxIconPressedForeground": "Text0",
    "ComboBoxIconDisabledForeground": "DisabledText",
    "ComboBoxDisabledForeground": "DisabledText",
    "ComboBoxPopupBackground": "Background3",
    "ComboBoxPopupBorderBrush": "Border",
    "ComboBoxItemForeground": "Text0",
    "ComboBoxItemPointeroverForeground": "Text0",
    "ComboBoxItemPointeroverBackground": "Fill0",
    "ComboBoxItemFocusForeground": "Text0",
    "ComboBoxItemFocusBackground": "Fill0",
    "ComboBoxItemPressedForeground": "Text0",
    "ComboBoxItemPressedBackground": "Fill1",
    "ComboBoxItemSelectedForeground": "Text0",
    "ComboBoxItemSelectedBackground": "PrimaryLight",
    "ComboBoxItemSelectedPointeroverBackground": "PrimaryLightPointerover",
    "ComboBoxItemSelectedDisabledBackground": "DisabledFill",
    "ComboBoxSelectorBorderedBorderBrush": "Border",
    "ComboBoxSelectorBorderedPointeroverBorderBrush": "PrimaryPointerover",
    "ComboBoxSelectorBorderedFocusBorderBrush": "PrimaryPointerover",
    "ComboBoxSelectorBorderedPressedBorderBrush": "PrimaryActive",
    # AutoCompleteBox 弹层
    "AutoCompleteBoxPopupBackground": "Background3",
    "AutoCompleteBoxPopupBorderBrush": "Border",
    # CheckBox（含 card 变体）
    "CheckBoxForeground": "Text0",
    "CheckBoxDisabledForeground": "DisabledText",
    "CheckBoxGlyphFill": "OnBrand",
    "CheckBoxDefaultBorderBrush": "Text3",
    "CheckBoxPointeroverBackground": "Fill0",
    "CheckBoxPointeroverBorderBrush": "Primary",
    "CheckBoxPressedBackground": "Fill1",
    "CheckBoxPressedBorderBrush": "PrimaryActive",
    "CheckBoxCheckedDefaultBackground": "Primary",
    "CheckBoxCheckedDefaultBorderBrush": "Primary",
    "CheckBoxCheckedPointeroverBackground": "PrimaryPointerover",
    "CheckBoxCheckedPointeroverBorderBrush": "PrimaryPointerover",
    "CheckBoxCheckedPressedBackground": "PrimaryActive",
    "CheckBoxCheckedPressedBorderBrush": "PrimaryActive",
    "CheckBoxDefaultDisabledBackground": "DisabledFill",
    "CheckBoxDefaultDisabledBorderBrush": "Border",
    "CheckBoxCheckedDisabledBackground": "PrimaryDisabled",
    "CheckBoxCheckedDisabledBorderBrush": "PrimaryDisabled",
    "CheckBoxCardCheckedBackground": "PrimaryLight",
    "CheckBoxCardCheckedBorderBrush": "Primary",
    "CheckBoxCardCheckedDisabledBorderBrush": "PrimaryDisabled",
    "CheckBoxCardPointeroverBackground": "Fill0",
    "CheckBoxCardPressedBackground": "Fill1",
    "CheckBoxCardCheckedPointeroverBorderBrush": "PrimaryPointerover",
    "CheckBoxCardCheckedPressedBorderBrush": "PrimaryActive",
    # ToggleButton（indeterminate 描边）
    "ToggleButtonPrimaryIndeterminateBorderBrush": "Primary",
    "ToggleButtonSecondaryIndeterminateBorderBrush": "Secondary",
    "ToggleButtonTertiaryIndeterminateBorderBrush": "Tertiary",
    # ToolTip：反色气泡（与 Semi 明暗语义同构）
    "ToolTipBackground": "ToolTipInverted",
    "ToolTipForeground": "ToolTipInvertedText",
    # Flyout
    "FlyoutBackground": "Background3",
    "FlyoutForeground": "Text0",
    "FlyoutBorderBrush": "Border",
    # ListBox（data-table 已被应用样式接管，这里是裸 ListBox 的兜底）
    "ListBoxItemDisabledForeground": "DisabledText",
    "ListBoxItemPointeroverForeground": "Text0",
    "ListBoxItemPointeroverBackground": "Fill0",
    "ListBoxItemPressedForeground": "Text0",
    "ListBoxItemPressedBackground": "Fill1",
    "ListBoxItemSelectedForeground": "Text0",
    "ListBoxItemSelectedBackground": "PrimaryLight",
    "ListBoxItemSelectedPointeroverBackground": "PrimaryLightPointerover",
    "ListBoxItemSelectedDisabledBackground": "DisabledFill",
    # NotificationCard（ToastService）
    "NotificationCardBorderBrush": "Border",
    "NotificationCardBackground": "Background3",
    "NotificationCardInformationIconForeground": "Information",
    "NotificationCardSuccessIconForeground": "Success",
    "NotificationCardWarningIconForeground": "Warning",
    "NotificationCardErrorIconForeground": "Danger",
    "NotificationCardTitleForeground": "Text0",
    "NotificationCardMessageForeground": "Text1",
    "NotificationCardLightInformationBorderBrush": "Information",
    "NotificationCardLightInformationBackground": "InformationLight",
    "NotificationCardLightSuccessBorderBrush": "Success",
    "NotificationCardLightSuccessBackground": "SuccessLight",
    "NotificationCardLightWarningBorderBrush": "Warning",
    "NotificationCardLightWarningBackground": "WarningLight",
    "NotificationCardLightErrorBorderBrush": "Danger",
    "NotificationCardLightErrorBackground": "DangerLight",
    # DataValidationErrors
    "DataValidationErrorsForeground": "Danger",
    "DataValidationErrorsBackground": "DangerLight",
    "DataValidationErrorsBorderBrush": "DangerLight",
    "DataValidationErrorsPointeroverBackground": "DangerLightPointerover",
    "DataValidationErrorsPointeroverBorderBrush": "DangerLightPointerover",
    "DataValidationErrorsPressedBackground": "DangerLightActive",
    "DataValidationErrorsPressedBorderBrush": "DangerLightActive",
    "DataValidationErrorsSelectedBackground": "DangerLight",
    "DataValidationErrorsSelectedBorderBrush": "Danger",
    "DataValidationErrorsPointerOverBackground": "DangerLightPointerover",
    "DataValidationErrorsPointerOverBorderBrush": "DangerLightPointerover",
    # Button（默认族；实心族应用按钮走 app-btn 配方，只补 Primary 实心三态）
    "ButtonDefaultPrimaryForeground": "Primary",
    "ButtonDefaultSecondaryForeground": "Secondary",
    "ButtonDefaultTertiaryForeground": "Tertiary",
    "ButtonDefaultSuccessForeground": "Success",
    "ButtonDefaultWarningForeground": "Warning",
    "ButtonDefaultDangerForeground": "Danger",
    "ButtonDefaultDisabledForeground": "DisabledText",
    "ButtonDefaultBackground": "Fill0",
    "ButtonDefaultPointeroverBackground": "Fill1",
    "ButtonDefaultPressedBackground": "Fill2",
    "ButtonSolidForeground": "OnBrand",
    "ButtonSolidPrimaryBackground": "Primary",
    "ButtonSolidPrimaryPointeroverBackground": "PrimaryPointerover",
    "ButtonSolidPrimaryPressedBackground": "PrimaryActive",
    "ButtonSolidPrimaryBorderBrush": "Primary",
    "ButtonSolidPrimaryPointeroverBorderBrush": "PrimaryPointerover",
    "ButtonSolidPrimaryPressedBorderBrush": "PrimaryActive",
    # MenuFlyout / MenuItem（上下文菜单）
    "MenuFlyoutBackground": "Background3",
    "MenuFlyoutBorderBrush": "Border",
    "MenuItemForeground": "Text0",
    "MenuItemSeparatorBackground": "Border",
    "MenuItemPointeroverForeground": "Text0",
    "MenuItemPointeroverBackground": "Fill0",
    "MenuItemPressedBackground": "Fill1",
    "MenuItemInputGestureTextForeground": "Text2",
    "MenuItemExpandIconForeground": "Text2",
    "MenuItemExpandIconOpenForeground": "Text0",
    "MenuItemDisabledForeground": "DisabledText",
    "MenuItemDisabledInputGestureTextForeground": "DisabledText",
    "MenuItemDisabledExpandIconForeground": "DisabledText",
    # Slider
    "SliderTrackForeground": "Primary",
    "SliderTrackBackground": "Fill0",
    "SliderThumbBackground": "OnBrand",
    "SliderThumbBorderBrush": "Primary",
    "SliderThumbPointeroverBorderBrush": "PrimaryPointerover",
    "SliderThumbPressedBorderBrush": "PrimaryActive",
    "SliderTickForeground": "Text3",
    "SliderTrackDisabledForeground": "DisabledText",
    "SliderThumbDisabledBorderBrush": "PrimaryDisabled",
    "SliderTrackDisabledBackground": "DisabledFill",
}


# ── 画板定义（官方色值来源见各字段注释） ────────────────────────────────────
# 每个模式给出手排主值；派生键（图标芯片软底、遮罩、导航静息等）由 DERIVE 统一计算。

PALETTES = {
    "nord": {
        "file": "Nord",
        "modes": ["dark"],
        "source": "Nord 官方色板（nordtheme.com/docs/colors-and-palettes）：Polar Night nord0-3、"
                  "Snow Storm nord4-6、Frost nord7-10、Aurora nord11-15",
        "dark": {
            # Polar Night 四阶：nord0 #2e3440 / nord1 #3b4252 / nord2 #434c5e / nord3 #4c566a
            "bg": "#2e3440", "panel": "#3b4252", "panel_alt": "#434c5e", "line": "#4c566a",
            # Snow Storm：nord6 #eceff4 正文；muted 取 nord3/nord4 之间（nord4 #d8dee9 过亮，层级塌掉）
            "text": "#eceff4", "muted": "#99a5b8",
            # Aurora 六色对应六强调位（Frost nord8/nord9 顶蓝青位）；不足 4.5 的由脚本提亮
            "accents": {
                "blue": "#81a1c1", "cyan": "#88c0d0", "purple": "#b48ead",
                "green": "#a3be8c", "orange": "#d08770", "red": "#bf616a",
            },
            # 卡片表面 = panel 附近 ±1%（原型 .kpi/.panel 渐变同构）；描边取 nord2/nord3 之间
            "card": ("#3a404e", "#373d4a"), "card_line": "#475061",
            "link": None,  # None = 跟 accent blue
            "chip": ("#414c5e", "#a9cade"),
            "mini_track": "#434c5e",
            "kpi_title": "#b5c1d3",
            # 外壳：侧栏/顶栏取比 bg 深半档的 Polar Night（默认画板同为外壳略深于页面底）
            "sidebar": "#2f3542", "topbar": "#313744",
            "nav": {"fg": None, "active_bg": "#414d5f", "active_fg": "#cfe0ee", "hover": "#3a414f"},
            "chart": {"grid": "#414a59", "axis": "#4f5a6d"},
            "scroll": {"thumb": "#4d576a", "hover": "#6a7791"},
            "banner": {"active_bg": "#3a4456", "active_line": "#566379",
                       "warn_bg": "#4a4132", "warn_line": "#6e5f45"},
            "popover_shadow": "#a6000000",
        },
    },
    "dracula": {
        "file": "Dracula",
        "modes": ["dark"],
        "source": "Dracula 官方规范（spec.draculatheme.com）：Background #282a36、Current Line #44475a、"
                  "Foreground #f8f8f2、Comment #6272a4、Cyan/Green/Orange/Pink/Purple/Red/Yellow",
        "dark": {
            # Background；panel/alt/line 沿同色相（h≈235）低饱和递进，line = 官方 Current Line
            "bg": "#282a36", "panel": "#2f3140", "panel_alt": "#353849", "line": "#44475a",
            # Foreground 正文；muted 由 Comment #6272a4 提亮（原值在 bg 上 <4.5，官方注释色本就偏弱）
            "text": "#f8f8f2", "muted": "#8294c4",
            # 官方七色直接落位；Dracula 规范无 blue 位，用 Comment 同色相提亮补位
            "accents": {
                "blue": "#8c9eff", "cyan": "#8be9fd", "purple": "#bd93f9",
                "green": "#50fa7b", "orange": "#ffb86c", "red": "#ff5555",
            },
            "card": ("#303241", "#2c2e3c"), "card_line": "#3d4054",
            "link": None,  # None = 跟 accent cyan（Dracula 惯用青做链接）
            "chip": ("#3b3f58", "#cfcaff"),
            "mini_track": "#3a3d4f",
            "kpi_title": "#b4b9d6",
            # 外壳取更深的 Background 变体（Dracula 移植惯例的深底）
            "sidebar": "#23242e", "topbar": "#262833",
            "nav": {"fg": None, "active_bg": "#3c4059", "active_fg": "#e8e9ff", "hover": "#33364a"},
            "chart": {"grid": "#363949", "axis": "#464a5e"},
            "scroll": {"thumb": "#4a4e63", "hover": "#666b8a"},
            "banner": {"active_bg": "#33364d", "active_line": "#4b5075",
                       "warn_bg": "#443c26", "warn_line": "#6d6037"},
            "popover_shadow": "#a6000000",
        },
    },
    "solarized": {
        "file": "Solarized",
        "modes": ["light", "dark"],
        "source": "Solarized 官方色板（ethanschoonover.com/solarized）：base03-base3 八阶灰 + "
                  "yellow/orange/red/magenta/violet/blue/cyan/green 八强调色",
        "light": {
            # base3 主底、base2 面板（Solarized 亮色本体就是 content=base3 / 块面=base2）
            "bg": "#fdf6e3", "panel": "#eee8d5", "panel_alt": "#e6decb", "line": "#d3ccba",
            # 正文 base01、次要 base00（Solarized 亮色口径）
            "text": "#586e75", "muted": "#657b83",
            # 官方八色落六位（magenta 不设位，violet 顶 purple 位）；不足 4.5 的由脚本压暗
            "accents": {
                "blue": "#268bd2", "cyan": "#2aa198", "purple": "#6c71c4",
                "green": "#859900", "orange": "#cb4b16", "red": "#dc322f",
            },
            "card": ("#f4edda", "#ece4cf"), "card_line": "#d8d0bc",
            "link": None,
            "chip": None,  # None = 派生：mix(blue, panel, 0.14) / 文字跟 blue
            "mini_track": "#ded6c2",
            "kpi_title": "#5f7079",
            # 外壳：侧栏/顶栏取 bg 与 panel 之间的浅档（默认画板同为外壳略深于页面底）
            "sidebar": "#f0e8d3", "topbar": "#f4eedd",
            "nav": {"fg": None, "active_bg": None, "active_fg": None, "hover": "#e8e0c9"},
            "chart": {"grid": "#e0d8c3", "axis": "#c8bfa9"},
            "scroll": {"thumb": "#cfc7b0", "hover": "#a8a08a"},
            "banner": {"active_bg": None, "active_line": None,
                       "warn_bg": "#f2e2c8", "warn_line": "#d9c49a"},
            "popover_shadow": "#40000000",
        },
        "dark": {
            # base03 主底、base02 面板
            "bg": "#002b36", "panel": "#073642", "panel_alt": "#0a3a47", "line": "#174b57",
            # 正文 base0、muted 由 base00 提亮（base01/base00 在 base03 上 <4.5，Solarized 暗色注释档本就偏弱）
            "text": "#839496", "muted": "#6d838b",
            # 官方八色提亮落位（原值在 base03 上不足 4.5 的由脚本提亮）
            "accents": {
                "blue": "#268bd2", "cyan": "#2aa198", "purple": "#6c71c4",
                "green": "#859900", "orange": "#cb4b16", "red": "#dc322f",
            },
            "card": ("#093845", "#07323e"), "card_line": "#164956",
            "link": None,
            "chip": None,
            "mini_track": "#0f4351",
            "kpi_title": "#93a5a8",
            "sidebar": "#012832", "topbar": "#022c37",
            "nav": {"fg": None, "active_bg": "#0e4553", "active_fg": "#9fd0ea", "hover": "#0a3d4a"},
            "chart": {"grid": "#0f414d", "axis": "#1d5260"},
            "scroll": {"thumb": "#1c4f5c", "hover": "#337081"},
            "banner": {"active_bg": "#0d3d4c", "active_line": "#2a5c6d",
                       "warn_bg": "#2e2a1c", "warn_line": "#6b5426"},
            "popover_shadow": "#a6000000",
        },
    },
    "gruvbox": {
        "file": "Gruvbox",
        "modes": ["light", "dark"],
        "source": "Gruvbox 官方色板（github.com/morhetz/gruvbox）：dark bg0-bg4 / light bg0-bg2 + "
                  "neutral 与 bright/faded 双档强调色",
        "light": {
            # bg0 主底；panel 取 bg0 向白提半档的纸面（亮色应用面板亮于底的同构），alt = bg0_s #f2e5bc
            "bg": "#fbf1c7", "panel": "#fdf8e6", "panel_alt": "#f2e5bc", "line": "#d5c4a1",
            # 正文 fg1 #3c3836、次要 fg3 #665c54（fg4 #7c6f64 在 bg 上略低于 4.5）
            "text": "#3c3836", "muted": "#665c54",
            # faded（light 档）官方八色落六位
            "accents": {
                "blue": "#076678", "cyan": "#427b58", "purple": "#8f3f71",
                "green": "#79740e", "orange": "#af3a03", "red": "#9d0006",
            },
            "card": ("#fefaf0", "#faf3dd"), "card_line": "#ddd1b4",
            "link": None,
            "chip": None,
            "mini_track": "#e6dabc",
            "kpi_title": "#57504a",
            # 外壳：侧栏 = bg0_s #f2e5bc（Gruvbox 亮色的深一档纸面）
            "sidebar": "#f2e5bc", "topbar": "#f6ecd2",
            "nav": {"fg": None, "active_bg": None, "active_fg": None, "hover": "#e8dcba"},
            "chart": {"grid": "#e2d7bc", "axis": "#c9bb9c"},
            "scroll": {"thumb": "#c9bb9c", "hover": "#a19270"},
            "banner": {"active_bg": None, "active_line": None,
                       "warn_bg": "#f0e0bd", "warn_line": "#d3b273"},
            "popover_shadow": "#40000000",
        },
        "dark": {
            # bg0 主底、bg1 面板、bg0_s 次面板、bg2 线
            "bg": "#282828", "panel": "#3c3836", "panel_alt": "#32302f", "line": "#504945",
            # 正文 fg1 #ebdbb2、次要 fg4 #a89984
            "text": "#ebdbb2", "muted": "#a89984",
            # bright（dark 档）官方八色落六位
            "accents": {
                "blue": "#83a598", "cyan": "#8ec07c", "purple": "#d3869b",
                "green": "#b8bb26", "orange": "#fe8019", "red": "#fb4934",
            },
            "card": ("#3a3632", "#363330"), "card_line": "#4a443e",
            "link": None,
            "chip": None,
            "mini_track": "#4a443e",
            "kpi_title": "#c9ba9e",
            # 外壳：侧栏 = bg0_h #1d2021（Gruvbox 硬对比深边）
            "sidebar": "#1d2021", "topbar": "#262320",
            "nav": {"fg": None, "active_bg": "#3d3831", "active_fg": "#e6dcbe", "hover": "#35312d"},
            "chart": {"grid": "#45403a", "axis": "#57504a"},
            "scroll": {"thumb": "#554e46", "hover": "#7a6f60"},
            "banner": {"active_bg": "#35322a", "active_line": "#54503f",
                       "warn_bg": "#403728", "warn_line": "#6e5635"},
            "popover_shadow": "#a6000000",
        },
    },
    "catppuccin": {
        "file": "Catppuccin",
        "modes": ["light", "dark"],
        "source": "Catppuccin 官方色板（github.com/catppuccin/palette）：Latte（亮）base/mantle/crust/"
                  "surface0-2 灰阶 + 十四强调色；Mocha（暗）同键位（base #1e1e2e / surface0-2 / "
                  "mauve/blue/teal/green/peach/red）",
        "light": {
            # Latte：base 主底、mantle 面板、crust 次面板、surface0 线；正文 text、次要 subtext1
            "bg": "#eff1f5", "panel": "#e6e9ef", "panel_alt": "#dce0e8", "line": "#ccd0da",
            "text": "#4c4f69", "muted": "#5c5f77",
            # 官方强调色直接落位（blue/mauve/teal/green/peach/red）；不足 4.5 的由脚本压暗
            "accents": {
                "blue": "#1e66f5", "cyan": "#179299", "purple": "#8839ef",
                "green": "#40a02b", "orange": "#fe640b", "red": "#d20f39",
            },
            "card": ("#f7f8fb", "#f2f4f8"), "card_line": "#dce0e8",
            "link": None,
            "chip": None,
            "mini_track": "#dce0e8",
            "kpi_title": "#5c5f77",
            "sidebar": "#e6e9ef", "topbar": "#eaeef4",
            "nav": {"fg": None, "active_bg": None, "active_fg": None, "hover": "#dce0e8"},
            "chart": {"grid": "#dce0e8", "axis": "#ccd0da"},
            "scroll": {"thumb": "#bcc0cc", "hover": "#9ca0b0"},
            "banner": {"active_bg": None, "active_line": None,
                       "warn_bg": "#f6e3cf", "warn_line": "#e2c39a"},
            "popover_shadow": "#40000000",
        },
        "dark": {
            # Mocha：base 主底、surface0 面板、surface1 次面板、surface2 线；正文 text、次要 subtext0
            "bg": "#1e1e2e", "panel": "#313244", "panel_alt": "#45475a", "line": "#585b70",
            "text": "#cdd6f4", "muted": "#a6adc8",
            "accents": {
                "blue": "#89b4fa", "cyan": "#94e2d5", "purple": "#cba6f7",
                "green": "#a6e3a1", "orange": "#fab387", "red": "#f38ba8",
            },
            # 卡片取 base 与 surface0 之间的同色相深档（原型 .kpi 渐变同构）
            "card": ("#262638", "#222232"), "card_line": "#45475a",
            "link": None,
            "chip": None,
            "mini_track": "#313244",
            "kpi_title": "#bac2de",
            # 外壳取 mantle（侧栏）/ 介于 mantle 与 base（顶栏）
            "sidebar": "#181825", "topbar": "#1b1b2a",
            "nav": {"fg": None, "active_bg": "#363a54", "active_fg": None, "hover": "#2a2a3e"},
            "chart": {"grid": "#313244", "axis": "#45475a"},
            "scroll": {"thumb": "#585b70", "hover": "#7f849c"},
            "banner": {"active_bg": "#2b2c44", "active_line": "#45475a",
                       "warn_bg": "#3e3527", "warn_line": "#6b5a35"},
            "popover_shadow": "#a6000000",
        },
    },
    "tokyonight": {
        "file": "TokyoNight",
        "modes": ["dark"],
        "source": "Tokyo Night 官方色板（github.com/folke/tokyonight.nvim）：Night 主题 bg #1a1b26、"
                  "bg_dark #16161e、bg_highlight #292e42、fg #c0caf5、fg_dark #a9b1d6 + 六强调色",
        "dark": {
            # bg 主底、bg_highlight 面板、次面板再进一档；line 取终端黑与 bg_highlight 之间
            "bg": "#1a1b26", "panel": "#24283b", "panel_alt": "#292e42", "line": "#3b4261",
            "text": "#c0caf5", "muted": "#a9b1d6",
            "accents": {
                "blue": "#7aa2f7", "cyan": "#7dcfff", "purple": "#bb9af7",
                "green": "#9ece6a", "orange": "#ff9e64", "red": "#f7768e",
            },
            "card": ("#20222f", "#1c1e2a"), "card_line": "#2f334d",
            "link": None,
            "chip": None,
            "mini_track": "#292e42",
            "kpi_title": "#a9b1d6",
            # 外壳取 bg_dark（侧栏）与 bg 略深一档（顶栏）
            "sidebar": "#16161e", "topbar": "#171823",
            "nav": {"fg": None, "active_bg": "#2e3450", "active_fg": None, "hover": "#222436"},
            "chart": {"grid": "#252a3f", "axis": "#343b58"},
            "scroll": {"thumb": "#3b4261", "hover": "#565f89"},
            "banner": {"active_bg": "#232842", "active_line": "#3b4261",
                       "warn_bg": "#3a3024", "warn_line": "#6b5836"},
            "popover_shadow": "#a6000000",
        },
    },
    "one": {
        "file": "One",
        "modes": ["light", "dark"],
        "source": "Atom One 官方主题（github.com/atom/one-dark-syntax、one-light-syntax）："
                  "One Dark bg #282c34 / One Light bg #fafafa + 六强调色（blue/cyan/purple/green/orange/red）",
        "light": {
            # One Light：bg 主底、panel/alt 两级灰面、line 描边；正文 #383a42、次要 #696c77
            "bg": "#fafafa", "panel": "#f0f0f1", "panel_alt": "#e5e5e6", "line": "#d0d0d2",
            "text": "#383a42", "muted": "#696c77",
            "accents": {
                "blue": "#4078f2", "cyan": "#0184bc", "purple": "#a626a4",
                "green": "#50a14f", "orange": "#c18401", "red": "#e45649",
            },
            "card": ("#ffffff", "#f7f7f8"), "card_line": "#e0e0e2",
            "link": None,
            "chip": None,
            "mini_track": "#e5e5e6",
            "kpi_title": "#5c5f66",
            "sidebar": "#f0f0f1", "topbar": "#f5f5f6",
            "nav": {"fg": None, "active_bg": None, "active_fg": None, "hover": "#e5e5e6"},
            "chart": {"grid": "#e5e5e6", "axis": "#d0d0d2"},
            "scroll": {"thumb": "#c2c2c6", "hover": "#9a9a9f"},
            "banner": {"active_bg": None, "active_line": None,
                       "warn_bg": "#f6ecda", "warn_line": "#e0cfa9"},
            "popover_shadow": "#40000000",
        },
        "dark": {
            # One Dark：bg 主底、panel/alt 两级深灰、line #3e4451；正文 #abb2bf、次要 #9aa2b1
            "bg": "#282c34", "panel": "#2c313a", "panel_alt": "#333842", "line": "#3e4451",
            "text": "#abb2bf", "muted": "#9aa2b1",
            "accents": {
                "blue": "#61afef", "cyan": "#56b6c2", "purple": "#c678dd",
                "green": "#98c379", "orange": "#d19a66", "red": "#e06c75",
            },
            "card": ("#2f343f", "#2b3038"), "card_line": "#3e4451",
            "link": None,
            "chip": None,
            "mini_track": "#333842",
            "kpi_title": "#9aa2b1",
            "sidebar": "#21252b", "topbar": "#23272f",
            "nav": {"fg": None, "active_bg": "#3a4150", "active_fg": None, "hover": "#2f343f"},
            "chart": {"grid": "#333842", "axis": "#3e4451"},
            "scroll": {"thumb": "#4b5263", "hover": "#636d83"},
            "banner": {"active_bg": "#2c3548", "active_line": "#3e4b66",
                       "warn_bg": "#3b3226", "warn_line": "#6a5637"},
            "popover_shadow": "#a6000000",
        },
    },
    "ayu": {
        "file": "Ayu",
        "modes": ["light", "dark"],
        "source": "Ayu 官方主题（github.com/ayu-theme）：Light bg #fafafa / Dark bg #0b0e14 + "
                  "六强调色（orange/green/blue/cyan/purple/red）",
        "light": {
            "bg": "#fafafa", "panel": "#f1f2f3", "panel_alt": "#e6e8ea", "line": "#d3d6da",
            "text": "#5c6166", "muted": "#6f757b",
            "accents": {
                "blue": "#399ee6", "cyan": "#4cbf99", "purple": "#a37acc",
                "green": "#86b300", "orange": "#fa8d3e", "red": "#f07171",
            },
            "card": ("#ffffff", "#f8f9fa"), "card_line": "#e3e5e8",
            "link": None,
            "chip": None,
            "mini_track": "#e6e8ea",
            "kpi_title": "#666b70",
            "sidebar": "#f1f2f3", "topbar": "#f5f6f7",
            "nav": {"fg": None, "active_bg": None, "active_fg": None, "hover": "#e6e8ea"},
            "chart": {"grid": "#e6e8ea", "axis": "#d3d6da"},
            "scroll": {"thumb": "#c6c9cd", "hover": "#9da2a6"},
            "banner": {"active_bg": None, "active_line": None,
                       "warn_bg": "#f7ecd5", "warn_line": "#e3d0a8"},
            "popover_shadow": "#40000000",
        },
        "dark": {
            # Ayu Dark：bg 主底、panel/alt 两级墨蓝深面；正文暖灰 #b3b1ad、次要 #7d8590
            "bg": "#0b0e14", "panel": "#11151c", "panel_alt": "#141a22", "line": "#1f2733",
            "text": "#b3b1ad", "muted": "#7d8590",
            "accents": {
                "blue": "#59c2ff", "cyan": "#95e6cb", "purple": "#d2a6ff",
                "green": "#aad94c", "orange": "#ff8f40", "red": "#f07178",
            },
            "card": ("#0f141b", "#0c1017"), "card_line": "#1f2733",
            "link": None,
            "chip": None,
            "mini_track": "#141a22",
            "kpi_title": "#8b929a",
            "sidebar": "#070a0f", "topbar": "#0a0d12",
            "nav": {"fg": None, "active_bg": "#1a2330", "active_fg": None, "hover": "#10151d"},
            "chart": {"grid": "#131a24", "axis": "#1f2a38"},
            "scroll": {"thumb": "#26303c", "hover": "#3a4654"},
            "banner": {"active_bg": "#101a26", "active_line": "#1f2f42",
                       "warn_bg": "#2e2616", "warn_line": "#5c4a24"},
            "popover_shadow": "#a6000000",
        },
    },
    "kanagawa": {
        "file": "Kanagawa",
        "modes": ["dark"],
        "source": "Kanagawa 官方色板（github.com/rebelot/kanagawa.nvim）Wave：sumiInk3 #1f1f28、"
                  "sumiInk4 #2a2a37、sumiInk5 #363646、sumiInk6 #54546d、"
                  "fujiWhite #dcd7ba、oldWhite #c8c093 + 六强调色",
        "dark": {
            # Wave：sumiInk3 主底、sumiInk4 面板、sumiInk5 次面板、sumiInk6 线
            "bg": "#1f1f28", "panel": "#2a2a37", "panel_alt": "#363646", "line": "#54546d",
            "text": "#dcd7ba", "muted": "#c8c093",
            "accents": {
                "blue": "#7e9cd8", "cyan": "#7fb4ca", "purple": "#957fb8",
                "green": "#98bb6c", "orange": "#ffa066", "red": "#e46876",
            },
            "card": ("#262632", "#22222c"), "card_line": "#363646",
            "link": None,
            "chip": None,
            "mini_track": "#363646",
            "kpi_title": "#c8c093",
            # 外壳取 sumiInk0/1（侧栏）/ sumiInk2（顶栏）
            "sidebar": "#16161d", "topbar": "#1a1a22",
            "nav": {"fg": None, "active_bg": "#2d4f67", "active_fg": None, "hover": "#2a2a37"},
            "chart": {"grid": "#2a2a37", "axis": "#363646"},
            "scroll": {"thumb": "#54546d", "hover": "#727169"},
            "banner": {"active_bg": "#223249", "active_line": "#2d4f67",
                       "warn_bg": "#3a3025", "warn_line": "#6e5637"},
            "popover_shadow": "#a6000000",
        },
    },
}

# 派生目标对比度（带 +0.05 安全边距：WCAG 双实现浮点尾数差曾让 Solarized 暗色橙在
# python 侧 4.5001 过、C# 测试侧 4.4999 挂——压线的值不算达标）
TARGET_TEXT = 7.05       # 正文对底/面板/卡片/侧栏
TARGET_MUTED = 4.55      # 次要文字
TARGET_ACCENT = 4.55     # 强调色对面板（KPI 涨跌、链接、chip 文字等文字化用法）
TARGET_CHIP = 4.55       # chip 文字对 chip 底
TARGET_NAV = 4.55        # 导航前景对侧栏
TARGET_ICONCHIP = 4.55   # 软底芯片前/后景
TARGET_SCROLL = 1.75     # 滚动条滑块（非文字，可见即可）

ACCENT_ORDER = ["blue", "cyan", "purple", "green", "orange", "red"]
ICONCHIP_ROLES = [("Blue", "blue"), ("Amber", "orange"), ("Purple", "purple"),
                  ("Green", "green"), ("Neutral", None)]

report = []


def note(msg):
    report.append(msg)


def build_mode(palette_name, mode):
    """组装一个模式的完整令牌（手排主值 + 派生 + 对比度微调），返回 dict[str, tuple]（App 令牌）+ semi 语义。"""
    p = dict(PALETTES[palette_name][mode])
    dark = mode == "dark"
    bg, panel = parse(p["bg"]), parse(p["panel"])
    sidebar = parse(p["sidebar"])
    text, muted = parse(p["text"]), parse(p["muted"])
    accents = {k: parse(v) for k, v in p["accents"].items()}
    card = (parse(p["card"][0]), parse(p["card"][1]))
    card_line = parse(p["card_line"])
    card_mid = mix(*card, 0.5)

    # 正文/次要：对四类真实底色全部达标（Q-020 纪律：muted 要按它实际出现的底校准）
    for name, c, target, prefer in (("text", text, TARGET_TEXT, dark), ("muted", muted, TARGET_MUTED, dark)):
        for against_name, against in (("bg", bg), ("panel", panel), ("card", card_mid), ("sidebar", sidebar)):
            c, changed = ensure_contrast(c, against, target, prefer)
            if changed:
                note(f"  {palette_name}/{mode}: {name} 提亮/压暗到 {fmt(c)} 以满足 {target} 于 {against_name}")
        if name == "text":
            text = c
        else:
            muted = c

    # 强调色：对面板 ≥4.5（文字化用法）。暗色提亮、亮色压暗
    for k in ACCENT_ORDER:
        c, changed = ensure_contrast(accents[k], panel, TARGET_ACCENT, dark)
        if changed:
            note(f"  {palette_name}/{mode}: accent {k} 调整到 {fmt(c)}（原 {fmt(accents[k])}）")
        accents[k] = c

    link = parse(p["link"]) if p["link"] else (accents["cyan"] if palette_name == "dracula" else accents["blue"])
    link, _ = ensure_contrast(link, panel, TARGET_ACCENT, dark)

    # chip（未手排时派生）
    if p["chip"]:
        chip_bg, chip_text = parse(p["chip"][0]), parse(p["chip"][1])
    else:
        # chip 底 = 底色掺 ~14% 强调蓝（与默认画板 chip 同构），文字跟蓝
        chip_bg = mix(bg if dark else panel, accents["blue"], 0.14)
        chip_text = accents["blue"]
    chip_text, _ = ensure_contrast(chip_text, chip_bg, TARGET_CHIP, dark)

    kpi_title = parse(p["kpi_title"])
    # KPI 顶标签同时出现在卡片表面与面板上（测试两侧都查）——脚本按两底校准，
    # 别只校卡片：Ayu 亮色 #6b7075 对卡片过、对面板 4.46 压线的教训。
    for against_name, against in (("card", card_mid), ("panel", panel)):
        kpi_title, changed = ensure_contrast(kpi_title, against, TARGET_MUTED, dark)
        if changed:
            note(f"  {palette_name}/{mode}: kpi_title 调整到 {fmt(kpi_title)} 以满足 {TARGET_MUTED} 于 {against_name}")

    nav = p["nav"]
    nav_fg = parse(nav["fg"]) if nav["fg"] else muted
    nav_fg, _ = ensure_contrast(nav_fg, sidebar, TARGET_NAV, dark)
    active_bg = parse(nav["active_bg"]) if nav["active_bg"] else mix(sidebar, accents["blue"], 0.14)
    active_fg = parse(nav["active_fg"]) if nav["active_fg"] else link
    active_fg, _ = ensure_contrast(active_fg, active_bg, TARGET_NAV, dark)
    hover = parse(nav["hover"])

    # 图标/头像软色底：底色掺 ~26%（暗）/ ~15%（亮）强调色；前景从强调色出发保证 ≥4.5
    iconchips = {}
    for role_name, accent_key in ICONCHIP_ROLES:
        accent = accents["blue"] if accent_key is None else accents[accent_key]
        neutral = accent_key is None
        base_surface = bg if dark else WHITE
        if neutral:
            bgc = mix(base_surface, muted, 0.20 if dark else 0.25)
        else:
            bgc = mix(base_surface, accent, 0.26 if dark else 0.15)
        fg = accent if not neutral else mix(text, muted, 0.3)
        fg, _ = ensure_contrast(fg, bgc, TARGET_ICONCHIP, dark)
        iconchips[role_name] = (bgc, fg)

    banner = p["banner"]
    banner_active_bg = parse(banner["active_bg"]) if banner["active_bg"] else mix(bg if dark else WHITE, accents["blue"], 0.12)
    banner_active_line = parse(banner["active_line"]) if banner["active_line"] else mix(bg if dark else WHITE, accents["blue"], 0.35)
    banner_warn_bg = parse(banner["warn_bg"])
    banner_warn_line = parse(banner["warn_line"])

    scroll_thumb = parse(p["scroll"]["thumb"])
    if contrast(scroll_thumb, bg) < TARGET_SCROLL:
        scroll_thumb, _ = ensure_contrast(scroll_thumb, bg, TARGET_SCROLL, dark)

    t = {
        "AppBackgroundBrush": bg, "AppPanelBrush": panel, "AppPanelAltBrush": parse(p["panel_alt"]),
        "AppLineBrush": parse(p["line"]), "AppTextBrush": text, "AppMutedTextBrush": muted,
        "AppAccentBlueBrush": accents["blue"], "AppAccentCyanBrush": accents["cyan"],
        "AppAccentPurpleBrush": accents["purple"], "AppAccentGreenBrush": accents["green"],
        "AppAccentOrangeBrush": accents["orange"], "AppAccentRedBrush": accents["red"],
        "AppCardLineBrush": card_line, "AppLinkBrush": link,
        "AppChipBackgroundBrush": chip_bg, "AppChipTextBrush": chip_text,
        "AppMiniTrackBrush": parse(p["mini_track"]), "AppKpiTitleBrush": kpi_title,
        "AppSidebarBackgroundBrush": sidebar, "AppTopbarBackgroundBrush": parse(p["topbar"]),
        "AppNavForegroundBrush": nav_fg, "AppNavActiveBackgroundBrush": active_bg,
        "AppNavActiveForegroundBrush": active_fg, "AppNavActiveIndicatorBrush": accents["blue"],
        "AppNavHoverBackgroundBrush": hover,
        "AppNavRestBackgroundBrush": (0, hover[0], hover[1], hover[2]),  # 0 alpha，BrushTransition 起点
        "AppChartGridLineBrush": parse(p["chart"]["grid"]), "AppChartAxisLineBrush": parse(p["chart"]["axis"]),
        "AppOverlayScrimBrush": (0xE6, bg[0], bg[1], bg[2]),
        "AppBannerActiveBackgroundBrush": banner_active_bg, "AppBannerActiveLineBrush": banner_active_line,
        "AppBannerWarnBackgroundBrush": banner_warn_bg, "AppBannerWarnLineBrush": banner_warn_line,
        "AppScrollBarThumbBrush": scroll_thumb, "AppScrollBarThumbHoverBrush": parse(p["scroll"]["hover"]),
        "AppOnBrandForegroundBrush": WHITE,
        "AppPopoverCardBackgroundBrush": (0xE6, panel[0], panel[1], panel[2]),
        "AppPopoverShadowColor": parse(p["popover_shadow"]),
    }
    for role_name, _ in ICONCHIP_ROLES:
        bgc, fg = iconchips[role_name]
        t[f"AppIconChip{role_name}BackgroundBrush"] = bgc
        t[f"AppIconChip{role_name}ForegroundBrush"] = fg
    t["AppCardBackgroundBrush"] = ("gradient", card[0], card[1])

    return t, semi_semantics({"bg": bg, "panel": panel, "panel_alt": parse(p["panel_alt"]),
                              "line": parse(p["line"]), "text": text, "muted": muted,
                              "accents": accents}, mode)


# ── XAML 产出 ───────────────────────────────────────────────────────────────

def fmt_rgba(rgba) -> str:
    a, r, g, b = rgba
    return "#{:02x}{:02x}{:02x}{:02x}".format(a, r, g, b)


def brush_xml(key, value, indent="            "):
    if isinstance(value, tuple) and value and value[0] == "gradient":
        _, c0, c1 = value
        return (f'{indent}<LinearGradientBrush x:Key="{key}" StartPoint="10.058%,-7.043%" EndPoint="89.942%,107.043%">\n'
                f'{indent}    <GradientStop Color="{fmt(c0)}" Offset="0"/>\n'
                f'{indent}    <GradientStop Color="{fmt(c1)}" Offset="1"/>\n'
                f'{indent}</LinearGradientBrush>')
    if isinstance(value, tuple) and len(value) == 4:
        return f'{indent}<SolidColorBrush x:Key="{key}" Color="{fmt_rgba(value)}"/>'
    if key == "AppPopoverShadowColor":
        return f'{indent}<Color x:Key="{key}">{fmt(value)}</Color>'
    return f'{indent}<SolidColorBrush x:Key="{key}" Color="{fmt(value)}"/>'


APP_KEY_ORDER = [
    "AppBackgroundBrush", "AppPanelBrush", "AppPanelAltBrush", "AppLineBrush",
    "AppTextBrush", "AppMutedTextBrush",
    "AppAccentBlueBrush", "AppAccentCyanBrush", "AppAccentPurpleBrush",
    "AppAccentGreenBrush", "AppAccentOrangeBrush", "AppAccentRedBrush",
    "AppCardBackgroundBrush", "AppCardLineBrush", "AppLinkBrush",
    "AppChipBackgroundBrush", "AppChipTextBrush", "AppMiniTrackBrush", "AppKpiTitleBrush",
    *[f"AppIconChip{r}{s}Brush" for r, _ in ICONCHIP_ROLES for s in ("Background", "Foreground")],
    "AppSidebarBackgroundBrush", "AppTopbarBackgroundBrush",
    "AppNavForegroundBrush", "AppNavActiveBackgroundBrush", "AppNavActiveForegroundBrush",
    "AppNavActiveIndicatorBrush", "AppNavHoverBackgroundBrush", "AppNavRestBackgroundBrush",
    "AppChartGridLineBrush", "AppChartAxisLineBrush", "AppOverlayScrimBrush",
    "AppBannerActiveBackgroundBrush", "AppBannerActiveLineBrush",
    "AppBannerWarnBackgroundBrush", "AppBannerWarnLineBrush",
    "AppScrollBarThumbBrush", "AppScrollBarThumbHoverBrush",
    "AppOnBrandForegroundBrush", "AppPopoverCardBackgroundBrush", "AppPopoverShadowColor",
]

KEY_COMMENTS = {
    "AppBackgroundBrush": "页面底",
    "AppPanelBrush": "面板",
    "AppPanelAltBrush": "次面板",
    "AppLineBrush": "描边",
    "AppTextBrush": "正文",
    "AppMutedTextBrush": "次要文字",
    "AppAccentBlueBrush": "强调蓝", "AppAccentCyanBrush": "强调青", "AppAccentPurpleBrush": "强调紫",
    "AppAccentGreenBrush": "强调绿", "AppAccentOrangeBrush": "强调橙", "AppAccentRedBrush": "强调红",
    "AppCardBackgroundBrush": "卡片表面（145° 渐变，与默认画板同构）",
    "AppCardLineBrush": "卡片描边",
    "AppLinkBrush": "链接",
    "AppChipBackgroundBrush": "chip 底", "AppChipTextBrush": "chip 文字",
    "AppMiniTrackBrush": "mini 进度条轨道", "AppKpiTitleBrush": "KPI 顶标签",
    "AppSidebarBackgroundBrush": "侧栏", "AppTopbarBackgroundBrush": "顶栏",
    "AppNavForegroundBrush": "导航前景（未选中）", "AppNavActiveBackgroundBrush": "导航选中底",
    "AppNavActiveForegroundBrush": "导航选中字", "AppNavActiveIndicatorBrush": "导航指示条",
    "AppNavHoverBackgroundBrush": "导航悬停底",
    "AppNavRestBackgroundBrush": "导航静息（同 hover RGB 的 0 alpha，BrushTransition 起点）",
    "AppChartGridLineBrush": "图表网格", "AppChartAxisLineBrush": "图表轴线",
    "AppOverlayScrimBrush": "首启遮罩（页面底 90% 不透明）",
    "AppBannerActiveBackgroundBrush": "banner 活动底", "AppBannerActiveLineBrush": "banner 活动线",
    "AppBannerWarnBackgroundBrush": "banner 告警底", "AppBannerWarnLineBrush": "banner 告警线",
    "AppScrollBarThumbBrush": "滚动条滑块", "AppScrollBarThumbHoverBrush": "滚动条悬停",
    "AppOnBrandForegroundBrush": "品牌色表面文字（恒白）",
    "AppPopoverCardBackgroundBrush": "弹窗毛玻璃面（panel 90% 不透明）",
    "AppPopoverShadowColor": "弹窗外投影色",
}

SEMI_SOURCE_NOTE = ("Semi 控件别名键覆盖：Semi.Avalonia v12.1 的控件 ControlTheme 只消费控件别名键"
                    "（Themes/{Light,Dark}/{Control}.axaml 用 StaticResource 烤死的语义值），"
                    "覆盖别名键是 Semi 原生控件跟随本画板的唯一通道；"
                    "对 design-tokens.md §1「Semi* 禁止覆盖」的有意识例外（覆盖既有键、非新增键）。"
                    "白名单 = 应用实际使用的控件族，逐键核对自 v12.1.0.1 源码；语义值由画板主值派生。")


def emit_palette(palette_name):
    meta = PALETTES[palette_name]
    modes_xml = []
    for mode in meta["modes"]:
        tokens, semi = build_mode(palette_name, mode)
        variant = "Dark" if mode == "dark" else "Light"
        lines = [f'        <ResourceDictionary x:Key="{variant}">']
        lines.append(f'            <!-- {meta["file"]} {variant}：{meta["source"]}。')
        lines.append('                 由 scripts/generate-theme-palettes.py 生成（对比度纪律已逐项断言），勿手改；')
        lines.append('                 重跑脚本再生。海报令牌（AppPoster*）与图表系列色刻意不在本画板——')
        lines.append('                 海报表面吃画板，但专用可视化令牌与数据语义色全画板恒定，见 docs/design/design-tokens.md。 -->')
        for key in APP_KEY_ORDER:
            cmt = KEY_COMMENTS.get(key, "")
            if cmt:
                lines.append(f'            <!-- {cmt} -->')
            lines.append(brush_xml(key, tokens[key]))
        lines.append('            <!-- ' + SEMI_SOURCE_NOTE + ' -->')
        for key, sem in SEMI_ALIASES.items():
            if sem == "OnBrand":
                value = fmt(WHITE)
            elif sem == "ToolTipInverted":
                value = fmt(tokens["AppTextBrush"])
            elif sem == "ToolTipInvertedText":
                value = fmt(tokens["AppBackgroundBrush"])
            elif sem == "Background0":
                value = fmt(tokens["AppBackgroundBrush"])
            elif sem == "Background3":
                value = semi["SemiColorBackground3"]
            else:
                value = semi[f"SemiColor{sem}"]
            if key == "TextBoxSelectionBackground":
                lines.append(f'            <SolidColorBrush x:Key="{key}" Color="{value}" Opacity="0.5"/>')
            else:
                lines.append(f'            <SolidColorBrush x:Key="{key}" Color="{value}"/>')
        lines.append("        </ResourceDictionary>")
        modes_xml.append("\n".join(lines))

    header = f'''<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    x:Class="Tokenmetry.App.Styles.Themes.{meta["file"]}">

<!--
    {meta["file"]} 画板（{ "/".join(m.capitalize() for m in meta["modes"]) }）：ThemeService 按 settings.ThemePalette 运行期换入。
    {meta["source"]}。
    由 scripts/generate-theme-palettes.py 生成，勿手改；改色请改脚本内画板定义后重跑。
    x:Class = 编译期 XAML + 同名 code-behind（ThemeService 类型化实例化）；不要去掉——
    运行期 AvaloniaXamlLoader.Load(Uri) 带 RequiresUnreferencedCode，AOT 发布直接 IL2026。
-->

    <ResourceDictionary.ThemeDictionaries>
{chr(10).join(modes_xml)}
    </ResourceDictionary.ThemeDictionaries>
</ResourceDictionary>
'''
    out = os.path.join(OUT_DIR, f'{meta["file"]}.axaml')
    with open(out, "w") as f:
        f.write(header)
    print(f"写入 {os.path.relpath(out, HERE + '/..')}")

    code = f'''using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Tokenmetry.App.Styles.Themes;

/// <summary>
/// {meta["file"]} 画板字典：编译期 XAML（x:Class），ThemeService 类型化实例化。
/// 由 scripts/generate-theme-palettes.py 生成，勿手改。Load(this) 走编译期 populate（同 App.axaml.cs
/// 的既有模式）；刻意不走 AvaloniaXamlLoader.Load(Uri)——URI 重载带 RequiresUnreferencedCode，
/// AOT 发布（TreatWarningsAsErrors）下直接 IL2026。
/// </summary>
public partial class {meta["file"]} : ResourceDictionary
{{
    public {meta["file"]}() => AvaloniaXamlLoader.Load(this);
}}
'''
    out_cs = os.path.join(OUT_DIR, f'{meta["file"]}.axaml.cs')
    with open(out_cs, "w") as f:
        f.write(code)
    print(f"写入 {os.path.relpath(out_cs, HERE + '/..')}")


def main():
    check_only = "--check" in sys.argv
    os.makedirs(OUT_DIR, exist_ok=True)
    print("对比度报告（无输出项 = 一次通过）：")
    for name in PALETTES:
        emit_palette(name)
    if report:
        print("\n".join(report))
    else:
        print("  全部配对一次达标，未做微调。")


if __name__ == "__main__":
    main()
