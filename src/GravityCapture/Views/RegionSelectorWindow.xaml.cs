using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GravityCapture.Views
{
    public partial class RegionSelectorWindow : Window
    {
        private Point _start;
        private bool _drag;

        public Rect SelectedRect { get; private set; }

        public RegionSelectorWindow()
        {
            InitializeComponent();
            KeyDown += (s,e)=> { if (e.Key == Key.Escape) { DialogResult = false; Close(); } };
            Loaded += (s,e) => { Left = 0; Top = 0; Width = SystemParameters.PrimaryScreenWidth; Height = SystemParameters.PrimaryScreenHeight; };
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _drag = true; _start = e.GetPosition(RootCanvas);
            Sel.Visibility = Visibility.Visible;
            Canvas.SetLeft(Sel, _start.X); Canvas.SetTop(Sel, _start.Y);
            Sel.Width = Sel.Height = 0;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_drag) return;
            var p = e.GetPosition(RootCanvas);
            var x = Math.Min(p.X, _start.X);
            var y = Math.Min(p.Y, _start.Y);
            var w = Math.Abs(p.X - _start.X);
            var h = Math.Abs(p.Y - _start.Y);
            Canvas.SetLeft(Sel, x); Canvas.SetTop(Sel, y);
            Sel.Width = w; Sel.Height = h;
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!_drag) return;
            _drag = false;
            var x = Canvas.GetLeft(Sel);
            var y = Canvas.GetTop(Sel);
            var w = Sel.Width;
            var h = Sel.Height;
            if (w < 5 || h < 5) { DialogResult = false; Close(); return; }
            SelectedRect = new Rect(x, y, w, h);
            DialogResult = true;
            Close();
        }
    }
}
