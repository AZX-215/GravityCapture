using System;
using System.Windows;
using GravityCapture.Services;

namespace GravityCapture
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            ProfileManager.Initialize(Environment.GetCommandLineArgs());
            base.OnStartup(e);

            // Force dark title bars for every window as it loads.
            EventManager.RegisterClassHandler(
                typeof(Window),
                Window.LoadedEvent,
                new RoutedEventHandler((s, _) =>
                {
                    if (s is Window w) WindowTheme.ApplyDark(w);
                }));
        }
    }
}
