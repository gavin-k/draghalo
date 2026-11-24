using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Win32;
using Windows.Win32.UI.Accessibility;

namespace DropHalo.Acceptance;

internal static partial class AppScenario
{
    /// <summary>
    /// 轮盘排布's disc, with the mouse, on the image tools wheel: dragging sector 1 onto sector 3 swaps them on the disc
    /// and in layout.json, dragging back swaps them back, a drop on the hub changes nothing, and layout.json rewritten
    /// by someone else mid-drag wins over the drag. 下移 still works for the keyboard.
    /// </summary>
    public static int RunLayoutDisc(string exe)
    {
        const string Name = "Layout disc";
        var checks = new Checks();
        if (AnotherInstanceRunning(checks, "layout disc test owns its app", "Another DropHalo is running; left it untouched."))
        {
            return checks.Report(Name);
        }

        var profile = Path.Combine(Path.GetTempPath(), "DropHaloAcceptance", "layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "settings.json"), """{"version":2,"onboardingDone":true,"language":"Chinese"}""");
        var file = Path.Combine(profile, "layout.json");
        var log = new AppLog();
        using var app = StartApp(exe, profile, "--settings");
        try
        {
            if (!checks.Check("layout disc app is ready", log.WaitFor(30000, "ready:") is not null && !app.HasExited, $"PID {app.Id}"))
            {
                return checks.Report(Name);
            }

            var window = default(Windows.Win32.Foundation.HWND);
            Checks.Wait(() => !(window = PInvoke.FindWindow(null, SettingsTitle)).IsNull && PInvoke.IsWindowVisible(window), 5000);
            PInvoke.SetForegroundWindow(window);
            if (!checks.Check("the disc of 轮盘排布 is scrolled into view", DiscInView(out var how), how)
                || DiscLabels() is not { } before
                || !checks.Check("sectors 1 and 3 of the image tools wheel hold something", before[0] != "（空）" && before[2] != "（空）", string.Join(" | ", before)))
            {
                return checks.Report(Name);
            }

            DiscScreenshot("shown");
            log.Mark();
            DragOnDisc(DiscCentre(0), DiscCentre(2), shot: "dragging");
            var changed = log.WaitFor(3000, "layout: changed");
            string[]? swapped = null;
            var shown = changed is not null && Checks.Wait(() => (swapped = DiscLabels()) is { } now
                && now[0] == before[2] && now[2] == before[0] && now[1] == before[1] && now.Skip(3).SequenceEqual(before.Skip(3)), 2000);
            checks.Check("dragging sector 1 onto sector 3 swaps them on the disc", shown,
                $"{changed ?? "no 'layout: changed' within 3 s"}; before {string.Join(" | ", before)}; after {string.Join(" | ", swapped ?? DiscLabels() ?? [])}");
            var first = ImageTools(file);

            log.Mark();
            DragOnDisc(DiscCentre(2), DiscCentre(0));
            changed = log.WaitFor(3000, "layout: changed");
            var back = changed is null ? null : ImageTools(file);
            var expected = first is { Count: >= 3 } ? Swap(first, 0, 2) : null;
            checks.Check("dragging sector 3 back onto sector 1 swaps them back in layout.json",
                back is not null && expected is not null && back.SequenceEqual(expected),
                $"{changed ?? "no 'layout: changed' within 3 s"}; after the first drag [{string.Join(", ", first ?? [])}]; now [{string.Join(", ", back ?? [])}]");

            var labels = DiscLabels() ?? [];
            var written = File.ReadAllText(file);
            log.Mark();
            DragOnDisc(DiscCentre(0), DiscHub());
            var hub = log.WaitFor(1500, "layout: changed");
            checks.Check("a sector dropped on the hub changes nothing",
                hub is null && File.ReadAllText(file) == written && (DiscLabels() ?? []).SequenceEqual(labels),
                $"{hub ?? "no 'layout: changed'"}; labels {string.Join(" | ", DiscLabels() ?? [])}");

            RewriteMidDrag(checks, log, file);
            EditLayout(checks, log, profile);
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

        static List<string> Swap(List<string> slots, int a, int b)
        {
            var copy = slots.ToList();
            (copy[a], copy[b]) = (copy[b], copy[a]);
            return copy;
        }
    }

    /// <summary>
    /// A sector picked up, then layout.json swapped by someone else while the button is still down: the disc redraws
    /// from the file and drops the drag, so letting go over sector 3 leaves the file as it was written.
    /// </summary>
    private static void RewriteMidDrag(Checks checks, AppLog log, string file)
    {
        var from = DiscCentre(0);
        var to = DiscCentre(2);
        var halfway = new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2);
        Input.MoveTo(from.X, from.Y);
        Thread.Sleep(150);
        Input.LeftDown();
        Thread.Sleep(120);
        Input.MoveSmooth(from, halfway, 8, 25);

        var root = JsonNode.Parse(File.ReadAllText(file))!;
        var image = root["wheels"]!["tools"]!["image"]!.AsArray();
        var (a, b) = (image[0]?.DeepClone(), image[1]?.DeepClone());
        image[0] = b;
        image[1] = a;
        var written = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        log.Mark();
        File.WriteAllText(file, written);
        var reloaded = log.WaitFor(3000, "layout: changed");
        Thread.Sleep(300);

        Input.MoveSmooth(halfway, to, 8, 25);
        Thread.Sleep(150);
        Input.LeftUp();
        Thread.Sleep(1500);
        var now = File.ReadAllText(file);
        checks.Check("layout.json rewritten mid-drag wins: letting go afterwards changes nothing",
            reloaded is not null && now == written,
            $"{reloaded ?? "no 'layout: changed' after the rewrite"}; file {(now == written ? "as written" : "changed after the rewrite: " + string.Join(", ", ImageTools(file) ?? []))}");
    }

    /// <summary>Presses on <paramref name="from"/>, moves to <paramref name="to"/> and lets go, taking <paramref name="shot"/> before letting go when named.</summary>
    private static void DragOnDisc(Point from, Point to, string? shot = null)
    {
        Input.MoveTo(from.X, from.Y);
        Thread.Sleep(150);
        Input.LeftDown();
        Thread.Sleep(120);
        Input.MoveSmooth(from, to, 15, 25);
        Thread.Sleep(150);
        if (shot is not null)
        {
            DiscScreenshot(shot);
        }

        Input.LeftUp();
    }

    private static void DiscScreenshot(string state)
    {
        if (!PInvoke.GetWindowRect(PInvoke.FindWindow(null, SettingsTitle), out var r))
        {
            return;
        }

        using var shot = Capture(Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom));
        var path = Path.ChangeExtension(Log.FilePath ?? Path.Combine(Path.GetTempPath(), "app"), null) + $"-layout-{state}.png";
        shot.Save(path, ImageFormat.Png);
        Log.Info($"layout: screenshot -> {path}");
    }

    /// <summary>The six labels of the disc without their "n · " prefix, or null when one is missing.</summary>
    private static string[]? DiscLabels()
    {
        var labels = new string[6];
        for (var i = 0; i < labels.Length; i++)
        {
            if (Element(SettingsTitle, $"layout-disc-{i}", out _, byId: true) is not { } label)
            {
                return null;
            }

            var name = label.CurrentName.ToString();
            var dot = name.IndexOf(" · ", StringComparison.Ordinal);
            labels[i] = dot < 0 ? name : name[(dot + 3)..];
        }

        return labels;
    }

    private static Point DiscCentre(int sector)
    {
        var r = Element(SettingsTitle, $"layout-disc-{sector}", out var how, byId: true)?.CurrentBoundingRectangle
            ?? throw new InvalidOperationException(how);
        return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
    }

    /// <summary>The middle of the disc: the six sector labels sit evenly around it.</summary>
    private static Point DiscHub()
    {
        var centres = Enumerable.Range(0, 6).Select(DiscCentre).ToList();
        return new Point((int)centres.Average(c => c.X), (int)centres.Average(c => c.Y));
    }

    /// <summary>Scrolls the settings page through UIA until the whole disc, top sector to bottom, is inside the page's view.</summary>
    private static bool DiscInView(out string how)
    {
        var window = PInvoke.FindWindow(null, SettingsTitle);
        var uia = (IUIAutomation)new CUIAutomation8();
        var scroller = window.IsNull ? null : uia.ElementFromHandle(window).FindFirst(TreeScope.TreeScope_Descendants,
            uia.CreatePropertyCondition(UIA_PROPERTY_ID.UIA_IsScrollPatternAvailablePropertyId, true));
        if (scroller is null)
        {
            how = window.IsNull ? $"no window titled '{SettingsTitle}'" : "no scrollable pane in the settings window";
            return false;
        }

        var scroll = (IUIAutomationScrollPattern)scroller.GetCurrentPattern(UIA_PATTERN_ID.UIA_ScrollPatternId);
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var top = Element(SettingsTitle, "layout-disc-0", out how, byId: true)?.CurrentBoundingRectangle;
            var bottom = Element(SettingsTitle, "layout-disc-3", out how, byId: true)?.CurrentBoundingRectangle;
            if (top is not { } t || bottom is not { } b || !PInvoke.GetWindowRect(window, out var frame))
            {
                return false;
            }

            // The pane reports the whole page as its rectangle, so what shows is where it meets the window.
            var pane = scroller.CurrentBoundingRectangle;
            var (viewTop, viewBottom) = (Math.Max(pane.top, frame.top), Math.Min(pane.bottom, frame.bottom));
            var (shown0, shown3) = (t.Height > 0, b.Height > 0); // a label scrolled out of view has an empty rectangle
            var (y0, y3) = (t.Y + t.Height / 2, b.Y + b.Height / 2);
            // A quarter of the way from the top label to the bottom one is past the rim (50 of 46 DIPs).
            var margin = (y3 - y0) / 4;
            if (shown0 && shown3 && y0 - margin >= viewTop && y3 + margin <= viewBottom)
            {
                how = $"disc labels at y {y0}..{y3} inside view {viewTop}..{viewBottom} after {attempt} scroll(s)";
                return true;
            }

            var size = scroll.CurrentVerticalViewSize;
            var at = scroll.CurrentVerticalScrollPercent;
            var height = Math.Max(1, viewBottom - viewTop);
            var step = shown0 && shown3
                ? ((y0 + y3) / 2.0 - (viewTop + viewBottom) / 2.0) * 100.0 / Math.Max(1, height * 100.0 / Math.Max(1, size) - height)
                : shown3 ? -size / 4 : size / 2; // only the bottom shows: the disc's top is above the view
            var percent = Math.Clamp(at + step, 0, 100);
            Log.Info($"layout: scroll {attempt}: view {viewTop}..{viewBottom}, label 1 {(shown0 ? y0 : "off")}, label 4 {(shown3 ? y3 : "off")}, "
                + $"view size {size:F1}%, at {at:F1}% -> {percent:F1}%");
            if (Math.Abs(percent - at) < 0.01)
            {
                how = $"the disc cannot be scrolled fully into view: view {viewTop}..{viewBottom}, labels {(shown0 ? y0 : "off")}..{(shown3 ? y3 : "off")}";
                return false;
            }

            scroll.SetScrollPercent(-1, percent);
            Thread.Sleep(300);
        }

        how = "the disc never came fully into view after 40 scrolls";
        return false;
    }
}
