using System.Diagnostics;
using DropHalo.App.Diagnostics;
using DropHalo.App.Overlay;
using DropHalo.Core.Conversion;
using DropHalo.Core.Input;
using DropHalo.Core.Jobs;
using DropHalo.Core.Settings;
using DropHalo.Core.Wheel;
using DropHalo.Shell;
using Microsoft.UI.Dispatching;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DropHalo.App;

/// <summary>
/// One drag's worth of wheel: a modifier tap, or a finger or pen held still, opens it at the pointer, DragEnter on the drop target host
/// says which files are being dragged, a drop on a sector enqueues the conversion or the file action.
/// The wheels are Convert, Tools and Actions; see <see cref="Modes"/>. Letters and digits typed on the
/// Convert wheel search every target the files can become, and the sectors show the best matches.
///
/// The wheel closes on Dropped / Canceled from the host, on the hook seeing the button go up (after a
/// grace period, because the hook sees the button-up before OLE delivers the drop), and on a watchdog
/// that polls the physical button, so a lost hook cannot leave the wheel on screen.
///
/// Threads: the Invoked events, Typed and PhaseChanged come from the hook thread, Entered / Hovered / Dropped /
/// Canceled from the host thread. Everything that touches a window is queued to the UI thread; the session
/// is only replaced on the UI thread. The host can publish a model only while its session is current.
/// </summary>
internal sealed class WheelController : IDisposable
{
    private static readonly TimeSpan IdleGrace = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan WatchdogPeriod = TimeSpan.FromMilliseconds(250);

    /// <summary>How long the pointer rests on a group sector before its ring opens, and how long it is off the group and its ring before the ring closes.</summary>
    private static readonly TimeSpan DwellDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RingCloseDelay = TimeSpan.FromMilliseconds(150);

    private readonly DispatcherQueue _ui;
    private readonly DragMonitor _monitor;
    private readonly DropTargetHost _host;
    private readonly WheelWindow _wheel;
    private readonly WheelBuilder _builder;
    private readonly JobQueue _queue;
    private readonly IRecentItems _recent;
    private readonly SettingsStore _settings;
    private readonly ModifierTapper _tapper;
    private readonly LongPress _longPress;
    private readonly TypeAhead _typeAhead;
    private readonly WheelDelay _idleGrace;
    private readonly DispatcherQueueTimer _watchdog;
    private readonly WheelDelay _dwell;
    private readonly WheelDelay _ringClose;
    private readonly LayoutStore? _layout;
    private readonly Lock _sessionGate = new();
    private volatile Session? _session;
    private int _buttonUpReadings;
    private bool _disposed;

    public WheelController(
        DispatcherQueue ui,
        DragMonitor monitor,
        DropTargetHost host,
        WheelWindow wheel,
        WheelBuilder builder,
        JobQueue queue,
        IRecentItems recent,
        SettingsStore settings,
        ModifierTapper tapper,
        LongPress longPress,
        TypeAhead typeAhead,
        LayoutStore? layout = null)
    {
        _ui = ui;
        _monitor = monitor;
        _host = host;
        _wheel = wheel;
        _builder = builder;
        _queue = queue;
        _recent = recent;
        _settings = settings;
        _tapper = tapper;
        _longPress = longPress;
        _typeAhead = typeAhead;
        _layout = layout;

        _idleGrace = new(ui, IdleGrace);
        _watchdog = ui.CreateTimer();
        _watchdog.Interval = WatchdogPeriod;
        _watchdog.Tick += (_, _) => CheckButton();
        _dwell = new(ui, DwellDelay);
        _ringClose = new(ui, RingCloseDelay);

        _tapper.Invoked += OnTapped;
        _longPress.Invoked += OnHeld;
        _typeAhead.Typed += OnTyped;
        _monitor.PhaseChanged += OnPhaseChanged;
        _host.Entered += OnEntered;
        _host.Noted += OnNoted;
        _host.Hovered += OnHovered;
        _host.HubReached += OnHubReached;
        _host.Dropped += OnDropped;
        _host.Canceled += OnCanceled;
    }

    /// <summary>True from the moment the wheel opens until it closes; any thread.</summary>
    public bool IsOpen => _session is not null;

    /// <summary>The wheels, in the order Settings lists their keys.</summary>
    public static IReadOnlyList<WheelMode> Modes { get; } = [WheelMode.Convert, WheelMode.Tools, WheelMode.Actions];

    public void Dispose()
    {
        _disposed = true;
        _tapper.Invoked -= OnTapped;
        _longPress.Invoked -= OnHeld;
        _typeAhead.Typed -= OnTyped;
        _monitor.PhaseChanged -= OnPhaseChanged;
        _host.Entered -= OnEntered;
        _host.Noted -= OnNoted;
        _host.Hovered -= OnHovered;
        _host.HubReached -= OnHubReached;
        _host.Dropped -= OnDropped;
        _host.Canceled -= OnCanceled;
        Close("shutdown");
    }

    // ---- hook thread ------------------------------------------------------------------------------

    private void OnTapped(WheelMode mode) => OpenFromHook(mode, "tap");

    private void OnHeld(WheelMode mode) => OpenFromHook(mode, "long press");

    private void OpenFromHook(WheelMode mode, string by)
    {
        var tappedAt = Stopwatch.GetTimestamp();
        var point = _monitor.LastPoint;
        var dragId = _monitor.DragId;
        _ui.TryEnqueue(() => Open(mode, point, dragId, tappedAt, by));
    }

    private void OnTyped(char c)
    {
        var session = _session;
        _ui.TryEnqueue(() =>
        {
            if (session is not null && _session == session && session.DragId == _monitor.DragId)
            {
                Type(session, c);
            }
        });
    }

    private void OnPhaseChanged(DragPhase phase)
    {
        if (phase == DragPhase.Idle)
        {
            var dragId = _monitor.DragId;
            _ui.TryEnqueue(() =>
            {
                if (_session is { } session && session.DragId == dragId)
                {
                    _idleGrace.Start(() => CloseShowing(session.View, "button up without a drop"));
                }
            });
        }
    }

    // ---- host thread ------------------------------------------------------------------------------

    private void OnEntered(WheelPlacement placement, IReadOnlyList<string> paths)
    {
        Session session;
        lock (_sessionGate)
        {
            if (_session is not { } current || current.View.Showing != placement.Showing || paths.Count == 0)
            {
                return;
            }

            session = current;
            session.Paths = paths;
        }

        // Built here, before DragEnter computes its first effect, so the very first DragOver already
        // hit-tests against the real enabled sectors. Anything typed since the tap is searched already.
        if (Rebuild(session) is not { } model)
        {
            return;
        }

        FileLog.Info($"wheel: {paths.Count} file(s) entered +{Stopwatch.GetElapsedTime(session.TappedAt).TotalMilliseconds:F0} ms after {session.By}, slots {Slots(model)}");
        _ui.TryEnqueue(() =>
        {
            // A search typed meanwhile has already shown a newer model.
            if (_session == session && session.View.Model == model)
            {
                _wheel.SetModel(model);
            }
        });
    }

    private void OnNoted(string note) => FileLog.Info($"wheel: {note}");

    private void OnHovered(WheelPlacement placement, WheelHit hit) => _ui.TryEnqueue(() =>
    {
        if (_session is { } session && session.View.Showing == placement.Showing)
        {
            Hover(session, session.View, hit);
        }
    });

    private void OnHubReached(WheelPlacement placement)
    {
        _ui.TryEnqueue(() =>
        {
            if (_session?.View.Showing == placement.Showing)
            {
                FileLog.Info("wheel: pointer reached the hub, sectors are live");
                _wheel.SetHubHint(false);
            }
        });
    }

    private void OnDropped(WheelPlacement placement, IReadOnlyList<string> paths, WheelHit hit)
    {
        var slot = SlotUnder(placement, hit);
        _ui.TryEnqueue(() =>
        {
            if (_disposed)
            {
                return;
            }

            CloseShowing(placement, $"dropped on {hit}");
            if (slot is null || placement.Settings is not { } settings || placement.Model is not { } model)
            {
                FileLog.Error($"wheel: drop on {hit} without a slot");
                return;
            }

            if (slot.IsGroup)
            {
                // A group sector is never enabled, so the host refuses the drop before it gets here; defensive.
                FileLog.Info($"wheel: drop on group {hit} ({slot.Label}) ignored");
                return;
            }

            var label = paths.Count == 1 ? $"{Path.GetFileName(paths[0])} → {slot.Label}" : L.T("{0} 项 → {1}", paths.Count, slot.Label);

            // File actions never count against the daily quota, and only conversions have recent items.
            _queue.Enqueue(settings.Request(paths, slot.ItemId), label, slot.Operation, countsTowardQuota: model.Mode != WheelMode.Actions);
            if (model.Mode == WheelMode.Convert)
            {
                _recent.Touch(FormatRegistry.FromPath(paths[0])?.Kind ?? MediaKind.Unknown, model.Mode, slot.ItemId);
            }

            FileLog.Info($"wheel: enqueued {label}");
        });
    }

    private void OnCanceled(WheelPlacement placement) => _ui.TryEnqueue(() => CloseShowing(placement, "canceled"));

    /// <summary>The slot a hit lands on: a sector's, or an item of the ring open at the time.</summary>
    private static WheelSlot? SlotUnder(WheelPlacement placement, WheelHit hit)
    {
        if (placement.Model is not { } model)
        {
            return null;
        }

        return hit.Ring switch
        {
            WheelRing.Sector => model.SlotAt(hit.Index),
            WheelRing.Child when placement.Ring is { } ring => model.SlotAt(ring.Parent)?.Children is { } children && hit.Index >= 0 && hit.Index < children.Count ? children[hit.Index] : null,
            _ => null,
        };
    }

    // ---- UI thread --------------------------------------------------------------------------------

    /// <param name="by">What opened it, for the log: "tap" or "long press".</param>
    private void Open(WheelMode mode, (int X, int Y) point, long dragId, long tappedAt, string by)
    {
        if (_disposed || _monitor.Phase == DragPhase.Idle || _monitor.DragId != dragId)
        {
            return;
        }

        if (_session is { } previous && previous.DragId != dragId)
        {
            Close("new drag started");
        }

        if (_session is { } current)
        {
            if (current.Mode != mode)
            {
                Switch(current, mode, tappedAt, by);
            }

            return;
        }

        // Near an edge the wheel is shifted away from the pointer and may open with a sector under it.
        // Then the sectors wait until the pointer has been on the hub, so an immediate release cancels.
        // A wheel with groups is placed by its ring's footprint, so an open ring stays on screen too.
        var ring = _layout?.HasGroups(mode) == true;
        var (x, y, scale) = Place(point, WheelWindow.SizeFor(ring));
        var onHub = WheelGeometry.OnHub((point.X - x) / scale, (point.Y - y) / scale);
        var placement = new WheelPlacement(x, y, scale, new bool[WheelModel.SlotCount], onHub) { Settings = _settings.Current };
        lock (_sessionGate)
        {
            _session = new Session(mode, placement, dragId, tappedAt, by) { RingSized = ring };
        }

        _typeAhead.Listening = mode == WheelMode.Convert;

        // Order matters: the host has to end up above the wheel. If the wheel were on top, OLE would see
        // the drag leave the host and the host would report a cancel.
        _wheel.SetHubHint(!onHub);
        _wheel.ShowAt(x, y, scale, ring);
        _host.Show(placement);
        _buttonUpReadings = 0;
        _watchdog.Start();
        FileLog.Info($"wheel: shown +{Stopwatch.GetElapsedTime(tappedAt).TotalMilliseconds:F1} ms after {by}, centre {x},{y} scale {scale:0.##} (pointer {point.X},{point.Y}{(onHub ? "" : ", off the hub: sectors wait for it")})");
    }

    /// <summary>
    /// Another wheel's keys tapped while one is open: the same drag and the same centre show the other wheel's
    /// items. The window keeps its place, so the sectors stay under the hand that knows them; it only grows or
    /// shrinks for the ring, which near an edge may then reach off the screen. The host keeps its gate: sectors
    /// that were waiting for the pointer to visit the hub still wait.
    /// </summary>
    private void Switch(Session current, WheelMode mode, long tappedAt, string by)
    {
        _dwell.Stop();
        _ringClose.Stop();
        var ring = _layout?.HasGroups(mode) == true;
        var placement = current.View with { Model = null, Ring = null, EnabledSectors = new bool[WheelModel.SlotCount] };
        Session next;
        lock (_sessionGate)
        {
            next = new Session(mode, placement, current.DragId, tappedAt, by) { Paths = current.Paths, RingSized = ring };
            _session = next;
        }

        _typeAhead.Listening = mode == WheelMode.Convert;
        _wheel.SetRing(-1, null);
        if (ring != current.RingSized)
        {
            _wheel.Resize(placement.CenterX, placement.CenterY, placement.Scale, ring);
        }

        var model = Rebuild(next);
        if (model is null)
        {
            // No files yet: the host still has to forget the old wheel's sectors and ring.
            lock (next.Gate)
            {
                if (next.View.Model is null)
                {
                    Publish(next, placement);
                }
                else
                {
                    model = next.View.Model;
                }
            }
        }

        _wheel.SetModel(model);
        FileLog.Info($"wheel: switched to {mode} +{Stopwatch.GetElapsedTime(tappedAt).TotalMilliseconds:F1} ms after {by}, slots {(model is null ? "none yet" : Slots(model))}");
    }

    private void Close(string why)
    {
        _idleGrace.Stop();
        _watchdog.Stop();
        _dwell.Stop();
        _ringClose.Stop();
        lock (_sessionGate)
        {
            if (_session is null)
            {
                return;
            }

            _session = null;
            _host.Hide();
        }

        _typeAhead.Listening = false;
        _wheel.Hide();
        FileLog.Info($"wheel: closed ({why})");
    }

    private void CloseShowing(WheelPlacement placement, string why)
    {
        if (_session?.View.Showing == placement.Showing)
        {
            Close(why);
        }
    }

    private void Type(Session session, char c)
    {
        if (session.Mode != WheelMode.Convert)
        {
            return;
        }

        WheelModel? model;
        lock (session.Gate)
        {
            var query = session.Query;
            var next = c == TypeAhead.Backspace ? query[..Math.Max(0, query.Length - 1)] : query.Length < WheelModel.MaxQuery ? query + c : query;
            if (next == query)
            {
                return;
            }

            session.Query = next;
            model = Rebuild(session);
        }

        // Before the files are known there is nothing to search yet; DragEnter builds with the query.
        if (model is not null)
        {
            _wheel.SetModel(model);
            FileLog.Info($"wheel: search \"{session.Query}\", slots {Slots(model)}");
        }
    }

    /// <summary>
    /// The pointer is somewhere new. Resting on a group sector opens its ring after <see cref="DwellDelay"/>;
    /// leaving the group and its ring for anywhere but the hub closes the ring after <see cref="RingCloseDelay"/>,
    /// so a pointer cutting across the gap between them does not shut it; the hub closes it at once.
    /// </summary>
    private void Hover(Session session, WheelPlacement view, WheelHit hit)
    {
        _wheel.SetHovered(hit);
        var openGroup = view.Ring?.Parent ?? -1;
        var group = hit.Ring == WheelRing.Sector && view.Model?.SlotAt(hit.Index) is { IsGroup: true } ? hit.Index : -1;
        var stays = hit.Ring == WheelRing.Child || (hit.Ring == WheelRing.Sector && hit.Index == openGroup);
        _dwell.Stop();
        _ringClose.Stop();
        if (hit.Ring == WheelRing.Hub)
        {
            CloseRing(session, "pointer on the hub");
            return;
        }

        if (group >= 0 && group != openGroup)
        {
            _dwell.Start(() => OpenRing(session, group));
        }

        if (openGroup >= 0 && !stays)
        {
            _ringClose.Start(() => CloseRing(session, "pointer left it"));
        }
    }

    private void OpenRing(Session session, int index)
    {
        lock (session.Gate)
        {
            var view = session.View;
            if (view.Model?.SlotAt(index) is not { Children: { } children } slot
                || !Publish(session, view with { Ring = new RingPlacement(index, [.. children.Select(c => c.Availability.IsAvailable)]) }))
            {
                return;
            }

            _wheel.SetRing(index, children);
            FileLog.Info($"wheel: ring open on sector {index} ({slot.Label}): {string.Join(",", children.Select(c => $"{c.Index}={c.ItemId}{(c.Availability.IsAvailable ? "" : "(off)")}"))}");
        }
    }

    private void CloseRing(Session session, string why)
    {
        lock (session.Gate)
        {
            if (session.View.Ring is null || !Publish(session, session.View with { Ring = null }))
            {
                return;
            }
        }

        _wheel.SetRing(-1, null);
        FileLog.Info($"wheel: ring closed ({why})");
    }

    /// <summary>Publishes under the same lock that replaces or closes the session.</summary>
    private bool Publish(Session session, WheelPlacement view)
    {
        lock (_sessionGate)
        {
            if (_session != session)
            {
                return false;
            }

            session.View = view;
            _host.Show(view);
            return true;
        }
    }

    /// <summary>
    /// Builds the session's model for its files and search and tells the host which sectors are live. Null
    /// before the files are known. Any thread; the lock keeps the model and the host's sectors from two
    /// builds from interleaving.
    /// </summary>
    private WheelModel? Rebuild(Session session)
    {
        lock (session.Gate)
        {
            if (session.Paths is not { } paths)
            {
                return null;
            }

            var view = session.View;
            var model = _builder.Build(paths, session.Mode, view.Settings!, session.Query);

            // A group sector is never enabled: a release on it cancels, and its ring is where the drop goes.
            var enabled = Enumerable.Range(0, WheelModel.SlotCount).Select(i => model.SlotAt(i) is { Availability.IsAvailable: true, IsGroup: false }).ToArray();
            var ring = view.Ring is { } oldRing && model.SlotAt(oldRing.Parent)?.Children is { } children
                ? new RingPlacement(oldRing.Parent, [.. children.Select(c => c.Availability.IsAvailable)]) : null;

            return Publish(session, view with { Model = model, EnabledSectors = enabled, Ring = ring }) ? model : null;
        }
    }

    /// <summary>"0=png,1=webp,2=gif(off)", the form the acceptance run reads.</summary>
    private static string Slots(WheelModel model)
        => model.Slots.Count == 0 ? "none" : string.Join(",", model.Slots.Select(s => $"{s.Index}={s.ItemId}{(s.Availability.IsAvailable ? "" : "(off)")}"));

    private void CheckButton()
    {
        if (_session is not { } session)
        {
            return;
        }

        if (session.DragId != _monitor.DragId)
        {
            Close("new drag started");
            return;
        }

        var button = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_SWAPBUTTON) != 0 ? VIRTUAL_KEY.VK_RBUTTON : VIRTUAL_KEY.VK_LBUTTON;
        var down = (PInvoke.GetAsyncKeyState((int)button) & 0x8000) != 0;
        _buttonUpReadings = down ? 0 : _buttonUpReadings + 1;
        if (_buttonUpReadings >= 2)
        {
            Close("watchdog: button is up");
        }
    }

    /// <summary>Centre on the pointer, pulled inside the work area of the pointer's monitor by the window's size in DIPs.</summary>
    internal static (int X, int Y, double Scale) Place((int X, int Y) point, double sizeDip)
    {
        var monitor = PInvoke.MonitorFromPoint(new System.Drawing.Point(point.X, point.Y), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var scale = PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpi, out _).Succeeded ? dpi / 96.0 : 1.0;
        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!PInvoke.GetMonitorInfo(monitor, ref info))
        {
            return (point.X, point.Y, scale);
        }

        var half = (int)Math.Ceiling(sizeDip * scale / 2);
        var work = info.rcWork;
        return (
            Math.Clamp(point.X, work.left + half, Math.Max(work.left + half, work.right - half)),
            Math.Clamp(point.Y, work.top + half, Math.Max(work.top + half, work.bottom - half)),
            scale);
    }

    private sealed class Session(WheelMode mode, WheelPlacement placement, long dragId, long tappedAt, string by)
    {
        public WheelMode Mode { get; } = mode;
        public long DragId { get; } = dragId;
        public long TappedAt { get; } = tappedAt;
        public string By { get; } = by;

        public volatile WheelPlacement View = placement;

        /// <summary>The files, once DragEnter said which.</summary>
        public volatile IReadOnlyList<string>? Paths;

        /// <summary>Whether the window was sized for a ring when this wheel was shown.</summary>
        public bool RingSized { get; init; }

        /// <summary>Guards the query and serializes builds and ring changes for this mode.</summary>
        public Lock Gate { get; } = new();

        public string Query { get; set; } = "";
    }

    /// <summary>One UI deadline. A late native tick cannot consume a newly scheduled action early.</summary>
    private sealed class WheelDelay
    {
        private readonly DispatcherQueueTimer _timer;
        private readonly TimeSpan _delay;
        private long _started;
        private Action? _fire;

        public WheelDelay(DispatcherQueue ui, TimeSpan delay)
        {
            _delay = delay;
            _timer = ui.CreateTimer();
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => Tick();
        }

        public void Start(Action fire)
        {
            _timer.Stop();
            _fire = fire;
            _started = Stopwatch.GetTimestamp();
            _timer.Interval = _delay;
            _timer.Start();
        }

        public void Stop()
        {
            _fire = null;
            _timer.Stop();
        }

        private void Tick()
        {
            if (_fire is not { } fire)
            {
                return;
            }

            var remaining = _delay - Stopwatch.GetElapsedTime(_started);
            if (remaining > TimeSpan.Zero)
            {
                _timer.Interval = remaining;
                _timer.Start();
                return;
            }

            _fire = null;
            fire();
        }
    }
}
