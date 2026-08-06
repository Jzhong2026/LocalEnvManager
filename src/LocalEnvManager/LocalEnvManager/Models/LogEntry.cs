using System;
using Caliburn.Micro;

namespace LocalEnvManager.Models
{
    /// <summary>
    /// A single line item rendered in the runtime log panel.
    /// </summary>
    public sealed class LogEntry : PropertyChangedBase
    {
        public enum LogLevel { Info, Warn, Error, Success }

        public DateTime Timestamp { get; }
        public LogLevel Level { get; }
        public string Message { get; }

        public LogEntry(LogLevel level, string message)
        {
            Timestamp = DateTime.Now;
            Level = level;
            Message = message;
        }

        public string TimestampText => Timestamp.ToString("HH:mm:ss.fff");

        public string LevelText => Level switch
        {
            LogLevel.Info => "INFO",
            LogLevel.Warn => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Success => " OK ",
            _ => "    "
        };
    }
}