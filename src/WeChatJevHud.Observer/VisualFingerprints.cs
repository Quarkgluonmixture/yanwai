using System.Globalization;
using System.Numerics;
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
    private readonly VisualConversationIdentityOptions _options;

    public VisualConversationIdentityProvider(VisualConversationIdentityOptions? options = null)
    {
        _options = options ?? new VisualConversationIdentityOptions();
        if (_options.MaxHammingDistance is < 0 or > 128 ||
            _options.MaxMeanLuminanceDifference is < 0 or > 255 ||
            !double.IsFinite(_options.MaxTitleVisualDistance) || _options.MaxTitleVisualDistance is <= 0 or >= 1 ||
            !double.IsFinite(_options.MaxTitleAspectDistance) || _options.MaxTitleAspectDistance <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Visual identity thresholds are outside their valid ranges.");
        }
    }

    public IConversationIdentityEvidence GetVisualEvidence(CapturedFrame frame, CapturePixelRect chatRegion)
    {
        var headerHeight = Math.Max(1, chatRegion.Y);
        var availableWidth = Math.Max(1, Math.Min(chatRegion.Width, frame.Width - chatRegion.X));
        var horizontalInset = Math.Min(availableWidth - 1, Math.Max(0, headerHeight / 12));
        var topInset = Math.Min(headerHeight - 1, Math.Max(0, headerHeight / 4));
        var headerWidth = Math.Max(1, (availableWidth * 11 / 16) - horizontalInset);
        var header = new CapturePixelRect(
            chatRegion.X + horizontalInset,
            topInset,
            headerWidth,
            Math.Max(1, headerHeight - topInset - headerHeight / 10));
        var fingerprint = PixelFingerprint.ComputePerceptual(frame, header);
        return new VisualConversationIdentityEvidence(
            fingerprint.AverageHash,
            fingerprint.DifferenceHash,
            fingerprint.MeanLuminance,
            TitleVisualFingerprint.Extract(frame, header));
    }

    public ConversationIdentityComparison Compare(
        IConversationIdentityEvidence accepted,
        IConversationIdentityEvidence candidate)
    {
        if (accepted is not VisualConversationIdentityEvidence acceptedVisual ||
            candidate is not VisualConversationIdentityEvidence candidateVisual)
        {
            throw new ArgumentException("Visual identity evidence must originate from this provider.");
        }

        var hammingDistance =
            BitOperations.PopCount(acceptedVisual.AverageHash ^ candidateVisual.AverageHash) +
            BitOperations.PopCount(acceptedVisual.DifferenceHash ^ candidateVisual.DifferenceHash);
        var meanLuminanceDifference = Math.Abs(
            acceptedVisual.MeanLuminance - candidateVisual.MeanLuminance);
        if (acceptedVisual.Title is { } oldTitle && candidateVisual.Title is { } newTitle)
        {
            var visualDistance = oldTitle.Distance(newTitle);
            var aspectDistance = Math.Abs(Math.Log(oldTitle.Aspect / newTitle.Aspect));
            return new(visualDistance <= _options.MaxTitleVisualDistance && aspectDistance <= _options.MaxTitleAspectDistance,
                $"title_ink=true title_roi={newTitle.Bounds}", visualDistance, aspectDistance);
        }
        if ((acceptedVisual.Title is null) != (candidateVisual.Title is null))
        {
            return new(false, "title_ink_availability_changed=true", 1, 1);
        }
        return new(
            hammingDistance <= _options.MaxHammingDistance &&
            meanLuminanceDifference <= _options.MaxMeanLuminanceDifference,
            FormattableString.Invariant(
                $"hamming_distance={hammingDistance} mean_luminance_delta={meanLuminanceDifference}"));
    }

    public CapturePixelRect? LocateTitleRegion(CapturedFrame frame, CapturePixelRect chatRegion) =>
        ((VisualConversationIdentityEvidence)GetVisualEvidence(frame, chatRegion)).Title?.Bounds;

    private sealed record VisualConversationIdentityEvidence(
        ulong AverageHash,
        ulong DifferenceHash,
        byte MeanLuminance,
        TitleVisualFingerprint? Title) : IConversationIdentityEvidence;
}

public sealed record VisualConversationIdentityOptions(
    int MaxHammingDistance = 18,
    int MaxMeanLuminanceDifference = 24,
    double MaxTitleVisualDistance = .24,
    double MaxTitleAspectDistance = .25);

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

    public static PerceptualFingerprint ComputePerceptual(
        CapturedFrame frame,
        CapturePixelRect region)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateRegion(frame, region);
        Span<byte> averageGrid = stackalloc byte[64];
        Span<byte> differenceGrid = stackalloc byte[72];
        SampleGrayscale(frame, region, averageGrid, width: 8, height: 8);
        SampleGrayscale(frame, region, differenceGrid, width: 9, height: 8);

        var luminanceTotal = 0;
        foreach (var value in averageGrid)
        {
            luminanceTotal += value;
        }

        var mean = (byte)((luminanceTotal + (averageGrid.Length / 2)) / averageGrid.Length);
        ulong averageHash = 0;
        ulong differenceHash = 0;
        for (var index = 0; index < averageGrid.Length; index++)
        {
            if (averageGrid[index] >= mean)
            {
                averageHash |= 1UL << index;
            }
        }

        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                if (differenceGrid[(y * 9) + x] > differenceGrid[(y * 9) + x + 1])
                {
                    differenceHash |= 1UL << ((y * 8) + x);
                }
            }
        }

        return new(averageHash, differenceHash, mean);
    }

    private static void SampleGrayscale(
        CapturedFrame frame,
        CapturePixelRect region,
        Span<byte> destination,
        int width,
        int height)
    {
        for (var targetY = 0; targetY < height; targetY++)
        {
            var sourceY = region.Y + Math.Min(
                region.Height - 1,
                (int)(((targetY + 0.5) * region.Height) / height));
            for (var targetX = 0; targetX < width; targetX++)
            {
                var sourceX = region.X + Math.Min(
                    region.Width - 1,
                    (int)(((targetX + 0.5) * region.Width) / width));
                var offset = checked((sourceY * frame.Stride) + (sourceX * 4));
                var blue = frame.Bgra32Pixels[offset];
                var green = frame.Bgra32Pixels[offset + 1];
                var red = frame.Bgra32Pixels[offset + 2];
                destination[(targetY * width) + targetX] =
                    (byte)((red * 77 + green * 150 + blue * 29) >> 8);
            }
        }
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

internal readonly record struct PerceptualFingerprint(
    ulong AverageHash,
    ulong DifferenceHash,
    byte MeanLuminance)
{
    public string Signature => $"{MeanLuminance:X2}{AverageHash:X16}{DifferenceHash:X16}";

    public static PerceptualFingerprint Parse(string signature)
    {
        if (signature.Length != 34)
        {
            throw new FormatException("Perceptual fingerprint must contain 34 hexadecimal characters.");
        }

        return new(
            ulong.Parse(signature.AsSpan(2, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            ulong.Parse(signature.AsSpan(18, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(signature.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    public static PerceptualDistance Distance(
        PerceptualFingerprint accepted,
        PerceptualFingerprint candidate) =>
        new(
            BitOperations.PopCount(accepted.AverageHash ^ candidate.AverageHash) +
            BitOperations.PopCount(accepted.DifferenceHash ^ candidate.DifferenceHash),
            Math.Abs(accepted.MeanLuminance - candidate.MeanLuminance));
}

internal readonly record struct PerceptualDistance(
    int HammingDistance,
    int MeanLuminanceDifference);
