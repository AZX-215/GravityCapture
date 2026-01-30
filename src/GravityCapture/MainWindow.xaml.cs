using System.Windows;

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
        Closing += (_, _) => _vm.OnClosing();
    }

    private void SecretBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        _vm.SharedSecret = SecretBox.Password;
    }
}
