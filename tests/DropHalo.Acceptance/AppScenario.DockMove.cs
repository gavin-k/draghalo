using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Win32;

namespace DropHalo.Acceptance;

internal static partial class AppScenario
{
    /// <summary>
    /// The dock moves by its title and opens there again: dragged by the title it follows the pointer and the place
    /// it is left is saved; the next drag opens it there; double-clicking the title forgets the place and puts the
    /// dock back against the screen edge. A click on the title that slips a pixel or two is still a click: it neither
    /// moves the dock nor saves a place, and a double-click whose first click slips still resets. A file dropped on the
    /// title still lands in the dock.
    /// </summary>
    public static int RunDockMove(string exe)
    {
        const string Name = "Dock move";
        var checks = new Checks();
        if (AnotherInstanceRunning(checks, "dock move test owns its app", "Another DropHalo is running; left it untouched."))
        {
            return checks.Report(Name);
        }

        var dir = Path.Combine(Path.GetTempPath(), "DropHaloAcceptance", "dockmove-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(dir, "profile");
        Directory.CreateDirectory(profile);
        var settings = Path.Combine(profile, "settings.json");
        File.WriteAllText(settings, """{"version":2,"onboardingDone":true,"language":"Chinese","privacy":{"strictOffline":true}}""");
        var files = Enumerable.Range(1, 3).Select(i => Path.Combine(dir, $"dockmove-{i}.txt")).ToArray();
        foreach (var file in files)
        {
            File.WriteAllText(file, Path.GetFileName(file));
        }

        var log = new AppLog();
        using var app = StartApp(exe, profile);
        try
        {
            if (!checks.Check("dock move app is ready", log.WaitFor(30000, "ready:") is not null && !app.HasExited, $"PID {app.Id}")
                || !DropOnDock(checks, log, files[0], explorer: true))
            {
                return checks.Report(Name);
            }

            if (DockTitleCentre(checks) is not { } title)
            {
                return checks.Report(Name);
            }

            using (var source = DragSource.Open(files[1], explorer: true))
            {
                log.Mark();
                var mid = source.PressAndDrag(120);
                Input.MoveSmooth(mid, title, 12, 30);
                for (var i = 0; i < 8; i++)
                {
                    Input.MoveTo(title.X + Wobble[i % Wobble.Length], title.Y + Wobble[(i + 3) % Wobble.Length]);
                    Thread.Sleep(50);
                }

                Thread.Sleep(200);
                Input.LeftUp();
                var added = log.WaitFor(5000, "dock: added 1 file(s), 2 in the dock");
                checks.Check("a file dropped on the dock's title, its handle, is added", added is not null && DockVisible(),
                    added ?? $"no 'dock: added 1 file(s), 2 in the dock' within 5 s; {log.Find("dock: ") ?? "no other dock line"}");
            }

            if (DockTitleCentre(checks) is not { } press || DockRect() is not { } before)
            {
                return checks.Report(Name);
            }

            var area = Screen.FromPoint(press).WorkingArea;
            var dx = before.Left - 300 >= area.Left ? -300 : 300;
            var dy = before.Bottom + 120 <= area.Bottom ? 120 : -120;
            log.Mark();
            Input.MoveTo(press.X, press.Y);
            Thread.Sleep(150);
            Input.LeftDown();
            Thread.Sleep(120);
            Input.MoveSmooth(press, new Point(press.X + dx, press.Y + dy), 20, 25);
            Thread.Sleep(200);
            Input.LeftUp();
            var moved = log.WaitFor(3000, "dock: moved to ");
            var after = DockRect() ?? Rectangle.Empty;
            checks.Check("dragging the title moves the dock with the pointer",
                moved is not null && Math.Abs(after.Left - before.Left - dx) <= 2 && Math.Abs(after.Top - before.Top - dy) <= 2,
                $"{moved ?? "no 'dock: moved to' within 3 s"}; window {before.Location} -> {after.Location}, pointer moved {dx},{dy}");
            var where = $"{after.Left},{after.Top}";
            checks.Check("where the dock was left is saved", Checks.Wait(() => SavedDockPosition(settings) == where, 3000),
                $"settings dockPosition {SavedDockPosition(settings) ?? "unreadable"}; window {where}");
            DockScreenshot(after, "moved");

            log.Mark();
            var cleared = Invoke(DockTitle, "清空", out var how) ? log.WaitFor(2000, "dock: hidden (cleared)") : null;
            if (!checks.Check("清空 hides the moved dock", cleared is not null && !DockVisible(), cleared ?? how)
                || !DropOnSavedDock(checks, log, files[2], after))
            {
                return checks.Report(Name);
            }

            if (DockTitleCentre(checks) is not { } handle || DockRect() is not { } placed)
            {
                return checks.Report(Name);
            }

            // Let go while still 2 px off the press point: a hand's slip, not a move.
            log.Mark();
            Input.MoveTo(handle.X, handle.Y);
            Thread.Sleep(150);
            Input.LeftDown();
            Thread.Sleep(60);
            Input.MoveTo(handle.X + 2, handle.Y + 1);
            Thread.Sleep(120);
            Input.LeftUp();
            Thread.Sleep(800);
            var slipped = DockRect() ?? Rectangle.Empty;
            checks.Check("a click on the title that slips 2 px neither moves the dock nor saves a new place",
                log.Find("dock: moved to") is null && slipped == placed && SavedDockPosition(settings) == where,
                $"{log.Find("dock: moved to") ?? "no move logged"}; window {placed.Location} -> {slipped.Location}; "
                + $"settings dockPosition {SavedDockPosition(settings) ?? "unreadable"}, left at {where}");
            Thread.Sleep((int)PInvoke.GetDoubleClickTime() + 200); // so the next press does not pair with this click

            // The first click of the double-click slips 2 px too; the second presses where the first did.
            log.Mark();
            Input.MoveTo(handle.X, handle.Y);
            Thread.Sleep(150);
            Input.LeftDown();
            Thread.Sleep(30);
            Input.MoveTo(handle.X + 2, handle.Y + 1);
            Thread.Sleep(30);
            Input.LeftUp();
            Input.MoveTo(handle.X, handle.Y);
            Thread.Sleep(50);
            Input.LeftDown();
            Thread.Sleep(30);
            Input.LeftUp();
            var reset = log.WaitFor(3000, "dock: position reset");
            var forgotten = Checks.Wait(() => SavedDockPosition(settings) == "none", 3000);
            Thread.Sleep(300);
            var edge = DockRect() ?? Rectangle.Empty;
            var work = Screen.FromPoint(new Point(edge.Left + edge.Width / 2, edge.Top + edge.Height / 2)).WorkingArea;
            var atEdge = Math.Abs(edge.Left - work.Left) <= 2 || Math.Abs(edge.Right - work.Right) <= 2;
            checks.Check("double-clicking the title, the first click slipping 2 px, forgets the place and puts the dock back against the screen edge",
                reset is not null && forgotten && atEdge && DockVisible(),
                $"{reset ?? "no 'dock: position reset' within 3 s"}; settings dockPosition {SavedDockPosition(settings) ?? "unreadable"}; "
                + $"window {edge}; work area {work}; {log.Find("dock: moved to") ?? "no move logged"}");

            log.Mark();
            var gone = Invoke(DockTitle, "清空", out how) ? log.WaitFor(2000, "dock: hidden (cleared)") : null;
            checks.Check("清空 hides the reset dock", gone is not null && !DockVisible(), gone ?? how);
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

        return checks.Report(Name);
    }

    /// <summary>
    /// Drags <paramref name="file"/> from a start away from <paramref name="saved"/>, the window the dock was left as,
    /// and drops it on the dock, which must open there.
    /// </summary>
    private static bool DropOnSavedDock(Checks checks, AppLog log, string file, Rectangle saved)
    {
        using var source = DragSource.Open(file, explorer: true);
        if (!checks.Check("the next drag starts away from where the dock was left", !saved.Contains(source.Grab), $"start {source.Grab}; dock {saved}"))
        {
            return false;
        }

        source.Press();
        var area = Screen.FromPoint(source.Grab).WorkingArea;
        var direction = source.Grab.X < area.X + area.Width / 2 ? 1 : -1;
        var mid = new Point(source.Grab.X + direction * 120, source.Grab.Y);
        log.Mark();
        Input.MoveSmooth(source.Grab, mid, 6, 25);
        var shown = log.WaitFor(2000, "dock: shown");
        int Group(Match m, string name) => int.Parse(m.Groups[name].Value, CultureInfo.InvariantCulture);
        if (shown is null || DockSavedRegex().Match(shown) is not { Success: true } match
            || Group(match, "x") != saved.Left || Group(match, "y") != saved.Top)
        {
            checks.Check("the next drag opens the dock where it was left", false, $"{shown ?? "no 'dock: shown' within 2 s"}; left at {saved.Location}");
            Input.MoveSmooth(mid, source.Grab, 12, 30);
            Thread.Sleep(250);
            Input.LeftUp();
            return false;
        }

        checks.Check("the next drag opens the dock where it was left", true, shown);
        var dock = new Rectangle(Group(match, "x"), Group(match, "y"), Group(match, "w"), Group(match, "h"));
        var inside = new Point(dock.X + dock.Width / 2, dock.Y + dock.Height / 2);
        Input.MoveSmooth(mid, inside, 8, 30);
        for (var i = 0; i < 20 && log.Find("dock: entered") is null; i++)
        {
            Input.MoveTo(inside.X + Wobble[i % Wobble.Length], inside.Y + Wobble[(i + 3) % Wobble.Length]);
            Thread.Sleep(50);
        }

        Thread.Sleep(200);
        Input.LeftUp();
        var added = log.WaitFor(3000, "dock: added 1 file(s)");
        return checks.Check("a file dropped on the dock where it was left is kept", added is not null && DockVisible(),
            $"{log.Find("dock: entered") ?? "no 'dock: entered'"}; {added ?? "no 'dock: added' within 3 s"}");
    }

    private static Point? DockTitleCentre(Checks checks)
    {
        if (Element(DockTitle, "dock-title", out var how, byId: true) is not { } title)
        {
            checks.Check("the dock's title can be found", false, how);
            return null;
        }

        var r = title.CurrentBoundingRectangle;
        return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
    }

    private static Rectangle? DockRect() =>
        PInvoke.FindWindow(null, DockTitle) is { IsNull: false } window && PInvoke.GetWindowRect(window, out var r)
            ? Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom)
            : null;

    /// <summary>The saved <c>dockPosition</c> as "left,top", "none" when there is none, null when the file cannot be read.</summary>
    private static string? SavedDockPosition(string settings)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(settings));
            return json.RootElement.TryGetProperty("dockPosition", out var p) && p.ValueKind == JsonValueKind.Object
                ? $"{p.GetProperty("left").GetInt32()},{p.GetProperty("top").GetInt32()}"
                : "none";
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"dock: shown at the saved position, (?<x>-?\d+),(?<y>-?\d+) (?<w>\d+)x(?<h>\d+)")]
    private static partial Regex DockSavedRegex();
}
