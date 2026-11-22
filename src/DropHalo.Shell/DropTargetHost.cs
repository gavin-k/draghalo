using System.Runtime.InteropServices;
using DropHalo.Core.Settings;
using DropHalo.Core.Wheel;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Com;
using Windows.Win32.System.Ole;
using Windows.Win32.System.SystemServices;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DropHalo.Shell;

/// <summary>Where the wheel is on screen. Centre in physical pixels; <paramref name="Scale"/> is the monitor DPI / 96.</summary>
/// <param name="EnabledSectors">Six flags, sector 0 at 12 o'clock, clockwise (see <see cref="WheelGeometry"/>).</param>
/// <param name="StartsOnHub">False when the wheel was shifted away from the pointer; see <see cref="SectorGate"/>.</param>
/// <param name="Ring">The ring open on a group sector, if one is; its items are hit-tested too.</param>
public sealed record WheelPlacement(int CenterX, int CenterY, double Scale, IReadOnlyList<bool> EnabledSectors, bool StartsOnHub = true, RingPlacement? Ring = null)
{
    /// <summary>Fresh for each showing; record copies retain it when the model or ring changes.</summary>
    public object Showing { get; } = new();

    /// <summary>The drop decision is made against this exact model and settings snapshot.</summary>
    public WheelModel? Model { get; init; }
    public AppSettings? Settings { get; init; }

    /// <summary>What is under a screen point, enabled or not; no sector or ring item while <paramref name="gate"/> is shut.</summary>
    public WheelHit HitAt(int x, int y, SectorGate gate, out bool opened) => gate.HitAt((x - CenterX) / Scale, (y - CenterY) / Scale, Ring, out opened);

    public bool IsEnabled(WheelHit hit) => hit.Ring switch
    {
        WheelRing.Sector => hit.Index >= 0 && hit.Index < EnabledSectors.Count && EnabledSectors[hit.Index],
        WheelRing.Child => Ring?.IsEnabled(hit.Index) == true,
        _ => false,
    };
}

/// <summary>
/// The OLE drop target behind the wheel (ADR-0004). A layered, top-most, never-activated popup that
/// covers the whole virtual screen at alpha 1 (alpha 0 is not hit-tested), created once on its own STA
/// thread and registered with RegisterDragDrop once; the wheel only shows and hides it. Covering the
/// whole screen means a release anywhere lands here, so a release off the wheel is a cancel rather
/// than a drop into whatever window happens to be underneath.
///
/// Events are raised on the host thread from inside the OLE callbacks: keep handlers short and
/// marshal to the UI thread.
/// </summary>
public sealed unsafe class DropTargetHost : IDisposable
{
    private const uint PlacementChanged = PInvoke.WM_APP + 1;
    private const uint DestroyHost = PInvoke.WM_APP + 2;
    private const string ClassName = "DropHalo.DropTargetHost";

    private readonly Target _target;
    private Thread? _thread;
    private HWND _hwnd;
    private ushort _classAtom;
    private WNDPROC? _wndProc;
    private volatile WheelPlacement? _placement;
    private object? _shown; // host thread only

    public DropTargetHost() => _target = new Target(this);

    /// <summary>A drag entered while the host was shown. Paths are empty when the data names no file.</summary>
    public event Action<WheelPlacement, IReadOnlyList<string>>? Entered;

    /// <summary>
    /// A line for the log about the drag that entered: its data had no CF_HDROP, and either the Shell's item
    /// list named the files instead or nothing did, with the formats the data offers.
    /// </summary>
    public event Action<string>? Noted;

    /// <summary>The sector or ring item under the pointer changed, enabled or not, so a disabled one can say why.</summary>
    public event Action<WheelPlacement, WheelHit>? Hovered;

    /// <summary>The pointer reached the hub of a wheel that opened off-centre, so sectors respond from now on.</summary>
    public event Action<WheelPlacement>? HubReached;

    /// <summary>Files were released on an enabled sector or ring item.</summary>
    public event Action<WheelPlacement, IReadOnlyList<string>, WheelHit>? Dropped;

    /// <summary>The drag left without dropping on a sector: released elsewhere, Esc, or the source gave up.</summary>
    public event Action<WheelPlacement>? Canceled;

    public bool IsVisible => _placement is not null;

    /// <summary>Creates the window and registers it as a drop target. Blocks until done; throws if registration fails.</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready, ref failure)) { IsBackground = true, Name = "DropTargetHost" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        if (failure is not null)
        {
            _thread = null;
            throw failure;
        }
    }

    /// <summary>Show the host over the whole virtual screen. Call after the wheel is visible.</summary>
    public void Show(WheelPlacement placement)
    {
        _placement = placement;
        PInvoke.PostMessage(_hwnd, PlacementChanged, default, default);
    }

    public void Hide()
    {
        _placement = null;
        PInvoke.PostMessage(_hwnd, PlacementChanged, default, default);
    }

    public void Dispose()
    {
        if (_thread is null)
        {
            return;
        }

        PInvoke.PostMessage(_hwnd, DestroyHost, default, default);
        _thread.Join(2000);
        _thread = null;
    }

    private void Run(ManualResetEventSlim ready, ref Exception? failure)
    {
        try
        {
            var hr = PInvoke.OleInitialize(null);
            Marshal.ThrowExceptionForHR(hr);
            _hwnd = CreateWindow();
            hr = PInvoke.RegisterDragDrop(_hwnd, _target);
            Marshal.ThrowExceptionForHR(hr);
        }
        catch (Exception ex)
        {
            failure = ex;
            ready.Set();
            return;
        }

        ready.Set();
        while (PInvoke.GetMessage(out var msg, default, 0, 0))
        {
            PInvoke.TranslateMessage(in msg);
            PInvoke.DispatchMessage(in msg);
        }

        if (_classAtom != 0)
        {
            using var module = PInvoke.GetModuleHandle((string?)null);
            PInvoke.UnregisterClass(ClassName, module);
            _classAtom = 0;
        }

        PInvoke.OleUninitialize();
    }

    private HWND CreateWindow()
    {
        _wndProc = WndProc;
        using var module = PInvoke.GetModuleHandle((string?)null);
        fixed (char* className = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _wndProc,
                hInstance = (HINSTANCE)module.DangerousGetHandle(),
                hbrBackground = (HBRUSH)PInvoke.GetStockObject(GET_STOCK_OBJECT_FLAGS.BLACK_BRUSH).Value,
                lpszClassName = className,
            };
            _classAtom = PInvoke.RegisterClassEx(in wc);
            if (_classAtom == 0)
            {
                throw new System.ComponentModel.Win32Exception();
            }
        }

        var hwnd = PInvoke.CreateWindowEx(
            WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
            ClassName,
            "DropHalo drop target",
            WINDOW_STYLE.WS_POPUP,
            0, 0, 1, 1,
            default, null, module, null);
        if (hwnd.IsNull)
        {
            throw new System.ComponentModel.Win32Exception();
        }

        // Alpha 1 of 255 over black: invisible, but still hit-tested (alpha 0 is not).
        PInvoke.SetLayeredWindowAttributes(hwnd, new COLORREF(0), 1, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
        return hwnd;
    }

    private void SyncPlacement(HWND hwnd)
    {
        var placement = _placement;
        var sameShowing = _shown is not null && _shown == placement?.Showing;
        if (!sameShowing)
        {
            _target.Reset();
            if (_shown is not null)
            {
                PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_HIDE);
            }

            _shown = placement?.Showing;
        }

        if (placement is null)
        {
            return;
        }

        PInvoke.SetWindowPos(
            hwnd,
            HWND.HWND_TOPMOST,
            PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN),
            PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN),
            PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN),
            PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN),
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);

        if (sameShowing && _target.IsEntered(placement.Showing))
        {
            // Nudge OLE to re-run DragOver when sectors or an outer ring change under a stationary pointer.
            PInvoke.SendInput([new INPUT { type = INPUT_TYPE.INPUT_MOUSE }], sizeof(INPUT));
        }
    }

    private LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case PlacementChanged:
                SyncPlacement(hwnd);
                return default;
            case DestroyHost:
                PInvoke.DestroyWindow(hwnd);
                return default;
            case PInvoke.WM_DESTROY:
                PInvoke.RevokeDragDrop(hwnd);
                PInvoke.PostQuitMessage(0);
                return default;
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>The paths in CF_HDROP, else those of the Shell's item list (a drag out of a WinUI app may offer only that).</summary>
    private List<string> FilePaths(IDataObject data)
    {
        var paths = DropPaths(data);
        if (paths.Count > 0)
        {
            return paths;
        }

        paths = ItemPaths(data);
        Noted?.Invoke(paths.Count > 0
            ? $"no CF_HDROP, {paths.Count} path(s) from the Shell's item list"
            : $"no file paths in the drag, which offers {FormatNames(data)}");
        return paths;
    }

    private static List<string> DropPaths(IDataObject data)
    {
        var format = new FORMATETC
        {
            cfFormat = (ushort)CLIPBOARD_FORMAT.CF_HDROP,
            dwAspect = (uint)DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = (uint)TYMED.TYMED_HGLOBAL,
        };
        try
        {
            data.GetData(&format, out var medium);
            try
            {
                var drop = (HDROP)medium.u.hGlobal.Value;
                var count = PInvoke.DragQueryFile(drop, 0xFFFFFFFF, null, 0);
                var paths = new List<string>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    var length = PInvoke.DragQueryFile(drop, i, null, 0);
                    var buffer = new char[length + 1];
                    fixed (char* p = buffer)
                    {
                        length = PInvoke.DragQueryFile(drop, i, p, length + 1);
                    }

                    paths.Add(new string(buffer, 0, (int)length));
                }

                return paths;
            }
            finally
            {
                PInvoke.ReleaseStgMedium(ref medium);
            }
        }
        catch (COMException)
        {
            return [];
        }
    }

    private static List<string> ItemPaths(IDataObject data)
    {
        var paths = new List<string>();
        try
        {
            PInvoke.SHCreateShellItemArrayFromDataObject((System.Runtime.InteropServices.ComTypes.IDataObject)data, typeof(IShellItemArray).GUID, out var created).ThrowOnFailure();
            var items = (IShellItemArray)created;
            try
            {
                items.GetCount(out var count);
                for (uint i = 0; i < count; i++)
                {
                    items.GetItemAt(i, out var item);
                    try
                    {
                        item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var name);
                        try
                        {
                            paths.Add(name.ToString());
                        }
                        finally
                        {
                            Marshal.FreeCoTaskMem((nint)name.Value);
                        }
                    }
                    catch (COMException)
                    {
                        // Not in the file system: a mail attachment, a phone's photo.
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(items);
            }
        }
        catch (COMException)
        {
        }

        return paths;
    }

    /// <summary>"CF_UNICODETEXT(13), FileGroupDescriptorW, …": what the data can be read as, for the log.</summary>
    private static string FormatNames(IDataObject data)
    {
        var names = new List<string>();
        try
        {
            data.EnumFormatEtc((uint)DATADIR.DATADIR_GET, out var formats);
            try
            {
                var format = default(FORMATETC);
                uint fetched;
                Span<char> name = stackalloc char[128];
                while (formats.Next(1, &format, &fetched).Value == 0 && fetched == 1) // S_FALSE once the list runs out
                {
                    try
                    {
                        fixed (char* p = name)
                        {
                            var length = PInvoke.GetClipboardFormatName(format.cfFormat, p, name.Length);
                            names.Add(length > 0 ? new string(p, 0, length) : $"{(CLIPBOARD_FORMAT)format.cfFormat}({format.cfFormat})");
                        }
                    }
                    finally
                    {
                        if (format.ptd is not null)
                        {
                            Marshal.FreeCoTaskMem((nint)format.ptd);
                            format.ptd = null;
                        }
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(formats);
            }
        }
        catch (COMException ex)
        {
            names.Add($"(listing failed: 0x{ex.HResult:X8})");
        }

        return names.Count == 0 ? "no format" : string.Join(", ", names.Distinct());
    }

    /// <summary>The IDropTarget OLE calls. Runs on the host thread.</summary>
    private sealed class Target(DropTargetHost host) : IDropTarget
    {
        private List<string> _paths = [];
        private WheelHit _hovered = WheelHit.None;
        private WheelPlacement? _hoveredPlacement;
        private WheelPlacement? _entered;
        private object? _finishedShowing;
        private SectorGate? _gate;

        public bool IsEntered(object showing) => _entered?.Showing == showing;

        /// <summary>Forget the drag. Runs when the host hides, so the next showing gets a fresh gate.</summary>
        public void Reset()
        {
            _paths = [];
            _hovered = WheelHit.None;
            _hoveredPlacement = null;
            _entered = null;
            _gate = null;
        }

        public void DragEnter(IDataObject pDataObj, MODIFIERKEYS_FLAGS grfKeyState, POINTL pt, DROPEFFECT* pdwEffect)
        {
            if (host._placement is { } pending && host._shown != pending.Showing)
            {
                host.SyncPlacement(host._hwnd);
            }

            if (host._placement is not { } placement || host._shown != placement.Showing || _finishedShowing == placement.Showing)
            {
                *pdwEffect = DROPEFFECT.DROPEFFECT_NONE;
                return;
            }

            _paths = host.FilePaths(pDataObj);
            _entered = placement;
            _hovered = WheelHit.None;
            _hoveredPlacement = null;
            host.Entered?.Invoke(placement, _paths);
            *pdwEffect = Effect(pt, *pdwEffect, out _, out _);
        }

        public void DragOver(MODIFIERKEYS_FLAGS grfKeyState, POINTL pt, DROPEFFECT* pdwEffect) =>
            *pdwEffect = Effect(pt, *pdwEffect, out _, out _);

        public void DragLeave()
        {
            if (_entered is { } entered)
            {
                _finishedShowing = entered.Showing;
                Reset();
                host.Canceled?.Invoke(entered);
            }
        }

        public void Drop(IDataObject pDataObj, MODIFIERKEYS_FLAGS grfKeyState, POINTL pt, DROPEFFECT* pdwEffect)
        {
            *pdwEffect = Effect(pt, *pdwEffect, out var placement, out var hit);
            var entered = _entered;
            var paths = _paths;
            if (entered is not null)
            {
                _finishedShowing = entered.Showing;
            }

            Reset();
            if (*pdwEffect == DROPEFFECT.DROPEFFECT_COPY && placement is not null)
            {
                host.Dropped?.Invoke(placement, paths, hit);
            }
            else if (entered is not null)
            {
                host.Canceled?.Invoke(entered);
            }
        }

        /// <summary>
        /// Copy over an enabled sector when the source allows it, otherwise none. Never Move: the
        /// source deletes the original after a Move.
        /// </summary>
        private DROPEFFECT Effect(POINTL pt, DROPEFFECT allowed, out WheelPlacement? placement, out WheelHit hit)
        {
            placement = host._placement;
            hit = WheelHit.None;
            if (placement is null || _entered?.Showing != placement.Showing)
            {
                return DROPEFFECT.DROPEFFECT_NONE;
            }

            _gate ??= new SectorGate(placement.StartsOnHub);
            hit = placement.HitAt(pt.x, pt.y, _gate, out var opened);
            if (opened)
            {
                host.HubReached?.Invoke(placement);
            }

            if (hit != _hovered || !ReferenceEquals(placement, _hoveredPlacement))
            {
                _hovered = hit;
                _hoveredPlacement = placement;
                host.Hovered?.Invoke(placement, hit);
            }

            return host._placement?.Showing == placement.Showing && placement.IsEnabled(hit)
                && _paths.Count > 0 && (allowed & DROPEFFECT.DROPEFFECT_COPY) != 0
                ? DROPEFFECT.DROPEFFECT_COPY
                : DROPEFFECT.DROPEFFECT_NONE;
        }
    }
}
