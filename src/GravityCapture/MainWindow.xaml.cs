using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using GravityCapture.ViewModels;

namespace GravityCapture;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();

        // Keep the ViewModel's SharedSecret in sync with the PasswordBox.
        // The PasswordBox intentionally isn't bindable in plain WPF.
        Loaded += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                SecretBox.Password = vm.SharedSecret ?? string.Empty;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Win11 dark title bar (Option A)
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowsDarkTitleBar.TryEnable(hwnd, enabled: true);
    }

    private void SecretBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        vm.SharedSecret = ((PasswordBox)sender).Password ?? string.Empty;
    }
}
