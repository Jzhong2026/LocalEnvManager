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
        /// <summary>True when the line came from the child process's stderr stream.
        /// This is an output-channel marker, not a log level — stderr frequently
        /// carries progress/diagnostics that are not errors.</summary>
        public bool IsStderr { get; }
        public ConsoleLineMessage(int projectId, string line, bool isStderr = false)
        {
            ProjectId = projectId;
            Line = line;
            IsStderr = isStderr;
        }
    }
}
