using System.Windows;
using System.Windows.Input;

namespace GravityCapture;

public partial class MainWindow : Window
{
    private readonly ViewModels.MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new ViewModels.MainViewModel();
        DataContext = _vm;
        Loaded += (_, _) => { _vm.OnLoaded(); SecretBox.Password = _vm.SharedSecret; };
        Closing += (_, _) =>
        {
            // Ensure any focused TextBox commits its binding source before settings are saved.
            // (Some WPF bindings only commit on focus change.)
            Keyboard.ClearFocus();
            _vm.OnClosing();
        };
    }

    private void SecretBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        _vm.SharedSecret = SecretBox.Password;
    }
}
