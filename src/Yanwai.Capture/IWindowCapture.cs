using Yanwai.Core.Windows;

namespace Yanwai.Capture;

public interface IWindowCapture
{
    CapturedFrame Capture(WeChatWindowSnapshot window);
}
