namespace Yanwai.Core.Geometry;

/// <summary>
/// A WPF device-independent-pixel rectangle relative to a monitor's top-left corner.
/// </summary>
public readonly record struct MonitorDipRect(double X, double Y, double Width, double Height);
