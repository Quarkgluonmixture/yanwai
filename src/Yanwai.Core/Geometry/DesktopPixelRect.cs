namespace Yanwai.Core.Geometry;

/// <summary>
/// A rectangle in physical virtual-desktop pixels. X and Y may be negative.
/// </summary>
public readonly record struct DesktopPixelRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public long Area => IsEmpty ? 0 : (long)Width * Height;
}
