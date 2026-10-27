using System.Text.Json;
using System.Text.Json.Serialization;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.Services;

/// <summary>
/// settings.json 的 JSON 源生成上下文（Native AOT 适配，P-004）：反射式序列化在 AOT 下
/// 不可用（IL3050），读写统一走 JsonSerializerContext。[JsonSerializable] 闭图覆盖
/// AppSettings 全部可达类型（List&lt;T&gt; / Dictionary / DateTime? 与嵌套设置模型）。
/// </summary>
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
    /// <summary>与原反射口径一致：WriteIndented 保持 settings.json 人工可读、diff 友好。</summary>
    public static AppSettingsJsonContext Indented { get; } = new(new JsonSerializerOptions
    {
        WriteIndented = true,
    });
}
