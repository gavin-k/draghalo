using System.Globalization;
using DropHalo.Core.Localization;
using FluentAssertions;

namespace DropHalo.Core.Tests.Localization;

public sealed class LocTests
{
    private static readonly Dictionary<string, string> English = new()
    {
        ["{0} 个文件"] = "{0} {0|file|files}",
        ["{0} · {1} 项"] = "{0} · {1} {1|item|items}",
    };

    [Fact]
    public void WithoutACatalogTheChineseIsUsed() =>
        Loc.Text("{0} 个文件", null, 3).Should().Be("3 个文件");

    [Fact]
    public void TextTheCatalogLacksStaysChinese() =>
        Loc.Text("已复制路径", English).Should().Be("已复制路径");

    [Theory]
    [InlineData(1, "1 file")]
    [InlineData(2, "2 files")]
    [InlineData(0, "0 files")]
    public void EnglishPicksTheWordForTheCount(int count, string expected) =>
        Loc.Text("{0} 个文件", English, count).Should().Be(expected);

    [Fact]
    public void ThePluralFollowsItsOwnArgument() =>
        Loc.Text("{0} · {1} 项", English, "Tools", 1).Should().Be("Tools · 1 item");

    [Theory]
    [InlineData("zh-CN", UiLanguage.Chinese)]
    [InlineData("zh-TW", UiLanguage.Chinese)]
    [InlineData("zh-Hans", UiLanguage.Chinese)]
    [InlineData("en-US", UiLanguage.English)]
    [InlineData("ko-KR", UiLanguage.Korean)]
    [InlineData("ja-JP", UiLanguage.Japanese)]
    [InlineData("de-DE", UiLanguage.German)]
    [InlineData("de-AT", UiLanguage.German)]
    [InlineData("fr-CA", UiLanguage.French)]
    [InlineData("es-MX", UiLanguage.Spanish)]
    [InlineData("it-IT", UiLanguage.English)]
    public void Supported_Windows_languages_are_matched_and_others_use_English(string culture, UiLanguage expected) =>
        Loc.For(CultureInfo.GetCultureInfo(culture)).Should().Be(expected);

    [Theory]
    [InlineData(UiLanguage.Chinese, "zh-CN")]
    [InlineData(UiLanguage.English, "en-US")]
    [InlineData(UiLanguage.Korean, "ko-KR")]
    [InlineData(UiLanguage.Japanese, "ja-JP")]
    [InlineData(UiLanguage.German, "de-DE")]
    [InlineData(UiLanguage.French, "fr-FR")]
    [InlineData(UiLanguage.Spanish, "es-ES")]
    public void Windows_controls_use_the_selected_language_tag(UiLanguage language, string tag) =>
        Loc.Tag(language).Should().Be(tag);

    [Theory]
    [InlineData(0, "0 fichier")]
    [InlineData(1, "1 fichier")]
    [InlineData(2, "2 fichiers")]
    public void French_uses_its_own_plural_rule(int count, string expected) =>
        Loc.Text("{0} 个文件", new Dictionary<string, string> { ["{0} 个文件"] = "{0} {0|fichier|fichiers}" }, UiLanguage.French, count).Should().Be(expected);

    [Theory]
    [InlineData(UiLanguage.Korean, "파일 2개")]
    [InlineData(UiLanguage.Japanese, "2 ファイル")]
    [InlineData(UiLanguage.German, "2 Dateien")]
    [InlineData(UiLanguage.French, "2 fichiers")]
    [InlineData(UiLanguage.Spanish, "2 archivos")]
    public void New_languages_render_their_embedded_translations(UiLanguage language, string expected) =>
        Loc.Text("{0} 个文件", Loc.Catalog(typeof(Loc).Assembly, language), language, 2).Should().Be(expected);

    [Fact]
    public void TheLibrariesSpeakChineseUntilTheAppSaysOtherwise() =>
        Loc.Current.Should().Be(UiLanguage.Chinese);

    [Fact]
    public void AnAssemblyWithoutACatalogHasAnEmptyOne() =>
        Loc.Catalog(typeof(LocTests).Assembly).Should().BeEmpty();
}
