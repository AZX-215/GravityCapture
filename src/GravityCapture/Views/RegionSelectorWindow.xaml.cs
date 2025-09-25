using System;
using System.Windows;
using System.Windows.Input;

namespace GravityCapture.Views
{
    public partial class RegionSelectorWindow : Window
    {
        private Point _start;
        private bool _dragging;

        public Rect SelectedRect { get; private set; } = Rect.Empty;

        // Default ctor: caller may set WindowState=Maximized for full-screen selection.
        public RegionSelectorWindow()
        {
            InitializeComponent();
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { DialogResult = false; Close(); }
            };
        }

        // Bounded overlay ctor: restrict selection to given screen rectangle.
        public RegionSelectorWindow(Rect screenBounds) : this()
        {
            Left = screenBounds.Left;
            Top  = screenBounds.Top;
            Width  = screenBounds.Width;
            Height = screenBounds.Height;
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

            // Convert to absolute screen coordinates
            var p0 = PointToScreen(new Point(x, y));
            SelectedRect = new Rect(p0.X, p0.Y, w, h);

            DialogResult = true;
            Close();
        }
    }
}
