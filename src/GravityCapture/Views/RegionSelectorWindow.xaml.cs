using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
            KeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; Close(); } };
        }

        // NEW: constructor that accepts screen bounds in *pixels* and converts to DIPs
        public RegionSelectorWindow(System.Drawing.Rectangle screenPxRect) : this()
        {
            // Ensure we have a valid DPI context
            var dpi = VisualTreeHelper.GetDpi(this);
            double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;

            Left   = screenPxRect.Left   / sx;
            Top    = screenPxRect.Top    / sy;
            Width  = screenPxRect.Width  / sx;
            Height = screenPxRect.Height / sy;

            // If bounds look invalid, fall back to full screen
            if (Width < 50 || Height < 50)
            {
                WindowState = WindowState.Maximized;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Make sure we receive mouse immediately
            Activate();
            Focus();
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _dragging = true;
            _start = e.GetPosition(this);
            Canvas.SetLeft(Sel, _start.X);
            Canvas.SetTop(Sel,  _start.Y);
            Sel.Width = Sel.Height = 0;
            Sel.Visibility = Visibility.Visible;
            CaptureMouse();
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            var p = e.GetPosition(this);

            var x = Math.Min(_start.X, p.X);
            var y = Math.Min(_start.Y, p.Y);
            var w = Math.Abs(p.X - _start.X);
            var h = Math.Abs(p.Y - _start.Y);

            Canvas.SetLeft(Sel, x);
            Canvas.SetTop(Sel,  y);
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

            if (w < 2 || h < 2) { DialogResult = false; Close(); return; }

            // Convert drawn rect to *screen* coords (DIPs → px handled by Win32 later)
            System.Windows.Point p0 = PointToScreen(new System.Windows.Point(x, y));
            SelectedRect = new System.Windows.Rect(p0.X, p0.Y, w, h);

            DialogResult = true;
            Close();
        }
    }
}
