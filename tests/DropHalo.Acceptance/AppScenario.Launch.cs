using System.Diagnostics;

namespace DropHalo.Acceptance;

internal static partial class AppScenario
{
    /// <summary>
    /// Starts the app under test on its own profile, from its own folder so sidecars resolve, with any further
    /// command-line switches after <c>--data-dir</c>.
    /// </summary>
    private static Process StartApp(string exe, string profile, params string[] moreArgs) =>
        Process.Start(new ProcessStartInfo(exe, ["--data-dir", profile, .. moreArgs]) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })
            ?? throw new InvalidOperationException($"could not start {exe}");

    /// <summary>
    /// Fails <paramref name="check"/> and answers true when another DropHalo already holds the app mutex: a
    /// scenario that must own the app leaves a stranger's instance untouched.
    /// </summary>
    private static bool AnotherInstanceRunning(Checks checks, string check, string detail)
    {
        if (!Mutex.TryOpenExisting(@"Local\DropHalo.App", out var existing))
        {
            return false;
        }

        existing.Dispose();
        checks.Check(check, false, detail);
        return true;
    }
}
