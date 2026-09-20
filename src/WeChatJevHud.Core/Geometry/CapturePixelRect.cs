namespace WeChatJevHud.Core.Geometry;

/// <summary>A rectangle relative to the top-left of a captured frame.</summary>
public readonly record struct CapturePixelRect(int X, int Y, int Width, int Height);
