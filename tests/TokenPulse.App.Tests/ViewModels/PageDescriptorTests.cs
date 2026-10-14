using System.Collections.Generic;
using System.ComponentModel;
using TokenPulse.App.Localization;
using TokenPulse.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace TokenPulse.App.Tests.ViewModels;

public class PageDescriptorTests : IDisposable
{
    // 进程级单例：保存并恢复语言基线，避免污染其他用例（同 SettingsViewModelTests 语言用例约定）。
    private readonly AppLanguage _originalLanguage = Translations.Instance.CurrentLanguage;

    public void Dispose() => Translations.Instance.SetLanguage(_originalLanguage);

    [Fact]
    public void LanguageSwitch_RaisesLabelTitleSubtitleOnDescriptorItself()
    {
        // {Binding Page.Title} 是多级绑定路径：中间段（Page）通知时值仍为同一描述符实例，
        // Avalonia 编译绑定不重读叶子——叶子对象必须自行发通知，否则侧栏标签 / 页头标题
        // 在语言切换后保持旧语言（真机实测）。描述符为 ShellPages.All 进程级单例。
        var descriptor = ShellPages.For(PageId.Settings);
        var raised = new List<string?>();
        PropertyChangedEventHandler handler = (_, e) => raised.Add(e.PropertyName);
        descriptor.PropertyChanged += handler;
        try
        {
            Translations.Instance.SetLanguage(AppLanguage.English);

            descriptor.Label.Should().Be("Data & settings");
            descriptor.Title.Should().Be("Data & settings");
            raised.Should().Contain(nameof(PageDescriptor.Label));
            raised.Should().Contain(nameof(PageDescriptor.Title));
            raised.Should().Contain(nameof(PageDescriptor.Subtitle));
            raised.Should().NotContain(nameof(PageDescriptor.Icon)); // 图标字形与语言无关
        }
        finally
        {
            descriptor.PropertyChanged -= handler;
        }
    }

    [Fact]
    public void ShellPages_AllEightPages_ResolveDistinctLabelsPerLanguage()
    {
        Translations.Instance.SetLanguage(AppLanguage.English);

        ShellPages.All.Select(p => p.Label).Should().OnlyHaveUniqueItems();

        Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);

        ShellPages.All.Select(p => p.Label).Should().OnlyHaveUniqueItems();
    }
}
