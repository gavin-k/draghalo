using System.Drawing;
using DropHalo.Core.Settings;
using FluentAssertions;

namespace DropHalo.Core.Tests;

public sealed class DockPositionTests
{
    private static readonly Size Dock = new(320, 400);
    private static readonly Point FarAway = new(-9999, -9999);

    [Theory]
    [InlineData(100, 200, 0, 0, 1920, 1040, 100, 200)]       // inside: unchanged
    [InlineData(3000, 2000, 0, 0, 1920, 1040, 1600, 640)]    // off the right and bottom: pulled back
    [InlineData(-2500, -50, -1920, 0, 1920, 1080, -1920, 0)] // a monitor left of the primary, off its top-left
    [InlineData(500, 500, 0, 0, 300, 380, 0, 0)]             // a work area smaller than the dock: its top-left
    public void Placement_lies_inside_the_work_area(int x, int y, int workX, int workY, int workWidth, int workHeight, int expectedX, int expectedY) =>
        new DockPosition(x, y).Place(Dock, new Rectangle(workX, workY, workWidth, workHeight), FarAway).Should().Be(new Point(expectedX, expectedY));

    [Fact]
    public void Pointer_over_the_placed_dock_gives_null() =>
        new DockPosition(100, 200).Place(Dock, new Rectangle(0, 0, 1920, 1040), new Point(150, 250)).Should().BeNull();

    [Fact]
    public void Pointer_over_where_the_dock_is_pulled_back_to_gives_null() =>
        new DockPosition(3000, 2000).Place(Dock, new Rectangle(0, 0, 1920, 1040), new Point(1700, 800)).Should().BeNull();
}
