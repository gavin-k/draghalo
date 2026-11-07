namespace DropHalo.Core.Wheel;

/// <summary>
/// Judges the wheel as it reached the screen. A graphics stack in a bad state presents every D3D window
/// as one flat white block while XAML still reports frames (spikes/AcrylicSpike, finding 9); a wheel that
/// drew has a dark disc or visible details across its light disc. A flat black block is not caught:
/// over a dark desktop it would look like a solid disc.
/// </summary>
public static class FrameCheck
{
    /// <summary>Samples per side of the grid laid over the window.</summary>
    public const int Grid = 16;

    /// <summary>
    /// Share of the samples one light colour must cover. Short of all of them because Explorer's drag image
    /// sits on the wheel's centre, drawn by GDI and so unaffected.
    /// </summary>
    public const double FlatShare = 0.8;

    /// <summary>Lowest red, green and blue of a colour that counts as light.</summary>
    public const int Light = 0xE0;

    /// <summary>Half-width of the square excluded around the drag image, as a fraction of the frame.</summary>
    public const double DragImageHalfWidthRatio = 0.225;

    /// <summary>Radius of the disc area searched for drawn detail, as a fraction of the frame.</summary>
    public const double DiscDetailOuterRatio = 0.44;

    private const int Tolerance = 8;

    /// <summary>Fewest detail pixels a quadrant needs to count as drawn, scaled with the frame's pixel area.</summary>
    public static double MinimumDetailPixels(int size) => Math.Max(4, size * (double)size / 4096);

    /// <param name="bgra">The window's pixels, 4 bytes each (blue, green, red, unused), rows top-down.</param>
    public static FrameVerdict Judge(ReadOnlySpan<byte> bgra, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(bgra.Length, width * height * 4);

        // Centres of a grid's cells, so no sample falls on the window's edge.
        Span<int> samples = stackalloc int[Grid * Grid];
        for (var row = 0; row < Grid; row++)
        {
            for (var col = 0; col < Grid; col++)
            {
                var x = (int)((col + 0.5) * width / Grid);
                var y = (int)((row + 0.5) * height / Grid);
                var i = (y * width + x) * 4;
                samples[row * Grid + col] = bgra[i + 2] << 16 | bgra[i + 1] << 8 | bgra[i];
            }
        }

        var commonest = 0;
        var most = 0;
        foreach (var colour in samples)
        {
            var count = 0;
            foreach (var other in samples)
            {
                if (Near(colour, other, Tolerance))
                {
                    count++;
                }
            }

            if (count > most)
            {
                commonest = colour;
                most = count;
            }
        }

        var share = (double)most / samples.Length;
        var flat = share >= FlatShare && IsLight(commonest) && !HasDiscDetails(bgra, width, height, commonest);
        return new FrameVerdict(flat, commonest, share, samples.Length);
    }

    private static bool HasDiscDetails(ReadOnlySpan<byte> bgra, int width, int height, int background)
    {
        var size = Math.Min(width, height);
        var cx = width / 2.0;
        var cy = height / 2.0;
        // A square exclusion keeps the drag thumbnail's corners from counting as drawn wheel details.
        var inner = size * DragImageHalfWidthRatio;
        var outer = size * DiscDetailOuterRatio;
        Span<int> details = stackalloc int[4];
        details.Clear();
        for (var y = (int)(cy - outer); y <= (int)(cy + outer); y++)
        {
            for (var x = (int)(cx - outer); x <= (int)(cx + outer); x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                if ((Math.Abs(dx) < inner && Math.Abs(dy) < inner) || dx * dx + dy * dy > outer * outer)
                {
                    continue;
                }

                var i = (y * width + x) * 4;
                var colour = bgra[i + 2] << 16 | bgra[i + 1] << 8 | bgra[i];
                if (!Near(colour, background, Tolerance * 3))
                {
                    details[(x < cx ? 0 : 1) + (y < cy ? 0 : 2)]++;
                }
            }
        }

        // Require distributed detail, not a cursor or a few noisy pixels; scale the count with pixel area.
        var minimum = MinimumDetailPixels(size);
        var quadrants = 0;
        foreach (var count in details)
        {
            if (count >= minimum)
            {
                quadrants++;
            }
        }

        // shortcut: heavy occlusion can hide these details; use a render probe if that needs diagnosing.
        return quadrants >= 3;
    }

    private static bool Near(int a, int b, int tolerance) =>
        Math.Abs((a >> 16 & 0xFF) - (b >> 16 & 0xFF)) <= tolerance
        && Math.Abs((a >> 8 & 0xFF) - (b >> 8 & 0xFF)) <= tolerance
        && Math.Abs((a & 0xFF) - (b & 0xFF)) <= tolerance;

    private static bool IsLight(int colour) => (colour >> 16 & 0xFF) >= Light && (colour >> 8 & 0xFF) >= Light && (colour & 0xFF) >= Light;
}

/// <param name="Flat">The frame is one light colour: what the user sees is a white block, not the wheel.</param>
/// <param name="Colour">The commonest colour, 0xRRGGBB.</param>
/// <param name="Share">Share of the samples within a few levels of that colour, 0 to 1.</param>
public sealed record FrameVerdict(bool Flat, int Colour, double Share, int Samples)
{
    public override string ToString() => $"{(Flat ? "flat" : "drawn")}, #{Colour:X6} on {Share * 100:F0}% of {Samples} samples";
}

/// <summary>
/// When to check the wheel's frame and when to tell the user. Checks the first show, then at most every
/// <see cref="Every"/>, but every show while the last check found a flat frame, so a fix is noticed. Tells
/// once each time the frames turn flat, not on every drag while they stay so.
/// </summary>
public sealed class FrameWatch(TimeProvider time)
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    private long? _checkedAt;
    private bool _flat;

    public bool Due => _checkedAt is not { } at || _flat || time.GetElapsedTime(at) >= Every;

    /// <returns>True when this is the first flat frame since a drawn one, or since the start: time to tell the user.</returns>
    public bool Record(FrameVerdict verdict)
    {
        _checkedAt = time.GetTimestamp();
        var turned = verdict.Flat && !_flat;
        _flat = verdict.Flat;
        return turned;
    }
}
