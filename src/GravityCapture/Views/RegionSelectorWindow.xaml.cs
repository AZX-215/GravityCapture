using System;
using System.Windows;
using System.Windows.Input;

namespace GravityCapture.Views
{
    public partial class RegionSelectorWindow : Window
    {
        // Use WPF types explicitly to avoid ambiguity with System.Drawing / WinForms
        private System.Windows.Point _start;
        private bool _drag;

        public System.Windows.Rect SelectedRect { get; private set; }

        public RegionSelectorWindow()
        {
            InitializeComponent();

            // ESC cancels
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    DialogResult = false;
                    Close();
                }
            };

            // Fullscreen overlay on primary display
            Loaded += (s, e) =>
            {
                Left = 0;
                Top = 0;
                Width = SystemParameters.PrimaryScreenWidth;
                Height = SystemParameters.PrimaryScreenHeight;
            };
        }

        // Mouse handlers — fully-qualified WPF event args
        private void OnDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _drag = true;
            _start = e.GetPosition(RootCanvas);
            Sel.Visibility = Visibility.Visible;

            System.Windows.Controls.Canvas.SetLeft(Sel, _start.X);
            System.Windows.Controls.Canvas.SetTop(Sel, _start.Y);
            Sel.Width = 0;
            Sel.Height = 0;
        }

        private void OnMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_drag) return;

            var p = e.GetPosition(RootCanvas);
            var x = Math.Min(p.X, _start.X);
            var y = Math.Min(p.Y, _start.Y);
            var w = Math.Abs(p.X - _start.X);
            var h = Math.Abs(p.Y - _start.Y);

            System.Windows.Controls.Canvas.SetLeft(Sel, x);
            System.Windows.Controls.Canvas.SetTop(Sel, y);
            Sel.Width = w;
            Sel.Height = h;
        }

        private void OnUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_drag) return;
            _drag = false;

            var x = System.Windows.Controls.Canvas.GetLeft(Sel);
            var y = System.Windows.Controls.Canvas.GetTop(Sel);
            var w = Sel.Width;
            var h = Sel.Height;

            if (w < 5 || h < 5)
            {
                DialogResult = false;
                Close();
                return;
            }

            SelectedRect = new System.Windows.Rect(x, y, w, h);
            DialogResult = true;
            Close();
        }
    }
}
