using System.Globalization;

namespace DropHalo.Core.Privacy;

/// <summary>Which daily logs, named drophalo-yyyyMMdd.log, are past keeping: they hold the paths of converted files.</summary>
public static class LogRetention
{
    public const int KeepDays = 7;

    private const string Prefix = "drophalo-";

    /// <summary>The logs among <paramref name="files"/> older than <see cref="KeepDays"/> days, today counted as one; other files are left alone.</summary>
    public static IEnumerable<string> Expired(IEnumerable<string> files, DateOnly today) =>
        files.Where(f => Day(f) is { } day && day <= today.AddDays(-KeepDays));

    private static DateOnly? Day(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            && DateOnly.TryParseExact(name[Prefix.Length..^".log".Length], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;
    }
}
