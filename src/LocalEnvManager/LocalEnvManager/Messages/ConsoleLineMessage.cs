namespace LocalEnvManager.Messages
{
    /// <summary>
    /// One line of stdout/stderr from a running child process (uvicorn / dotnet /
    /// powershell). Tagged with the originating ProjectId so subscribers can
    /// route it to the right project without scanning a global log buffer.
    /// </summary>
    public sealed class ConsoleLineMessage
    {
        public int ProjectId { get; }
        public string Line { get; }
        /// <summary>True when the line came from stderr; UI may tint it red.</summary>
        public bool IsError { get; }
        public ConsoleLineMessage(int projectId, string line, bool isError = false)
        {
            ProjectId = projectId;
            Line = line;
            IsError = isError;
        }
    }
}
