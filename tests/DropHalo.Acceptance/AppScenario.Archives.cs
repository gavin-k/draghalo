using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace DropHalo.Acceptance;

internal static partial class AppScenario
{
    /// <summary>Repeatedly switches from subtitle conversion to ZIP repacking through real Explorer drags.</summary>
    public static int RunArchives(string exe, int iterations)
    {
        var checks = new Checks();
        if (AnotherInstanceRunning(checks, "archive test owns its app", "Another DropHalo instance is running."))
        {
            return checks.Report("Archive drags");
        }

        var runId = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(Path.GetTempPath(), "DropHaloAcceptance", "archives-" + runId);
        var profile = Path.Combine(dir, "profile");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "settings.json"), """{"version":2,"onboardingDone":true,"language":"Chinese"}""");
        var log = new AppLog();
        using var app = StartApp(exe, profile);
        var ownsApp = false;
        try
        {
            ownsApp = log.WaitFor(30000, "ready:") is not null && !app.HasExited && OwnerPid(PInvoke.FindWindow(null, QueueTitle)) == app.Id;
            if (checks.Check("archive app reports ready and owns its task window", ownsApp, $"PID {app.Id}"))
            {
                for (var i = 1; i <= iterations; i++)
                {
                    if (app.HasExited)
                    {
                        checks.Check("archive app stays running", false, $"PID {app.Id} exited with code {app.ExitCode} before iteration {i}.");
                        break;
                    }

                    Log.Info($"---- archive iteration {i}/{iterations} ----");
                    var source = Path.Combine(dir, $"archive-{runId}-{i:D3}");
                    Directory.CreateDirectory(source);
                    var jpg = Path.Combine(source, $"m0-{i:D7}.jpg");
                    WriteJpeg(jpg, 640, 400);
                    Attempt(checks, $"convert and undo before ZIP {i}", () => ConvertAndUndo(checks, log, app, jpg, new FileInfo(jpg).Length, explorer: true));
                    Attempt(checks, $"cancel before ZIP {i}", () => ReleaseOff(checks, log, jpg, explorer: true));
                    Attempt(checks, $"edge before ZIP {i}", () => EdgeShift(checks, log, jpg, explorer: true));
                    Attempt(checks, $"subtitle before ZIP {i}", () => ConvertSubtitle(checks, log, source, explorer: true));
                    Attempt(checks, $"ZIP repack {i}", () => RepackArchive(checks, log, source, explorer: true));
                }
            }
        }
        catch (Exception ex)
        {
            checks.Check("archive scenario completes", false, ex.ToString());
        }
        finally
        {
            try
            {
                if (ownsApp)
                {
                    ReleaseDrag(grouped: false);
                    Quit(checks, log, app, exe);
                }
            }
            finally
            {
                if (!app.HasExited)
                {
                    app.Kill();
                    app.WaitForExit(5000);
                }
            }
        }

        return checks.Report("Archive drags");
    }

    private static void LogDragState()
    {
        _ = PInvoke.GetCursorPos(out var point);
        var host = PInvoke.FindWindow("DropHalo.DropTargetHost", null);
        var image = PInvoke.FindWindowEx(default, default, "SysDragImage", null);
        var hit = PInvoke.WindowFromPoint(point);
        var wheel = PInvoke.FindWindow(null, WheelTitle);
        _ = PInvoke.GetWindowRect(host, out var bounds);
        Log.Info($"drag state: cursor {point.X},{point.Y}, button {(PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_LBUTTON) & 0x8000) != 0}, "
            + $"drag image {image} visible {(bool)PInvoke.IsWindowVisible(image)}, host {host} visible {(bool)PInvoke.IsWindowVisible(host)} "
            + $"rect {bounds.left},{bounds.top},{bounds.right},{bounds.bottom}, wheel {wheel} visible {(bool)PInvoke.IsWindowVisible(wheel)}, "
            + $"hit {hit} '{Title(hit)}' PID {OwnerPid(hit)}, foreground {PInvoke.GetForegroundWindow()} at {DateTime.UtcNow:O}");
    }

    private static void CaptureDragFailure(Bitmap? before = null, DragSource? source = null)
    {
        source?.LogStartState();
        LogDragState();
        var path = Path.ChangeExtension(Log.FilePath!, null) + $"-failure-{DateTime.UtcNow.Ticks}.png";
        using var screen = Capture(SystemInformation.VirtualScreen);
        screen.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Log.Info($"drag failure screenshot: {path}");
        if (before is not null)
        {
            var beforePath = Path.ChangeExtension(path, null) + "-before.png";
            before.Save(beforePath, System.Drawing.Imaging.ImageFormat.Png);
            Log.Info($"drag before the wheel screenshot: {beforePath}");
        }
    }
}
