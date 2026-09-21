namespace WeChatJevHud.Ocr;

public interface IOcrRoutingPolicy
{
    OcrRoute SelectRoute(ImageCrop crop);
}
