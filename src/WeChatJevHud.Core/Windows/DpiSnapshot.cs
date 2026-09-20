namespace WeChatJevHud.Core.Windows;

public readonly record struct DpiSnapshot(uint X, uint Y)
{
    public double ScaleX => X / 96d;

    public double ScaleY => Y / 96d;
}
