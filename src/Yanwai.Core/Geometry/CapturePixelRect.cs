namespace Yanwai.Core.Geometry;

/// <summary>A rectangle relative to the top-left of a captured frame.</summary>
public readonly record struct CapturePixelRect(int X, int Y, int Width, int Height)
{
    public int Right => checked(X + Width);

    public int Bottom => checked(Y + Height);

    public bool IsEmpty => Width <= 0 || Height <= 0;
}
