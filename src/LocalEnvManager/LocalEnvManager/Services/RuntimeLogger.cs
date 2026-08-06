using LocalEnvManager.Models;
using System;
using System.IO;
using System.Threading;

namespace LocalEnvManager.Services
{
    /// <summary>
    /// Persists every log line forwarded by the run pipeline to a per-day file under
    /// <c>%LOCALAPPDATA%\LocalEnvManager\logs\runtime-yyyyMMdd.log</c>. The on-disk
    /// file outlives the running process so users can review what happened after a
    /// crash, attach logs to bug reports, or grep for failures across runs.
    ///
    /// Writes are best-effort — a transient IO failure is swallowed rather than
    /// propagated, so a broken log file can never abort an in-flight deployment.
    /// </summary>
    public interface IRuntimeLogger
    {
        /// <summary>Append a single entry to the day's log file.</summary>
        void Append(LogEntry.LogLevel level, string message);

        /// <summary>
        /// Wrap the supplied <see cref="IProgress{T}"/> so that any line it receives
        /// is mirrored to disk on top of the original progress callback. The inner
        /// handler is invoked first so the in-memory UI log stays the source of truth
        /// for ordering.
        /// </summary>
        IProgress<string> Tee(IProgress<string>? inner);

        /// <summary>Full path to today's log file (created on demand).</summary>
        string LogFilePath { get; }
    }

    public sealed class RuntimeLogger : IRuntimeLogger
    {
        private readonly object _sync = new();
        private readonly string _filePath;
        private int _sessionCounter;

        public RuntimeLogger()
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LocalEnvManager", "logs");
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, $"runtime-{DateTime.Now:yyyyMMdd}.log");
        }

        public string LogFilePath => _filePath;

        public void Append(LogEntry.LogLevel level, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            var session = Interlocked.Increment(ref _sessionCounter);
            var entry = new LogEntry(level, message);
            // Format mirrors the in-memory view so the file can be tailed like a normal log.
            var line = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:HH:mm:ss.fff} S{1:D4} {2,-5} {3}",
                entry.Timestamp, session, entry.LevelText, entry.Message);

            try
            {
                lock (_sync)
                {
                    File.AppendAllText(_filePath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never break the run pipeline.
            }
        }

        public IProgress<string> Tee(IProgress<string>? inner)
        {
            return new TeeProgress(inner, this);
        }

        private sealed class TeeProgress : IProgress<string>
        {
            private readonly IProgress<string>? _inner;
            private readonly IRuntimeLogger _logger;

            public TeeProgress(IProgress<string>? inner, IRuntimeLogger logger)
            {
                _inner = inner;
                _logger = logger;
            }

            public void Report(string value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    _logger.Append(LogEntry.LogLevel.Info, value);
                }
                _inner?.Report(value);
            }
        }
    }
}
