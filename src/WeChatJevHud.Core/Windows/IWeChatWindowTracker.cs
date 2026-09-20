namespace WeChatJevHud.Core.Windows;

public interface IWeChatWindowTracker
{
    WeChatWindowSnapshot? Locate();
}
