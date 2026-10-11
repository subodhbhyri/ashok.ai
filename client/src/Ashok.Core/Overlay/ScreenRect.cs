namespace Ashok.Core.Overlay;

/// <summary>A rectangle in physical screen pixels, the same convention as <c>UiElement.Bounds</c>.</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    /// <summary>From the [x, y, width, height] array used by the ui-snapshot contract.</summary>
    public static ScreenRect FromBounds(IReadOnlyList<int> bounds)
    {
        if (bounds.Count != 4) throw new ArgumentException("Bounds must be [x, y, width, height].", nameof(bounds));
        return new ScreenRect(bounds[0], bounds[1], bounds[2], bounds[3]);
    }

    public ScreenRect Inflate(int amount) =>
        new(X - amount, Y - amount, Width + 2 * amount, Height + 2 * amount);
}
