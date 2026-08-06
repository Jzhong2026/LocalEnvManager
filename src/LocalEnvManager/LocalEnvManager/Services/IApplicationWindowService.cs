using System;
using System.Windows;

namespace LocalEnvManager.Services
{
    /// <summary>
    /// WPF window-chrome boundary. Keeps Window APIs out of view-model and
    /// application workflow code while allowing declarative Caliburn actions.
    /// </summary>
    public interface IApplicationWindowService
    {
        void Minimize();
        void Close();
    }

    public sealed class ApplicationWindowService : IApplicationWindowService
    {
        public void Minimize()
        {
            var window = Application.Current?.MainWindow;
            if (window is not null)
            {
                window.WindowState = WindowState.Minimized;
            }
        }

        public void Close()
        {
            // Caliburn's bootstrapper can keep the dispatcher alive after its
            // root window closes. Shutdown closes the root window (raising its
            // Closing event so ShellViewModel flushes pending saves) and exits
            // the application deterministically.
            Application.Current?.Shutdown();
        }
    }
}
