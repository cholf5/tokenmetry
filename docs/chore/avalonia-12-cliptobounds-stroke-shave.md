# 踩坑：Avalonia 12 把 TemplatedControl 的 ClipToBounds 默认翻成 true——描边类图形被静默削边

> 2026-10-24，I-040 修正项 4（fix 2d47ec2）。现象是"矢量图标的外圈被削掉一截"，
> 根因是三件各自合理的事叠出来的，单独看哪件都不像凶手。以后凡是"模板内容有意
> 溢出自身边界"的控件在 Avalonia 12 上被静默裁切，先想起这篇。

## 一句话结论

Avalonia 12 起 `TemplatedControl` 的 `ClipToBounds` 元数据默认值从 false 翻成
true（`IconElement`/`PathIcon` 继承）。模板内容凡是溢出控件自身 bounds 的部分
（这里是指定宽描边的外半环）会被**静默裁掉**——不报错、不告警、只削 0.75px，
但圆类图形的圆弧会读成直边。

## 根因链：三件事各自合理，叠起来才削

1. **Shape.Stretch 只按几何 bounds 缩放，描边不参与 fit。**
   `Shape.CalculateSizeAndTransform`（12.1.4 源码级确认）用 `DefiningGeometry.Bounds`
   算缩放，pen 粗细不进 fit 也不随缩放——描边以路径为中心绘制，墨迹必然溢出元素
   bounds `stroke/2`（本项目模板定值 1.5 → 溢出 0.75px）。这是 Avalonia 与 WPF 的
   语义差（WPF 的 fit 会把 pen 算进 `GetRenderBounds`）。
2. **Avalonia 12 把 `TemplatedControl.ClipToBounds` 元数据默认翻成 true。**
   11.x 是 false。`IconElement`/`PathIcon` 继承 TemplatedControl，全部中招：
   上面溢出的 0.75px 描边整圈被削。
3. **裸 `Path` 是 `Shape`（Control 直系），元数据仍默认 false。**
   于是"拿裸 Path 隔离复现"得到的是**假阴性**——同样的几何、同样的槽位，裸 Path
   好好的，装进 PathIcon 就被削。隔离实验的载体必须与线上渲染控件同类型。

探针（一步实锤元数据默认值，免翻源码猜版本行为）：

```csharp
foreach (var t in new[] { typeof(Visual), typeof(Control), typeof(TemplatedControl),
                          typeof(IconElement), typeof(PathIcon) })
    Console.WriteLine($"{t.Name}: {Visual.ClipToBoundsProperty.GetMetadata(t)!.DefaultValue}");
// 12.1.4 实测：Visual=False / Control=False / TemplatedControl=True / IconElement=True / PathIcon=True
```

## 为什么只有部分图标看得出来

- **圆类几何**（clock / dollar-circle / database / house）：几何极限点以切线方式
  触碰 bounds，削切横跨切点 → 左右弧读成平弦，肉眼可辨。
- **直线边缘几何**（table / bot / cpu / folder）：边缘本来就是直线且与 bounds 重合，
  削切与线重合 → 目检完全不可辨。
- 结论：**这类 bug 的目检靠圆类图标兜底，靠像素量测定案。**

## 取证套路（本次实锤路径，可复用）

headless 渲染 + 墨迹盒量测（`tests/.../IconResourceTests.cs` 的
`MeasureInkBox` 即沉淀版）：

1. 真实主题下构造控件（走 App 资源，别手搓模板）→ `window.Show()` +
   `Dispatcher.UIThread.RunJobs()` + `AvaloniaHeadlessPlatform.ForceRenderTimerTick()`
   → `window.CaptureRenderedFrame()`（直接 `RenderTargetBitmap.Render(window)` 拿不到
   headless 帧，别用）。
2. `CopyPixels` 后逐像素找墨迹包围盒。**字节序坑**：帧可能是 BGRA 也可能 RGBA
   （同 API 不同上下文翻车过一次），红墨迹谓词写成一低一高两个序都命中
   （`ChartTestHelpers.FrameContainsColor` 是同款教训的先行沉淀）。
3. 三变体对照定性：
   - 控件默认渲染 → 被削（16px 槽 clock 墨迹盒恰 16×16、赤道整行贴边）；
   - 控件显式 `ClipToBounds=false` → 与裸 Path 一致（18×18 圆弧收尾）；
   - 同几何裸 Path → 假阴性对照组（见根因链 3）。
   "墨迹盒 == 几何盒"本身即削边指纹——描边出墨时墨迹盒必然大于几何盒。

## 修法与取舍

- **修法**：在控件的隐式 ControlTheme 里显式
  `<Setter Property="ClipToBounds" Value="False"/>`——ControlTheme setter 打得赢
  元数据默认值。本项目在生成器源头（`scripts/generate-ui-icons.py`）改，再生
  `Icons.axaml`（几何串零漂移，pin 版本重拉验证生成确定性）。
- **没走 Margin=stroke/2 把描边收回界内**：可行且对裁剪免疫，但墨迹要缩小
  1.5px（16px 槽缩 ≈9%），推翻 I-040 修正项 2 定下的"墨迹填槽"尺寸决策。若哪天
  需要"墨迹绝不出界"的场景（如贴边布局），Margin 方案是备选，代价是全局墨迹变小。
- **没让描边随槽位缩放**：1.5 定值是跨槽位描边一致性的既有决策，与本坑无关。

## 防回归

`IconResourceTests.Stroke_bleed_escapes_path_icon_bounds`：属性断言（主题应用后
`ClipToBounds` 为 false）+ 渲染断言（墨迹盒宽高 ≥ 几何盒 + 1）。将来谁删掉
ControlTheme 里那个 setter、或升级 Avalonia 后默认值再变，这条会先红。

## 举一反三（何时想起这篇）

- 升级 Avalonia 12 后，任何 TemplatedControl 的模板内容**有意溢出自身 bounds** 的
  设计都会被静默削：badge 圆点、负 margin、阴影、旋转出框的菊花、越界下划线……
- "图形在极限点被削平/读成直边"而几何本身没画错 → 先查渲染控件是不是
  TemplatedControl 系 + ClipToBounds 链路，再怀疑几何转换。
- 隔离复现必须用与线上同类型的控件——载体差一个基类，ClipToBounds 默认值就差一个档。
