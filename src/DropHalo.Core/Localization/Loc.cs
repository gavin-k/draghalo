using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DropHalo.Core.Localization;

/// <summary>The language DropHalo speaks to the user in.</summary>
public enum UiLanguage { Chinese, English, Korean, Japanese, German, French, Spanish }

/// <summary>
/// User-facing text is written in Chinese where it is used, through the assembly's own <c>L.T</c>, and the Chinese is
/// also the key into the translated catalogs the assembly embeds. <see cref="Current"/> stays Chinese
/// until the app sets it, once, at startup; the libraries and their tests speak Chinese unless told otherwise.
/// </summary>
public static partial class Loc
{
    public const string CatalogName = "Strings.en.json";

    public static UiLanguage Current { get; set; } = UiLanguage.Chinese;

    /// <summary>Supported display languages, with English for other Windows languages.</summary>
    public static UiLanguage For(CultureInfo culture) => culture.TwoLetterISOLanguageName switch
    {
        "zh" => UiLanguage.Chinese,
        "ko" => UiLanguage.Korean,
        "ja" => UiLanguage.Japanese,
        "de" => UiLanguage.German,
        "fr" => UiLanguage.French,
        "es" => UiLanguage.Spanish,
        _ => UiLanguage.English,
    };

    public static string Tag(UiLanguage language) => language switch
    {
        UiLanguage.Chinese => "zh-CN",
        UiLanguage.Korean => "ko-KR",
        UiLanguage.Japanese => "ja-JP",
        UiLanguage.German => "de-DE",
        UiLanguage.French => "fr-FR",
        UiLanguage.Spanish => "es-ES",
        _ => "en-US",
    };

    /// <summary>The catalog for <paramref name="language"/>; empty when the assembly has none.</summary>
    public static IReadOnlyDictionary<string, string> Catalog(Assembly assembly, UiLanguage language = UiLanguage.English)
    {
        using var stream = assembly.GetManifestResourceStream($"Strings.{Tag(language).Split('-')[0]}.json");
        if (stream is null)
        {
            return new Dictionary<string, string>();
        }

        using var json = JsonDocument.Parse(stream);
        return json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
    }

    /// <summary>
    /// <paramref name="chinese"/>, or its translation in <paramref name="catalog"/> when there is one, with
    /// <paramref name="args"/> put in as <see cref="string.Format(IFormatProvider, string, object[])"/> does. Translations
    /// can say <c>{0|file|files}</c> for singular and plural forms, chosen using the language's count rule.
    /// </summary>
    public static string Text(string chinese, IReadOnlyDictionary<string, string>? catalog, params object?[] args) =>
        Text(chinese, catalog, UiLanguage.English, args);

    public static string Text(string chinese, IReadOnlyDictionary<string, string>? catalog, UiLanguage language, params object?[] args)
    {
        var template = chinese;
        if (catalog is not null && catalog.TryGetValue(chinese, out var translated))
        {
            template = Plural().Replace(translated, m =>
            {
                var value = args[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)];
                var n = value is null ? double.NaN : Math.Abs(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                return (language == UiLanguage.French ? n < 2 : n == 1) ? m.Groups[2].Value : m.Groups[3].Value;
            });
        }

        return args.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, args);
    }

    [GeneratedRegex(@"\{(\d+)\|([^|{}]*)\|([^|{}]*)\}")]
    private static partial Regex Plural();
}
