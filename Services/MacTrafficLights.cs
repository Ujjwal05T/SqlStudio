using System.Runtime.InteropServices;

namespace SqlStudio.Services;

/// <summary>
/// Moves the red/yellow/green window buttons away from the window's corner. macOS draws them at a fixed spot; an app that
/// owns its title bar asks AppKit to enlarge the title bar and then places the buttons itself. AppKit may reset them when
/// the window is resized, so this is applied again after resizes. Never throws: a button in the old spot is not worth a crash.
/// </summary>
public static class MacTrafficLights
{
    const string ObjC = "/usr/lib/libobjc.A.dylib";
    const nint FullScreenMask = 1 << 14;

    [StructLayout(LayoutKind.Sequential)] struct CGPoint { public double X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct CGSize { public double Width, Height; }
    [StructLayout(LayoutKind.Sequential)] struct CGRect { public CGPoint Origin; public CGSize Size; }

    [DllImport(ObjC)] static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector, nint arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern nint SendInt(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern void Send(IntPtr receiver, IntPtr selector, CGRect rect);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern void Send(IntPtr receiver, IntPtr selector, CGPoint point);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern CGRect SendRect(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] static extern void SendRectStret(out CGRect result, IntPtr receiver, IntPtr selector);

    static CGRect Frame(IntPtr view)
    {
        var selector = sel_registerName("frame");
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return SendRect(view, selector);
        SendRectStret(out var rect, view, selector);
        return rect;
    }

    /// <param name="nsWindow">The NSWindow handle of the window.</param>
    /// <param name="titleBarHeight">Height of the strip the buttons are centred in.</param>
    /// <param name="left">Distance from the window's left edge to the red button.</param>
    public static bool TryPlace(IntPtr nsWindow, double titleBarHeight, double left)
    {
        if (!OperatingSystem.IsMacOS() || nsWindow == IntPtr.Zero)
            return false;
        try
        {
            if ((SendInt(nsWindow, sel_registerName("styleMask")) & FullScreenMask) != 0)
                return false; // full screen has its own title bar behaviour

            var standardButton = sel_registerName("standardWindowButton:");
            var close = Send(nsWindow, standardButton, 0);
            var mini = Send(nsWindow, standardButton, 1);
            var zoom = Send(nsWindow, standardButton, 2);
            if (close == IntPtr.Zero || mini == IntPtr.Zero || zoom == IntPtr.Zero)
                return false;

            var superview = sel_registerName("superview");
            var titleBar = Send(close, superview);
            var container = titleBar == IntPtr.Zero ? IntPtr.Zero : Send(titleBar, superview);
            if (titleBar == IntPtr.Zero || container == IntPtr.Zero)
                return false;

            var window = Frame(nsWindow);
            var containerFrame = Frame(container);
            var setFrame = sel_registerName("setFrame:");
            // Make the title bar as tall as the strip so the buttons have room to sit lower without being clipped.
            Send(container, setFrame, new CGRect
            {
                Origin = new CGPoint { X = 0, Y = window.Size.Height - titleBarHeight },
                Size = new CGSize { Width = containerFrame.Size.Width, Height = titleBarHeight },
            });
            Send(titleBar, setFrame, new CGRect { Size = new CGSize { Width = containerFrame.Size.Width, Height = titleBarHeight } });

            var buttonHeight = Frame(close).Size.Height;
            var setOrigin = sel_registerName("setFrameOrigin:");
            var y = (titleBarHeight - buttonHeight) / 2;
            const double Spacing = 20; // distance between the buttons' left edges in macOS
            Send(close, setOrigin, new CGPoint { X = left, Y = y });
            Send(mini, setOrigin, new CGPoint { X = left + Spacing, Y = y });
            Send(zoom, setOrigin, new CGPoint { X = left + 2 * Spacing, Y = y });
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException or AccessViolationException)
        {
            return false;
        }
    }
}
