using System.Collections.Generic;
using System.Linq;
using Caliburn.Micro;
using LocalEnvManager.Messages;
using LocalEnvManager.Models;
using LocalEnvManager.Services;

namespace LocalEnvManager.ViewModels
{
    /// <summary>
    /// Owns the "更新日志" (per-project run log) tab — one <see cref="ProjectLogTab"/>
    /// per project so newly-added projects show up as "未运行" right away, and
    /// the user can clear every project's log with the toolbar button. Caliburn
    /// convention resolves this view-model to <c>RunLogsView.xaml</c>.
    /// </summary>
    public sealed class RunLogsViewModel : Screen, IPageViewModel,
        IHandle<ProjectsChangedMessage>,
        IHandle<ConsoleLineMessage>,
        IHandle<ProjectRunStartedMessage>,
        IHandle<ProjectRunFinishedMessage>
    {
        private readonly IProjectCatalog _catalog;
        private readonly IEventAggregator _events;
        private bool _isActive;

        public RunLogsViewModel(IProjectCatalog catalog, IEventAggregator events)
        {
            _catalog = catalog;
            _events = events;
            ProjectLogs = new BindableCollection<ProjectLogTab>();
            _events.SubscribeOnUI(this);
            RebuildProjectLogs();
        }

        public new string DisplayName { get; } = "更新日志";
        public int Order { get; } = 2;
        public new bool IsActive
        {
            get => _isActive;
            set => Set(ref _isActive, value);
        }

        public BindableCollection<ProjectLogTab> ProjectLogs { get; }

        private ProjectLogTab? _activeLogTab;
        public ProjectLogTab? ActiveLogTab
        {
            get => _activeLogTab;
            set => Set(ref _activeLogTab, value);
        }

        public void OnActivated() { /* no-op for now */ }

        public Task HandleAsync(ProjectsChangedMessage message, CancellationToken cancellationToken)
        {
            RebuildProjectLogs();
            return Task.CompletedTask;
        }

        public Task HandleAsync(ConsoleLineMessage message, CancellationToken cancellationToken)
        {
            var tab = FindTab(message.ProjectId);
            if (tab is not null && !string.IsNullOrWhiteSpace(message.Line))
            {
                tab.Entries.Add(new LogEntry(
                    message.IsError ? LogEntry.LogLevel.Error : LogEntry.LogLevel.Info,
                    message.Line));
            }
            return Task.CompletedTask;
        }

        public Task HandleAsync(ProjectRunStartedMessage message, CancellationToken cancellationToken)
        {
            var tab = FindTab(message.ProjectId);
            if (tab is not null)
            {
                tab.Entries.Clear();
                ActiveLogTab = tab;
                tab.Entries.Add(new LogEntry(LogEntry.LogLevel.Info, "Update started."));
            }
            return Task.CompletedTask;
        }

        public Task HandleAsync(ProjectRunFinishedMessage message, CancellationToken cancellationToken)
        {
            var tab = FindTab(message.ProjectId);
            if (tab is not null)
            {
                tab.Entries.Add(new LogEntry(
                    message.Success ? LogEntry.LogLevel.Success : LogEntry.LogLevel.Error,
                    message.Message));
            }
            return Task.CompletedTask;
        }

        public void ClearLog()
        {
            foreach (var tab in ProjectLogs)
            {
                tab.Entries.Clear();
            }
        }

        private void RebuildProjectLogs()
        {
            var wantedProjects = new HashSet<ManagedProject>(_catalog.Projects);
            for (var i = ProjectLogs.Count - 1; i >= 0; i--)
            {
                if (!wantedProjects.Contains(ProjectLogs[i].Project))
                {
                    ProjectLogs.RemoveAt(i);
                }
            }
            foreach (var p in _catalog.Projects)
            {
                if (!ProjectLogs.Any(t => t.Project == p))
                {
                    ProjectLogs.Add(new ProjectLogTab(p));
                }
            }
            if (ActiveLogTab is null || !ProjectLogs.Contains(ActiveLogTab))
            {
                ActiveLogTab = ProjectLogs.FirstOrDefault();
            }
        }

        private ProjectLogTab? FindTab(int projectId)
            => ProjectLogs.FirstOrDefault(tab => _catalog.GetProjectId(tab.Project) == projectId);
    }
}
