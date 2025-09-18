using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace GravityCapture.Services
{
    public static class ScreenCapture
    {
        // ---------- Public API (unchanged) ----------

        public static Bitmap Capture(bool activeWindow)
        {
            if (activeWindow)
            {
                var hwnd = PInvoke.GetForegroundWindow();
                if (TryCaptureWindowWgc(hwnd, out var bmp))
                    return bmp;
                return CaptureGdiFullOrWindow(hwnd, onlyWindow: true);
            }
            else
            {
                // whole desktop
                if (TryCaptureMonitorWgc(IntPtr.Zero, out var bmp))
                    return bmp;
                return CaptureGdiFullOrWindow(HWND.Null, onlyWindow: false);
            }
        }

        public static Bitmap Capture(IntPtr hwnd)
        {
            if (TryCaptureWindowWgc(hwnd, out var bmp))
                return bmp;
            return CaptureGdiFullOrWindow(hwnd, onlyWindow: true);
        }

        public static Bitmap CaptureCropNormalized(IntPtr hwnd, double x, double y, double w, double h)
        {
            using var baseBmp = Capture(hwnd == IntPtr.Zero ? PInvoke.GetForegroundWindow() : hwnd);
            int cx = (int)Math.Round(x * baseBmp.Width);
            int cy = (int)Math.Round(y * baseBmp.Height);
            int cw = (int)Math.Round(w * baseBmp.Width);
            int ch = (int)Math.Round(h * baseBmp.Height);

            cx = Math.Clamp(cx, 0, baseBmp.Width - 1);
            cy = Math.Clamp(cy, 0, baseBmp.Height - 1);
            cw = Math.Clamp(cw, 1, baseBmp.Width - cx);
            ch = Math.Clamp(ch, 1, baseBmp.Height - cy);

            var rect = new Rectangle(cx, cy, cw, ch);
            var crop = new Bitmap(rect.Width, rect.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(crop))
            {
                g.DrawImage(baseBmp, new Rectangle(0, 0, rect.Width, rect.Height), rect, GraphicsUnit.Pixel);
            }
            return crop;
        }

        public static byte[] ToJpegBytes(Bitmap bmp, int quality)
        {
            using var ms = new MemoryStream();
            var enc = GetJpegEncoder();
            var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(quality, 10, 100));
            bmp.Save(ms, enc, encParams);
            return ms.ToArray();
        }

        // ---------- Windows Graphics Capture (HDR-aware) ----------

        private static bool TryCaptureWindowWgc(IntPtr hwnd, out Bitmap bmp)
        {
            bmp = null!;
            try
            {
                if (!GraphicsCaptureSession.IsSupported()) return false;

                var item = CreateItemForWindow(hwnd);
                if (item == null) return false;

                return TryCaptureItemOnce(item, out bmp);
            }
            catch { bmp = null!; return false; }
        }

        private static bool TryCaptureMonitorWgc(IntPtr hmon, out Bitmap bmp)
        {
            bmp = null!;
            try
            {
                if (!GraphicsCaptureSession.IsSupported()) return false;

                // If hmon == IntPtr.Zero, get primary monitor
                if (hmon == IntPtr.Zero)
                {
                    var pt = new POINT(1, 1);
                    hmon = PInvoke.MonitorFromPoint(pt, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
                }

                var item = CreateItemForMonitor(hmon);
                if (item == null) return false;

                return TryCaptureItemOnce(item, out bmp);
            }
            catch { bmp = null!; return false; }
        }

        private static GraphicsCaptureItem? CreateItemForWindow(IntPtr hwnd)
        {
            var interop = WindowsRuntimeMarshal.GetActivationFactory(typeof(GraphicsCaptureItem))
                          as IGraphicsCaptureItemInterop;
            Guid iid = typeof(GraphicsCaptureItem).GUID;
            object itemObj;
            interop!.CreateForWindow(hwnd, iid, out itemObj);
            return (GraphicsCaptureItem)itemObj;
        }

        private static GraphicsCaptureItem? CreateItemForMonitor(IntPtr hmon)
        {
            var interop = WindowsRuntimeMarshal.GetActivationFactory(typeof(GraphicsCaptureItem))
                          as IGraphicsCaptureItemInterop;
            Guid iid = typeof(GraphicsCaptureItem).GUID;
            object itemObj;
            interop!.CreateForMonitor(hmon, iid, out itemObj);
            return (GraphicsCaptureItem)itemObj;
        }

        private static bool TryCaptureItemOnce(GraphicsCaptureItem item, out Bitmap bitmap)
        {
            bitmap = null!;

            // Create D3D device for capture
            ID3D11Device d3d = CreateD3DDevice();
            using var winrtDevice = CreateWinRtD3DDevice(d3d);

            int w = Math.Max(1, item.Size.Width);
            int h = Math.Max(1, item.Size.Height);

            using var framePool = Direct3D11CaptureFramePool.Create(winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
            using var session = framePool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;

            Direct3D11CaptureFrame? grabbed = null;
            using var gotEvt = new ManualResetEvent(false);
            framePool.FrameArrived += (_, __) =>
            {
                grabbed = framePool.TryGetNextFrame();
                gotEvt.Set();
            };

            session.StartCapture();

            // Wait briefly for a frame
            gotEvt.WaitOne(120);
            if (grabbed == null)
            {
                session.Close();
                framePool.Close();
                return false;
            }

            try
            {
                // Get native texture
                using var surf = grabbed.Surface;
                var unknown = Marshal.GetIUnknownForObject(surf);
                var tex = (ID3D11Texture2D)Marshal.GetObjectForIUnknown(unknown);
                Marshal.Release(unknown);

                // Copy to a CPU-readable staging texture
                var desc = tex.GetDesc();
                desc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
                desc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
                desc.BindFlags = 0;
                desc.MiscFlags = 0;

                var d3dTex = d3d.CreateTexture2D(desc);
                var ctx = d3d.GetImmediateContext();
                ctx.CopyResource(d3dTex, tex);

                // Map and create a Bitmap
                var mapped = ctx.Map(d3dTex, 0, D3D11_MAP.D3D11_MAP_READ, 0);
                try
                {
                    bitmap = new Bitmap((int)desc.Width, (int)desc.Height, PixelFormat.Format32bppArgb);
                    var bd = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                        ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                    unsafe
                    {
                        byte* src = (byte*)mapped.pData;
                        byte* dst = (byte*)bd.Scan0;
                        for (int y = 0; y < bitmap.Height; y++)
                        {
                            Buffer.MemoryCopy(src + y * mapped.RowPitch, dst + y * bd.Stride,
                                bd.Stride, bitmap.Width * 4);
                        }
                    }

                    bitmap.UnlockBits(bd);
                }
                finally
                {
                    ctx.Unmap(d3dTex, 0);
                    ctx.Dispose();
                    d3dTex.Dispose();
                    tex.Dispose();
                }

                // Quick tonemap if needed (if the frame is linear/scRGB-ish it will look too bright)
                ToneMapIfHDRInPlace(bitmap);

                return true;
            }
            finally
            {
                grabbed.Dispose();
                session.Close();
                framePool.Close();
            }
        }

        // Simple heuristic: if 95th percentile is too bright, compress highlights & apply mild gamma
        private static void ToneMapIfHDRInPlace(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride, bytes = Math.Abs(stride) * h;
                byte[] buf = new byte[bytes];
                Marshal.Copy(data.Scan0, buf, 0, bytes);

                int[] hist = new int[256];
                double sum = 0;
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + x * 4;
                        byte b = buf[i + 0], g = buf[i + 1], r = buf[i + 2];
                        byte m = (byte)Math.Max(r, Math.Max(g, b));
                        hist[m]++; sum += (r + g + b) / 3.0;
                    }
                }
                int total = w * h;
                int target = (int)(total * 0.95);
                int acc = 0, p95 = 255;
                for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= target) { p95 = i; break; } }
                double mean = sum / Math.Max(1, total);

                double scale = p95 > 235 ? 220.0 / p95 : 1.0;
                double gamma = mean > 160 ? 1.25 : 1.0;
                if (Math.Abs(scale - 1.0) < 0.01 && Math.Abs(gamma - 1.0) < 0.01) return;

                byte[] lut = new byte[256];
                for (int i = 0; i < 256; i++)
                {
                    double v = i / 255.0;
                    v = Math.Pow(Math.Min(1.0, v * scale), gamma);
                    lut[i] = (byte)(v * 255.0 + 0.5);
                }

                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + x * 4;
                        buf[i + 0] = lut[buf[i + 0]];
                        buf[i + 1] = lut[buf[i + 1]];
                        buf[i + 2] = lut[buf[i + 2]];
                    }
                }
                Marshal.Copy(buf, 0, data.Scan0, bytes);
            }
            finally { bmp.UnlockBits(data); }
        }

        // ---------- Fallback: classic GDI capture ----------

        private static Bitmap CaptureGdiFullOrWindow(HWND hwnd, bool onlyWindow)
        {
            RECT r;
            if (onlyWindow && hwnd != HWND.Null && PInvoke.GetClientRect(hwnd, out var rc))
            {
                POINT tl = new(rc.left, rc.top);
                PInvoke.ClientToScreen(hwnd, ref tl);
                r = new RECT(tl.X, tl.Y, tl.X + rc.right, tl.Y + rc.bottom);
            }
            else
            {
                var hmon = PInvoke.MonitorFromWindow(hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
                PInvoke.GetMonitorInfo(hmon, out MONITORINFO mi);
                r = mi.rcMonitor;
            }

            int w = Math.Max(1, r.right - r.left);
            int h = Math.Max(1, r.bottom - r.top);

            var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(r.left, r.top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            return bmp;
        }

        private static ImageCodecInfo GetJpegEncoder()
        {
            foreach (var e in ImageCodecInfo.GetImageEncoders())
                if (e.MimeType == "image/jpeg") return e;
            throw new InvalidOperationException("JPEG encoder not found");
        }

        // ---------- D3D helpers & interop ----------

        private static ID3D11Device CreateD3DDevice()
        {
            ID3D11Device? device = null;
            var flags = (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT;

            var levels = new[]
            {
                D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1,
                D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
                D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_1,
                D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_0,
            };

            var hr = PInvoke.D3D11CreateDevice(
                null, // default adapter
                D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                HMODULE.Null,
                flags,
                levels,
                (uint)levels.Length,
                7, // D3D11_SDK_VERSION
                out device,
                out _,
                out _);

            if (hr.Failed || device == null)
                throw new InvalidOperationException("Failed to create D3D11 device for GraphicsCapture.");

            return device;
        }

        private static IDirect3DDevice CreateWinRtD3DDevice(ID3D11Device d3d)
        {
            var access = d3d.As<IDXGIDevice>();
            IntPtr ptr = Marshal.GetIUnknownForObject(access);
            try
            {
                return WindowsRuntimeMarshal.AsWindowsFoundationObject<IDirect3DDevice>(ptr);
            }
            finally
            {
                Marshal.Release(ptr);
                access.Dispose();
            }
        }

        // WinRT interop to create GraphicsCaptureItem for HWND/HMONITOR
        [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            void CreateForWindow(IntPtr window, [In] ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
            void CreateForMonitor(IntPtr monitor, [In] ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        }
    }

    // ------------- minimal COM interop wrappers for D3D11 we use -------------

    [ComImport, Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140")]
    internal class D3D11DeviceCom { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140")]
    internal interface ID3D11Device
    {
        IntPtr CreateBuffer(); // not used (vtable padding)
        // We don’t need the full interface in C#, we’ll just QI through built-in CsWin32 wrappers below
    }
}
