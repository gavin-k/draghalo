using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DropHalo.Core.Settings;

/// <summary>
/// settings.json: loaded once, saved on every change. A file that cannot be parsed is moved aside to
/// settings.json.bad and the defaults are used, so a bad file never stops the app from starting. A file
/// that cannot be opened still holds what the user chose (strict offline, where outputs go), so loading
/// fails instead of carrying on with defaults that would act for them and be saved over it.
/// </summary>
public sealed class SettingsStore
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan LastRetry = TimeSpan.FromMilliseconds(400);

    private readonly string _path;
    private readonly Lock _gate = new();

    /// <param name="path">Where settings.json is.</param>
    /// <param name="wait">Sits out the pause before another try at a file that would not open; sleeps when left out.</param>
    public SettingsStore(string path, Action<TimeSpan>? wait = null)
    {
        _path = path;
        var (loaded, problem) = LoadWhenFree(path, wait ?? Thread.Sleep);
        Current = Migrate(Repair(loaded));
        LoadProblem = problem;
    }

    /// <summary>Raised after a change has been saved, on the thread that made it.</summary>
    public event Action<AppSettings>? Changed;

    public AppSettings Current { get; private set; }

    /// <summary>Why the file on disk was not used, or null if it was (or did not exist yet).</summary>
    public string? LoadProblem { get; }

    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        AppSettings next;
        lock (_gate)
        {
            next = change(Current);
            JsonFile.Save(_path, next, SettingsJson.Default.AppSettings);
            Current = next;
        }

        Changed?.Invoke(next);
        return next;
    }

    /// <summary>
    /// Whatever holds the file at startup (a scanner, a backup, the instance this one replaces still saving)
    /// tends to let go within moments, so a few short waits come before giving up. A file that opens the first
    /// time costs nothing extra.
    /// </summary>
    private static (AppSettings Value, string? Problem) LoadWhenFree(string path, Action<TimeSpan> wait)
    {
        for (var pause = FirstRetry; ; pause *= 2)
        {
            try
            {
                return JsonFile.Load(path, SettingsJson.Default.AppSettings, AppSettings.Default);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && pause <= LastRetry)
            {
                wait(pause);
            }
        }
    }

    /// <summary>A hand-edited file can leave out or null whole sections; those fall back to their defaults.</summary>
    private static AppSettings Repair(AppSettings s)
    {
        var keys = s.Keys?.Bindings is null ? KeyBindings.Default : s.Keys;
        keys = keys with
        {
            TapWindowMs = Math.Clamp(keys.TapWindowMs, 50, 5000),
            LongPressMs = Math.Clamp(keys.LongPressMs, 100, 5000),
        };
        return s with
        {
            Keys = keys,
            Output = s.Output ?? new(),
            Actions = s.Actions?.Enabled is null ? FileActionSettings.Default : s.Actions,
            Privacy = s.Privacy ?? new(),
            Tools = s.Tools?.Trusted is null ? UserToolSettings.Default : s.Tools,
            Wheel = s.Wheel ?? new(),
        };
    }

    /// <summary>
    /// Version 1 had no file actions in the settings window, so its files hold that version's default,
    /// copy path and move to only. Those get today's default; a hand-edited set is kept.
    /// </summary>
    private static AppSettings Migrate(AppSettings s) => s.Version >= 2 ? s : s with
    {
        Version = AppSettings.CurrentVersion,
        Actions = s.Actions.Enabled.SetEquals([FileAction.CopyPath, FileAction.MoveTo]) ? FileActionSettings.Default : s.Actions,
    };
}

/// <summary>Small JSON documents written atomically: a temp file next to the target, then a replace.</summary>
public static class JsonFile
{
    public static (T Value, string? Problem) Load<T>(string path, JsonTypeInfo<T> type, T fallback, bool moveBadAside = true)
    {
        if (!File.Exists(path))
        {
            return (fallback, null);
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return (JsonSerializer.Deserialize(stream, type) ?? fallback, null);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            if (!moveBadAside)
            {
                throw;
            }

            var aside = path + ".bad";
            try
            {
                File.Move(path, aside, overwrite: true);
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                // Still the user's file at its own path: a caller handed the fallback would save over it.
                throw new IOException($"{Path.GetFileName(path)} could not be parsed ({ex.Message}) and could not be moved to {aside} ({moveEx.Message})", moveEx);
            }

            return (fallback, $"{Path.GetFileName(path)} could not be read ({ex.Message}); moved to {aside} and using defaults");
        }
    }

    public static void Save<T>(string path, T value, JsonTypeInfo<T> type)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            using (var stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, value, type);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Wheel.WheelLayout))]
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
internal sealed partial class SettingsJson : JsonSerializerContext;
