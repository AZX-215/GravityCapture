using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using DXGI = Vortice.DXGI;
using D3D11Api = Vortice.Direct3D11.D3D11;

namespace GravityCapture.Services
{
    public static class ScreenCapture
    {
        public static (bool ok, Rectangle rectScreen, IntPtr hwndUsed) SelectRegion(IntPtr preferredHwnd)
            => OverlaySelector.Select(preferredHwnd);

        public static IntPtr ResolveArkWindow()
        {
            foreach (var p in Process.GetProcessesByName("arkascended"))
                if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;

            IntPtr found = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                var title = GetWindowText(h);
                if (!string.IsNullOrEmpty(title) &&
                    title.IndexOf("ARK", StringComparison.OrdinalIgnoreCase) >= 0)
                { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public static bool TryNormalizeRect(IntPtr hwnd, Rectangle screenRect,
            out double nx, out double ny, out double nw, out double nh)
        {
            nx = ny = nw = nh = 0;
            if (hwnd == IntPtr.Zero) return false;

            var client = GetClientScreenRect(hwnd);
            if (client.Width <= 0 || client.Height <= 0) return false;

            var inter = Rectangle.Intersect(screenRect, client);
            if (inter.Width <= 0 || inter.Height <= 0) return false;

            nx = (inter.X - client.X) / (double)client.Width;
            ny = (inter.Y - client.Y) / (double)client.Height;
            nw = inter.Width / (double)client.Width;
            nh = inter.Height / (double)client.Height;
            return true;
        }

        public static bool TryNormalizeRectDesktop(Rectangle screenRect,
            out double nx, out double ny, out double nw, out double nh)
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            nx = (screenRect.X - b.X) / (double)b.Width;
            ny = (screenRect.Y - b.Y) / (double)b.Height;
            nw = screenRect.Width / (double)b.Width;
            nh = screenRect.Height / (double)b.Height;
            return true;
        }

        // Strict capture for posting: WGC only for window, GDI for desktop.
        public static Bitmap Capture(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return CaptureDesktopFull();
            if (TryCaptureWgc(hwnd, out var bmp, out _)) return bmp;
            throw new InvalidOperationException("WGC failed for window capture.");
        }

        // Robust capture for preview: try WGC, else screen fallback of window rect.
        public static Bitmap CaptureForPreview(IntPtr hwnd, out bool usedFallback, out string? failReason)
        {
            usedFallback = false;
            if (hwnd == IntPtr.Zero) { failReason = "no hwnd"; return CaptureDesktopFull(); }
            if (TryCaptureWgc(hwnd, out var bmp, out failReason)) return bmp;
            usedFallback = true;
            failReason ??= "WGC unavailable";
            return CaptureWindowFallback(hwnd);
        }

        public static Bitmap CaptureCropNormalized(IntPtr hwnd, double nx, double ny, double nw, double nh)
        {
            if (hwnd == IntPtr.Zero)
            {
                var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                int rx = b.X + (int)Math.Round(nx * b.Width);
                int ry = b.Y + (int)Math.Round(ny * b.Height);
                int rw = Math.Max(1, (int)Math.Round(nw * b.Width));
                int rh = Math.Max(1, (int)Math.Round(nh * b.Height));

                var bmpDesk = new Bitmap(rw, rh, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bmpDesk))
                    g.CopyFromScreen(rx, ry, 0, 0, new Size(rw, rh), CopyPixelOperation.SourceCopy);
                return bmpDesk;
            }

            using var full = Capture(hwnd);

            var rx2 = (int)Math.Round(nx * full.Width);
            var ry2 = (int)Math.Round(ny * full.Height);
            var rw2 = Math.Clamp((int)Math.Round(nw * full.Width), 1, full.Width);
            var rh2 = Math.Clamp((int)Math.Round(nh * full.Height), 1, full.Height);

            rx2 = Math.Clamp(rx2, 0, full.Width - 1);
            ry2 = Math.Clamp(ry2, 0, full.Height - 1);
            rw2 = Math.Clamp(rw2, 1, full.Width - rx2);
            rh2 = Math.Clamp(rh2, 1, full.Height - ry2);

            var crop = new Rectangle(rx2, ry2, rw2, rh2);
            var bmp = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppPArgb);
            using var gg = Graphics.FromImage(bmp);
            gg.DrawImage(full, new Rectangle(0, 0, crop.Width, crop.Height), crop, GraphicsUnit.Pixel);
            return bmp;
        }

        public static byte[] ToJpegBytes(Bitmap bmp, int quality)
        {
            using var ms = new System.IO.MemoryStream();
            var codec = GetEncoder(ImageFormat.Jpeg);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(quality, 50, 100));
            bmp.Save(ms, codec, ep);
            return ms.ToArray();
        }

        public static bool TryGetWindowRect(IntPtr hwnd, out Rectangle r)
        {
            r = Rectangle.Empty;
            if (hwnd == IntPtr.Zero) return false;
            if (!GetWindowRect(hwnd, out var wr)) return false;
            r = Rectangle.FromLTRB(wr.left, wr.top, wr.right, wr.bottom);
            return r.Width > 0 && r.Height > 0;
        }

        // -------- WGC (ignores occlusion) --------
        private static bool TryCaptureWgc(IntPtr hwnd, out Bitmap bmp, out string? reason)
        {
            bmp = null!;
            reason = null;
            try
            {
                if (Environment.OSVersion.Version.Major < 10) { reason = "OS<10"; return false; }

                var item = CreateItemForWindow(hwnd);
                if (item is null || item.Size.Width <= 0 || item.Size.Height <= 0)
                { reason = "CreateForWindow failed"; return false; }

                using var device = CreateD3DDevice();
                using var d3d = CreateDirect3DDevice(device);

                using var pool = Direct3D11CaptureFramePool.Create(
                    d3d, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
                using var session = pool.CreateCaptureSession(item);
                session.IsCursorCaptureEnabled = false;
                session.IsBorderRequired = false;

                Direct3D11CaptureFrame? frame = null;

                pool.FrameArrived += (s, e) =>
                {
                    frame ??= s.TryGetNextFrame();
                };

                session.StartCapture();

                var t0 = Environment.TickCount;
                while (frame == null && Environment.TickCount - t0 < 1500)
                {
                    frame = pool.TryGetNextFrame();
                    Thread.Sleep(16);
                }

                session.Dispose();

                if (frame is null) { reason = "no frames (fullscreen/elevation/protected?)"; return false; }

                using (frame)
                {
                    var tex = GetD3DTexture2D(device, frame.Surface);
                    bmp = CopyTextureToBitmap(device, tex);
                    tex.Dispose();
                }
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                bmp = null!;
                return false;
            }
        }

        private static bool TryCaptureWgc(IntPtr hwnd, out Bitmap bmp)
            => TryCaptureWgc(hwnd, out bmp, out _);

        private static Bitmap CaptureDesktopFull()
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(b.X, b.Y, 0, 0, b.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }

        private static Bitmap CaptureWindowFallback(IntPtr hwnd)
        {
            if (!GetWindowRect(hwnd, out var r) || r.right <= r.left || r.bottom <= r.top)
                throw new InvalidOperationException("Bad window rect.");
            int w = r.right - r.left, h = r.bottom - r.top;
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(r.left, r.top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            return bmp;
        }

        private static class OverlaySelector
        {
            public static (bool ok, Rectangle rectScreen, IntPtr hwndUsed) Select(IntPtr preferredHwnd)
            {
                var win = new GravityCapture.Views.RegionSelectorWindow(preferredHwnd);
                try
                {
                    var ok = win.ShowDialog() == true;
                    return (ok, win.SelectedRect, win.CapturedHwnd != IntPtr.Zero ? win.CapturedHwnd : preferredHwnd);
                }
                finally
                {
                    if (win.IsVisible) win.Close();
                }
            }
        }

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

        // ---- WGC interop helpers ----
        private static GraphicsCaptureItem? CreateItemForWindow(IntPtr hwnd)
        {
            var interop = (IGraphicsCaptureItemInterop)Activator.CreateInstance(typeof(GraphicsCaptureItem))!;
            var iid = typeof(GraphicsCaptureItem).GUID;
            int hr = interop.CreateForWindow(hwnd, ref iid, out GraphicsCaptureItem item);
            return hr == 0 ? item : null;
        }

        private static ID3D11Device CreateD3DDevice()
        {
            _ = D3D11Api.D3D11CreateDevice(
                null, DriverType.Hardware,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                null, out ID3D11Device device);
            if (device == null) throw new InvalidOperationException("D3D11CreateDevice failed.");
            return device;
        }

        private static Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice CreateDirect3DDevice(ID3D11Device device)
        {
            using var dxgi = device.QueryInterface<DXGI.IDXGIDevice>();
            int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var unk);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            return WinRT.MarshalInterface<Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice>.FromAbi(unk);
        }

        private static ID3D11Texture2D GetD3DTexture2D(ID3D11Device device, Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface surface)
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
                int width = (int)desc.Width;
                int height = (int)desc.Height;
                int rowPitch = (int)map.RowPitch;

                var bmp = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);

                int rowBytes = width * 4;
                unsafe
                {
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(
                            (void*)IntPtr.Add(map.DataPointer, y * rowPitch),
                            (void*)IntPtr.Add(data.Scan0, y * data.Stride),
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

        [ComImport, Guid("3628E81B-3CAC-4A9E-8545-75C971C37E80"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            int CreateForWindow(IntPtr hwnd, ref Guid iid, out GraphicsCaptureItem result);
            int CreateForMonitor(IntPtr hmon, ref Guid iid, out GraphicsCaptureItem result);
        }

        [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDirect3DDxgiInterfaceAccess
        {
            IntPtr GetInterface(ref Guid iid);
        }

        [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        private static string GetWindowText(IntPtr hWnd)
        {
            int length = GetWindowTextLength(hWnd);
            var sb = new System.Text.StringBuilder(length + 1);
            _ = GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private struct RECT { public int left, top, right, bottom; }
        private struct POINT { public int x, y; }
    }
}
