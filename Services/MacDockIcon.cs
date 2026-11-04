using System.Runtime.InteropServices;

namespace SqlStudio.Services;

/// <summary>
/// Sets the Dock icon of the running app. A bundled .app takes its icon from the .icns inside it, but a program started
/// with `dotnet run` has no bundle, so macOS would show a generic icon; this tells AppKit which picture to use.
/// </summary>
public static class MacDockIcon
{
    const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)] static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr bytes, nuint length);

    /// <summary>Must run on the main (UI) thread. Never throws: a missing Dock icon is not worth stopping the app for.</summary>
    public static bool TrySet(byte[] png)
    {
        if (!OperatingSystem.IsMacOS() || png.Length == 0)
            return false;

        var buffer = Marshal.AllocHGlobal(png.Length);
        try
        {
            Marshal.Copy(png, 0, buffer, png.Length);
            var data = Send(objc_getClass("NSData"), sel_registerName("dataWithBytes:length:"), buffer, (nuint)png.Length); // copies the bytes
            var image = Send(Send(objc_getClass("NSImage"), sel_registerName("alloc")), sel_registerName("initWithData:"), data);
            if (data == IntPtr.Zero || image == IntPtr.Zero)
                return false;
            var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            if (app == IntPtr.Zero)
                return false;
            Send(app, sel_registerName("setApplicationIconImage:"), image);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
