using Caliburn.Micro;

namespace LocalEnvManager.Models
{
    /// <summary>
    /// One tab inside the Log tab — a buffer of log lines scoped to a single project run.
    /// Header shows the project name + a state badge so users can see at a glance whether
    /// each project's run succeeded, failed, or is still running.
    /// </summary>
    public sealed class ProjectLogTab : PropertyChangedBase
    {
        public ManagedProject Project { get; }
        public BindableCollection<LogEntry> Entries { get; } = new();

        public ProjectLogTab(ManagedProject project)
        {
            Project = project;
            ProjectName = project.Name;
            project.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ManagedProject.LastRunState))
                {
                    NotifyOfPropertyChange(nameof(StateText));
                    NotifyOfPropertyChange(nameof(StateBrush));
                }
                else if (e.PropertyName == nameof(ManagedProject.Name))
                {
                    ProjectName = Project.Name;
                }
            };
        }

        private string _projectName = string.Empty;
        public string ProjectName
        {
            get => _projectName;
            set => Set(ref _projectName, value);
        }

        public string StateText => Project.LastRunState switch
        {
            ExecutionState.Idle => "未运行",
            ExecutionState.Running => "运行中",
            ExecutionState.Success => "成功",
            ExecutionState.Failed => "失败",
            _ => "未知"
        };

        // The pill color of the state badge; bound from XAML. Kept here so the
        // tab header stays a single binding target.
        public string StateBrush => Project.LastRunState switch
        {
            ExecutionState.Running => "#3B82F6",
            ExecutionState.Success => "#16A34A",
            ExecutionState.Failed => "#DC2626",
            _ => "#64748B"
        };
    }
}