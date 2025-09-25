// src/GravityCapture/Services/ScreenCapture.cs
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GravityCapture.Services
{
    public static class ScreenCapture
    {
        // ---------- Public API used by MainWindow ----------

        public static (bool ok, Rectangle rectScreen, IntPtr hwndUsed) SelectRegion(IntPtr targetHwndOrZero)
            => OverlaySelector.Select(targetHwndOrZero);

        public static bool TryNormalizeRect(IntPtr hwnd, Rectangle screenRect, out double nx, out double ny, out double nw, out double nh)
        {
            nx = ny = nw = nh = 0;
            if (hwnd == IntPtr.Zero) return false;
            if (!TryGetWindowRect(hwnd, out var wrect)) return false;

            // convert absolute pixels -> relative to hwnd client rect
            var client = GetClientScreenRect(hwnd);
            if (client.Width <= 0 || client.Height <= 0) return false;

            // intersect to client to avoid out-of-range
            var inter = Rectangle.Intersect(screenRect, client);
            if (inter.Width <= 0 || inter.Height <= 0) return false;

            nx = (inter.X - client.X) / (double)client.Width;
            ny = (inter.Y - client.Y) / (double)client.Height;
            nw = inter.Width / (double)client.Width;
            nh = inter.Height / (double)client.Height;
            return true;
        }

        public static bool TryNormalizeRectDesktop(Rectangle screenRect, out double nx, out double ny, out double nw, out double nh)
        {
            var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            nx = (screenRect.X - bounds.X) / (double)bounds.Width;
            ny = (screenRect.Y - bounds.Y) / (double)bounds.Height;
            nw = screenRect.Width / (double)bounds.Width;
            nh = screenRect.Height / (double)bounds.Height;
            return true;
        }

        public static Bitmap Capture(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Capture target handle is null.");
            if (TryCaptureWgc(hwnd, out var bmp)) return bmp;
            // Fallback: GDI (occluded)
            return CaptureGdi(hwnd);
        }

        public static Bitmap CaptureCropNormalized(IntPtr hwnd, double nx, double ny, double nw, double nh)
        {
            using var full = Capture(hwnd);
            var rx = (int)Math.Round(nx * full.Width);
            var ry = (int)Math.Round(ny * full.Height);
            var rw = (int)Math.Round(nw * full.Width);
            var rh = (int)Math.Round(nh * full.Height);

            rx = Math.Clamp(rx, 0, full.Width - 1);
            ry = Math.Clamp(ry, 0, full.Height - 1);
            rw = Math.Clamp(rw, 1, full.Width - rx);
            rh = Math.Clamp(rh, 1, full.Height - ry);

            var crop = new Rectangle(rx, ry, rw, rh);
            var bmp = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.DrawImage(full, new Rectangle(0, 0, crop.Width, crop.Height), crop, GraphicsUnit.Pixel);
            return bmp;
        }

        public static byte[] ToJpegBytes(Bitmap bmp, int quality)
        {
            using var ms = new System.IO.MemoryStream();
            var codec = GetEncoder(ImageFormat.Jpeg);
            var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(quality, 50, 100));
            bmp.Save(ms, codec, ep);
            return ms.ToArray();
        }

        public static IntPtr ResolveWindowByTitleHint(string hint, IntPtr last, out IntPtr resolved)
        {
            resolved = last;
            if (last != IntPtr.Zero && TryGetWindowRect(last, out _)) return last;

            IntPtr found = IntPtr.Zero;
            EnumWindows((h, p) =>
            {
                if (!IsWindowVisible(h)) return true;
                var title = GetWindowText(h);
                if (!string.IsNullOrEmpty(title) && title.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = h; return false;
                }
                return true;
            }, IntPtr.Zero);

            resolved = found;
            return found;
        }

        public static bool TryGetWindowRect(IntPtr hwnd, out Rectangle r)
        {
            r = Rectangle.Empty;
            if (hwnd == IntPtr.Zero) return false;
            if (!GetWindowRect(hwnd, out var wr)) return false;
            r = Rectangle.FromLTRB(wr.left, wr.top, wr.right, wr.bottom);
            return r.Width > 0 && r.Height > 0;
        }

        // ---------- WGC (occlusion-proof) ----------

        private static bool TryCaptureWgc(IntPtr hwnd, out Bitmap bmp)
        {
            bmp = null!;
            try
            {
                if (Environment.OSVersion.Version.Major < 10) return false;

                // Create GraphicsCaptureItem for the window
                var item = CreateItemForWindow(hwnd);
                if (item is null) return false;

                // D3D11 device with BGRA support
                using var device = CreateD3DDevice();
                using var d3d = CreateDirect3DDevice(device);
                var size = item.Size;

                using var pool = Direct3D11CaptureFramePool.Create(
                    d3d, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, size);
                using var session = pool.CreateCaptureSession(item);

                Direct3D11CaptureFrame? frame = null;
                using var got = new ManualResetEventSlim(false);
                void onFrame(Direct3D11CaptureFramePool s, object e)
                {
                    if (frame is null)
                    {
                        frame = s.TryGetNextFrame();
                        got.Set();
                    }
                }

                pool.FrameArrived += onFrame;
                session.StartCapture();
                got.Wait(250); // a single frame

                pool.FrameArrived -= onFrame;
                session.Dispose(); // end capture

                if (frame is null) return false;
                using (frame)
                {
                    // ID3D11Texture2D from frame.Surface
                    var tex = GetD3DTexture2D(device, frame.Surface);

                    // Copy to staging & readback
                    bmp = CopyTextureToBitmap(device, tex);
                    tex.Dispose();
                }
                return true;
            }
            catch
            {
                bmp = null!;
                return false;
            }
        }

        // ---------- GDI fallback (occluded if covered) ----------

        private static Bitmap CaptureGdi(IntPtr hwnd)
        {
            if (!TryGetWindowRect(hwnd, out var wr))
                throw new InvalidOperationException("Window rect not found.");

            var w = Math.Max(1, wr.Width);
            var h = Math.Max(1, wr.Height);

            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(wr.Left, wr.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            return bmp;
        }

        // ---------- Overlay region selector ----------

        private static class OverlaySelector
        {
            public static (bool ok, Rectangle rectScreen, IntPtr hwndUsed) Select(IntPtr preferredHwnd)
            {
                using var win = new Views.RegionSelectorWindow(preferredHwnd);
                var ok = win.ShowDialog() == true;
                return (ok, win.SelectedRect, win.CapturedHwnd);
            }
        }

        // ---------- Helpers ----------

        private static Rectangle GetClientScreenRect(IntPtr hwnd)
        {
            GetClientRect(hwnd, out var rc);
            var pt = new POINT { x = rc.left, y = rc.top };
            ClientToScreen(hwnd, ref pt);
            return new Rectangle(pt.x, pt.y, rc.right - rc.left, rc.bottom - rc.top);
        }

        private static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            var encoders = ImageCodecInfo.GetImageDecoders();
            foreach (var c in encoders)
                if (c.FormatID == format.Guid) return c;
            throw new InvalidOperationException("JPEG encoder not found.");
        }

        // ---------- WGC plumbing ----------

        private static GraphicsCaptureItem? CreateItemForWindow(IntPtr hwnd)
        {
            var interop = (IGraphicsCaptureItemInterop)Activator.CreateInstance(typeof(GraphicsCaptureItem));
            Guid iid = typeof(GraphicsCaptureItem).GUID;
            int hr = interop.CreateForWindow(hwnd, ref iid, out GraphicsCaptureItem item);
            return hr == 0 ? item : null;
        }

        private static ID3D11Device CreateD3DDevice()
        {
            D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                null,
                out ID3D11Device device).CheckError();
            return device;
        }

        private static IDirect3DDevice CreateDirect3DDevice(ID3D11Device device)
        {
            var dxgi = device.QueryInterface<IDXGIDevice>();
            CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var unk).CheckError();
            dxgi.Dispose();
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(unk);
        }

        private static ID3D11Texture2D GetD3DTexture2D(ID3D11Device device, IDirect3DSurface surface)
        {
            var access = (IDirect3DDxgiInterfaceAccess)surface;
            var iid = typeof(ID3D11Texture2D).GUID;
            var ptr = access.GetInterface(ref iid);
            return new ID3D11Texture2D(ptr);
        }

        private static Bitmap CopyTextureToBitmap(ID3D11Device device, ID3D11Texture2D src)
        {
            var desc = src.Description;
            var stagingDesc = desc;
            stagingDesc.BindFlags = 0;
            stagingDesc.CPUAccessFlags = CpuAccessFlags.Read;
            stagingDesc.Usage = ResourceUsage.Staging;
            stagingDesc.MiscFlags = ResourceOptionFlags.None;

            using var staging = device.CreateTexture2D(stagingDesc);
            device.ImmediateContext.CopyResource(staging, src);

            var map = device.ImmediateContext.Map(staging, 0, MapMode.Read, MapFlags.None);
            try
            {
                var bmp = new Bitmap(desc.Width, desc.Height, PixelFormat.Format32bppPArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, desc.Width, desc.Height),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);

                int rowBytes = desc.Width * 4;
                for (int y = 0; y < desc.Height; y++)
                {
                    unsafe
                    {
                        Buffer.MemoryCopy(
                            (void*)(map.DataPointer + y * map.RowPitch),
                            (void*)(data.Scan0 + y * data.Stride),
                            rowBytes, rowBytes);
                    }
                }

                bmp.UnlockBits(data);
                return bmp;
            }
            finally
            {
                device.ImmediateContext.Unmap(staging, 0);
            }
        }

        // ---------- Win32 / WinRT interop ----------

        [ComImport, Guid("3628E81B-3CAC-4A9E-8545-75C971C37E80"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            int CreateForWindow(IntPtr hwnd, ref Guid iid, out GraphicsCaptureItem result);
            int CreateForMonitor(IntPtr hmon, ref Guid iid, out GraphicsCaptureItem result);
        }

        [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDirect3DDxgiInterfaceAccess
        {
            IntPtr GetInterface(ref Guid iid);
        }

        [DllImport("d3d11.dll")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        private static HRESULT CheckError(this int hr) => new HRESULT(hr).ThrowIfFailed();

        private readonly struct HRESULT
        {
            private readonly int _hr;
            public HRESULT(int hr) { _hr = hr; }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public HRESULT ThrowIfFailed()
            {
                if (_hr < 0) Marshal.ThrowExceptionForHR(_hr);
                return this;
            }
        }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        private static string GetWindowText(IntPtr hWnd)
        {
            int length = GetWindowTextLength(hWnd);
            var sb = new System.Text.StringBuilder(length + 1);
            _ = GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private struct RECT { public int left, top, right, bottom; }
        private struct POINT { public int x, y; }
    }
}
