using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Windows.Data.Pdf;
using Windows.Storage.Streams;
using WinRT;

namespace InkCanvas.Modules.Pdf;

/// <summary>Owns the PDF renderer and its DirectX device until all rendering has finished.</summary>
internal sealed class PdfNativeRenderer : IDisposable
{
    private readonly CanvasDevice _device;
    private IPdfRendererNative _renderer;
    public CanvasDevice Device => _device;

    public PdfNativeRenderer(bool forceSoftwareRenderer = false)
    {
        _device = new CanvasDevice(forceSoftwareRenderer);
        try
        {
            using var access = ((IWinRTObject)_device).NativeObject.As(new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"));
            nint dxgiDevice = GetDxgiDevice(access.ThisPtr);
            try { Marshal.ThrowExceptionForHR(PdfCreateRenderer(dxgiDevice, out _renderer)); }
            finally { Marshal.Release(dxgiDevice); }
        }
        catch { _device.Dispose(); throw; }
    }

    public Task<byte[]> RenderAsync(PdfPage page, Vector4 source, uint width, uint height, CancellationToken token)
        => Task.Run(async () =>
        {
            using var target = RenderTarget(page, source, width, height, token);
            using var output = new InMemoryRandomAccessStream();
            // Finish native work before disposing its resources, including when input cancels it.
            await target.SaveAsync(output, CanvasBitmapFileFormat.Png).AsTask().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            output.Seek(0);
            var bytes = new byte[checked((int)output.Size)];
            using var reader = new DataReader(output);
            await reader.LoadAsync((uint)bytes.Length).AsTask().ConfigureAwait(false);
            reader.ReadBytes(bytes);
            return bytes;
        }, token);

    public Task<CanvasBitmap> RenderBitmapAsync(PdfPage page, Vector4 source, uint width, uint height, CancellationToken token)
        => Task.Run<CanvasBitmap>(() => RenderTarget(page, source, width, height, token), token);

    private CanvasRenderTarget RenderTarget(PdfPage page, Vector4 source, uint width, uint height, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = new CanvasRenderTarget(_device, width, height, 96);
        try
        {
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Microsoft.UI.Colors.White);
                using var wrapper = ((IWinRTObject)drawing).NativeObject.As(new Guid("5F10688D-EA55-4D55-A3B0-4DDB55C0C20A"));
                nint context = GetDrawingContext(wrapper.ThisPtr);
                try
                {
                    var parameters = new PdfRenderParameters
                    {
                        SourceRect = source,
                        DestinationWidth = width,
                        DestinationHeight = height,
                        BackgroundColor = Vector4.One,
                        IgnoreHighContrast = 1,
                    };
                    Marshal.ThrowExceptionForHR(_renderer.RenderPageToDeviceContext(((IWinRTObject)page).NativeObject.ThisPtr, context, ref parameters));
                }
                finally { Marshal.Release(context); }
            }
            token.ThrowIfCancellationRequested();
            return target;
        }
        catch { target.Dispose(); throw; }
    }

    private static unsafe nint GetDxgiDevice(nint access)
    {
        Guid iid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
        nint result;
        var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)access)[3];
        Marshal.ThrowExceptionForHR(getInterface(access, &iid, &result));
        return result;
    }

    private static unsafe nint GetDrawingContext(nint wrapper)
    {
        Guid iid = new("E8F7FE7A-191C-466D-AD95-975678BDA998");
        nint result;
        var getResource = (delegate* unmanaged[Stdcall]<nint, nint, float, Guid*, nint*, int>)(*(nint**)wrapper)[3];
        Marshal.ThrowExceptionForHR(getResource(wrapper, 0, 96, &iid, &result));
        return result;
    }

    public void Dispose()
    {
        if (_renderer is null) return;
        // RenderToStreamAsync retains a process-wide PDF/WARP device until DLL detach.
        // Our renderer releases its device while the Windows thread pool is still running.
        Marshal.ReleaseComObject(_renderer);
        _renderer = null;
        _device.Dispose();
    }

    [DllImport("Windows.Data.Pdf.dll", ExactSpelling = true)]
    private static extern int PdfCreateRenderer(nint device, out IPdfRendererNative renderer);

    [StructLayout(LayoutKind.Sequential)]
    private struct PdfRenderParameters
    {
        public Vector4 SourceRect;
        public uint DestinationWidth, DestinationHeight;
        public Vector4 BackgroundColor;
        public byte IgnoreHighContrast;
    }

    [ComImport, Guid("7D9DCD91-D277-4947-8527-07A0DAEDA94A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPdfRendererNative
    {
        [PreserveSig] int RenderPageToSurface(nint page, nint surface, long offset, ref PdfRenderParameters parameters);
        [PreserveSig] int RenderPageToDeviceContext(nint page, nint context, ref PdfRenderParameters parameters);
    }
}
