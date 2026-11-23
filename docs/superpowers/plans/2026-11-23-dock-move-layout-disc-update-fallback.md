# Dock Move, Layout Disc and Update Fallback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The dock can be dragged and remembers where it was left; installed apps find v0.1.7+ even while the CNB mirror lags; Settings → 轮盘排布 gets a disc whose sectors can be dragged onto each other to swap them.

**Architecture:** Core gains `DockPosition` (a settings field plus a pure placement function) and `UpdateClient.CheckNewestAsync`, which combines the existing single-source `CheckAsync` calls. The App adds a pointer-capture move handle to `DockWindow`, switches the update check to `CheckNewestAsync`, and adds a `LayoutDisc` control that raises `Moved(from, to)`; Settings turns that into `WheelLayout.Swap`. layout.json and the update recheck before install are unchanged.

**Tech Stack:** .NET 10, C#, WinUI 3 / Windows App SDK 2.5.1 (App only), CsWin32, xunit + FluentAssertions, System.Text.Json source generation.

**Spec:** No separate spec file. The decisions taken with the user on 2026-11-23 are listed below and in Global Constraints.
- The dock is moved by dragging its header. Its position is saved in settings.json and reused on every showing, pulled back inside the work area when monitors or resolution changed.
- Update fallback: CNB stays the default. When CNB is the preferred source, GitHub is also asked, and the newer release wins. GitHub-preferred users never contact cnb.cool.
- The disc covers the Tools wheel (per file type) and the Actions wheel, with the same selectors as today; the Convert wheel is out of scope. Dropping a sector on another swaps them, and the button rows stay.

## Global Constraints

- `DropHalo.Core` references no Windows API. `./build.sh` on macOS (with `DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH`) builds and tests Core and Engines, and must stay green after every Core task; read its exit code, not the piped output.
- Every new `L.T("…")` needs an entry in all six `src/DropHalo.App/Strings.{de,en,es,fr,ja,ko}.json`. Strings.en.json is kept sorted by code point. `CatalogTests` bans Chinese outside `L.T`, and tests never set `Loc.Current`.
- Keep these unchanged: the existing `dock: shown at the … edge` log line (`DockShownRegex` parses it), the `layout-sector-{i}` rows with 上移/下移 (`AppScenario.Settings.EditLayout` uses them), and the `发现新版本：{0}` / `已是最新版本：{0}` strings.
- `InstallUpdateAsync` keeps rechecking `CheckAsync(release.Source, …)` and requiring `latest == release`. Strict offline makes zero requests on every path.
- The App builds only on the Parallels VM, via `artifacts/vm/split-build.ps1`, from an export of the **working tree**. Stage scripts under `artifacts/vm` and run them with `-File`, wrapping prlctl in `timeout`.
- Git: branch `feat/dock-move-layout-disc-update-fallback`. Commit only after the user OKs commits; never push, open a PR or touch the CNB repo without asking. Before each commit, check the staged diff for the test laptop's user name and LAN address and expect neither. Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **A drag that starts over the remembered dock spot.** `DragMonitor` opens the dock at drag start. A topmost dock under the pointer would swallow the drag, so for that showing the dock opens at the edge instead (Task 1 test `Pointer_over_the_placed_dock_gives_null`).
2. **The saved spot is now off every monitor** (a monitor unplugged, the resolution lowered, or a negative-coordinate monitor gone). The dock is pulled fully inside the nearest work area (Task 1 tests).
3. **CNB answers but GitHub hangs** (no GitHub access from China). The check still ends within the GitHub fallback budget, with CNB's answer (Task 3 test `SlowGitHubDoesNotHoldBackTheCnbAnswer`).
4. **layout.json changes in the middle of a disc drag** (an AI agent edits it, or the file watcher reloads). The drag is cancelled and no swap is made from a stale index (Task 5 acceptance check).
5. **Header interactions other than a move.** Clicking 清空 or dragging a file row out must not move the window, and a file dropped on the header must still be added (Task 2 acceptance checks; the `dock` scenario covers 清空 and drag-out).

---

### Task 1: Core: `DockPosition` setting and placement

**Files:**
- Create: `src/DropHalo.Core/Settings/DockPosition.cs`
- Modify: `src/DropHalo.Core/Settings/AppSettings.cs` (new property after `Wheel`)
- Test: `tests/DropHalo.Core.Tests/DockPositionTests.cs`, `tests/DropHalo.Core.Tests/SettingsStoreTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record DockPosition(int Left, int Top)`, physical pixels in virtual-screen coordinates.
  - `public System.Drawing.Point? Place(System.Drawing.Size dock, System.Drawing.Rectangle work, System.Drawing.Point pointer)` returns this position moved just far enough for the dock to lie inside `work`. It returns null when `pointer` is inside the resulting rectangle.
  - `AppSettings.DockPosition : DockPosition?` (init, default null), which serialises as `"dockPosition"`.

- [ ] **Step 1: Write the failing tests.** All use dock size 320×400.

```csharp
[Theory]
[InlineData(100, 200, 0, 0, 1920, 1040, 100, 200)]         // inside: unchanged
[InlineData(3000, 2000, 0, 0, 1920, 1040, 1600, 640)]      // off right/bottom: pulled back
[InlineData(-2500, -50, -1920, 0, 1920, 1080, -1920, 0)]   // left monitor, off its top-left
[InlineData(500, 500, 0, 0, 300, 380, 0, 0)]               // work smaller than the dock: its top-left
public void Placement_lies_inside_the_work_area(int x, int y, int wl, int wt, int ww, int wh, int ex, int ey) =>
    new DockPosition(x, y).Place(new(320, 400), new(wl, wt, ww, wh), new(-9999, -9999)).Should().Be(new Point(ex, ey));

[Fact]
public void Pointer_over_the_placed_dock_gives_null() =>
    new DockPosition(100, 200).Place(new(320, 400), new(0, 0, 1920, 1040), new(150, 250)).Should().BeNull();
```

In `SettingsStoreTests`, add `Dock_position_round_trips_and_defaults_to_null`:
- A new store has `Current.DockPosition == null`.
- After `Update(s => s with { DockPosition = new(-1500, 300) })`, a reloaded store returns `new DockPosition(-1500, 300)`.
- After setting it back to null, a reload gives null.

- [ ] **Step 2:** Run `./build.sh`. Expect a compile failure: `DockPosition` does not exist.
- [ ] **Step 3: Implement.**
  - Use `Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - dock.Width))`, and the same for Top. The `Max` keeps Clamp valid when the dock is larger than the work area.
  - Then test `new Rectangle(placed, dock).Contains(pointer)`.
  - `SettingsJson` picks the new type up through `AppSettings`, so no `[JsonSerializable]` is needed.
- [ ] **Step 4:** Run `./build.sh`. Expect exit 0.
- [ ] **Step 5:** Commit `feat(settings): remember a dock position`.

### Task 2: App: drag the dock and reopen it where it was left

**Files:**
- Modify: `src/DropHalo.App/Dock/DockWindow.cs` (constructor at :56, header grid at ~:146, `Open` at :222)
- Modify: `src/DropHalo.App/App.xaml.cs:165` (pass `settings`)
- Modify: `src/DropHalo.App/Strings.*.json`
- Create: `tests/DropHalo.Acceptance/AppScenario.DockMove.cs`. Modify: `tests/DropHalo.Acceptance/Program.cs` (new `case "dockmove" when ArgValue(args, "--exe")`)

**Interfaces:**
- Consumes: `DockPosition.Place` and `AppSettings.DockPosition` (Task 1).
- Produces:
  - The constructor becomes `DockWindow(DispatcherQueue ui, DragMonitor monitor, Func<bool> wheelOpen, SettingsStore settings)`.
  - Log lines `dock: moved to {left},{top}`, `dock: position reset`, and `dock: shown at the saved position, {left},{top} {w}x{h} (pointer {x},{y})`.
  - The `_title` TextBlock gets AutomationId `dock-title`.

- [ ] **Step 1: Write the acceptance scenario `RunDockMove(string exe)`.**
  - Setup: a fresh profile `%TEMP%\DropHaloAcceptance\dockmove profile`, settings `{"version":2,"onboardingDone":true}`. Open the dock and keep it up with one file, using `StartDockDrag` + `DropOnDock`.
  - Check: drop a second file on the `dock-title` element; expect two rows.
  - Move: press on `dock-title`'s centre, run `Input.MoveSmooth` 20 steps by (-300, +120), then release.
    - Check: the GetWindowRect top-left moved by that delta ±2 px.
    - Check: the log has `dock: moved to`.
    - Check: settings.json `dockPosition` equals the new top-left.
  - Check: invoke 清空 and expect `dock: hidden (cleared)`. Start a drag whose start point is outside the saved rect, and expect `dock: shown at the saved position` with the same left/top.
    - `StartDockDrag` and `DropOnDock` cannot be reused here: they require the edge line (`DockShownRegex`). Add a local helper that waits for the saved-position line, then moves inside, wobbles (`Wobble`) and releases. Expect `dock: added 1 file(s)`.
  - Check, while that file keeps the dock up: double-click `dock-title` (two LeftDown/LeftUp pairs 80 ms apart). Expect `dock: position reset`, `dockPosition` absent or null, and the window against a screen edge. Then invoke 清空.
  - The setup uses the same open-and-drop path as the `dock` scenario, which already fails on main on the laptop. If a setup check fails, run `dock` on main in the same session before debugging.
- [ ] **Step 2: Implement the handle.**
  - Header column 0 becomes a `Border { Background = Transparent }` holding a horizontal StackPanel: `FontIcon { Glyph = "\uE76F" }` (GripperBarHorizontal) and `_title`. ToolTip: `L.T("拖动这里移动停靠栏，双击回到屏幕边缘")`.
  - `PointerPressed`:
    - Act only for a left button, pen or touch contact.
    - `CapturePointer`. Record `PInvoke.GetCursorPos` and `AppWindow.Position`.
  - `PointerMoved` while captured: `AppWindow.Move(start + (cursor − pressCursor))`. Use GetCursorPos rather than the event point, because the window moves under the pointer.
  - `PointerReleased` / `PointerCaptureLost` end the move. If the position changed:
    - Re-place: the monitor is `MonitorFromPoint(top-left, NEAREST)`. Size the dock as `Open` does at that monitor's DPI, then use `DockPosition.Place` with pointer `(int.MinValue, int.MinValue)`.
    - `MoveAndResize`, then save and log `dock: moved to`.
  - `DoubleTapped` on the handle: save null and log `dock: position reset`. Re-place the dock at the edge nearer its centre, keeping its vertical centre (the `Open` edge path).
- [ ] **Step 3: Restore in `Open`.**
  - With `_settings.Current.DockPosition is { } saved`, take the monitor from `MonitorFromPoint(saved, NEAREST)` and size the dock for that monitor.
  - If `saved.Place(size, work, pointer)` gives a point, use it and log the saved-position line. If it gives null, use today's edge code unchanged (log line included).
  - Saving: `_settings.Update(s => s with { DockPosition = … })`. Catch the exceptions `SettingsWindow.Save` catches, log them through `FileLog.Error`, and keep the window where it is.
- [ ] **Step 4:** Add the tooltip string to the six App catalogs.
- [ ] **Step 5:** Build the app and the driver on the VM (Task 6 Step 2), then run `dockmove`. Expect every check to pass.
- [ ] **Step 6:** Commit `feat(dock): drag the dock and reopen it where it was left`.

### Task 3: Core: `UpdateClient.CheckNewestAsync`

**Files:**
- Modify: `src/DropHalo.Core/Updates/UpdateClient.cs` (new method after `CheckAsync`; `CheckAsync` itself is unchanged)
- Test: `tests/DropHalo.Core.Tests/Updates/UpdateClientTests.cs`

**Interfaces:**
- Produces:

```csharp
public Task<UpdateRelease?> CheckNewestAsync(UpdateSource preferred, Version current, string architecture, bool packaged,
    PrivacySettings privacy, Action<UpdateSource, Exception>? sourceFailed, CancellationToken cancellationToken,
    TimeSpan? fallbackBudget = null)   // default 10 s, GitHub's own limit when CNB is preferred
```

Rules:
- `Offline(privacy)` runs first and throws before any request.
- `preferred == GitHub`: return `CheckAsync(GitHub, …)`. No cnb.cool request is made.
- `preferred == Cnb`: start both checks at once. The GitHub check gets a linked token that cancels after `fallbackBudget`.
  - Caller cancellation propagates as `OperationCanceledException`.
  - Any other failure of a source, including GitHub's budget running out, is reported through `sourceFailed(source, ex)` and that source counts as absent.
- Result: the newer of the two releases (compare `Version.Parse(r.Version)`), with CNB winning a tie. When both are absent, return null. When **both failed**, rethrow CNB's exception.

- [ ] **Step 1: Write the failing tests.**
  - Extend `Responses`:
    - `CnbTag` / `GitHubTag` (default `Tag`) choose the tag by request host.
    - `Fail : string?` names a host that answers 500.
    - `Hang : string?` names a host whose response awaits `Task.Delay(Timeout.Infinite, cancellationToken)`.
    - The served installer name follows the tag's version.
  - Tests:
    - `CnbBehindGitHubOffersGitHub`: Theory over current `0.1.5` and `0.1.6`, with CNB at v0.1.6 and GitHub at v0.1.7. Expect `Source == GitHub` and `Version == "0.1.7"`.
    - `EqualReleasesPreferCnb`: both at v0.1.7. Expect `Source == Cnb`.
    - `CnbFailureFallsBackToGitHub`: `Fail = "cnb.cool"`. Expect a GitHub release, and `sourceFailed` called once with `Cnb`.
    - `GitHubFailureKeepsCnbAndReportsIt`: `Fail = "api.github.com"`. Expect a CNB release, and `sourceFailed` called once with `GitHub`.
    - `BothFailingThrows`: both hosts fail. Expect `ThrowAsync<Exception>()`, with URLs for both hosts recorded.
    - `GitHubPreferenceNeverAsksCnb`: `handler.Urls` holds no `cnb.cool` host.
    - `SlowGitHubDoesNotHoldBackTheCnbAnswer`: `Hang = "api.github.com"` and `fallbackBudget = 200 ms`. Expect a CNB release within 5 s and `sourceFailed(GitHub, …)`.
    - `CallerCancellationIsNotAFallback`: a cancelled token gives `OperationCanceledException`, and `sourceFailed` is not called.
    - `StrictOfflineMakesNoRequestForTheCombinedCheck`: `InvalidOperationException` and `Urls` empty.
- [ ] **Step 2:** Run `./build.sh`. Expect a compile failure: `CheckNewestAsync` does not exist.
- [ ] **Step 3:** Implement as the rules state.
- [ ] **Step 4:** Run `./build.sh`. Expect exit 0 and the existing `UpdateClientTests` unchanged and green.
- [ ] **Step 5:** Commit `feat(updates): offer GitHub's release when the CNB mirror lags`.

### Task 4: App: use the combined check; docs

**Files:**
- Modify: `src/DropHalo.App/Settings/SettingsWindow.Updates.cs` (`CheckUpdatesAsync`, the `CheckAsync` call at ~:175)
- Modify: `src/DropHalo.App/Strings.*.json`
- Modify: `tests/DropHalo.Acceptance/AppScenario.Updates.cs` (the version parse in `Check`)
- Modify: `docs/updates.md`, `README.md`, `README_zh.md` (privacy and update paragraphs)

**Interfaces:**
- Consumes: `CheckNewestAsync` (Task 3).

- [ ] **Step 1: Switch the call.** Replace the call with `CheckNewestAsync(settings.UpdateSource, …, sourceFailed: (source, ex) => FileLog.Error($"update: {source} check failed", ex), token)`.
  - When `release is { } r && r.Source != settings.UpdateSource`, log `update: {settings.UpdateSource} has nothing newer or failed; offering {r.Version} from {r.Source}`. Set the status to `L.T("发现新版本：{0}（CNB 镜像还没同步，这次从 GitHub 下载）", r.Version)`. Its English must start with `New version available: {0}`.
  - Everything else stays as it is.
- [ ] **Step 2: Fix the acceptance parse.** In `AppScenario.Updates.Check`, read the version with `Regex.Match(status, @"\d+\.\d+\.\d+").Value` instead of slicing after the prefix.
- [ ] **Step 3: Update the docs.**
  - docs/updates.md: CNB-preferred checks also ask `api.github.com` (10 s limit), and the newer release wins, so a lagging mirror no longer strands users. The source of the offered release also supplies the download and the pre-install recheck.
  - README / README_zh: say that with CNB selected each check also contacts `api.github.com`, and that strict offline still makes no request.
- [ ] **Step 4:** Add the string to the six catalogs.
- [ ] **Step 5:** Build on the VM (Task 6 Step 2). Expect a clean build with CatalogTests green. Running `updates` stays optional (Task 6 Step 4).
- [ ] **Step 6:** Commit `feat(updates): fall back to GitHub in the update check`.

### Task 5: App: drag sectors on a disc in 轮盘排布

**Files:**
- Create: `src/DropHalo.App/Settings/LayoutDisc.cs`
- Modify: `src/DropHalo.App/Settings/SettingsWindow.cs`: the field next to `_layoutRows` (:91); the section at :288-294; `ShowLayout` (:518)
- Modify: `src/DropHalo.App/Strings.*.json`, `docs/04-roadmap.md:87`
- Create: `tests/DropHalo.Acceptance/AppScenario.LayoutDisc.cs`. Modify: `tests/DropHalo.Acceptance/Program.cs` (`case "layout" when ArgValue(args, "--exe")`)

**Interfaces:**
- Consumes: `WheelGeometry.StartAngle/SectorSweep/SectorAt/SectorCenter/InnerRadius/OuterRadius`, `WheelLayout.Swap`, and `EditLayout(Func<WheelLayout,WheelLayout>)`.
- Produces:

```csharp
internal sealed record DiscSector(string Label, bool IsGroup, bool IsEmpty);
internal sealed class LayoutDisc
{
    public UIElement View { get; }                    // Canvas, 2 × OuterRadius square, Transparent background
    public event Action<int, int>? Moved;             // (from, to), from != to, both 0..5
    public void Show(IReadOnlyList<DiscSector> sectors);  // exactly 6; redraws and cancels any drag in progress
}
```

- [ ] **Step 1: Write the acceptance scenario `RunLayoutDisc(string exe)`.**
  - Setup: fresh profile, settings `{"version":2,"onboardingDone":true,"language":"Chinese"}`, start the app with `--settings`. Bring the disc on screen: scroll the settings ScrollViewer through its UIA ScrollPattern until `layout-disc-0`'s bounding rectangle lies inside the window, at most 40 tries.
  - Check: drag the centre of `layout-disc-0` to the centre of `layout-disc-2` (LeftDown, `MoveSmooth` 15 steps, LeftUp). Expect `layout: changed` and the two labels swapped (UIA Name). Record `ImageTools(layout.json)` as L1.
  - Check: drag 2 → 0. Expect `layout: changed` and `ImageTools` = L1 with [0] and [2] swapped.
  - Check: drag 0 to the disc centre. Expect no `layout: changed` within 1.5 s and labels unchanged.
  - Check (Review Focus 4): press on 0 and move halfway to 2. Rewrite layout.json with [0] and [1] swapped. `App.xaml.cs:181` calls `layout.Watch()`, a FileSystemWatcher with a 300 ms debounce, so wait for `layout: changed`. Finish the move onto 2 and release. Expect layout.json to equal what the scenario wrote.
  - Check: 下移 still works (reuse `EditLayout(checks, log, profile)`).
- [ ] **Step 2: Implement `LayoutDisc`.**
  - Draw six wedge `Path`s from `WheelGeometry`, the same arcs `WheelWindow` draws, around the centre (OuterRadius, OuterRadius). Each gets a `TextBlock` label centred at `SectorCenter(i)`, MaxWidth 80 and trimmed, with AutomationId `layout-disc-{i}` and Name `"{i+1} · {Label}"`.
  - Empty sectors show `L.T("（空）")` at 50% opacity. Groups use a `DwAccentBrush` outline.
  - Pointer handlers sit on the Canvas:
    - Press on a non-empty sector (`SectorAt`) captures and dims the source.
    - Move highlights `SectorAt` as the target.
    - Release on another sector raises `Moved`. Release on the hub, outside the rim or on the source does nothing.
    - `PointerCaptureLost` cancels.
  - Set `ManipulationMode = TranslateX | TranslateY` so a touch drag is not taken by the page's ScrollViewer.
- [ ] **Step 3: Wire it into Settings.**
  - Place `_layoutDisc.View` between the selector row and `_layoutRows`, with a hint `L.T("拖动扇区可以互换位置；用键盘时请用下面的上移、下移。")`.
  - `ShowLayout` calls `_layoutDisc.Show` from the same `slots`: label as `LayoutRow` names it, group name for groups, `IsEmpty` for null.
  - In the constructor: `_layoutDisc.Moved += (a, b) => { var (m, k, ops) = (LayoutMode, LayoutKind, Operations()); EditLayout(l => l.Swap(m, k, ops, a, b)); }`.
- [ ] **Step 4:** Add the hint string to the six catalogs. Change roadmap :87 from 「没做拖拽和轮盘预览图」 to say the disc and drag-to-swap are done (the Convert wheel still has no layout).
- [ ] **Step 5:** Build on the VM (Task 6 Step 2), then run `layout`. Expect every check to pass.
- [ ] **Step 6:** Commit `feat(settings): drag sectors on a disc to rearrange a wheel`.

### Task 6: Build route, and comparison with main

Steps 2 and 3 are the build and run route that Tasks 2, 4 and 5 use. Run this whole task once more at the end, on the finished branch.

- [ ] **Step 1:** Run `./build.sh` on the Mac. Expect exit 0.
- [ ] **Step 2:** Export the working tree to `artifacts/vm/split/src/<name>` and run `split-build.ps1` for the app and for the acceptance driver. Expect no warnings-as-errors and the CatalogTests green.
- [ ] **Step 3:** In the VM console session, run the scenarios `dockmove`, `layout` and `dock` (runner `artifacts/vm/acceptance-kit/scen-run.ps1`; read `RESULT:` and the check lines, not `exit=`).
  - Expect every check in `dockmove` and `layout` to pass.
  - Run `dock` on main in the same session, and compare failures one for one.
  - If a start never reaches `ready:`, rerun once: about 27% of starts on the VM stall, main included.
- [ ] **Step 4:** Optional, only with the user's OK: run `updates` on the VM against real CNB and GitHub. With the build's version below 0.1.7, expect the GitHub fallback status, then cancel at the prompt.
- [ ] **Step 5:** Report the results to the user and ask about commits, push and a PR. Remind them that the CNB mirror itself still needs fixing on CNB (pipeline log or 「同步 GitHub 镜像」).
