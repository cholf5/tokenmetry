namespace Tokenmetry.App.Localization;

/// <summary>
/// 界面语言（AppSettings.Language 字符串偏好经 <see cref="Translations.ApplyPreference"/> 解析后的取值）。
/// System = 跟随操作系统 UI 语言：zh 系按书写系统分简繁（zh-TW/zh-HK/zh-Hant* → 繁体，其余 zh → 简体），
/// ja/ko/pt/ru/es/de/fr → 对应语言（pt 系统一落 pt-BR，唯一葡语变体），其余 → English（开发者通用回退）。
/// </summary>
public enum AppLanguage
{
    System = 0,
    SimplifiedChinese = 1,
    English = 2,
    TraditionalChinese = 3,
    Japanese = 4,
    Korean = 5,
    BrazilianPortuguese = 6,
    Russian = 7,
    Spanish = 8,
    German = 9,
    French = 10,
}
