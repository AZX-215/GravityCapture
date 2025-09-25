using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace GravityCapture.Views
{
    public partial class RegionSelectorWindow : Window
    {
        private System.Windows.Point _startCanvas;
        private bool _dragging;

        /// <summary>Screen-space rectangle in <b>physical pixels</b>.</summary>
        public System.Drawing.Rectangle SelectedRect { get; private set; } = System.Drawing.Rectangle.Empty;

        /// <summary>Top-level HWND that we locked at mouse down.</summary>
        public IntPtr CapturedHwnd { get; private set; } = IntPtr.Zero;

        private readonly IntPtr _preferredHwnd;

        public RegionSelectorWindow(IntPtr preferredHwnd)
        {
            InitializeComponent();
            _preferredHwnd = preferredHwnd;

            // Global preview handlers so clicks anywhere on the overlay register.
            PreviewMouseLeftButtonDown += OnDown;
            PreviewMouseMove += OnMove;
            PreviewMouseLeftButtonUp += OnUp;

            Loaded += OnLoaded;

            KeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape)
                {
                    DialogResult = false;
                    Close();
                }
            };

            // Default to full screen; we may resize on load if a preferred hwnd is valid.
            WindowState = WindowState.Maximized;
            Topmost = true;
            ShowInTaskbar = false;
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            // If a preferred window is given, resize overlay to that rect in DIPs.
            if (_preferredHwnd != IntPtr.Zero && TryGetWindowRect(_preferredHwnd, out var wr))
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                Left   = wr.left   / dpi.DpiScaleX;
                Top    = wr.top    / dpi.DpiScaleY;
                Width  = (wr.right  - wr.left) / dpi.DpiScaleX;
                Height = (wr.bottom - wr.top ) / dpi.DpiScaleY;
                WindowState = WindowState.Normal;
            }

            Activate();
            RootCanvas.Focusable = true;
            RootCanvas.Focus();
        }

        private void OnDown(object? sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _dragging = true;
            _startCanvas = e.GetPosition(RootCanvas);

            // Resolve HWND under the mouse at press time (in physical px)
            var pScreenDip = PointToScreen(_startCanvas);
            var dpi = VisualTreeHelper.GetDpi(this);
            var pt = new POINT
            {
                x = (int)Math.Round(pScreenDip.X * dpi.DpiScaleX),
                y = (int)Math.Round(pScreenDip.Y * dpi.DpiScaleY)
            };
            var child = WindowFromPoint(pt);
            CapturedHwnd = GetAncestor(child, GA_ROOT); // top-level

            System.Windows.Controls.Canvas.SetLeft(Sel, _startCanvas.X);
            System.Windows.Controls.Canvas.SetTop(Sel, _startCanvas.Y);
            Sel.Width = Sel.Height = 0;
            Sel.Visibility = Visibility.Visible;

            CaptureMouse();
            e.Handled = true;
        }

        private void OnMove(object? sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_dragging) return;

            var p = e.GetPosition(RootCanvas);

            var x = Math.Min(_startCanvas.X, p.X);
            var y = Math.Min(_startCanvas.Y, p.Y);
            var w = Math.Abs(p.X - _startCanvas.X);
            var h = Math.Abs(p.Y - _startCanvas.Y);

            // Clamp inside canvas
            x = Math.Max(0, Math.Min(x, RootCanvas.ActualWidth));
            y = Math.Max(0, Math.Min(y, RootCanvas.ActualHeight));
            w = Math.Max(0, Math.Min(w, RootCanvas.ActualWidth - x));
            h = Math.Max(0, Math.Min(h, RootCanvas.ActualHeight - y));

            System.Windows.Controls.Canvas.SetLeft(Sel, x);
            System.Windows.Controls.Canvas.SetTop(Sel, y);
            Sel.Width = w;
            Sel.Height = h;

            e.Handled = true;
        }

        private void OnUp(object? sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();

            var x = System.Windows.Controls.Canvas.GetLeft(Sel);
            var y = System.Windows.Controls.Canvas.GetTop(Sel);
            var w = Sel.Width;
            var h = Sel.Height;

            if (w < 2 || h < 2) { DialogResult = false; Close(); return; }

            // Convert the canvas rectangle to PHYSICAL screen pixels.
            var p0Dip = RootCanvas.PointToScreen(new System.Windows.Point(x, y));
            var dpi = VisualTreeHelper.GetDpi(this);

            int sx = (int)Math.Round(p0Dip.X * dpi.DpiScaleX);
            int sy = (int)Math.Round(p0Dip.Y * dpi.DpiScaleY);
            int sw = (int)Math.Round(w * dpi.DpiScaleX);
            int sh = (int)Math.Round(h * dpi.DpiScaleY);

            SelectedRect = new System.Drawing.Rectangle(sx, sy, sw, sh);

            DialogResult = true;
            Close();

            e.Handled = true;
        }

        // -------- Win32 helpers --------

        private const uint GA_ROOT = 2;

        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        private static bool TryGetWindowRect(IntPtr hwnd, out RECT r) => GetWindowRect(hwnd, out r);

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    }
}
