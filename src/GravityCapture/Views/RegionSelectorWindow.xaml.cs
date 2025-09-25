using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DrawingRectangle = System.Drawing.Rectangle;
using WpfPoint = System.Windows.Point;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

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

        public RegionSelectorWindow() : this(IntPtr.Zero) { }
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

                RootCanvas.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(32, 0, 0, 0));
                Sel.Visibility = Visibility.Collapsed;

                Activate();
                RootCanvas.Focus();
            };
        }

        private void OnKeyDown(object? sender, WpfKeyEventArgs e)
        {
            if (e.Key == Key.Escape) { DialogResult = false; Close(); }
        }

        private void OnCancel(object? sender, WpfMouseButtonEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OnDown(object? sender, WpfMouseButtonEventArgs e)
        {
            _dragging = true;
            _start = e.GetPosition(RootCanvas);

            var screen = PointToScreen(_start);
            var pt = new POINT { x = (int)Math.Round(screen.X), y = (int)Math.Round(screen.Y) };
            CapturedHwnd = FindForeignRootAt(pt, _preferredHwnd);

            System.Windows.Controls.Canvas.SetLeft(Sel, _start.X);
            System.Windows.Controls.Canvas.SetTop(Sel, _start.Y);
            Sel.Width = 0; Sel.Height = 0;
            Sel.Visibility = Visibility.Visible;

            RootCanvas.CaptureMouse();
        }

        private void OnMove(object? sender, WpfMouseEventArgs e)
        {
            if (!_dragging) return;

            var cur = e.GetPosition(RootCanvas);
            var x = Math.Min(cur.X, _start.X);
            var y = Math.Min(cur.Y, _start.Y);
            var w = Math.Abs(cur.X - _start.X);
            var h = Math.Abs(cur.Y - _start.Y);

            System.Windows.Controls.Canvas.SetLeft(Sel, x);
            System.Windows.Controls.Canvas.SetTop(Sel, y);
            Sel.Width = w;
            Sel.Height = h;
        }

        private void OnUp(object? sender, WpfMouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            RootCanvas.ReleaseMouseCapture();

            var x = System.Windows.Controls.Canvas.GetLeft(Sel);
            var y = System.Windows.Controls.Canvas.GetTop(Sel);
            var w = Sel.Width;
            var h = Sel.Height;

            var tl = PointToScreen(new WpfPoint(x, y));
            var br = PointToScreen(new WpfPoint(x + w, y + h));

            int ix = (int)Math.Round(tl.X);
            int iy = (int)Math.Round(tl.Y);
            int iw = Math.Max(0, (int)Math.Round(br.X - tl.X));
            int ih = Math.Max(0, (int)Math.Round(br.Y - tl.Y));

            if (iw < MinSelWidth || ih < MinSelHeight) { DialogResult = false; Close(); return; }

            SelectedRect = new DrawingRectangle(ix, iy, iw, ih);
            DialogResult = true;
            Close();
        }

        private static IntPtr FindForeignRootAt(POINT pt, IntPtr fallback)
        {
            uint curPid = GetCurrentProcessId();
            IntPtr h = WindowFromPoint(pt);

            for (int i = 0; i < 32 && h != IntPtr.Zero; i++)
            {
                IntPtr root = GetAncestor(h, GA_ROOT);
                if (root == IntPtr.Zero) break;

                GetWindowThreadProcessId(root, out uint pid);
                if (pid != curPid) return root;

                root = GetWindow(root, GW_HWNDPREV);
                h = root;
            }
            return fallback;
        }

        private const int GA_ROOT = 2;
        private const uint GW_HWNDPREV = 3;

        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, int gaFlags);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    }
}
