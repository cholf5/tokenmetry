namespace TokenPulse.App.Localization;

/// <summary>
/// 界面语言（AppSettings.Language 字符串偏好经 <see cref="Translations.ApplyPreference"/> 解析后的取值）。
/// System = 跟随操作系统 UI 语言（zh 系 → 简体中文，其余 → English）。
/// </summary>
public enum AppLanguage
{
    System = 0,
    SimplifiedChinese = 1,
    English = 2,
}
