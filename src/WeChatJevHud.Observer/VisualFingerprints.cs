using WeChatJevHud.Capture;
using WeChatJevHud.Core.Geometry;

namespace WeChatJevHud.Observer;

public sealed class ChatRoiChangeDetector : IChatRoiChangeDetector
{
    public string ComputeFingerprint(CapturedFrame frame, CapturePixelRect chatRegion) =>
        PixelFingerprint.HashSampled(frame, chatRegion, targetSamples: 32_768, includeDimensions: true);
}

public sealed class VisualConversationIdentityProvider : IConversationIdentityProvider
{
    public string GetVisualSignature(CapturedFrame frame, CapturePixelRect chatRegion)
    {
        var headerHeight = Math.Max(1, chatRegion.Y);
        var headerWidth = Math.Max(1, Math.Min(chatRegion.Width, frame.Width - chatRegion.X));
        var header = new CapturePixelRect(chatRegion.X, 0, headerWidth, headerHeight);
        return PixelFingerprint.HashSampled(frame, header, targetSamples: 8_192, includeDimensions: false);
    }
}

internal static class PixelFingerprint
{
    private const ulong OffsetBasis = 14695981039346656037;
    private const ulong Prime = 1099511628211;

    public static string HashSampled(
        CapturedFrame frame,
        CapturePixelRect region,
        int targetSamples,
        bool includeDimensions)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateRegion(frame, region);
        var pixelCount = checked(region.Width * region.Height);
        var step = Math.Max(1, (int)Math.Sqrt(pixelCount / (double)targetSamples));
        var hash = OffsetBasis;
        if (includeDimensions)
        {
            Add(ref hash, region.Width);
            Add(ref hash, region.Height);
        }

        for (var y = region.Y; y < region.Bottom; y += step)
        {
            for (var x = region.X; x < region.Right; x += step)
            {
                var offset = checked((y * frame.Stride) + (x * 4));
                Add(ref hash, (byte)(frame.Bgra32Pixels[offset] >> 3));
                Add(ref hash, (byte)(frame.Bgra32Pixels[offset + 1] >> 3));
                Add(ref hash, (byte)(frame.Bgra32Pixels[offset + 2] >> 3));
            }
        }

        return hash.ToString("X16");
    }

    private static void ValidateRegion(CapturedFrame frame, CapturePixelRect region)
    {
        if (region.IsEmpty || region.X < 0 || region.Y < 0 ||
            region.Right > frame.Width || region.Bottom > frame.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "Fingerprint region must be inside the captured frame.");
        }
    }

    private static void Add(ref ulong hash, int value)
    {
        hash ^= unchecked((uint)value);
        hash *= Prime;
    }
}
