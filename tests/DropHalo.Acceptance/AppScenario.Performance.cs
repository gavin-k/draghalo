using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DropHalo.Core.Wheel;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace DropHalo.Acceptance;

internal static partial class AppScenario
{
    private static readonly JsonSerializerOptions PerformanceJsonOptions = new() { WriteIndented = true };

    /// <summary>Repeats the real Explorer drag path, including every slow or missing frame in the result.</summary>
    public static unsafe int RunPerformance(string exe, int iterations, bool reopenExplorer = false, bool grouped = false)
    {
        var checks = new Checks();
        if (AnotherInstanceRunning(checks, "performance owns the app under test", "Another DropHalo instance is running; left it untouched."))
        {
            return checks.Report("Wheel performance");
        }

        var dir = Path.Combine(Path.GetTempPath(), "DropHaloAcceptance", "performance-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(dir, "profile");
        Directory.CreateDirectory(profile);
        var jpg = Path.Combine(dir, "performance.jpg");
        WriteJpeg(jpg, 640, 400);
        var sourceLength = new FileInfo(jpg).Length;
        File.WriteAllText(Path.Combine(profile, "settings.json"), """{"version":2,"onboardingDone":true,"language":"Chinese"}""");
        if (grouped)
        {
            // Groups belong to the Tools/Actions layouts; use real image tools so the app builds the group.
            File.WriteAllText(Path.Combine(profile, "layout.json"), """
                { "version": 1, "wheels": { "tools": { "image": [
                  { "item": "tool.half-size" },
                  { "item": "tool.compress-image" },
                  { "item": "tool.strip-metadata" },
                  { "item": "tool.combine-pdf" },
                  { "item": "tool.ocr" },
                  { "group": { "name": { "zh": "性能分组", "en": "Performance" }, "items": ["tool.rotate-right"] } }
                ] } }, "installed": [] }
                """);
        }

        var samples = new List<PerformanceSample>();
        object? metadata = null;
        var log = new AppLog();
        Process? app = null;
        Log.Info($"performance: exe {exe}; iterations {iterations}; budget {BudgetMs} ms; reopen Explorer={reopenExplorer}; grouped Tools={grouped}; source {jpg}; profile {profile}");
        try
        {
            app = StartApp(exe, profile);
            var ready = log.WaitFor(30000, "ready:");
            if (!checks.Check("performance app reports ready", ready is not null && !app.HasExited,
                ready ?? $"no ready line within 30 s; exited={app.HasExited}"))
            {
                return checks.Report("Wheel performance");
            }

            checks.Check("offscreen prewarm is separate from real first-frame telemetry", log.Count("overlay: first frame") == 0,
                $"{log.Count("overlay: first frame")} real first-frame events before any input");

            var queueWindow = PInvoke.FindWindow(null, QueueTitle);
            uint queuePid = 0;
            var uiThreadId = PInvoke.GetWindowThreadProcessId(queueWindow, &queuePid);
            metadata = new
            {
                appPid = app.Id,
                queueHwnd = $"0x{(nint)queueWindow.Value:X}",
                queuePid,
                uiThreadId,
                startUtc = app.StartTime.ToUniversalTime(),
                timezone = TimeZoneInfo.Local.Id,
                utcOffset = DateTimeOffset.Now.Offset.ToString(),
                stopwatchFrequency = Stopwatch.Frequency,
            };
            Log.Info("PERFORMANCE METADATA " + JsonSerializer.Serialize(metadata));

            using var sharedSource = reopenExplorer ? null : DragSource.Open(jpg, explorer: true);
            var lastEnded = Stopwatch.GetTimestamp();
            for (var iteration = 1; iteration <= iterations; iteration++)
            {
                var sample = new PerformanceSample(iteration);
                samples.Add(sample);
                Log.Info($"---- performance iteration {iteration}/{iterations}; first after start={iteration == 1} ----");
                DragSource? iterationSource = null;
                try
                {
                    if (app.HasExited)
                    {
                        throw new InvalidOperationException($"App exited with code {app.ExitCode} before iteration {iteration}.");
                    }

                    iterationSource = reopenExplorer ? DragSource.Open(jpg, explorer: true) : null;
                    var mid = (iterationSource ?? sharedSource)!.PressAndDrag();
                    var foreground = PInvoke.GetForegroundWindow();
                    using var before = iteration == 1 || iteration == iterations ? Capture(SystemInformation.VirtualScreen) : null;
                    log.Mark();
                    sample.IdleMs = iteration > 1 ? Stopwatch.GetElapsedTime(lastEnded).TotalMilliseconds : null;
                    // Tap/Shown owns the original showMs + firstFrameMs <= 150 ms acceptance, unchanged.
                    var wheel = Tap(checks, log, mid, grouped ? CtrlAlt : null);
                    sample.ShownLine = log.Find("wheel: shown");
                    sample.FrameLine = log.Find("overlay: first frame");
                    if (sample.ShownLine is { } shown && ShownRegex().Match(shown) is { Success: true } s)
                    {
                        sample.ShowMs = double.Parse(s.Groups["ms"].Value, CultureInfo.InvariantCulture);
                    }

                    if (sample.FrameLine is { } frame && FrameRegex().Match(frame) is { Success: true } f)
                    {
                        sample.FrameMs = double.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture);
                    }

                    sample.TotalMs = sample.ShowMs + sample.FrameMs;
                    sample.WithinBudget = sample.TotalMs is >= 0 and <= BudgetMs;
                    checks.Check($"iteration {iteration}: one show and one first frame", log.Count("wheel: shown") == 1 && log.Count("overlay: first frame") == 1,
                        $"show {sample.ShowMs?.ToString("F1", CultureInfo.InvariantCulture) ?? "missing"} ms; frame {sample.FrameMs?.ToString("F0", CultureInfo.InvariantCulture) ?? "missing"} ms; total {sample.TotalMs?.ToString("F1", CultureInfo.InvariantCulture) ?? "missing"} ms");
                    sample.KeptFocus = PInvoke.GetForegroundWindow() == foreground && OwnerPid(foreground) != app.Id;
                    checks.Check($"iteration {iteration}: wheel keeps Explorer focus", sample.KeptFocus, $"foreground '{Title(PInvoke.GetForegroundWindow())}'");
                    var entryDeadline = Environment.TickCount64 + 2000;
                    var entered = log.WaitFor(2000, "file(s) entered");
                    checks.Check($"iteration {iteration}: real file enters the wheel", SlotOf(entered, grouped ? "tool.half-size" : "png") >= 0, entered ?? "No file entered within 2 s.");
                    var remaining = (int)Math.Max(0, entryDeadline - Environment.TickCount64);
                    sample.ModelFrameLine = remaining > 0 ? log.WaitFor(remaining, "overlay: model frame") : null;
                    sample.ModelRenderedWithinDeadline = sample.ModelFrameLine is not null && Environment.TickCount64 <= entryDeadline;
                    checks.Check($"iteration {iteration}: file model renders within the same 2 s entry deadline",
                        sample.ModelRenderedWithinDeadline, sample.ModelFrameLine ?? "No model frame within the file-entry deadline.");
                    if (grouped)
                    {
                        checks.Check($"iteration {iteration}: Tools reads the group from the temporary layout", SlotOf(entered, "group:性能分组") == 5,
                            entered ?? "No grouped Tools layout was shown.");
                        var wheelWindow = PInvoke.FindWindow(null, WheelTitle);
                        RECT bounds = default;
                        var result = PInvoke.DwmGetWindowAttribute(wheelWindow, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &bounds, (uint)sizeof(RECT));
                        var expected = wheel is { } placement ? (int)Math.Ceiling(2 * (WheelGeometry.RingOuterRadius + WheelGeometry.RingMargin) * placement.Scale) : 0;
                        checks.Check($"iteration {iteration}: grouped wheel uses the ring footprint", result.Succeeded && expected > 0
                            && bounds.right - bounds.left == expected && bounds.bottom - bounds.top == expected,
                            $"actual {bounds.right - bounds.left}x{bounds.bottom - bounds.top} px; expected {expected}x{expected} px; HRESULT {result}");
                    }
                    if (before is not null && wheel is { } w)
                    {
                        using var after = Capture(SystemInformation.VirtualScreen);
                        Screenshot(after, w);
                        var path = Path.ChangeExtension(Log.FilePath!, null) + "-wheel.png";
                        File.Copy(path, Path.ChangeExtension(Log.FilePath!, null) + $"-wheel-{iteration:D3}.png", overwrite: true);
                        var (screenFrame, changedQuadrants) = WheelDrawing(before, after, w, hovered: 0);
                        checks.Check($"iteration {iteration}: wheel has drawn detail", screenFrame is { Flat: false } && changedQuadrants >= 3,
                            $"{screenFrame}; changed quadrants {changedQuadrants}");
                    }
                }
                catch (Exception ex)
                {
                    sample.Error = ex.ToString();
                    checks.Check($"iteration {iteration}: completes", false, sample.Error);
                }
                finally
                {
                    ReleaseDrag(grouped);
                    var hidden = Checks.Wait(() =>
                    {
                        var wheelWindow = PInvoke.FindWindow(null, WheelTitle);
                        return (wheelWindow.IsNull || !PInvoke.IsWindowVisible(wheelWindow)) && !DockVisible();
                    }, 2000);
                    checks.Check($"iteration {iteration}: cancellation hides wheel and empty dock", hidden, log.Find("wheel: closed") ?? "No close line.");
                    sample.NoTask = log.Find("wheel: enqueued") is null && Directory.GetFiles(dir).Length == 1 && File.Exists(jpg) && new FileInfo(jpg).Length == sourceLength;
                    checks.Check($"iteration {iteration}: cancellation creates no task or output", sample.NoTask, log.Find("wheel: enqueued") ?? "Source remains and no output was added.");
                    try { iterationSource?.Dispose(); }
                    catch (Exception ex)
                    {
                        sample.Error ??= ex.ToString();
                        checks.Check($"iteration {iteration}: Explorer closes", false, ex.ToString());
                    }
                    lastEnded = Stopwatch.GetTimestamp();
                    Log.Info("PERFORMANCE SAMPLE " + JsonSerializer.Serialize(sample));
                }
            }

            checks.Check($"all {iterations} samples meet the unchanged {BudgetMs} ms limit", samples.Count == iterations && samples.All(s => s.WithinBudget && s.Error is null),
                $"{samples.Count(s => s.WithinBudget && s.Error is null)}/{iterations} within budget; all samples retained");
        }
        catch (Exception ex)
        {
            checks.Check("performance scenario completes", false, ex.ToString());
        }
        finally
        {
            ReleaseDrag(grouped);
            if (app is not null)
            {
                try { Quit(checks, log, app, exe); }
                catch (Exception ex) { checks.Check("performance app shuts down cleanly", false, ex.ToString()); }
                finally
                {
                    if (!app.HasExited)
                    {
                        app.Kill();
                        app.WaitForExit(5000);
                    }

                    app.Dispose();
                }
            }

            var result = Path.ChangeExtension(Log.FilePath!, ".json");
            File.WriteAllText(result, JsonSerializer.Serialize(new { exe, profile, iterations, reopenExplorer, grouped, budgetMs = BudgetMs, metadata, samples }, PerformanceJsonOptions));
            Log.Info($"performance: raw samples {result}");
        }

        return checks.Report("Wheel performance");
    }

    private static void ReleaseDrag(bool grouped)
    {
        Input.Key(VIRTUAL_KEY.VK_ESCAPE, up: false);
        Input.Key(VIRTUAL_KEY.VK_ESCAPE, up: true);
        Input.Key(VIRTUAL_KEY.VK_LSHIFT, up: true);
        if (grouped)
        {
            Input.Key(VIRTUAL_KEY.VK_LMENU, up: true);
            Input.Key(VIRTUAL_KEY.VK_LCONTROL, up: true);
        }

        Input.LeftUp();
    }

    private sealed class PerformanceSample(int iteration)
    {
        public int Iteration { get; } = iteration;
        public double? IdleMs { get; set; }
        public double? ShowMs { get; set; }
        public double? FrameMs { get; set; }
        public double? TotalMs { get; set; }
        public bool WithinBudget { get; set; }
        public bool KeptFocus { get; set; }
        public bool NoTask { get; set; }
        public string? ShownLine { get; set; }
        public string? FrameLine { get; set; }
        public string? Error { get; set; }
        public string? ModelFrameLine { get; set; }
        public bool ModelRenderedWithinDeadline { get; set; }
    }
}
