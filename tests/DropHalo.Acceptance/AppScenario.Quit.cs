using System.Diagnostics;
using System.IO.Compression;
using Windows.Win32;

namespace DropHalo.Acceptance;

internal static partial class AppScenario
{
    /// <summary>
    /// Quits the app while it repacks a ZIP large enough to still be running: the job must be cancelled and say
    /// so in the log, nothing but the ZIP may be left in its folder, and the process must end by itself.
    /// </summary>
    public static int RunQuit(string exe, int megabytes)
    {
        const string Title = "Quit during a job";
        var checks = new Checks();
        if (AnotherInstanceRunning(checks, "quit test owns its app", "Another DropHalo instance is running."))
        {
            return checks.Report(Title);
        }

        var runId = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(Path.GetTempPath(), "DropHaloAcceptance", "quit-" + runId);
        var profile = Path.Combine(dir, "profile");
        var source = Path.Combine(dir, "quit-" + runId[..8]);
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(profile, "settings.json"), """{"version":2,"onboardingDone":true,"language":"Chinese"}""");
        var zip = Path.Combine(source, "quit-big.zip");
        WriteStoredZip(zip, megabytes);
        var log = new AppLog();
        using var app = StartApp(exe, profile);
        try
        {
            var ready = log.WaitFor(30000, "ready:") is not null && !app.HasExited && OwnerPid(PInvoke.FindWindow(null, QueueTitle)) == app.Id;
            if (checks.Check("quit app reports ready and owns its task window", ready, $"PID {app.Id}")
                && DropOnWheel(checks, log, zip, "7z", explorer: true) is { } label)
            {
                QuitMidJob(checks, log, app, exe, source, zip, label);
            }
        }
        catch (Exception ex)
        {
            checks.Check("quit scenario completes", false, ex.ToString());
        }
        finally
        {
            ReleaseDrag(grouped: false);
            if (!app.HasExited)
            {
                app.Kill();
                app.WaitForExit(5000);
            }

            // The ZIP is large; what was left next to it is already in the checks.
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"could not remove {dir}: {ex.Message}");
            }
        }

        return checks.Report(Title);
    }

    private static void QuitMidJob(Checks checks, AppLog log, Process app, string exe, string source, string zip, string label)
    {
        // The job writes under a temporary name next to its source: a second file there means it is under way.
        var writing = Checks.Wait(() => Directory.EnumerateFiles(source).Skip(1).Any(), 10000);
        var early = log.Find($"queue: {label} ");
        if (!checks.Check("the job is still running when --quit is sent", writing && early is null && !app.HasExited,
            early ?? (writing ? "writing, no outcome yet" : $"nothing written within 10 s: {Listing(source)}")))
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        using var quit = Process.Start(new ProcessStartInfo(exe, ["--quit"]) { UseShellExecute = false });
        var exited = app.WaitForExit(20000);
        var exitMs = clock.ElapsedMilliseconds;

        // Listed at once: with the process gone nothing can still be tidying up.
        var left = Directory.GetFiles(source);
        if (!checks.Check("--quit ends the app while the job runs", exited && app.ExitCode == 0,
            exited ? $"exit code {app.ExitCode}, {exitMs} ms after --quit was started" : "still running 20 s after --quit, killed"))
        {
            return;
        }

        var outcome = log.WaitFor(2000, $"queue: {label} canceled");
        checks.Check("the job is cancelled before the app exits", outcome is not null,
            outcome ?? log.Find($"queue: {label} ") ?? log.Find("a job was still running at exit") ?? "no outcome line for the job");
        checks.Check("only the source ZIP is left in its folder", left is [var only] && string.Equals(only, zip, StringComparison.OrdinalIgnoreCase), Listing(source));
        var error = log.FindSinceStart(" ERROR ");
        checks.Check("app logged no errors", error is null, error ?? "none");
    }

    /// <summary>An uncompressed ZIP of random bytes: repacking it as 7z takes long enough to quit in the middle.</summary>
    private static void WriteStoredZip(string path, int megabytes)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var random = new Random(7);
        var block = new byte[1 << 20];
        for (var part = 0; part < 4; part++)
        {
            using var entry = archive.CreateEntry($"data/blob-{part}.bin", CompressionLevel.NoCompression).Open();
            for (var i = 0; i < megabytes / 4; i++)
            {
                random.NextBytes(block);
                entry.Write(block);
            }
        }
    }

    private static string Listing(string dir) =>
        string.Join("; ", Directory.EnumerateFiles(dir).Select(f => $"{Path.GetFileName(f)} {new FileInfo(f).Length} bytes"));
}
