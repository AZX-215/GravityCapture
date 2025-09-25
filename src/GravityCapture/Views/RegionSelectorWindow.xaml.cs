using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DrawingRectangle = System.Drawing.Rectangle;

namespace GravityCapture.Views
{
    public partial class RegionSelectorWindow : Window
    {
        private readonly IntPtr _preferredHwnd;
        private Point _start;
        private bool _dragging;

        public DrawingRectangle SelectedRect { get; private set; } = DrawingRectangle.Empty;
        public IntPtr CapturedHwnd { get; private set; } = IntPtr.Zero;

        // Minimum crop size (in device pixels)
        private const int MinWidth = 40;
        private const int MinHeight = 40;

        public RegionSelectorWindow(IntPtr preferredHwnd)
        {
            InitializeComponent();
            _preferredHwnd = preferredHwnd;

            // Go borderless-fullscreen over target window or entire virtual screen.
            Loaded += (_, __) =>
            {
                if (_preferredHwnd != IntPtr.Zero && GetWindowRect(_preferredHwnd, out var wr))
                {
                    Left = wr.left; Top = wr.top;
                    Width = Math.Max(1, wr.right - wr.left);
                    Height = Math.Max(1, wr.bottom - wr.top);
                }
                else
                {
                    Left = SystemParameters.VirtualScreenLeft;
                    Top = SystemParameters.VirtualScreenTop;
                    Width = SystemParameters.VirtualScreenWidth;
                    Height = SystemParameters.VirtualScreenHeight;
                }

                Sel.Visibility = Visibility.Collapsed;
                RootCanvas.Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
            };

            KeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; Close(); } };
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _dragging = true;
            _start = e.GetPosition(RootCanvas);

            // Lock the window under the cursor (top-level).
            var screenPt = PointToScreen(_start);
            CapturedHwnd = GetAncestor(WindowFromPhysicalPoint((int)screenPt.X, (int)screenPt.Y), 2 /*GA_ROOT*/);

            Canvas.SetLeft(Sel, _start.X);
            Canvas.SetTop(Sel, _start.Y);
            Sel.Width = 0;
            Sel.Height = 0;
            Sel.Visibility = Visibility.Visible;
            CaptureMouse();
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;

            var cur = e.GetPosition(RootCanvas);
            var x = Math.Min(cur.X, _start.X);
            var y = Math.Min(cur.Y, _start.Y);
            var w = Math.Abs(cur.X - _start.X);
            var h = Math.Abs(cur.Y - _start.Y);

            Canvas.SetLeft(Sel, x);
            Canvas.SetTop(Sel, y);
            Sel.Width = w;
            Sel.Height = h;
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();

            var x = Canvas.GetLeft(Sel);
            var y = Canvas.GetTop(Sel);
            var w = Sel.Width;
            var h = Sel.Height;

            // Convert to screen coords
            var tl = PointToScreen(new Point(x, y));
            var br = PointToScreen(new Point(x + w, y + h));

            int ix = (int)Math.Round(tl.X);
            int iy = (int)Math.Round(tl.Y);
            int iw = Math.Max(0, (int)Math.Round(br.X - tl.X));
            int ih = Math.Max(0, (int)Math.Round(br.Y - tl.Y));

            // Enforce minimum usable size
            if (iw < MinWidth || ih < MinHeight)
            {
                DialogResult = false;
                Close();
                return;
            }

            SelectedRect = new DrawingRectangle(ix, iy, iw, ih);
            DialogResult = true;
            Close();
        }

        // ---- Win32 interop ----

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPhysicalPoint(POINT Point);
        private static IntPtr WindowFromPhysicalPoint(int x, int y)
            => WindowFromPhysicalPoint(new POINT { x = x, y = y });

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, int gaFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }
    }
}
