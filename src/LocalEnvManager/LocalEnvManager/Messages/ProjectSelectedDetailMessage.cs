using LocalEnvManager.Models;

namespace LocalEnvManager.Messages
{
    /// <summary>
    /// Carries the full project record (not just an id) so subscribers don't
    /// need to look the project up. Published alongside
    /// <see cref="ProjectSelectedMessage"/> by ShellViewModel.
    /// </summary>
    public sealed class ProjectSelectedDetailMessage
    {
        public int ProjectId { get; }
        public ManagedProject Project { get; }
        public ProjectSelectedDetailMessage(int projectId, ManagedProject project)
        {
            ProjectId = projectId;
            Project = project;
        }
    }
}
