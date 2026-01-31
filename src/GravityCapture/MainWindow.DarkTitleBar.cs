using System;
using System.Reflection;
using System.Windows.Interop;

namespace GravityCapture;

/// <summary>
/// Applies Windows immersive dark mode to the native title bar (when OS theme is dark).
/// This file is intentionally resilient to XAML changes: it works via OnSourceInitialized,
/// and also provides common event handler names that may be referenced in MainWindow.xaml.
/// </summary>
public partial class MainWindow
{
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDarkTitleBar();
    }

    // Common handler name (seen in some earlier XAML versions)
    private void Window_SourceInitialized(object? sender, EventArgs e) => ApplyDarkTitleBar();

    // Another handler name used in prior patches
    private void ApplyDarkTitleBar_OnSourceInitialized(object? sender, EventArgs e) => ApplyDarkTitleBar();

    private void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            // Call WindowsDarkTitleBar.TryEnable(...) without depending on an exact signature.
            // (Some repo versions used IntPtr; others used nint; some may have added optional params.)
            var t = typeof(WindowsDarkTitleBar);
            var m = t.GetMethod("TryEnable", BindingFlags.Public | BindingFlags.Static);
            if (m == null) return;

            // Prefer (hwnd, true) if it supports it; otherwise fall back to (hwnd).
            try
            {
                m.Invoke(null, new object?[] { hwnd, true });
            }
            catch (TargetParameterCountException)
            {
                m.Invoke(null, new object?[] { hwnd });
            }
        }
        catch
        {
            // Never block app startup for a cosmetic feature.
        }
    }
}
