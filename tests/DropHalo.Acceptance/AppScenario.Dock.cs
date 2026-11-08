using System.Drawing.Imaging;
using System.Globalization;
using System.Text.RegularExpressions;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace DropHalo.Acceptance;

internal static partial class AppScenario
{
    private const string DockTitle = "DropHalo 停靠栏";
    private const int DockBudgetMs = 300;

    /// <summary>
    /// The dock appears when a JPG is dragged, without first reaching an edge, and cancellation hides it again.
    /// Dropped on the dock, the JPG is kept there and nothing is copied or moved; dragged out of the dock onto the wheel's PNG
    /// sector, it comes out as a PNG next to itself and leaves the dock, which hides. Dropped once more,
    /// 清空 empties the dock and hides it.
    /// </summary>
    private static void EdgeDock(Checks checks, AppLog log, string dir, bool explorer)
    {
        var jpg = Path.Combine(dir, $"m1-dock-{DateTime.Now:HHmmss}.jpg");
        WriteJpeg(jpg, 320, 200);
        using (var source = DragSource.Open(jpg, explorer))
        {
            var dock = StartDockDrag(checks, log, source, out _);
            Input.Key(VIRTUAL_KEY.VK_ESCAPE, up: false);
            Input.Key(VIRTUAL_KEY.VK_ESCAPE, up: true);
            Input.LeftUp();

            if (dock is null)
            {
                return;
            }

            var hidden = log.WaitFor(2000, "dock: hidden (the drag ended without a drop)");
            checks.Check("canceling a drag hides the empty dock again", hidden is not null && !DockVisible(),
                hidden ?? $"no 'dock: hidden' within 2 s; {log.Find("dock: ") ?? "no other dock line"}");
        }

        if (!DropOnDock(checks, log, jpg, explorer))
        {
            return;
        }

        DragOutToWheel(checks, log, jpg);

        if (!DropOnDock(checks, log, jpg, explorer))
        {
            return;
        }

        log.Mark();
        var cleared = Invoke(DockTitle, "清空", out var how) ? log.WaitFor(2000, "dock: hidden (cleared)") : null;
        checks.Check("清空 empties the dock and hides it", cleared is not null && log.Find("dock: cleared 1 file(s)") is not null && !DockVisible(),
            cleared ?? how);
    }

    /// <summary>
    /// Drags <paramref name="jpg"/> and drops it on the dock that appears, then checks the dock
    /// keeps it while the folder stays as it was.
    /// </summary>
    private static bool DropOnDock(Checks checks, AppLog log, string jpg, bool explorer, int count = 1, bool selectAll = false)
    {
        var dir = Path.GetDirectoryName(jpg)!;
        var before = Directory.GetFiles(dir);
        using var source = DragSource.Open(jpg, explorer);
        if (selectAll)
        {
            Input.Key(VIRTUAL_KEY.VK_CONTROL, up: false);
            Input.Key(VIRTUAL_KEY.VK_A, up: false);
            Input.Key(VIRTUAL_KEY.VK_A, up: true);
            Input.Key(VIRTUAL_KEY.VK_CONTROL, up: true);
            Thread.Sleep(200);
        }
        if (StartDockDrag(checks, log, source, out var mid) is not { } dock)
        {
            Input.MoveSmooth(mid, source.Grab, 12, 30);
            Thread.Sleep(250);
            Input.LeftUp();
            return false;
        }

        // A few moves inside so OLE finds the window that appeared under the drag.
        var inside = new Point(dock.X + dock.Width / 2, dock.Y + dock.Height / 2);
        Input.MoveSmooth(mid, inside, 8, 30);
        for (var i = 0; i < 20 && log.Find("dock: entered") is null; i++)
        {
            Input.MoveTo(inside.X + Wobble[i % Wobble.Length], inside.Y + Wobble[(i + 3) % Wobble.Length]);
            Thread.Sleep(50);
        }

        var entered = log.Find("dock: entered");
        Thread.Sleep(200);
        Input.LeftUp();
        var added = log.WaitFor(3000, "dock: added ");
        Thread.Sleep(500);
        var after = Directory.GetFiles(dir);
        var kept = added?.Contains($"dock: added {count} file(s), {count} in the dock", StringComparison.Ordinal) == true && DockVisible();
        checks.Check($"{count} file(s) dropped on the dock are kept there, and nothing is copied or moved",
            kept && File.Exists(jpg) && after.Length == before.Length,
            $"{entered ?? "no 'dock: entered'"}; {added ?? "no 'dock: added' within 3 s"}; dock {(DockVisible() ? "shown" : "hidden")}; "
            + $"source {(File.Exists(jpg) ? "there" : "gone")}; files {before.Length} -> {after.Length}");
        if (kept)
        {
            DockScreenshot(dock, "filled");
        }

        return kept;
    }

    /// <summary>
    /// Presses on <paramref name="jpg"/>'s row in the dock, drags towards the middle of the screen, taps Shift
    /// and releases on the PNG sector: the PNG lands next to the JPG, and the JPG leaves the dock, which hides.
    /// </summary>
    private static void DragOutToWheel(Checks checks, AppLog log, string jpg)
    {
        if (Element(DockTitle, Path.GetFileName(jpg), out var how) is not { } row)
        {
            checks.Check("the dock lists the JPG by name", false, how);
            return;
        }

        var r = row.CurrentBoundingRectangle;
        var grab = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        var area = Screen.FromPoint(grab).WorkingArea;
        var mid = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
        log.Mark();
        Input.MoveTo(grab.X, grab.Y);
        Thread.Sleep(150);
        Input.LeftDown();
        Thread.Sleep(120);
        Input.MoveSmooth(grab, mid, 14, 25);
        Thread.Sleep(250);

        // Back over the dock, which refuses its own files, whenever the drag has to be called off.
        void CallOff(Point from)
        {
            Input.MoveSmooth(from, grab, 10, 30);
            Thread.Sleep(250);
            Input.LeftUp();
            log.WaitFor(2000, "dock: dragged out ");
        }

        if (Tap(checks, log, mid) is not { } w)
        {
            CallOff(mid);
            return;
        }

        var sector = SlotOf(log.WaitFor(2000, "file(s) entered"), "png");
        if (!checks.Check("a JPG dragged out of the dock offers png", sector >= 0, log.Find("file(s) entered") ?? "no 'file(s) entered' line within 2 s"))
        {
            Input.MoveSmooth(new Point(mid.X + 6, mid.Y + 6), new Point(w.X, w.Y), 4, 30);
            Input.LeftUp();
            return;
        }

        Input.MoveSmooth(new Point(mid.X + 6, mid.Y + 6), SectorPoint(w, sector), 8, 30);
        Thread.Sleep(250);
        Input.LeftUp();
        var enqueued = log.WaitFor(3000, "wheel: enqueued ");
        var label = enqueued?[(enqueued.IndexOf("wheel: enqueued ", StringComparison.Ordinal) + "wheel: enqueued ".Length)..];
        var done = label is null ? null : log.WaitFor(30000, $"queue: {label} ");
        var png = done is not null && OutputsRegex().Match(done) is { Success: true } m ? m.Groups[1].Value : null;
        checks.Check("the JPG dragged out of the dock onto the PNG sector comes out as a PNG next to it",
            png is not null && File.Exists(png) && SameDir(png, jpg) && png.EndsWith(".png", StringComparison.OrdinalIgnoreCase),
            done ?? enqueued ?? log.Find("wheel: closed") ?? "wheel never closed");

        var outcome = log.WaitFor(3000, "dock: dragged out ");
        var hidden = log.WaitFor(2000, "dock: hidden (every file was dragged out)");
        checks.Check("the JPG dragged out leaves the dock, which hides once empty, and stays where it was",
            outcome?.Contains("dock: dragged out 1 file(s), copied", StringComparison.Ordinal) == true && hidden is not null && !DockVisible() && File.Exists(jpg),
            $"{outcome ?? "no 'dock: dragged out' within 3 s"}; {hidden ?? "no 'dock: hidden'"}; source {(File.Exists(jpg) ? "there" : "gone")}");
    }

    /// <summary>Starts a file drag away from the edges and checks the dock appears without taking focus.</summary>
    private static Rectangle? StartDockDrag(Checks checks, AppLog log, DragSource source, out Point mid)
    {
        source.Press();
        checks.Check("pressing a file without dragging does not open the dock", !DockVisible(), "checked before crossing the drag threshold");
        var foreground = PInvoke.GetForegroundWindow();
        var area = Screen.FromPoint(source.Grab).WorkingArea;
        var direction = source.Grab.X < area.X + area.Width / 2 ? 1 : -1;
        mid = new Point(source.Grab.X + direction * 120, source.Grab.Y);
        log.Mark();
        var from = DateTime.Now;
        Input.MoveSmooth(source.Grab, mid, 6, 25);
        var shown = log.WaitFor(2000, "dock: shown");
        if (shown is null || DockShownRegex().Match(shown) is not { Success: true } m)
        {
            checks.Check("dragging a file automatically shows the dock", false, shown ?? "no 'dock: shown' within 2 s");
            return null;
        }

        if (DateTime.TryParseExact(shown[..23], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamped))
        {
            var ms = (stamped - from).TotalMilliseconds;
            checks.Check($"dragging a file shows the dock within {DockBudgetMs} ms", ms >= 0 && ms <= DockBudgetMs,
                $"{ms:F0} ms from the first drag move to '{shown[24..]}'");
        }

        int Group(string name) => int.Parse(m.Groups[name].Value, CultureInfo.InvariantCulture);
        var dock = new Rectangle(Group("x"), Group("y"), Group("w"), Group("h"));
        checks.Check("the dock stays in the work area, away from the starting pointer, without taking focus",
            area.Contains(dock) && !dock.Contains(source.Grab) && PInvoke.GetForegroundWindow() == foreground,
            $"dock {dock}; work area {area}; starting pointer {source.Grab}");
        DockScreenshot(dock, "empty");
        return dock;
    }

    private static void DockScreenshot(Rectangle area, string state)
    {
        using var shot = Capture(area);
        var path = Path.ChangeExtension(Log.FilePath ?? Path.Combine(Path.GetTempPath(), "app"), null) + $"-dock-{state}.png";
        shot.Save(path, ImageFormat.Png);
        Log.Info($"app: dock screenshot {area} -> {path}");
    }

    /// <summary>The last pixel column of the screen under <paramref name="from"/> on its right, else its left, where no other screen carries on; null when both do.</summary>
    private static Point? OpenEdge(Point from)
    {
        var bounds = Screen.FromPoint(from).Bounds;
        bool Open(int x) => !Screen.AllScreens.Any(s => s.Bounds.Contains(x, from.Y));
        return Open(bounds.Right) ? new Point(bounds.Right - 1, from.Y)
            : Open(bounds.Left - 1) ? new Point(bounds.Left, from.Y)
            : null;
    }

    /// <summary>
    /// With the dock switched off neither starting a drag nor reaching an edge opens it; dropping back on the source
    /// which does nothing.
    /// </summary>
    private static void EdgeOpensNothing(Checks checks, AppLog log, string source, bool explorer)
    {
        using var drag = DragSource.Open(source, explorer);
        log.Mark();
        var mid = drag.PressAndDrag();
        var edge = OpenEdge(mid) ?? mid;
        Input.MoveSmooth(mid, edge, 12, 30);
        Rest(edge, 700, 0);
        var opened = log.WaitFor(300, "dock: shown");
        checks.Check("with the dock off starting a drag and reaching the edge opens nothing", opened is null && !DockVisible(),
            opened ?? (DockVisible() ? "no 'dock: shown', but the dock is still up from before" : "no 'dock: shown' within 1 s"));
        Input.MoveSmooth(edge, drag.Grab, 12, 30);
        Thread.Sleep(250);
        Input.LeftUp();
        Thread.Sleep(300);
    }

    private static bool DockVisible() => PInvoke.FindWindow(null, DockTitle) is { IsNull: false } window && PInvoke.IsWindowVisible(window);

    [GeneratedRegex(@"dock: shown at the (?<side>left|right) edge, (?<x>-?\d+),(?<y>-?\d+) (?<w>\d+)x(?<h>\d+)")]
    private static partial Regex DockShownRegex();
}
