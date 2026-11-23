namespace Tokenmetry.App.Tests.Services;

using Tokenmetry.App.Services;
using Tokenmetry.Core.Models;

/// <summary>
/// SettingsSyncKeys.TryWrite 的远端输入防御（审查 #1）：预设清单是不可信复合值，必须按与
/// TimeRangePresetService.TryAdd 相同的封闭词表逐条过滤——null 元素/越界天数一旦落盘，
/// 下次启动装载会抛 NRE 或放大物化窗口（永久启动失败）。
/// </summary>
public sealed class SettingsSyncKeysTests
{
    private const string ValidRolling =
        """{"id":"preset:a1","kind":"RollingDays","offset":0,"days":14}""";

    private const string ValidMonth =
        """{"id":"preset:b2","kind":"Month","offset":-1,"days":7}""";

    [Fact]
    public void CustomTimePresets_ValidEntries_AreWritten()
    {
        var settings = new AppSettings();

        SettingsSyncKeys.TryWrite(settings, SettingsSyncKeys.CustomTimePresets, $"[{ValidRolling},{ValidMonth}]")
            .Should().BeTrue();

        settings.CustomTimePresets.Should().HaveCount(2);
        settings.CustomTimePresets[0].Id.Should().Be("preset:a1");
        settings.CustomTimePresets[0].Days.Should().Be(14);
        settings.CustomTimePresets[1].Kind.Should().Be(nameof(RelativeRangeKind.Month));
    }

    [Fact]
    public void CustomTimePresets_NullEntry_IsDropped()
    {
        var settings = new AppSettings();

        SettingsSyncKeys.TryWrite(settings, SettingsSyncKeys.CustomTimePresets, $"[null,{ValidRolling}]")
            .Should().BeTrue();

        settings.CustomTimePresets.Should().ContainSingle().Which.Id.Should().Be("preset:a1");
    }

    [Fact]
    public void CustomTimePresets_OnlyNullEntries_IsRejected()
    {
        var settings = new AppSettings();
        var before = settings.CustomTimePresets;

        SettingsSyncKeys.TryWrite(settings, SettingsSyncKeys.CustomTimePresets, "[null]")
            .Should().BeFalse("一条都站不住时不得拿空清单覆盖本机");

        settings.CustomTimePresets.Should().BeSameAs(before);
    }

    [Fact]
    public void CustomTimePresets_OutOfDomainDays_IsRejected()
    {
        var settings = new AppSettings();
        var before = settings.CustomTimePresets;

        var value = """[{"id":"preset:big","kind":"RollingDays","offset":0,"days":100000}]""";

        SettingsSyncKeys.TryWrite(settings, SettingsSyncKeys.CustomTimePresets, value)
            .Should().BeFalse("越界天数会放大物化窗口");

        settings.CustomTimePresets.Should().BeSameAs(before);
    }

    [Fact]
    public void CustomTimePresets_UnknownKind_IsDropped()
    {
        var settings = new AppSettings();

        var value = $$"""[{"id":"preset:x","kind":"Fortnight","offset":0,"days":14},{{ValidMonth}}]""";

        SettingsSyncKeys.TryWrite(settings, SettingsSyncKeys.CustomTimePresets, value)
            .Should().BeTrue();

        settings.CustomTimePresets.Should().ContainSingle().Which.Id.Should().Be("preset:b2");
    }

    [Fact]
    public void CustomTimePresets_InvalidId_IsDropped()
    {
        var settings = new AppSettings();

        var value = $$"""[{"id":"not-a-preset","kind":"Week","offset":0,"days":7},{{ValidMonth}}]""";

        SettingsSyncKeys.TryWrite(settings, SettingsSyncKeys.CustomTimePresets, value)
            .Should().BeTrue();

        settings.CustomTimePresets.Should().ContainSingle().Which.Id.Should().Be("preset:b2");
    }

    [Fact]
    public void CustomTimePresets_Duplicates_Collapse()
    {
        var settings = new AppSettings();

        SettingsSyncKeys.TryWrite(settings, SettingsSyncKeys.CustomTimePresets, $"[{ValidMonth},{ValidMonth}]")
            .Should().BeTrue();

        settings.CustomTimePresets.Should().ContainSingle("与添加预设共用去重键");
    }

    [Fact]
    public void CustomTimePresets_EmptyArray_ClearsLocal()
    {
        var settings = new AppSettings();
        settings.CustomTimePresets.Add(new RelativeRangePresetSetting
        {
            Id = "preset:local",
            Kind = nameof(RelativeRangeKind.Week),
            Offset = 0,
            Days = 7,
        });

        SettingsSyncKeys.TryWrite(settings, SettingsSyncKeys.CustomTimePresets, "[]").Should().BeTrue();

        settings.CustomTimePresets.Should().BeEmpty("空清单是合法的远端状态，不是坏编码");
    }
}
