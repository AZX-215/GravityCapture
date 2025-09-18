using System;
using System.ComponentModel;

namespace GravityCapture
{
    public partial class MainWindow
    {
        protected override void OnClosing(CancelEventArgs e)
        {
            // Persist the current UI values no matter how the app is closed.
            try
            {
                SaveSettings();   // uses your existing private method in MainWindow.xaml.cs
            }
            catch
            {
                // Don't block the app from closing because of a save problem.
            }

            base.OnClosing(e);
        }
    }
}
