using Caliburn.Micro;
using LocalEnvManager.Models;
using LocalEnvManager.ViewModels;
using System.IO;

namespace LocalEnvManager.Services
{
    /// <summary>
    /// Builds the right-pane child view-model that matches a project's
    /// deployment mode. Centralized so the parent's selection logic is
    /// testable and the rules (uvicorn → console, anything else → log file)
    /// live in one place.
    /// </summary>
    public interface ILiveLogViewModelFactory
    {
        ILiveLogViewModel Create(int projectId, DeploymentMode mode, System.Func<ManagedProject?> projectResolver);
    }

    public sealed class LiveLogViewModelFactory : ILiveLogViewModelFactory
    {
        private readonly IEventAggregator _events;

        public LiveLogViewModelFactory(IEventAggregator events)
        {
            _events = events;
        }

        public ILiveLogViewModel Create(int projectId, DeploymentMode mode, System.Func<ManagedProject?> projectResolver)
        {
            // Pick the right view-model based on whether the project actually
            // has a logs directory we can tail. The historical convention was
            // "uvicorn -> console, everything else -> log file", but that's
            // wrong for uvicorn projects that write persistent log files
            // (e.g. FastAPI apps that log to D:\Plus\Logs\<project>\*.log).
            // Showing those in the file view gives users access to previous
            // days' logs, which the console view (which only sees live stdout)
            // cannot provide.
            var project = projectResolver();
            if (project is not null && HasLogsDirectory(project))
            {
                return new LogFileLiveLogViewModel(projectId, _events, new ProjectLogsWatcher(), projectResolver);
            }
            if (mode == DeploymentMode.Uvicorn)
            {
                return new ConsoleLiveLogViewModel(projectId, _events);
            }
            return new LogFileLiveLogViewModel(projectId, _events, new ProjectLogsWatcher(), projectResolver);
        }

        // True when ResolveLogsDirectory finds an existing folder for this
        // project. We don't bother checking the explicit override alone —
        // the watcher would just show an empty pane if the user typed a path
        // that doesn't exist yet, and switching to console view would mask
        // the real "directory not found" hint.
        private static bool HasLogsDirectory(ManagedProject project)
        {
            var dir = ProjectLogsWatcher.ResolveLogsDirectory(project);
            return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir);
        }
    }
}
