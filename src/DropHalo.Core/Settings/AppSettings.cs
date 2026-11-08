using System.Globalization;
using DropHalo.Core.Conversion;
using DropHalo.Core.Jobs;
using DropHalo.Core.Localization;

namespace DropHalo.Core.Settings;

public enum OutputLocation { NextToSource, Folder }

/// <summary>The language the user picked; <see cref="System"/> follows the Windows display language.</summary>
public enum LanguageSetting { System, Chinese, English, Korean, Japanese, German, French, Spanish }

/// <summary>Where results are written and what they are called.</summary>
public sealed record OutputSettings(
    OutputLocation Location = OutputLocation.NextToSource,
    string Folder = "",
    string Naming = NamingTemplate.Default,
    ConflictPolicy Conflict = ConflictPolicy.AutoNumber)
{
    /// <summary>The <see cref="ConversionRequest.OutputDirectory"/> for these settings; empty means next to each source.</summary>
    public string Directory => Location == OutputLocation.Folder ? Folder : "";
}

/// <param name="StrictOffline">Refuse every network request, including the update check and optional module downloads.</param>
public sealed record PrivacySettings(bool StrictOffline = false, bool CheckForUpdates = true);

public enum UpdateSource { Cnb, GitHub }

/// <summary>The wheel's background strength; labels, icons and hover highlights keep their contrast.</summary>
public sealed record WheelSettings
{
    private int _backgroundOpacity;

    public WheelSettings(int backgroundOpacity = 100) => BackgroundOpacity = backgroundOpacity;

    /// <summary>0–100 percent of the default background, clamped for hand-edited settings.</summary>
    public int BackgroundOpacity
    {
        get => _backgroundOpacity;
        init => _backgroundOpacity = Math.Clamp(value, 0, 100);
    }
}

/// <summary>Everything the user can change in Settings. Immutable; <see cref="SettingsStore"/> swaps whole snapshots.</summary>
public sealed record AppSettings
{
    /// <summary>2: the Actions wheel's items got a new default, see <see cref="SettingsStore"/>.</summary>
    public const int CurrentVersion = 2;

    public int Version { get; init; } = CurrentVersion;

    public KeyBindings Keys { get; init; } = KeyBindings.Default;

    public OutputSettings Output { get; init; } = new();

    public FileActionSettings Actions { get; init; } = FileActionSettings.Default;

    public PrivacySettings Privacy { get; init; } = new();

    public UpdateSource UpdateSource { get; init; } = UpdateSource.Cnb;

    public UserToolSettings Tools { get; init; } = UserToolSettings.Default;

    public WheelSettings Wheel { get; init; } = new();

    /// <summary>The first-run guide has been shown (finished or skipped).</summary>
    public bool OnboardingDone { get; init; }

    /// <summary>Takes effect at the next start: the windows are built once, in the language of their start.</summary>
    public LanguageSetting Language { get; init; }

    public static AppSettings Default { get; } = new();

    /// <summary>The language to speak when Windows shows <paramref name="display"/>.</summary>
    public UiLanguage LanguageFor(CultureInfo display) => Language switch
    {
        LanguageSetting.Chinese => UiLanguage.Chinese,
        LanguageSetting.English => UiLanguage.English,
        LanguageSetting.Korean => UiLanguage.Korean,
        LanguageSetting.Japanese => UiLanguage.Japanese,
        LanguageSetting.German => UiLanguage.German,
        LanguageSetting.French => UiLanguage.French,
        LanguageSetting.Spanish => UiLanguage.Spanish,
        _ => Loc.For(display),
    };

    /// <summary>A conversion of <paramref name="sourcePaths"/> to <paramref name="targetFormatId"/> under these settings.</summary>
    public ConversionRequest Request(IReadOnlyList<string> sourcePaths, string targetFormatId) =>
        new(sourcePaths, targetFormatId, Output.Directory) { Naming = Output.Naming, Conflict = Output.Conflict };
}
