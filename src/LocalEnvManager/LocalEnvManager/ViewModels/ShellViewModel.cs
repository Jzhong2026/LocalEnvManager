using Caliburn.Micro;
using LocalEnvManager.Messages;
using LocalEnvManager.Services;

namespace LocalEnvManager.ViewModels
{
    /// <summary>
    /// Root view-model for the application shell. Stays deliberately thin —
    /// it owns the title bar, the <see cref="ContentHostViewModel"/> that
    /// contains the three top-level page view-models, and the lifetime
    /// hooks for shutting the application down cleanly. Every business
    /// concern lives in a child view-model or a service.
    /// </summary>
    public sealed class ShellViewModel : Screen
    {
        private readonly IProjectCatalog _catalog;
        private readonly IRuntimeLogger _logger;
        private readonly IEventAggregator _events;
        private readonly IApplicationWindowService _window;

        public ShellViewModel(
            ContentHostViewModel host,
            IProjectCatalog catalog,
            IRuntimeLogger logger,
            IEventAggregator events,
            IApplicationWindowService window)
        {
            _catalog = catalog;
            _logger = logger;
            _events = events;
            _window = window;
            Host = host;

            // First on-disk log entry: surface the log file path so users know where to look
            // after the app closes or crashes.
            _logger.Append(Models.LogEntry.LogLevel.Info, $"运行日志文件：{_logger.LogFilePath}");
        }

        public new string DisplayName { get; } = "Local Environment Manager";

        public ContentHostViewModel Host { get; }

        public void OnWindowClosing()
        {
            // Force any pending debounced project save to flush so the user's
            // last edit isn't lost when the window closes.
            _catalog.FlushPendingSave();
        }

        public void ShowManage() => ActivatePage(0);
        public void ShowExecute() => ActivatePage(1);
        public void ShowLog() => ActivatePage(2);
        public void MinimizeWindow() => _window.Minimize();
        public void CloseWindow() => _window.Close();

        private void ActivatePage(int pageOrder)
            => _events.PublishOnCurrentThread(new ActivatePageMessage(pageOrder));
    }
}
