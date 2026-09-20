using WeChatJevHud.Core.Windows;

namespace WeChatJevHud.Capture;

public interface IWindowCapture
{
    CapturedFrame Capture(WeChatWindowSnapshot window);
}
