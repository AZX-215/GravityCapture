using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GravityCapture.Views
{
    public partial class RegionSelectorWindow : Window
    {
        private System.Windows.Point _start;
        private bool _dragging;

        public System.Windows.Rect SelectedRect { get; private set; } = System.Windows.Rect.Empty;

        public RegionSelectorWindow()
        {
            InitializeComponent();

            // Window-level preview handlers so clicks anywhere register
            PreviewMouseLeftButtonDown += OnDown;
            PreviewMouseMove += OnMove;
            PreviewMouseLeftButtonUp += OnUp;

            KeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape)
                {
                    DialogResult = false;
                    Close();
                }
            };
        }

        // Accept bounds in *pixels*; convert to DIPs so the overlay aligns on scaled monitors
        public RegionSelectorWindow(System.Drawing.Rectangle screenPxRect) : this()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;

            Left   = screenPxRect.Left   / sx;
            Top    = screenPxRect.Top    / sy;
            Width  = screenPxRect.Width  / sx;
            Height = screenPxRect.Height / sy;

            if (Width < 50 || Height < 50)
                WindowState = WindowState.Maximized;
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            // Ensure we take focus and receive input
            Activate();
            RootCanvas.Focusable = true;
            RootCanvas.Focus();
        }

        private void OnDown(object? sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _dragging = true;
            _start = e.GetPosition(RootCanvas);     // coords relative to Canvas
            Canvas.SetLeft(Sel, _start.X);
            Canvas.SetTop(Sel,  _start.Y);
            Sel.Width = Sel.Height = 0;
            Sel.Visibility = Visibility.Visible;
            CaptureMouse();
            e.Handled = true;
        }

        private void OnMove(object? sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_dragging) return;
            var p = e.GetPosition(RootCanvas);

            var x = Math.Min(_start.X, p.X);
            var y = Math.Min(_start.Y, p.Y);
            var w = Math.Abs(p.X - _start.X);
            var h = Math.Abs(p.Y - _start.Y);

            // Clamp inside canvas
            x = Math.Max(0, Math.Min(x, RootCanvas.ActualWidth));
            y = Math.Max(0, Math.Min(y, RootCanvas.ActualHeight));
            w = Math.Max(0, Math.Min(w, RootCanvas.ActualWidth - x));
            h = Math.Max(0, Math.Min(h, RootCanvas.ActualHeight - y));

            Canvas.SetLeft(Sel, x);
            Canvas.SetTop(Sel,  y);
            Sel.Width = w;
            Sel.Height = h;

            e.Handled = true;
        }

        private void OnUp(object? sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();

            var x = Canvas.GetLeft(Sel);
            var y = Canvas.GetTop(Sel);
            var w = Sel.Width;
            var h = Sel.Height;

            if (w < 2 || h < 2) { DialogResult = false; Close(); return; }

            // Convert canvas coords → screen coords
            var p0 = RootCanvas.PointToScreen(new System.Windows.Point(x, y));
            SelectedRect = new System.Windows.Rect(p0.X, p0.Y, w, h);

            DialogResult = true;
            Close();

            e.Handled = true;
        }
    }
}
