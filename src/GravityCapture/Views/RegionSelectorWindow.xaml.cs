using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
// Explicit WPF aliases to avoid clashes with Windows Forms / System.Drawing.
using WpfPoint = System.Windows.Point;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using DrawingRectangle = System.Drawing.Rectangle;

namespace GravityCapture.Views
{
    public partial class RegionSelectorWindow : Window
    {
        private readonly IntPtr _preferredHwnd;

        private WpfPoint _start;
        private bool _dragging;

        public DrawingRectangle SelectedRect { get; private set; } = DrawingRectangle.Empty;
        public IntPtr CapturedHwnd { get; private set; } = IntPtr.Zero;

        private const int MinSelWidth = 40;
        private const int MinSelHeight = 40;

        public RegionSelectorWindow(IntPtr preferredHwnd)
        {
            InitializeComponent();
            _preferredHwnd = preferredHwnd;

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

                Sel!.Visibility = Visibility.Collapsed;
                RootCanvas!.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 0, 0, 0));
            };

            KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { DialogResult = false; Close(); } };
        }

        private void OnDown(object sender, WpfMouseButtonEventArgs e)
        {
            _dragging = true;
            _start = e.GetPosition(RootCanvas!);

            // Lock the window under the cursor (top-level).
            var screenPt = PointToScreen(_start);
            CapturedHwnd = GetAncestor(WindowFromPhysicalPoint((int)screenPt.X, (int)screenPt.Y), 2 /*GA_ROOT*/);

            System.Windows.Controls.Canvas.SetLeft(Sel!, _start.X);
            System.Windows.Controls.Canvas.SetTop(Sel!, _start.Y);
            Sel!.Width = 0;
            Sel!.Height = 0;
            Sel!.Visibility = Visibility.Visible;
            CaptureMouse();
        }

        private void OnMove(object sender, WpfMouseEventArgs e)
        {
            if (!_dragging) return;

            var cur = e.GetPosition(RootCanvas!);
            var x = Math.Min(cur.X, _start.X);
            var y = Math.Min(cur.Y, _start.Y);
            var w = Math.Abs(cur.X - _start.X);
            var h = Math.Abs(cur.Y - _start.Y);

            System.Windows.Controls.Canvas.SetLeft(Sel!, x);
            System.Windows.Controls.Canvas.SetTop(Sel!, y);
            Sel!.Width = w;
            Sel!.Height = h;
        }

        private void OnUp(object sender, WpfMouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();

            var x = System.Windows.Controls.Canvas.GetLeft(Sel!);
            var y = System.Windows.Controls.Canvas.GetTop(Sel!);
            var w = Sel!.Width;
            var h = Sel!.Height;

            var tl = PointToScreen(new WpfPoint(x, y));
            var br = PointToScreen(new WpfPoint(x + w, y + h));

            int ix = (int)Math.Round(tl.X);
            int iy = (int)Math.Round(tl.Y);
            int iw = Math.Max(0, (int)Math.Round(br.X - tl.X));
            int ih = Math.Max(0, (int)Math.Round(br.Y - tl.Y));

            if (iw < MinSelWidth || ih < MinSelHeight)
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
