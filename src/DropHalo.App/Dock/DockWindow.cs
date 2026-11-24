using DropHalo.App.Diagnostics;
using DropHalo.App.Styles;
using DropHalo.Core.Input;
using DropHalo.Core.Settings;
using DockPosition = DropHalo.Core.Settings.DockPosition;
using DropHalo.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace DropHalo.App.Dock;

/// <summary>
/// The dock: a confirmed drag (<see cref="EdgeDock"/>) opens a strip on the opposite screen edge, or where the
/// user last dragged it by its title (<see cref="AppSettings.DockPosition"/>), where
/// files can be dropped and left, from several folders one after another, then
/// dragged out together or one by one: onto the wheel, into a folder, into a mail. Dropping copies
/// nothing and moves nothing, the dock only remembers the paths; dragging out offers copy and link only,
/// so files never leave where they are. A file dragged out somewhere leaves the dock, which hides once
/// it is empty, and also when a drag that opened it ends without dropping anything.
///
/// A top-most WinUI window, created hidden and shown without taking focus. The drag out is WinUI's own
/// (CanDrag): one the Shell runs with DoDragDrop from here never saw a mouse message, on the UI thread or
/// on a thread of its own, and never ended. UI thread only, apart from the hook events it posts from.
/// </summary>
internal sealed class DockWindow
{
    private const double Width = 320;
    private const double Height = 400;
    private static readonly TimeSpan IdleGrace = TimeSpan.FromMilliseconds(500);

    private readonly Window _window;
    private readonly DispatcherQueue _ui;
    private readonly Func<bool> _wheelOpen;
    private readonly SettingsStore _settings;
    private readonly Dictionary<string, IStorageItem> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly TextBlock _title = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("DwTextBrush") };
    private readonly Border _empty;
    private readonly Button _clear;
    private readonly Border _handle;
    private readonly Border _dropSurface;
    private readonly Border _all;
    private readonly DispatcherQueueTimer _idleGrace;
    private bool _draggingOut;
    private volatile bool _dragImage;
    private int _dropGeneration;
    private int _pendingDrops;
    private bool _over;
    private bool _moving;
    private bool _moved;
    private PointInt32 _moveFrom;
    private System.Drawing.Point _pressCursor;
    private long? _lastPressAt;
    private System.Drawing.Point _lastPress;

    public DockWindow(DispatcherQueue ui, DragMonitor monitor, Func<bool> wheelOpen, SettingsStore settings)
    {
        _ui = ui;
        _wheelOpen = wheelOpen;
        _settings = settings;
        _window = new Window { Title = L.T("DropHalo 停靠栏") };
        _window.AppWindow.SetIcon("Assets/DropHalo.ico");
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        _window.AppWindow.SetPresenter(presenter);
        _window.AppWindow.IsShownInSwitchers = false;
        _window.AppWindow.Closing += (window, e) =>
        {
            if (!Quitting)
            {
                e.Cancel = true;
                Hide("closed");
            }
        };

        var clear = _clear = new Button { Content = L.T("清空"), VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(8) };
        clear.Click += (_, _) =>
        {
            FileLog.Info($"dock: cleared {_files.Count} file(s)");
            _files.Clear();
            Hide("cleared");
        };
        Grid.SetColumn(clear, 1);
        _empty = new Border
        {
            Padding = new Thickness(24, 42, 24, 42),
            Child = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    new SymbolIcon(Symbol.Download) { Foreground = Brush("DwAccentBrush"), HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = L.T("把文件放在这里，回头再拖走。"), FontSize = 15, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = Brush("DwText2Brush") },
                },
            },
        };
        _all = new Border
        {
            Padding = new Thickness(12, 12, 12, 12),
            CornerRadius = new CornerRadius(10),
            Background = Brush("DwSurface2Brush"),
            BorderBrush = Brush("DwAccentBrush"),
            BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = L.T("全部拖出"), FontWeight = FontWeights.SemiBold, Foreground = Brush("DwAccentBrush"), HorizontalAlignment = HorizontalAlignment.Center },
        };
        AutomationProperties.SetName(_all, L.T("全部拖出"));
        DragsOut(_all, () => [.. _files.Keys]);
        var footer = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                _all,
                new TextBlock { Text = L.T("原文件保留在原位置"), FontSize = 12, Foreground = Brush("DwText3Brush"), HorizontalAlignment = HorizontalAlignment.Center },
            },
        };
        Grid.SetRow(footer, 2);
        _dropSurface = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = Brush("DwSurface2Brush"),
            BorderBrush = Brush("DwBorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Child = new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
        Grid.SetRow(_dropSurface, 1);

        // The title is the handle the dock is moved by: the window has no title bar of its own.
        AutomationProperties.SetAutomationId(_title, "dock-title");
        _handle = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uE76F", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("DwText3Brush") },
                    _title,
                },
            },
        };
        ToolTipService.SetToolTip(_handle, L.T("拖动这里移动停靠栏，双击回到屏幕边缘"));
        _handle.PointerPressed += OnHandlePressed;
        _handle.PointerMoved += OnHandleMoved;
        _handle.PointerReleased += (_, e) =>
        {
            _handle.ReleasePointerCapture(e.Pointer);
            EndMove();
        };
        _handle.PointerCaptureLost += (_, _) => EndMove();

        var root = new Grid
        {
            Padding = new Thickness(18),
            RowSpacing = 16,
            AllowDrop = true,
            Background = Brush("DwSurfaceBrush"),
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto },
            },
            Children =
            {
                new Grid
                {
                    ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
                    Children = { _handle, clear },
                },
                _dropSurface,
                footer,
            },
        };
        root.DragEnter += OnDragEnter;
        root.DragOver += OnDragOver;
        root.DragLeave += (_, _) =>
        {
            _over = false;
            _dropSurface.BorderBrush = Brush("DwBorderBrush");
        };
        root.Drop += OnDrop;
        _window.Content = root;
        Render();

        _idleGrace = ui.CreateTimer();
        _idleGrace.Interval = IdleGrace;
        _idleGrace.IsRepeating = false;
        _idleGrace.Tick += (_, _) =>
        {
            if (_files.Count == 0 && _pendingDrops == 0 && !_draggingOut)
            {
                Hide("the drag ended without a drop");
            }
        };

        monitor.DockRequested += (side, x, y) => ui.TryEnqueue(() =>
        {
            if (monitor.Phase == DragPhase.Dragging)
            {
                Open(side, x, y);
            }
        });
        monitor.PhaseChanged += phase =>
        {
            // Whether the last drag showed the Shell's drag image, which the hook needs to see before the dock opens.
            _dragImage = phase == DragPhase.Dragging || (_dragImage && phase == DragPhase.Idle);
            if (phase == DragPhase.Idle)
            {
                ui.TryEnqueue(DragEnded);
            }
        };
    }

    /// <summary>
    /// Set once the app is quitting: the dock is not shown again and a close is not turned into a hide.
    /// </summary>
    public bool Quitting { get; set; }

    public bool IsVisible => _window.AppWindow.IsVisible;

    /// <summary>Takes the dock off the screen for good. It lives on until <see cref="Close"/>.</summary>
    public void Withdraw()
    {
        Quitting = true;
        Hide("quitting");
    }

    public void Close()
    {
        Quitting = true;
        _idleGrace.Stop();
        InvalidateDrops();
        _window.Close();
    }

    /// <summary>
    /// A drag requests <paramref name="side"/> at <paramref name="x"/>, <paramref name="y"/> (physical pixels):
    /// shows the dock where the user last left it, else along that edge of the work area, centred on the pointer.
    /// A saved position the pointer would be over gives way to the edge, as the dock would catch the drag there.
    /// Not while the wheel is open, whose drop target covers the screen, nor when the dock is already up.
    /// </summary>
    private void Open(DockSide side, int x, int y)
    {
        _idleGrace.Stop();
        if (Quitting || IsVisible || _wheelOpen())
        {
            return;
        }

        var pointer = new System.Drawing.Point(x, y);
        if (_settings.Current.DockPosition is { } saved && Area(new System.Drawing.Point(saved.Left, saved.Top)) is { } there
            && saved.Place(there.Dock, there.Work, pointer) is { } placed)
        {
            _window.AppWindow.MoveAndResize(new RectInt32(placed.X, placed.Y, there.Dock.Width, there.Dock.Height));
            _window.AppWindow.Show(activateWindow: false);
            FileLog.Info($"dock: shown at the saved position, {placed.X},{placed.Y} {there.Dock.Width}x{there.Dock.Height} (pointer {x},{y})");
            return;
        }

        if (Area(pointer) is not { } area)
        {
            return;
        }

        var rect = EdgeRect(side, y, area);
        _window.AppWindow.MoveAndResize(rect);
        _window.AppWindow.Show(activateWindow: false);
        FileLog.Info($"dock: shown at the {(side == DockSide.Left ? "left" : "right")} edge, {rect.X},{rect.Y} {rect.Width}x{rect.Height} (pointer {x},{y})");
    }

    /// <summary>
    /// The dock's size on the monitor nearest <paramref name="point"/> and that monitor's work area, in physical
    /// pixels; null when Windows cannot say.
    /// </summary>
    private static MonitorArea? Area(System.Drawing.Point point)
    {
        var monitor = PInvoke.MonitorFromPoint(point, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var scale = PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpi, out _).Succeeded ? dpi / 96.0 : 1.0;
        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!PInvoke.GetMonitorInfo(monitor, ref info))
        {
            return null;
        }

        var work = System.Drawing.Rectangle.FromLTRB(info.rcWork.left, info.rcWork.top, info.rcWork.right, info.rcWork.bottom);
        return new MonitorArea(new(Math.Min((int)Math.Round(Width * scale), work.Width), Math.Min((int)Math.Round(Height * scale), work.Height)), work);
    }

    /// <summary>The dock against <paramref name="side"/> of <paramref name="area"/>, centred on <paramref name="y"/> as far as it fits.</summary>
    private static RectInt32 EdgeRect(DockSide side, int y, MonitorArea area)
    {
        var (w, h) = (area.Dock.Width, area.Dock.Height);
        var left = side == DockSide.Left ? area.Work.Left : area.Work.Right - w;
        var top = Math.Clamp(y - h / 2, area.Work.Top, area.Work.Bottom - h);
        return new RectInt32(left, top, w, h);
    }

    private void DragEnded()
    {
        _over = false;
        if (!Quitting && IsVisible && _files.Count == 0)
        {
            _idleGrace.Start();
        }
    }

    private void Hide(string why)
    {
        _idleGrace.Stop();
        InvalidateDrops();
        _over = false;
        _dropSurface.BorderBrush = Brush("DwBorderBrush");
        Render();
        if (IsVisible)
        {
            _window.AppWindow.Hide();
            FileLog.Info($"dock: hidden ({why})");
        }
    }

    private void InvalidateDrops()
    {
        _dropGeneration++;
        _pendingDrops = 0;
    }

    // ---- moving the dock by its title ---------------------------------------------------------------

    private void OnHandlePressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_handle);
        var pressed = e.Pointer.PointerDeviceType == PointerDeviceType.Mouse ? point.Properties.IsLeftButtonPressed : point.IsInContact;
        if (!pressed || !PInvoke.GetCursorPos(out var cursor))
        {
            return;
        }

        // A double-click by Windows' own rules: DoubleTapped did not fire on this captured handle.
        var now = Environment.TickCount64;
        if (now - _lastPressAt <= PInvoke.GetDoubleClickTime()
            && Math.Abs(cursor.X - _lastPress.X) * 2 <= PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CXDOUBLECLK)
            && Math.Abs(cursor.Y - _lastPress.Y) * 2 <= PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CYDOUBLECLK))
        {
            _lastPressAt = null;
            ResetPosition();
            return;
        }

        (_lastPressAt, _lastPress) = (now, cursor);
        if (!_handle.CapturePointer(e.Pointer))
        {
            return;
        }

        _pressCursor = cursor;
        _moveFrom = _window.AppWindow.Position;
        (_moving, _moved) = (true, false);
    }

    private void OnHandleMoved(object sender, PointerRoutedEventArgs e)
    {
        // The screen position, not the event's: the window moves under the pointer.
        if (!_moving || !PInvoke.GetCursorPos(out var cursor))
        {
            return;
        }

        // Inside Windows' drag rectangle around the press it is still a click: a hand's slip neither moves the dock nor
        // saves a place, and the first click of a double-click stays a click.
        var (dx, dy) = (cursor.X - _pressCursor.X, cursor.Y - _pressCursor.Y);
        if (!_moved
            && Math.Abs(dx) * 2 <= PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CXDRAG)
            && Math.Abs(dy) * 2 <= PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CYDRAG))
        {
            return;
        }

        _moved = true;
        _window.AppWindow.Move(new PointInt32(_moveFrom.X + dx, _moveFrom.Y + dy));
    }

    /// <summary>Pulls a moved dock back inside its monitor's work area and remembers where it was left.</summary>
    private void EndMove()
    {
        if (!_moving)
        {
            return;
        }

        _moving = false;
        if (_moved && PInvoke.GetCursorPos(out var cursor))
        {
            // Where the pointer let go: its last moves can still be queued behind a busy UI thread.
            _window.AppWindow.Move(new PointInt32(_moveFrom.X + cursor.X - _pressCursor.X, _moveFrom.Y + cursor.Y - _pressCursor.Y));
        }

        var at = _window.AppWindow.Position;
        if ((at.X == _moveFrom.X && at.Y == _moveFrom.Y) || Area(new System.Drawing.Point(at.X, at.Y)) is not { } area)
        {
            return;
        }

        _lastPressAt = null;

        var placed = new DockPosition(at.X, at.Y).Place(area.Dock, area.Work, new System.Drawing.Point(int.MinValue, int.MinValue))
            ?? new System.Drawing.Point(at.X, at.Y);
        _window.AppWindow.MoveAndResize(new RectInt32(placed.X, placed.Y, area.Dock.Width, area.Dock.Height));
        SavePosition(new DockPosition(placed.X, placed.Y));
        FileLog.Info($"dock: moved to {placed.X},{placed.Y}");
    }

    /// <summary>Forgets the saved position and puts the dock against the screen edge nearer its middle.</summary>
    private void ResetPosition()
    {
        _moving = false;
        SavePosition(null);
        FileLog.Info("dock: position reset");
        var (at, size) = (_window.AppWindow.Position, _window.AppWindow.Size);
        var middle = new System.Drawing.Point(at.X + size.Width / 2, at.Y + size.Height / 2);
        if (Area(middle) is { } area)
        {
            var side = middle.X < area.Work.Left + area.Work.Width / 2 ? DockSide.Left : DockSide.Right;
            _window.AppWindow.MoveAndResize(EdgeRect(side, middle.Y, area));
        }
    }

    private void SavePosition(DockPosition? position)
    {
        try
        {
            _settings.Update(s => s with { DockPosition = position });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Error("dock: could not save where it was left", ex);
        }
    }

    // ---- drops onto the dock ------------------------------------------------------------------------

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!_over)
        {
            _over = true;
            _dropSurface.BorderBrush = Brush(!_draggingOut && e.DataView.Contains(StandardDataFormats.StorageItems) ? "DwAccentBrush" : "DwBorderBrush");
            FileLog.Info($"dock: entered by a drag {(_draggingOut ? "out of the dock" : e.DataView.Contains(StandardDataFormats.StorageItems) ? "of files" : "of something else")}");
        }

        OnDragOver(sender, e);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        // Its own files dragged back in would only be dropped where they already are.
        var takes = !_draggingOut && e.DataView.Contains(StandardDataFormats.StorageItems);
        e.AcceptedOperation = takes ? DataPackageOperation.Copy : DataPackageOperation.None;
        if (takes)
        {
            e.DragUIOverride.Caption = L.T("放进停靠栏");
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        _over = false;
        _idleGrace.Stop();
        _dropSurface.BorderBrush = Brush("DwBorderBrush");
        e.AcceptedOperation = DataPackageOperation.None;
        if (Quitting || !IsVisible || _draggingOut || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        DragOperationDeferral? deferral = null;
        var generation = _dropGeneration;
        _pendingDrops++;
        try
        {
            deferral = e.GetDeferral();
            var items = await e.DataView.GetStorageItemsAsync();
            if (generation != _dropGeneration)
            {
                return;
            }

            var added = 0;
            foreach (var item in items.Where(i => i.Path.Length > 0))
            {
                if (_files.TryAdd(item.Path, item))
                {
                    added++;
                }
            }

            e.AcceptedOperation = DataPackageOperation.Copy;
            FileLog.Info($"dock: added {added} file(s), {_files.Count} in the dock");
        }
        catch (Exception ex)
        {
            FileLog.Error("dock: could not read the dropped files", ex);
        }
        finally
        {
            if (generation == _dropGeneration)
            {
                _pendingDrops--;
            }

            deferral?.Complete();
        }

        if (generation == _dropGeneration)
        {
            Settle("nothing to keep");
        }
    }

    // ---- drags out of the dock ----------------------------------------------------------------------

    /// <summary>
    /// Makes <paramref name="element"/> drag <paramref name="paths"/> out, as files to copy or link. Files that
    /// are gone by then are forgotten instead. Each file a drop takes leaves the dock.
    /// </summary>
    private void DragsOut(UIElement element, Func<IReadOnlyList<string>> paths)
    {
        List<string> dragged = [];
        element.CanDrag = true;
        element.DragStarting += (_, e) =>
        {
            dragged = [];
            var all = paths();
            var gone = all.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
            if (gone.Count > 0)
            {
                foreach (var path in gone)
                {
                    _files.Remove(path);
                }

                FileLog.Info($"dock: forgot {gone.Count} file(s) that are no longer there");
            }

            var present = all.Except(gone).ToList();
            if (present.Count == 0)
            {
                e.Cancel = true;
                _ui.TryEnqueue(() => Settle("nothing to keep"));
                return;
            }

            try
            {
                // Reuse the incoming items: awaiting WinRT here can outlive DragStarting and stall the native drag.
                var items = present.Select(path => _files[path]).ToArray();
                e.Data.SetStorageItems(items, true);
                e.Data.RequestedOperation = DataPackageOperation.Copy;
                e.AllowedOperations = DataPackageOperation.Copy | DataPackageOperation.Link;
                dragged = present;
                _draggingOut = true;
                FileLog.Info($"dock: drag out of {present.Count} file(s) started");
            }
            catch (Exception ex)
            {
                FileLog.Error("dock: could not start the drag out", ex);
                e.Cancel = true;
            }
        };
        element.DropCompleted += (_, e) =>
        {
            _draggingOut = false;
            if (dragged.Count == 0)
            {
                return;
            }

            var outcome = e.DropResult switch
            {
                DataPackageOperation.Copy => "copied",
                DataPackageOperation.Link => "linked",
                DataPackageOperation.Move => "moved",
                _ => "canceled",
            };
            FileLog.Info($"dock: dragged out {dragged.Count} file(s), {outcome}{(_dragImage ? "" : ", no drag image seen")}");
            if (e.DropResult != DataPackageOperation.None)
            {
                foreach (var path in dragged)
                {
                    _files.Remove(path);
                }
            }

            dragged = [];
            Settle("every file was dragged out");
        };
    }

    /// <summary>Shows what is left, and hides once nothing is.</summary>
    private void Settle(string why)
    {
        Render();
        if (_files.Count == 0 && _pendingDrops == 0)
        {
            Hide(why);
        }
    }

    private void Render()
    {
        _title.Text = _files.Count == 0 ? L.T("停靠栏") : L.T("停靠栏 · {0} 个文件", _files.Count);
        _rows.Children.Clear();
        if (_files.Count == 0)
        {
            _rows.Children.Add(_empty);
        }

        foreach (var path in _files.Keys)
        {
            var text = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = Path.GetFileName(path), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brush("DwTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = Path.GetDirectoryName(path) ?? "", FontSize = 12, Foreground = Brush("DwText3Brush"), TextTrimming = TextTrimming.CharacterEllipsis },
                },
            };
            Grid.SetColumn(text, 1);
            var row = new Border
            {
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(8),
                Background = Brush("DwSurfaceBrush"),
                Child = new Grid
                {
                    ColumnSpacing = 12,
                    ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } },
                    Children =
                    {
                        new SymbolIcon(Symbol.Document) { Foreground = Brush("DwAccentBrush"), VerticalAlignment = VerticalAlignment.Center },
                        text,
                    },
                },
            };
            DragsOut(row, () => [path]);
            _rows.Children.Add(row);
        }

        _all.Visibility = _files.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _clear.Visibility = _files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Brush Brush(string key) => AppStyles.Brush(key);

    /// <summary>The dock's size on a monitor and that monitor's work area, in physical pixels.</summary>
    private readonly record struct MonitorArea(System.Drawing.Size Dock, System.Drawing.Rectangle Work);
}
