#nullable enable
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace GravityCapture.Services
{
    /// <summary>Windows.Graphics.Capture + D3D11. Event-driven, CPU-readback, thread-safe snapshot.</summary>
    public sealed class WgcCapture : IDisposable
    {
        // ---- Public API ------------------------------------------------------
        public static bool IsSupported() => GraphicsCaptureSession.IsSupported();

        public static WgcCapture? TryStartForWindow(IntPtr hwnd, out string? why)
        {
            why = null;
            try
            {
                if (!IsSupported()) { why = "WGC not supported"; return null; }
                var item = GraphicsCaptureItemInterop.CreateForWindow(hwnd);
                if (item is null) { why = "CreateForWindow returned null"; return null; }
                return new WgcCapture(item);
            }
            catch (Exception ex) { why = ex.Message; return null; }
        }

        /// <summary>Get the newest frame as a Bitmap; returns false if none available yet.</summary>
        public bool TryGetLatest(out Bitmap? bmp, out string? why)
        {
            bmp = null; why = null;
            if (_disposed) { why = "disposed"; return false; }

            var snap = Interlocked.Exchange(ref _lastBitmap, null);
            if (snap != null) { bmp = snap; return true; }

            if (_firstFrame.IsSet)
            {
                snap = Interlocked.Exchange(ref _lastBitmap, null);
                if (snap != null) { bmp = snap; return true; }
            }

            why = "no frame yet";
            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _session?.Dispose(); } catch {}
            try { _pool?.Dispose(); } catch {}
            try { _item?.Dispose(); } catch {}

            try { _context?.ClearState(); _context?.Flush(); } catch {}
            try { _context?.Dispose(); } catch {}
            try { _device?.Dispose(); } catch {}
            try { _dxgiDevice?.Dispose(); } catch {}

            try { _lastBitmap?.Dispose(); } catch {}
        }

        // ---- Impl ------------------------------------------------------------
        private readonly GraphicsCaptureItem _item;
        private Direct3D11CaptureFramePool? _pool;
        private GraphicsCaptureSession? _session;

        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private IDXGIDevice? _dxgiDevice;

        private volatile bool _disposed;
        private volatile Bitmap? _lastBitmap;
        private readonly ManualResetEventSlim _firstFrame = new(false);

        private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

        private WgcCapture(GraphicsCaptureItem item)
        {
            _item = item;

            CreateDevice();
            CreatePoolAndSession(item);
        }

        private void CreateDevice()
        {
            // BGRA + Video support recommended for capture
            D3D11.D3D11CreateDevice(
                null,
                Vortice.Direct3D.DriverType.Hardware,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                null,
                out _device,
                out _context).CheckError();

            _dxgiDevice = _device!.QueryInterfaceOrNull<IDXGIDevice>();
            var winrtDev = CreateDirect3DDeviceFromDXGIDevice(_dxgiDevice!);
            _pool = Direct3D11CaptureFramePool.Create(winrtDev, PixelFormat, 2, _item.Size);
        }

        private void CreatePoolAndSession(GraphicsCaptureItem item)
        {
            _pool!.FrameArrived += OnFrameArrived;
            _session = _pool.CreateCaptureSession(item);
            _session.IsCursorCaptureEnabled = false;
            _session.IsBorderRequired = false;
            _session.StartCapture();

            item.Closed += (_, __) => Dispose();
        }

        private unsafe void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            if (_disposed) return;

            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;

            try
            {
                using var tex = GetTextureFromSurface(frame.Surface);

                var desc = tex.Description;
                var stagingDesc = desc with
                {
                    BindFlags = 0,
                    CpuAccessFlags = CpuAccessFlags.Read,
                    Usage = ResourceUsage.Staging,
                    MiscFlags = 0
                };

                using var staging = _device!.CreateTexture2D(stagingDesc);
                _context!.CopyResource(staging, tex);

                var map = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

                try
                {
                    var bmp = new Bitmap(desc.Width, desc.Height, PixelFormat.Format32bppPArgb);
                    var bd = bmp.LockBits(new Rectangle(0, 0, desc.Width, desc.Height),
                                          ImageLockMode.WriteOnly, bmp.PixelFormat);
                    try
                    {
                        int rowBytes = desc.Width * 4;
                        for (int y = 0; y < desc.Height; y++)
                        {
                            Buffer.MemoryCopy(
                                source: (byte*)map.DataPointer + (y * map.RowPitch),
                                destination: (byte*)bd.Scan0 + (y * bd.Stride),
                                destinationSizeInBytes: bd.Stride,
                                sourceBytesToCopy: rowBytes);
                        }
                    }
                    finally
                    {
                        bmp.UnlockBits(bd);
                    }

                    var old = Interlocked.Exchange(ref _lastBitmap, bmp);
                    old?.Dispose();
                    _firstFrame.Set();
                }
                finally
                {
                    _context.Unmap(staging, 0);
                }
            }
            catch
            {
                // ignore; next frame will arrive
            }
        }

        // ---- helpers ----------------------------------------------------------

        // WinRT device from DXGI device
        private static IDirect3DDevice CreateDirect3DDeviceFromDXGIDevice(IDXGIDevice dxgi)
        {
            CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var p).CheckError();
            return MarshalInterface<IDirect3DDevice>.FromAbi(p);
        }

        // Surface -> ID3D11Texture2D
        private static ID3D11Texture2D GetTextureFromSurface(IDirect3DSurface surface)
        {
            var access = (IDirect3DDxgiInterfaceAccess)(object)surface;
            var iid = typeof(ID3D11Texture2D).GUID;
            access.GetInterface(ref iid, out var ptr);
            return new ID3D11Texture2D(ptr);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
        private interface IDirect3DDxgiInterfaceAccess
        {
            void GetInterface([In] ref Guid iid, out IntPtr p);
        }

        [DllImport("d3d11.dll")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    }

    internal static class GraphicsCaptureItemInterop
    {
        public static GraphicsCaptureItem? CreateForWindow(IntPtr hwnd)
        {
            var interop = WinRT.ActivationFactory.As<IGraphicsCaptureItemInterop>(typeof(GraphicsCaptureItem));
            var iid = typeof(GraphicsCaptureItem).GUID;
            interop.CreateForWindow(hwnd, ref iid, out var result);
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(result);
        }

        [ComImport, Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            void CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);
            void CreateForMonitor(IntPtr hmon, ref Guid iid, out IntPtr result);
        }
    }

    internal static class HResultExt
    {
        public static void CheckError(this int hr)
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }
    }
}
