namespace LocalEnvManager.Messages
{
    /// <summary>
    /// Published when a project pipeline finishes (success or failure). The
    /// right-pane views use it for state badges; the Log tab uses it to
    /// classify the final entry color.
    /// </summary>
    public sealed class ProjectRunFinishedMessage
    {
        public int ProjectId { get; }
        public bool Success { get; }
        public string Message { get; }
        public ProjectRunFinishedMessage(int projectId, bool success, string message)
        {
            ProjectId = projectId;
            Success = success;
            Message = message;
        }
    }
}
