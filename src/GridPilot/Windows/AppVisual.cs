using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GridPilot.Windows;

internal sealed record AppVisual(string Name, ImageSource Icon)
{
    internal static AppVisual Placeholder(string title) => new(title, FallbackIcon());
    public static AppVisual Read(WindowTarget target)
    {
        ImageSource? icon = null;
        string name = target.Title;
        if (WindowService.IsSameWindow(target))
        {
            // A slow or hung application must not hold up the grid indefinitely.
            foreach (int kind in new[] { 1, 2, 0 })
            {
                var success = SendMessageTimeout(target.Handle, 0x7f, kind, 144, 2, 60, out var handle);
                if (success == 0) break;
                if ((icon = CopyIcon(handle)) is not null) break;
            }
            icon ??= CopyIcon(GetClassLongPtr(target.Handle, -14));
            icon ??= CopyIcon(GetClassLongPtr(target.Handle, -34));
            var process = Native.OpenProcess(0x1000, false, target.ProcessId);
            if (process != 0)
            {
                try
                {
                    // Packaged applications can expose their logo through the Windows shell.
                    icon ??= PackageIcon(process);
                    var path = new StringBuilder(32768); uint length = (uint)path.Capacity;
                    if (QueryFullProcessImageName(process, 0, path, ref length))
                    {
                        try
                        {
                            var info = FileVersionInfo.GetVersionInfo(path.ToString());
                            if (!string.IsNullOrWhiteSpace(info.FileDescription) && !path.ToString().EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
                                name = info.FileDescription;
                            if (icon is null)
                            {
                                using var extracted = System.Drawing.Icon.ExtractAssociatedIcon(path.ToString());
                                if (extracted is not null) icon = CopyIcon(extracted.Handle);
                            }
                        }
                        catch (Exception ex) when (ex is ArgumentException or IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { }
                    }
                }
                finally { Native.CloseHandle(process); }
            }
        }
        return new(name, icon ?? FallbackIcon());
    }

    private static ImageSource? CopyIcon(nint handle)
    {
        if (handle == 0) return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            image.Freeze(); return image;
        }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception) { return null; }
        // The window owns this HICON. Never destroy it.
    }

    private static ImageSource? PackageIcon(nint process)
    {
        uint length = 0;
        if (GetApplicationUserModelId(process, ref length, null) != 122 || length > 1024) return null;
        var id = new StringBuilder((int)length);
        if (GetApplicationUserModelId(process, ref length, id) != 0) return null;
        var iid = typeof(IShellItemImageFactory).GUID;
        IShellItemImageFactory? factory = null; nint bitmap = 0;
        try
        {
            if (SHCreateItemFromParsingName("shell:AppsFolder\\" + id, 0, ref iid, out factory) != 0 || factory is null) return null;
            if (factory.GetImage(new IconSize { Width = 48, Height = 48 }, 5, out bitmap) != 0 || bitmap == 0) return null;
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze(); return image;
        }
        catch (COMException) { return null; }
        finally
        {
            if (bitmap != 0) DeleteObject(bitmap);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }
    private static ImageSource FallbackIcon()
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(58, 58, 58)), new Pen(Brushes.DarkGray, 1), new Rect(1, 3, 30, 26), 4, 4);
            dc.DrawLine(new Pen(Brushes.Orange, 2), new Point(2, 10), new Point(30, 10));
        }
        var image = new DrawingImage(drawing); image.Freeze(); return image;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IconSize { public int Width, Height; }
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory { [PreserveSig] int GetImage(IconSize size, uint flags, out nint bitmap); }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern nint GetClassLongPtr(nint window, int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(nint process, ref uint length, StringBuilder? id);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] private static extern int SHCreateItemFromParsingName(string name, nint context, ref Guid iid, out IShellItemImageFactory? result);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
}
