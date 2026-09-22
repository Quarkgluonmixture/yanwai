namespace Yanwai.Core.Windows;

public interface IWeChatWindowTracker
{
    WeChatWindowSnapshot? Locate();
}
