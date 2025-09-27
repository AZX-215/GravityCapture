#nullable enable
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using WinRT.Interop;

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

            // fast-path: consume the last prepared CPU bitmap if present
            var snap = Interlocked.Exchange(ref _lastBitmap, null);
            if (snap != null) { bmp = snap; return true; }

            // If no event has fired yet, try to force one by waiting a tiny bit
            if (_firstFrame.Wait(0))
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

            if (_session != null)
            {
                try { _session.IsCursorCaptureEnabled = false; _session.Dispose(); } catch {}
            }
            if (_pool != null)
            {
                try { _pool.Dispose(); } catch {}
            }
            _item?.Dispose();

            _context?.ClearState();
            _context?.Flush();
            _context?.Dispose();
            _device?.Dispose();
            _dxgiDevice?.Dispose();

            _lastBitmap?.Dispose();
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

        public WgcCapture(GraphicsCaptureItem item)
        {
            _item = item;

            CreateDevice();
            CreatePoolAndSession(item);
        }

        private void CreateDevice()
        {
            // D3D11 device with BGRA + VideoSupport for WGC
            var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
            D3D11.D3D11CreateDevice(
                null, DriverType.Hardware, DeviceCreationFlags.None,
                flags, null, out _device, out _context).CheckError();

            _dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
            var winrtDev = CreateDirect3DDeviceFromDXGIDevice(_dxgiDevice!);
            _pool = Direct3D11CaptureFramePool.Create(
                winrtDev, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
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

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            if (_disposed) return;

            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;

            try
            {
                // Convert WinRT surface -> ID3D11Texture2D
                using var tex = GetTextureFromSurface(frame.Surface);

                // Copy to CPU-readable staging
                var desc = tex.Description;
                desc.BindFlags = 0;
                desc.CpuAccessFlags = CpuAccessFlags.Read;
                desc.Usage = ResourceUsage.Staging;
                desc.MiscFlags = 0;

                using var staging = _device!.CreateTexture2D(desc);
                _context!.CopyResource(staging, tex);

                // Map and create GDI bitmap
                var db = _context.Map(staging, 0, MapMode.Read, MapFlags.None);
                try
                {
                    var bmp = new Bitmap(desc.Width, desc.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                    var bd = bmp.LockBits(new Rectangle(0, 0, desc.Width, desc.Height),
                        System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);

                    try
                    {
                        // row-by-row copy honoring pitch
                        unsafe
                        {
                            Buffer.MemoryCopy(db.DataPointer.ToPointer(), bd.Scan0.ToPointer(),
                                              bd.Stride * bd.Height, desc.Width * 4L * desc.Height);
                        }
                    }
                    finally
                    {
                        bmp.UnlockBits(bd);
                    }

                    // Swap-in as latest frame
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
                // swallow; next frame will try again
            }
        }

        // ---- helpers ----------------------------------------------------------

        // Create WinRT IDirect3DDevice from DXGI device
        private static IDirect3DDevice CreateDirect3DDeviceFromDXGIDevice(IDXGIDevice dxgi)
        {
            CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var p).CheckError();
            return MarshalInterface<IDirect3DDevice>.FromAbi(p);
        }

        // Extract ID3D11Texture2D from WinRT IDirect3DSurface
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

    // Simple HRESULT helper
    internal static class HResultExt
    {
        public static void CheckError(this int hr)
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }
    }
}
