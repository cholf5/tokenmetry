using System.Text;
using Tokenmetry.Infrastructure.Plugins;

namespace Tokenmetry.Plugins.Tests.Runtime;

/// <summary>
/// 插件包结构校验：合法包通过；路径穿越/绝对路径/符号链接、契约程序集混入、清单与图标缺失、
/// 超限、显示名冲突都必须被拒。校验器只读 zip 条目与 plugin.json，不加载插件程序集。
/// </summary>
public class PluginPackageValidatorTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Valid_package_passes()
    {
        using var package = TestPackage.CreateValid();

        var result = PluginPackageValidator.Validate(package.Path);

        Assert.Empty(result.Errors);
        Assert.NotNull(result.Manifest);
        Assert.Equal("com.acme.foo", result.Manifest!.Id);
    }

    [Fact]
    public void Missing_package_is_reported()
    {
        var missing = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "tokenmetry-missing-" + Guid.NewGuid().ToString("N") + ".tkmplugin");

        var result = PluginPackageValidator.Validate(missing);

        Assert.NotEmpty(result.Errors);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public void Non_zip_file_is_reported()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "tokenmetry-not-zip-" + Guid.NewGuid().ToString("N") + ".tkmplugin");
        File.WriteAllText(path, "this is not a zip archive");
        try
        {
            var result = PluginPackageValidator.Validate(path);

            Assert.Contains(result.Errors, e => e.Contains("zip", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Package_with_path_traversal_is_rejected()
    {
        using var package = TestPackage.CreateRaw(
            ("plugin.json", Utf8(ValidManifest.Json()), 0),
            ("Foo.Plugin.dll", Utf8("x"), 0),
            ("icon.png", ValidManifest.PngBytes, 0),
            ("../evil.dll", Utf8("x"), 0));

        var result = PluginPackageValidator.Validate(package.Path);

        Assert.Contains(result.Errors, e => e.Contains("path", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\evil.dll")]
    public void Package_with_absolute_path_is_rejected(string absoluteEntry)
    {
        using var package = TestPackage.CreateRaw(
            ("plugin.json", Utf8(ValidManifest.Json()), 0),
            ("Foo.Plugin.dll", Utf8("x"), 0),
            ("icon.png", ValidManifest.PngBytes, 0),
            (absoluteEntry, Utf8("x"), 0));

        var result = PluginPackageValidator.Validate(package.Path);

        Assert.Contains(result.Errors, e => e.Contains("path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Package_with_symlink_entry_is_rejected()
    {
        using var package = TestPackage.CreateRaw(
            ("plugin.json", Utf8(ValidManifest.Json()), 0),
            ("Foo.Plugin.dll", Utf8("x"), 0),
            ("icon.png", ValidManifest.PngBytes, 0),
            ("link.dll", Utf8("/target"), TestPackage.SymlinkAttributes));

        var result = PluginPackageValidator.Validate(package.Path);

        Assert.Contains(result.Errors, e => e.Contains("symbolic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Package_with_contract_assembly_is_rejected()
    {
        using var package = TestPackage.CreateRaw(
            ("plugin.json", Utf8(ValidManifest.Json()), 0),
            ("Foo.Plugin.dll", Utf8("x"), 0),
            ("icon.png", ValidManifest.PngBytes, 0),
            ("Tokenmetry.Plugin.Abstractions.dll", Utf8("x"), 0));

        var result = PluginPackageValidator.Validate(package.Path);

        Assert.Contains(
            result.Errors,
            e => e.Contains("Tokenmetry.Plugin.Abstractions", StringComparison.Ordinal));
    }

    [Fact]
    public void Package_with_colliding_display_name_is_rejected()
    {
        using var package = TestPackage.CreateValid(displayName: "Claude Code");

        var result = PluginPackageValidator.Validate(package.Path, reservedAgentNames: ["Claude Code"]);

        Assert.Contains(result.Errors, e => e.Contains("displayName", StringComparison.Ordinal));
    }

    [Fact]
    public void Non_colliding_display_name_passes()
    {
        using var package = TestPackage.CreateValid(displayName: "Foo");

        var result = PluginPackageValidator.Validate(package.Path, reservedAgentNames: ["Claude Code"]);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Missing_plugin_json_is_rejected()
    {
        using var package = TestPackage.Create(
            ("Foo.Plugin.dll", "x"),
            ("icon.png", "x"));

        var result = PluginPackageValidator.Validate(package.Path);

        Assert.Contains(result.Errors, e => e.Contains("plugin.json", StringComparison.Ordinal));
        Assert.Null(result.Manifest);
    }

    [Fact]
    public void Nested_plugin_json_is_rejected()
    {
        using var package = TestPackage.CreateRaw(
            ("nested/plugin.json", Utf8(ValidManifest.Json()), 0),
            ("Foo.Plugin.dll", Utf8("x"), 0),
            ("icon.png", ValidManifest.PngBytes, 0));

        var result = PluginPackageValidator.Validate(package.Path);

        Assert.Contains(result.Errors, e => e.Contains("plugin.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_manifest_is_rejected()
    {
        using var package = TestPackage.CreateRaw(
            ("plugin.json", Utf8(ValidManifest.Json(displayName: "")), 0),
            ("Foo.Plugin.dll", Utf8("x"), 0),
            ("icon.png", ValidManifest.PngBytes, 0));

        var result = PluginPackageValidator.Validate(package.Path);

        Assert.Contains(result.Errors, e => e.Contains("displayName", StringComparison.Ordinal));
    }

    [Fact]
    public void Unsafe_manifest_id_is_rejected()
    {
        using var package = TestPackage.CreateRaw(
            ("plugin.json", Utf8(ValidManifest.Json(id: "../escape")), 0),
            ("Foo.Plugin.dll", Utf8("x"), 0),
            ("icon.png", ValidManifest.PngBytes, 0));

        var result = PluginPackageValidator.Validate(package.Path);

        Assert.Contains(result.Errors, e => e.Contains("id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unsafe_manifest_version_is_rejected()
    {
        using var package = TestPackage.CreateRaw(
            ("plugin.json", Utf8(ValidManifest.Json(version: "../1.0.0")), 0),
            ("Foo.Plugin.dll", Utf8("x"), 0),
            ("icon.png", ValidManifest.PngBytes, 0));

        var result = PluginPackageValidator.Validate(package.Path);

        Assert.Contains(result.Errors, e => e.Contains("version", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_entry_assembly_is_rejected()
    {
        using var package = TestPackage.CreateRaw(
            ("plugin.json", Utf8(ValidManifest.Json()), 0),
            ("icon.png", ValidManifest.PngBytes, 0));

        var result = PluginPackageValidator.Validate(package.Path);

        Assert.Contains(result.Errors, e => e.Contains("entry assembly", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_icon_is_rejected()
    {
        using var package = TestPackage.CreateRaw(
            ("plugin.json", Utf8(ValidManifest.Json()), 0),
            ("Foo.Plugin.dll", Utf8("x"), 0));

        var result = PluginPackageValidator.Validate(package.Path);

        Assert.Contains(result.Errors, e => e.Contains("icon", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Icon_without_png_magic_is_rejected()
    {
        using var package = TestPackage.CreateRaw(
            ("plugin.json", Utf8(ValidManifest.Json()), 0),
            ("Foo.Plugin.dll", Utf8("x"), 0),
            ("icon.png", Utf8("not a png"), 0));

        var result = PluginPackageValidator.Validate(package.Path);

        Assert.Contains(result.Errors, e => e.Contains("PNG", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Too_many_files_is_rejected()
    {
        var entries = new List<(string Name, byte[] Bytes, int Attributes)>
        {
            ("plugin.json", Utf8(ValidManifest.Json()), 0),
            ("Foo.Plugin.dll", Utf8("x"), 0),
            ("icon.png", ValidManifest.PngBytes, 0),
        };
        for (var i = 0; i < 2000; i++)
        {
            entries.Add(($"file-{i}.txt", Utf8("x"), 0));
        }

        using var package = TestPackage.CreateRaw(entries.ToArray());

        var result = PluginPackageValidator.Validate(package.Path);

        Assert.Contains(result.Errors, e => e.Contains("files", StringComparison.OrdinalIgnoreCase));
    }
}
