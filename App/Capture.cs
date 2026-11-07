using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace FF14ModManager;

/// <summary>
/// Grabs one frame of the game window with Windows.Graphics.Capture: works while the game is behind
/// other windows (not while minimised), and needs no admin rights or hooks into the game.
/// </summary>
static class Capture
{
    public static IntPtr FindGameWindow() =>
        Process.GetProcessesByName("ffxiv_dx11").Select(p => p.MainWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);

    public static async Task<BitmapSource> Window(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) throw new InvalidOperationException("The game window is minimised; restore it first.");
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            try { await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless); } catch (Exception) { /* yellow border stays: cosmetic */ }

        var item = CreateItem(hwnd);
        using var device = CreateDevice();
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            try { session.IsBorderRequired = false; } catch (Exception) { }

        var got = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.FrameArrived += (p, _) => { var f = p.TryGetNextFrame(); if (f != null && !got.TrySetResult(f)) f.Dispose(); };
        session.StartCapture();
        using var frame = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var soft = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore);

        var bytes = new byte[soft.PixelWidth * soft.PixelHeight * 4];
        soft.CopyToBuffer(bytes.AsBuffer());
        // Bgr32 ignores the alpha byte: the game's swap chain alpha is not meaningful and would make the PNG see-through.
        var bmp = BitmapSource.Create(soft.PixelWidth, soft.PixelHeight, 96, 96, PixelFormats.Bgr32, null, bytes, soft.PixelWidth * 4);
        bmp.Freeze();
        return bmp;
    }

    public static void SavePng(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        enc.Save(f);
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    static GraphicsCaptureItem CreateItem(IntPtr hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");   // IGraphicsCaptureItem
        var ptr = interop.CreateForWindow(hwnd, ref iid);
        try { return GraphicsCaptureItem.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    static IDirect3DDevice CreateDevice()
    {
        const int Hardware = 1, Warp = 5;
        const uint BgraSupport = 0x20;
        var hr = D3D11CreateDevice(IntPtr.Zero, Hardware, IntPtr.Zero, BgraSupport, IntPtr.Zero, 0, 7, out var d3d, out _, out var ctx);
        if (hr < 0) hr = D3D11CreateDevice(IntPtr.Zero, Warp, IntPtr.Zero, BgraSupport, IntPtr.Zero, 0, 7, out d3d, out _, out ctx);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            var iid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");   // IDXGIDevice
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in iid, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                try { return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
                finally { Marshal.Release(inspectable); }
            }
            finally { Marshal.Release(dxgi); }
        }
        finally
        {
            Marshal.Release(d3d);
            if (ctx != IntPtr.Zero) Marshal.Release(ctx);
        }
    }

    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
        uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("d3d11.dll")]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr hwnd);
}
