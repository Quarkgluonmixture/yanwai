using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WeChatJevHud.Capture.Interop;
using WeChatJevHud.Core.Windows;

namespace WeChatJevHud.Capture;

/// <summary>
/// Phase 1 capture adapter. It prefers an off-screen render-window capture and
/// falls back to visible desktop pixels for the WeChat render/client surface.
/// </summary>
public sealed class Win32ScreenRegionCapture : IWindowCapture
{
    public CapturedFrame Capture(WeChatWindowSnapshot window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WeChat capture requires the Windows desktop session.");
        }

        if (window.IsMinimized)
        {
            throw new WindowCaptureUnavailableException("WeChat is minimized; capture is suspended until it is restored.");
        }

        if (!window.IsVisible)
        {
            throw new WindowCaptureUnavailableException("WeChat is not visible; capture is suspended.");
        }

        var bounds = window.CaptureBounds;
        if (bounds.IsEmpty)
        {
            throw new WindowCaptureUnavailableException("WeChat did not expose a valid capture rectangle.");
        }

        var timer = Stopwatch.StartNew();
        var screenDc = CaptureNativeMethods.GetDC(0);
        if (screenDc == 0)
        {
            throw CreateWin32Failure("Could not access the Windows desktop device context.");
        }

        nint memoryDc = 0;
        nint bitmap = 0;
        nint previousObject = 0;
        try
        {
            memoryDc = CaptureNativeMethods.CreateCompatibleDC(screenDc);
            if (memoryDc == 0)
            {
                throw CreateWin32Failure("Could not create the capture device context.");
            }

            bitmap = CaptureNativeMethods.CreateCompatibleBitmap(screenDc, bounds.Width, bounds.Height);
            if (bitmap == 0)
            {
                throw CreateWin32Failure("Could not allocate the capture bitmap.");
            }

            previousObject = CaptureNativeMethods.SelectObject(memoryDc, bitmap);
            if (previousObject == 0 || previousObject == -1)
            {
                throw CreateWin32Failure("Could not select the capture bitmap.");
            }

            var method = CaptureMethod.RenderWindow;
            var copied = window.RenderHandle is { } renderHandle &&
                         renderHandle != 0 &&
                         CaptureNativeMethods.PrintWindow(
                             renderHandle,
                             memoryDc,
                             CaptureNativeMethods.PrintWindowRenderFullContent);
            if (!copied)
            {
                method = CaptureMethod.VisibleDesktopFallback;
                copied = CaptureNativeMethods.BitBlt(
                    memoryDc,
                    0,
                    0,
                    bounds.Width,
                    bounds.Height,
                    screenDc,
                    bounds.X,
                    bounds.Y,
                    CaptureNativeMethods.SrcCopy | CaptureNativeMethods.CaptureBlt);
            }

            if (!copied)
            {
                throw CreateWin32Failure("Windows could not copy the visible WeChat pixels.");
            }

            var pixels = CopyPixels(bitmap, bounds.Width, bounds.Height, out var stride);
            timer.Stop();
            return new CapturedFrame(
                bounds.Width,
                bounds.Height,
                stride,
                pixels,
                bounds,
                method,
                DateTimeOffset.UtcNow,
                timer.Elapsed);
        }
        finally
        {
            if (previousObject != 0 && previousObject != -1 && memoryDc != 0)
            {
                _ = CaptureNativeMethods.SelectObject(memoryDc, previousObject);
            }

            if (bitmap != 0)
            {
                _ = CaptureNativeMethods.DeleteObject(bitmap);
            }

            if (memoryDc != 0)
            {
                _ = CaptureNativeMethods.DeleteDC(memoryDc);
            }

            _ = CaptureNativeMethods.ReleaseDC(0, screenDc);
        }
    }

    private static byte[] CopyPixels(nint bitmap, int width, int height, out int stride)
    {
        var source = Imaging.CreateBitmapSourceFromHBitmap(
            bitmap,
            0,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static WindowCaptureUnavailableException CreateWin32Failure(string message) =>
        new(message, new Win32Exception(Marshal.GetLastWin32Error()));
}
