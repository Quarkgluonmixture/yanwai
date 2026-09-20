using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WeChatJevHud.Core.Geometry;
using WeChatJevHud.Core.Windows;
using WeChatJevHud.Windows.Discovery;
using WeChatJevHud.Windows.Interop;

namespace WeChatJevHud.Windows;

public sealed class Win32WeChatWindowTracker : IWeChatWindowTracker
{
    private const string RenderClassPrefix = "MMUIRenderSubWindow";
    private readonly WeChatWindowSelector _selector;

    public Win32WeChatWindowTracker()
        : this(new WeChatWindowSelector())
    {
    }

    public Win32WeChatWindowTracker(WeChatWindowSelector selector)
    {
        _selector = selector;
    }

    public WeChatWindowSnapshot? Locate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var candidates = EnumerateCandidates();
        var selected = _selector.SelectBest(candidates);
        return selected is null ? null : CreateSnapshot(selected);
    }

    private static IReadOnlyList<WindowCandidate> EnumerateCandidates()
    {
        var candidates = new List<WindowCandidate>();
        NativeMethods.EnumWindows((window, _) =>
        {
            try
            {
                var candidate = TryCreateCandidate(window);
                if (candidate is not null)
                {
                    candidates.Add(candidate);
                }
            }
            catch (Exception)
            {
                // A window can disappear while EnumWindows is walking the desktop.
            }

            return true;
        }, 0);
        return candidates;
    }

    private static WindowCandidate? TryCreateCandidate(nint window)
    {
        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return null;
        }

        string processName;
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            processName = process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (!processName.Equals("Weixin", StringComparison.OrdinalIgnoreCase) &&
            !processName.Equals("WeChat", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var bounds = GetTopLevelBounds(window);
        return new WindowCandidate(
            window,
            processId,
            processName,
            GetWindowText(window),
            GetClassName(window),
            bounds,
            NativeMethods.IsWindowVisible(window),
            NativeMethods.IsIconic(window),
            NativeMethods.GetWindow(window, NativeMethods.GwOwner) != 0,
            IsCloaked(window),
            FindRenderChild(window) != 0);
    }

    private static WeChatWindowSnapshot? CreateSnapshot(WindowCandidate candidate)
    {
        var monitorHandle = NativeMethods.MonitorFromWindow(candidate.Handle, NativeMethods.MonitorDefaultToNearest);
        if (monitorHandle == 0)
        {
            return null;
        }

        var monitorInfo = new NativeMethods.MonitorInfoEx
        {
            Size = checked((uint)Marshal.SizeOf<NativeMethods.MonitorInfoEx>()),
            DeviceName = string.Empty,
        };
        if (!NativeMethods.GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            return null;
        }

        var renderWindow = FindRenderChild(candidate.Handle);
        DesktopPixelRect? renderBounds = renderWindow == 0 ? null : GetWindowBounds(renderWindow);
        var dpi = NativeMethods.GetDpiForWindow(candidate.Handle);
        if (dpi == 0)
        {
            dpi = 96;
        }

        var foregroundRoot = NativeMethods.GetAncestor(NativeMethods.GetForegroundWindow(), NativeMethods.GaRoot);
        return new WeChatWindowSnapshot(
            candidate.Handle,
            candidate.ProcessId,
            candidate.ProcessName,
            GetWindowText(candidate.Handle),
            GetClassName(candidate.Handle),
            GetTopLevelBounds(candidate.Handle),
            GetClientBounds(candidate.Handle),
            renderWindow == 0 ? null : renderWindow,
            renderBounds,
            new MonitorSnapshot(
                monitorInfo.DeviceName,
                ToRect(monitorInfo.Monitor),
                ToRect(monitorInfo.WorkArea),
                (monitorInfo.Flags & NativeMethods.MonitorInfoPrimary) != 0),
            new DpiSnapshot(dpi, dpi),
            NativeMethods.IsWindowVisible(candidate.Handle),
            NativeMethods.IsIconic(candidate.Handle),
            foregroundRoot == candidate.Handle,
            DateTimeOffset.UtcNow);
    }

    private static nint FindRenderChild(nint parent)
    {
        nint best = 0;
        long bestArea = 0;
        NativeMethods.EnumChildWindows(parent, (child, _) =>
        {
            var className = GetClassName(child);
            if (!className.StartsWith(RenderClassPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var bounds = GetWindowBounds(child);
            if (bounds.Area > bestArea)
            {
                best = child;
                bestArea = bounds.Area;
            }

            return true;
        }, 0);
        return best;
    }

    private static DesktopPixelRect GetTopLevelBounds(nint window)
    {
        if (NativeMethods.DwmGetWindowAttribute(
                window,
                NativeMethods.DwmwaExtendedFrameBounds,
                out var dwmBounds,
                checked((uint)Marshal.SizeOf<NativeMethods.Rect>())) == 0)
        {
            var bounds = ToRect(dwmBounds);
            if (!bounds.IsEmpty)
            {
                return bounds;
            }
        }

        return GetWindowBounds(window);
    }

    private static DesktopPixelRect GetWindowBounds(nint window) =>
        NativeMethods.GetWindowRect(window, out var bounds) ? ToRect(bounds) : default;

    private static DesktopPixelRect GetClientBounds(nint window)
    {
        if (!NativeMethods.GetClientRect(window, out var client))
        {
            return default;
        }

        var origin = new NativeMethods.Point { X = client.Left, Y = client.Top };
        if (!NativeMethods.ClientToScreen(window, ref origin))
        {
            return default;
        }

        return new DesktopPixelRect(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
    }

    private static bool IsCloaked(nint window) =>
        NativeMethods.DwmGetWindowAttributeInt(
            window,
            NativeMethods.DwmwaCloaked,
            out var cloaked,
            sizeof(int)) == 0 && cloaked != 0;

    private static string GetWindowText(nint window)
    {
        var length = NativeMethods.GetWindowTextLength(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder(length + 1);
        _ = NativeMethods.GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    private static string GetClassName(nint window)
    {
        var className = new StringBuilder(256);
        _ = NativeMethods.GetClassName(window, className, className.Capacity);
        return className.ToString();
    }

    private static DesktopPixelRect ToRect(NativeMethods.Rect rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
}
