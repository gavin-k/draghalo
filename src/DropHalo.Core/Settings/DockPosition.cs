using System.Drawing;

namespace DropHalo.Core.Settings;

/// <summary>
/// Where the user last left the dock: its top-left corner in physical pixels on the virtual screen. Monitors and
/// resolutions change between sessions, so the position is pulled back on screen each time it is used.
/// </summary>
public sealed record DockPosition(int Left, int Top)
{
    /// <summary>
    /// The top-left for a <paramref name="dock"/>-sized dock on a monitor whose work area is <paramref name="work"/>:
    /// this position moved just far enough for the dock to lie inside it. Null when <paramref name="pointer"/> would
    /// then be over the dock, where it would catch the drag that is opening it; the dock goes to the edge instead.
    /// </summary>
    public Point? Place(Size dock, Rectangle work, Point pointer)
    {
        var placed = new Point(
            Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - dock.Width)),
            Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - dock.Height)));
        return new Rectangle(placed, dock).Contains(pointer) ? null : placed;
    }
}
