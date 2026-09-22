namespace Yanwai.Capture;

public sealed class WindowCaptureUnavailableException : InvalidOperationException
{
    public WindowCaptureUnavailableException(string message)
        : base(message)
    {
    }

    public WindowCaptureUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
