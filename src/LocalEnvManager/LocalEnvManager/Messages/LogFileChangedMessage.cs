namespace LocalEnvManager.Messages
{
    /// <summary>
    /// Published by LogFileLiveLogViewModel when its underlying
    /// ProjectLogsWatcher switches the file it is tailing (e.g. a new log
    /// appears in the directory and is fresher than the previous one).
    /// Subscribed by ProjectLiveLogViewModel so it can refresh the badge
    /// in the right-pane header.
    /// </summary>
    public sealed class LogFileChangedMessage
    {
        public int ProjectId { get; }
        public string FileName { get; }
        public LogFileChangedMessage(int projectId, string fileName)
        {
            ProjectId = projectId;
            FileName = fileName;
        }
    }
}
