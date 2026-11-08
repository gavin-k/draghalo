using DropHalo.Core.Localization;

namespace DropHalo;

/// <summary>This assembly's user-facing text, in the language the app speaks; see <see cref="Loc"/>.</summary>
internal static class L
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Catalog = new(() => Loc.Catalog(typeof(L).Assembly, Loc.Current));

    /// <summary><paramref name="chinese"/>, always a plain string literal so the catalog test can find it, in the current language.</summary>
    public static string T(string chinese, params object?[] args) =>
        Loc.Text(chinese, Loc.Current == UiLanguage.Chinese ? null : Catalog.Value, Loc.Current, args);
}
