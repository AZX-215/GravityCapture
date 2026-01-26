using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GravityCapture.Models;
using Screen = System.Windows.Forms.Screen;

namespace GravityCapture.Views;

public partial class RegionCalibratorWindow : Window
{
    private System.Windows.Point _start;
    private bool _dragging;

    public NormalizedRect? SelectedRegion { get; private set; }
    public string? SelectedScreenDeviceName { get; private set; }

    public RegionCalibratorWindow(NormalizedRect? current, string? currentScreenDeviceName)
    {
        InitializeComponent();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        };

        // Primary screen overlay (best for ARK in fullscreen/borderless).
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        SelectedScreenDeviceName = screen.DeviceName;

        Loaded += (_, _) =>
        {
            // Use WPF primary screen dimensions (DIPs). (This is fine for selecting a normalized region.)
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
        };
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _start = e.GetPosition(RootCanvas);
        Selection.Visibility = Visibility.Visible;
        Canvas.SetLeft(Selection, _start.X);
        Canvas.SetTop(Selection, _start.Y);
        Selection.Width = 0;
        Selection.Height = 0;
        RootCanvas.CaptureMouse();
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging) return;

        var p = e.GetPosition(RootCanvas);
        var x = Math.Min(p.X, _start.X);
        var y = Math.Min(p.Y, _start.Y);
        var w = Math.Abs(p.X - _start.X);
        var h = Math.Abs(p.Y - _start.Y);

        Canvas.SetLeft(Selection, x);
        Canvas.SetTop(Selection, y);
        Selection.Width = w;
        Selection.Height = h;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        RootCanvas.ReleaseMouseCapture();

        var x = Canvas.GetLeft(Selection);
        var y = Canvas.GetTop(Selection);
        var w = Selection.Width;
        var h = Selection.Height;

        if (w < 50 || h < 50)
        {
            Selection.Visibility = Visibility.Collapsed;
            return;
        }

        var nx = x / Math.Max(1, RootCanvas.ActualWidth);
        var ny = y / Math.Max(1, RootCanvas.ActualHeight);
        var nw = w / Math.Max(1, RootCanvas.ActualWidth);
        var nh = h / Math.Max(1, RootCanvas.ActualHeight);

        var r = new NormalizedRect { Left = nx, Top = ny, Width = nw, Height = nh };
        r.Clamp();
        SelectedRegion = r;

        DialogResult = true;
        Close();
    }
}
