using System;
using System.ComponentModel;

namespace GravityCapture
{
    public partial class MainWindow
    {
        protected override void OnClosing(CancelEventArgs e)
        {
            // Persist current UI → settings on any app exit path.
            try
            {
                BindToSettings();   // defined in MainWindow.xaml.cs
                _settings.Save();   // defined on AppSettings
            }
            catch
            {
                // Never block shutdown on save errors.
            }

            base.OnClosing(e);
        }
    }
}
