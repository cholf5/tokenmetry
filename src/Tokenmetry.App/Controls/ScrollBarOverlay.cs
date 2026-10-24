using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Tokenmetry.App.Controls;

/// <summary>
/// 滚动条悬浮显隐行为（overlay 滚动条状态机，配 Styles/ScrollBarTheme.axaml 使用）：
/// <list type="bullet">
/// <item>显示来源 = 滚动值变化（滚轮/拖拽/程序滚动）或指针移入滚动条邻近区（命中条外扩 <see cref="NearDistance"/>）；</item>
/// <item>最后一次活动后 <see cref="IdleHideDelay"/> 无活动 → 隐藏（悬停滑块或拖拽中钉住不隐）；</item>
/// <item>显隐只翻译成 ScrollBar 的 <c>overlay-shown</c> 类，透明度/命中开关由样式驱动
/// （隐藏态 Opacity 0 + IsHitTestVisible=False，悬浮条不挡贴边内容）。</item>
/// </list>
/// 经 ControlTheme 的 attached setter 全局启用（所有 ScrollBar，含 TextBox/下拉内部），
/// headless 测试可用 <see cref="SetIdleHideDelay"/> 注入更短/更长的闲置窗。
/// </summary>
public static class ScrollBarOverlay
{
    /// <summary>启用悬浮显隐（ScrollBarTheme.axaml 的 ScrollBar ControlTheme 全局置 True）。</summary>
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollBar, bool>("IsEnabled", typeof(ScrollBarOverlay));

    /// <summary>最后一次活动后延迟多久隐藏；悬停/拖拽滑块期间不计入。</summary>
    public static readonly AttachedProperty<TimeSpan> IdleHideDelayProperty =
        AvaloniaProperty.RegisterAttached<ScrollBar, TimeSpan>(
            "IdleHideDelay", typeof(ScrollBarOverlay), TimeSpan.FromSeconds(1));

    private static readonly AttachedProperty<Behavior?> BehaviorProperty =
        AvaloniaProperty.RegisterAttached<ScrollBar, Behavior?>("Behavior", typeof(ScrollBarOverlay));

    static ScrollBarOverlay()
    {
        IsEnabledProperty.Changed.AddClassHandler<ScrollBar>(OnIsEnabledChanged);
    }

    public static bool GetIsEnabled(ScrollBar bar) => bar.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(ScrollBar bar, bool value) => bar.SetValue(IsEnabledProperty, value);

    public static TimeSpan GetIdleHideDelay(ScrollBar bar) => bar.GetValue(IdleHideDelayProperty);

    public static void SetIdleHideDelay(ScrollBar bar, TimeSpan value) => bar.SetValue(IdleHideDelayProperty, value);

    private static void OnIsEnabledChanged(ScrollBar bar, AvaloniaPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue!)
        {
            bar.SetValue(BehaviorProperty, new Behavior(bar));
        }
        else
        {
            bar.GetValue(BehaviorProperty)?.Dispose();
            bar.SetValue(BehaviorProperty, null);
        }
    }

    /// <summary>单个 ScrollBar 的显隐状态机；订阅随 <see cref="Dispose"/> 与滚动条离树全部释放。</summary>
    private sealed class Behavior : IDisposable
    {
        // 普通类而非伪类：Avalonia 12 禁止控件外部添加 ':' 前缀伪类（Classes.ThrowIfPseudoclass），
        // 行为作为外部附加方只能用普通类；样式选择器 ^.overlay-shown 机制等价
        private const string ShownClass = "overlay-shown";

        // 指针活动显示区的邻近窗：命中条本身只有 10px 宽（隐藏态还不可命中），
        // 用户把鼠标移向滚动条准备抓握时，需要在够到细条之前就能看到它——
        // 命中条矩形向四周外扩 24px 视作"移近滚动条"，再远的内容区活动不触发显示
        public const double NearDistance = 24;

        private readonly ScrollBar _bar;
        private ScrollViewer? _owner;
        private DispatcherTimer? _timer;
        private bool _dragging;
        private bool _disposed;

        public Behavior(ScrollBar bar)
        {
            _bar = bar;

            // 滚动值变化即显示（滚轮/拖拽/程序滚动同路）；宿主可随模板重建更换，经 TemplatedParent 变化重挂
            bar.PropertyChanged += OnBarPropertyChanged;
            bar.AttachedToVisualTree += OnAttachedToVisualTree;
            bar.DetachedFromVisualTree += OnDetachedFromVisualTree;
            HookOwner();

            bar.PointerEntered += OnBarPointerEntered;
            bar.AddHandler(Thumb.DragStartedEvent, OnDragStarted, RoutingStrategies.Bubble);
            bar.AddHandler(Thumb.DragCompletedEvent, OnDragCompleted, RoutingStrategies.Bubble);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer?.Stop();
            _bar.PropertyChanged -= OnBarPropertyChanged;
            _bar.AttachedToVisualTree -= OnAttachedToVisualTree;
            _bar.DetachedFromVisualTree -= OnDetachedFromVisualTree;
            _bar.PointerEntered -= OnBarPointerEntered;
            _bar.RemoveHandler(Thumb.DragStartedEvent, OnDragStarted);
            _bar.RemoveHandler(Thumb.DragCompletedEvent, OnDragCompleted);
            SetOwnerPointerMovedHooked(false);
        }

        private void OnBarPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                Show();
            }
            else if (e.Property == Visual.TemplatedParentProperty)
            {
                HookOwner();
            }
        }

        private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e) => HookOwner();

        private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            // 模板重建会遗弃旧 ScrollBar 实例：旧实例必须停止响应宿主活动，
            // 否则对已死控件 Show/ArmTimer，且对宿主的订阅成为泄漏
            _timer?.Stop();
            SetShown(false);
            SetOwnerPointerMovedHooked(false);
            _owner = null;
        }

        private void HookOwner()
        {
            var owner = _bar.TemplatedParent as ScrollViewer;
            if (ReferenceEquals(_owner, owner))
            {
                return;
            }

            SetOwnerPointerMovedHooked(false);
            _owner = owner;
            SetOwnerPointerMovedHooked(true);
        }

        private void SetOwnerPointerMovedHooked(bool hooked)
        {
            if (_owner is null)
            {
                return;
            }

            _owner.PointerMoved -= OnOwnerPointerMoved;
            if (hooked)
            {
                _owner.PointerMoved += OnOwnerPointerMoved;
            }
        }

        private void OnOwnerPointerMoved(object? sender, PointerEventArgs e)
        {
            if (IsPointerNearBar(e))
            {
                Show();
            }
        }

        /// <summary>
        /// 指针是否移入邻近区：命中条矩形经宿主坐标系外扩 <see cref="NearDistance"/> 后包含指针位置。
        /// 纯几何判定、不依赖命中测试——隐藏态 Opacity 0 + IsHitTestVisible=False 照样可"接近"。
        /// </summary>
        private bool IsPointerNearBar(PointerEventArgs e)
        {
            if (_owner is null)
            {
                return false;
            }

            var topLeft = _bar.TranslatePoint(new Point(0, 0), _owner);
            if (topLeft is null)
            {
                return false;
            }

            var nearZone = new Rect(topLeft.Value, _bar.Bounds.Size).Inflate(NearDistance);
            return nearZone.Contains(e.GetCurrentPoint(_owner).Position);
        }

        private void OnBarPointerEntered(object? sender, PointerEventArgs e) => Show();

        private void OnDragStarted(object? sender, VectorEventArgs e)
        {
            _dragging = true;
            Show();
        }

        private void OnDragCompleted(object? sender, VectorEventArgs e)
        {
            _dragging = false;
            ArmTimer();
        }

        private void Show()
        {
            if (_disposed)
            {
                return;
            }

            SetShown(true);
            ArmTimer();
        }

        private void ArmTimer()
        {
            if (_disposed)
            {
                return;
            }

            if (_timer is null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Background);
                _timer.Tick += OnTimerTick;
            }
            else
            {
                _timer.Stop();
            }

            _timer.Interval = _bar.GetValue(IdleHideDelayProperty);
            _timer.Start();
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            _timer!.Stop();

            // 钉住：正在拖拽滑块，或指针停在滑块/命中条上（用户正准备抓握）——继续轮询
            if (_dragging || _bar.IsPointerOver)
            {
                ArmTimer();
                return;
            }

            SetShown(false);
        }

        /// <summary>显示态类（伪类仅控件自内可加，外部附加方用普通类，机制等价）。</summary>
        private void SetShown(bool shown)
        {
            if (shown)
            {
                _bar.Classes.Add(ShownClass);
            }
            else
            {
                _bar.Classes.Remove(ShownClass);
            }
        }
    }
}
