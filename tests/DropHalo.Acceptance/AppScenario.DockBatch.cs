using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DropHalo.Acceptance;

internal static partial class AppScenario
{
    /// <summary>Replays a multi-file Explorer drop, clicking and dragging 全部拖出, cancellation and copying.</summary>
    public static int RunDockBatch(string exe, int iterations, bool cancelBeforeCopy = true)
    {
        var checks = new Checks();
        if (AnotherInstanceRunning(checks, "dock test owns its app", "Another DropHalo is running; left it untouched."))
        {
            return checks.Report("Dock batch drags");
        }

        var dir = Path.Combine(Path.GetTempPath(), "DropHaloAcceptance", "dock-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(dir, "source");
        var profile = Path.Combine(dir, "profile");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "settings.json"), """{"version":2,"onboardingDone":true,"language":"Chinese","privacy":{"strictOffline":true}}""");
        var files = Enumerable.Range(1, 3).Select(i => Path.Combine(sourceDir, $"file-{i}.txt")).ToArray();
        foreach (var file in files)
        {
            File.WriteAllText(file, Path.GetFileName(file));
        }

        var folder = Directory.CreateDirectory(Path.Combine(sourceDir, "folder")).FullName;
        File.WriteAllText(Path.Combine(folder, "nested.txt"), "nested");
        var log = new AppLog();
        using var app = StartApp(exe, profile);
        try
        {
            if (!checks.Check("dock app is ready", log.WaitFor(30000, "ready:") is not null && !app.HasExited, $"PID {app.Id}"))
            {
                return checks.Report("Dock batch drags");
            }

            for (var i = 1; i <= iterations; i++)
            {
                Log.Info($"---- dock batch {i}/{iterations} ----");
                var collected = i % 2 == 1
                    ? DropOnDock(checks, log, files[0], explorer: true, count: 4, selectAll: true)
                    : DropOnDock(checks, log, files[0], explorer: true)
                        && AddDockFile(checks, log, files[1], 2)
                        && AddDockFile(checks, log, files[2], 3)
                        && AddDockFile(checks, log, folder, 4);
                if (!collected)
                {
                    break;
                }

                var row = Element(DockTitle, "全部拖出", out var how);
                if (!checks.Check("全部拖出 is available for all four items", row is not null, how))
                {
                    break;
                }

                var rect = row!.CurrentBoundingRectangle;
                var grab = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
                var area = Screen.FromPoint(grab).WorkingArea;
                var mid = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
                log.Mark();
                Input.MoveTo(grab.X, grab.Y);
                Input.LeftDown();
                Thread.Sleep(70);
                Input.LeftUp();
                Thread.Sleep(250);
                checks.Check("clicking 全部拖出 keeps the app responsive", !app.HasExited && app.Responding && DockVisible(), $"PID {app.Id}");
                if (cancelBeforeCopy)
                {
                    Input.LeftDown();
                    Thread.Sleep(80);
                    Input.MoveSmooth(grab, mid, 12, 20);
                    var started = log.WaitFor(5000, "dock: drag out of 4 file(s) started");
                    Input.MoveSmooth(mid, new Point(mid.X + 50, mid.Y), 8, 30);
                    Thread.Sleep(300);
                    Input.Key(VIRTUAL_KEY.VK_ESCAPE, up: false);
                    Thread.Sleep(150);
                    Input.Key(VIRTUAL_KEY.VK_ESCAPE, up: true);
                    Input.LeftUp();
                    var canceled = log.WaitFor(5000, "dock: dragged out 4 file(s), canceled");
                    if (!checks.Check("canceling 全部拖出 preserves all files and the app", started is not null && canceled is not null && !app.HasExited && app.Responding && DockVisible(),
                        $"{started ?? "drag did not start"}; {canceled ?? "drag did not finish"}; exited={app.HasExited}"))
                    {
                        CaptureDragFailure();
                        break;
                    }
                }

                var target = Directory.CreateDirectory(Path.Combine(dir, $"target-{i}")).FullName;
                var marker = Path.Combine(target, "target.txt");
                File.WriteAllText(marker, "target");
                using (var receiver = DragSource.Open(marker, explorer: true))
                {
                    var targetWindow = ExplorerSource.LastWindow;
                    var destination = new Point(receiver.Grab.X + 180, receiver.Grab.Y + 120);
                    log.Mark();
                    Input.MoveTo(grab.X, grab.Y);
                    Input.LeftDown();
                    Thread.Sleep(80);
                    Input.MoveSmooth(grab, destination, 16, 25);
                    Thread.Sleep(400);
                    LogDragState();
                    var targetAtRelease = PInvoke.GetAncestor(PInvoke.WindowFromPoint(destination), GET_ANCESTOR_FLAGS.GA_ROOT);
                    checks.Check("the copy is released over its Explorer destination", targetAtRelease == targetWindow,
                        $"expected {targetWindow}, actual {targetAtRelease} '{Title(targetAtRelease)}', destination {destination}");
                    Input.LeftUp();
                    var completed = log.WaitFor(5000, "dock: dragged out 4 file(s), copied");
                    var copied = Checks.Wait(() => files.All(f => File.Exists(Path.Combine(target, Path.GetFileName(f)))) && File.Exists(Path.Combine(target, "folder", "nested.txt")), 5000);
                    if (completed is null || !copied)
                    {
                        LogDragState();
                        if (PInvoke.GetWindowRect(targetWindow, out var targetBounds))
                        {
                            DockScreenshot(new Rectangle(targetBounds.left, targetBounds.top, targetBounds.right - targetBounds.left, targetBounds.bottom - targetBounds.top), "destination-failure");
                        }
                    }
                    checks.Check("全部拖出 copies every file and folder and leaves originals intact", completed is not null && copied && files.All(File.Exists) && Directory.Exists(folder), completed ?? "copy did not complete");
                    checks.Check("completed 全部拖出 hides the empty dock and keeps the app responsive", !app.HasExited && app.Responding && Checks.Wait(() => !DockVisible(), 3000), $"exited={app.HasExited}");
                }

                if (app.HasExited)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            checks.Check("dock batch completes", false, ex.ToString());
        }
        finally
        {
            Input.LeftUp();
            if (!app.HasExited)
            {
                using var quit = Process.Start(new ProcessStartInfo(exe, ["--quit"]) { UseShellExecute = false });
                quit?.WaitForExit(5000);
                if (!app.WaitForExit(5000))
                {
                    app.Kill();
                }
            }
        }

        return checks.Report("Dock batch drags");
    }

    private static bool AddDockFile(Checks checks, AppLog log, string file, int total)
    {
        using var source = DragSource.Open(file, explorer: true);
        if (!PInvoke.GetWindowRect(PInvoke.FindWindow(null, DockTitle), out var dock))
        {
            return checks.Check("the occupied dock is available for another file", false, "dock window missing");
        }

        log.Mark();
        var mid = source.PressAndDrag(120);
        Input.MoveSmooth(mid, new Point((dock.left + dock.right) / 2, (dock.top + dock.bottom) / 2), 12, 30);
        Thread.Sleep(300);
        Input.LeftUp();
        var added = log.WaitFor(5000, $"dock: added 1 file(s), {total} in the dock");
        return checks.Check($"a separate drag accumulates item {total} in the occupied dock", added is not null && DockVisible(), added ?? "item was not added");
    }
}
