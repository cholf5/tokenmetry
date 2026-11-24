namespace Tokenmetry.App.ViewModels;

/// <summary>
/// 项目行右键菜单的作用对象（项目统计页排行行 / 总览项目排行行共用形状）：
/// 原始路径 + 是否可交给系统打开。<see cref="RawPath"/> 在非项目维度行为 null。
/// </summary>
public interface IProjectPathRow
{
    /// <summary>项目原始路径（打开动作的目标；非项目维度行为 null）。</summary>
    string? RawPath { get; }

    /// <summary>
    /// 本行是否可交给系统打开（菜单门禁）：键必须是当前平台的绝对路径。
    /// 编码目录名 / 占位串为 false；目录是否存在在点击时再确认（加载期不做 I/O）。
    /// </summary>
    bool CanOpen { get; }
}
