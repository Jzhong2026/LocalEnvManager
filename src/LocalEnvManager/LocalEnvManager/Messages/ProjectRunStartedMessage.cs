using LocalEnvManager.Models;

namespace LocalEnvManager.Messages
{
    /// <summary>
    /// Published by ShellViewModel.RunSelectedAsync when a project pipeline is
    /// about to start. Used by the right-pane ConsoleLiveLogViewModel to
    /// clear its in-memory buffer so the new run starts on a clean console.
    /// </summary>
    public sealed class ProjectRunStartedMessage
    {
        public int ProjectId { get; }
        public DeploymentMode Mode { get; }
        public ProjectRunStartedMessage(int projectId, DeploymentMode mode)
        {
            ProjectId = projectId;
            Mode = mode;
        }
    }
}
