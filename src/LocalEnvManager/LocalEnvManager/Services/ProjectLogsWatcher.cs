//
// Watches a project's logs directory and tails the most recently modified
// file. Used by the Execute-tab right pane to surface live service output
// (e.g. FastAPI startup, uvicorn requests, dotnet publish diagnostics)
// without forcing the user to manually open the file.
//
// The logs directory is resolved in two layers:
//   1. An explicit per-project override (project.LogsDirectory).
//   2. Auto-discovery: a list of conventional locations based on SourcePath /
//      TargetPath / project name — see BuildCandidateDirectories.
// When neither produces an existing directory, the watcher reports "no logs
// directory" and the view shows a hint instead of the dark log console. The
// candidate list still drives the "where we'll watch" hint even before the
// directory exists.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LocalEnvManager.Services
{
    /// <summary>
    /// Resolves the logs directory for a project and exposes its existence +
    /// contents to the UI. The watcher itself is per-instance and threadsafe.
    /// </summary>
    public sealed class ProjectLogsWatcher : IDisposable
    {
        private readonly object _gate = new();
        private FileSystemWatcher? _watcher;
        private string? _logsDirectory;
        private long _readOffset;
        private string? _watchedFile;
        private CancellationTokenSource? _readCts;

        /// <summary>Absolute path of the logs directory, or null when not configured.</summary>
        public string? LogsDirectory
        {
            get { lock (_gate) return _logsDirectory; }
        }

        /// <summary>True when the logs directory exists on disk.</summary>
        public bool IsAvailable
        {
            get
            {
                var dir = LogsDirectory;
                return dir is not null && Directory.Exists(dir);
            }
        }

        /// <summary>The file currently being tailed (most recently written in the logs dir), or null.</summary>
        public string? CurrentFile
        {
            get { lock (_gate) return _watchedFile; }
        }

        /// <summary>
        /// Compute the logs directory for a project. The rules are:
        ///
        ///   1. <c>project.LogsDirectory</c>, when non-blank — explicit override.
        ///   2. Auto-discovery: a list of conventional candidate directories
        ///      based on the project's SourcePath / TargetPath / Name. Returns
        ///      the first directory that exists on disk.
        ///
        /// Auto-discovery is intentionally generous — projects follow no
        /// universal convention for where they write logs. The current
        /// candidates (in priority order) are:
        ///
        ///   * &lt;parent of TargetPath&gt;\Logs\{name|kebab-name}
        ///   * &lt;parent of TargetPath&gt;\logs\{name|kebab-name}
        ///   * &lt;parent of TargetPath&gt;\Logs\
        ///   * &lt;parent of TargetPath&gt;\logs\
        ///   * &lt;parent of SourcePath&gt;\Logs\{name|kebab-name}
        ///   * &lt;parent of SourcePath&gt;\logs\{name|kebab-name}
        ///   * &lt;parent of SourcePath&gt;\Logs\
        ///   * &lt;parent of SourcePath&gt;\logs\
        ///   * &lt;parent of SourcePath&gt;         (project folder IS the logs folder)
        ///   * &lt;parent of TargetPath&gt;
        ///
        /// The same priority list is used regardless of deployment mode. The
        /// "{name}" variant uses the project's name verbatim; "{kebab-name}"
        /// converts PascalCase / camelCase into lower-kebab-case so a project
        /// named "LLMAgentApi" can match a folder named "llm-agent-api".
        /// </summary>
        public static string? ResolveLogsDirectory(Models.ManagedProject? project)
        {
            if (project is null) return null;

            // 1. Explicit override wins. We still check Directory.Exists so a
            //    user who changed disks (or has a stale path) gets the
            //    auto-discovery fallback rather than a permanently-empty pane.
            var explicitDir = project.LogsDirectory?.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(explicitDir) && Directory.Exists(explicitDir))
            {
                return explicitDir;
            }

            // 2. Auto-discovery. Build the candidate list once so we can walk it
            //    in priority order without re-doing the name conversions.
            var candidates = BuildCandidateDirectories(project);
            foreach (var candidate in candidates)
            {
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            // 3. No candidate exists yet — surface the FIRST candidate (the
            //    explicit override if set, otherwise the highest-priority
            //    auto-discovered one) so the UI can show "we'll watch this
            //    directory as soon as the service creates it".
            return candidates.FirstOrDefault();
        }

        // Build the ordered list of candidate log directories for a project.
        // Kept separate from ResolveLogsDirectory so it's easy to unit-test
        // the priority order without touching the filesystem.
        private static IEnumerable<string> BuildCandidateDirectories(Models.ManagedProject project)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nameVariants = NameVariants(project.Name);

            // Explicit override first so it's surfaced as the "watch this"
            // directory even when nothing exists yet.
            var explicitDir = project.LogsDirectory?.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(explicitDir))
            {
                if (seen.Add(explicitDir)) yield return explicitDir;
            }

            // TargetPath is the deployed runtime location, so it wins over the
            // source checkout when both contain a logs folder. This ensures a
            // project such as LLMAgentApi follows D:\Plus\Logs\llm-agent-api
            // rather than a stale log retained next to its source code.
            foreach (var dir in CandidatesForParent(Path.GetDirectoryName(Normalize(project.TargetPath)), nameVariants))
            {
                if (seen.Add(dir)) yield return dir;
            }

            // SourcePath remains a fallback for projects that run directly
            // from their checkout and have no deployed runtime log directory.
            foreach (var dir in CandidatesForParent(Path.GetDirectoryName(Normalize(project.SourcePath)), nameVariants))
            {
                if (seen.Add(dir)) yield return dir;
            }
        }

        // Convert the project name into a list of folder-name variants we
        // should check. PascalCase projects (LLMAgentApi) often have log
        // folders in kebab-case (llm-agent-api); we try the original spelling
        // first because that's what users will type, then fall back to the
        // kebab-case conversion when nothing matches.
        private static IEnumerable<string> NameVariants(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) yield break;
            var trimmed = name.Trim();
            yield return trimmed;
            var kebab = ToKebabCase(trimmed);
            if (!string.Equals(kebab, trimmed, StringComparison.Ordinal))
            {
                yield return kebab;
            }
        }

        // PascalCase / camelCase → lower-kebab-case. "LLMAgentApi" -> "llm-agent-api",
        // "PyAgents" -> "py-agents", "tfplus_web" -> "tfplus-web". Insert a hyphen
        // before each uppercase letter that follows a lowercase letter OR between
        // two uppercase letters when the next char is lowercase (so "HTTPServer"
        // becomes "http-server", not "h-t-t-p-server").
        private static string ToKebabCase(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var builder = new StringBuilder(name.Length + 8);
            for (var i = 0; i < name.Length; i++)
            {
                var ch = name[i];
                if (i > 0 && ch == '_') { builder.Append('-'); continue; }
                if (i > 0 && char.IsUpper(ch))
                {
                    var prev = name[i - 1];
                    var next = i + 1 < name.Length ? name[i + 1] : '\0';
                    var boundaryAfterLower = char.IsLower(prev);
                    var boundaryBetweenUppers = char.IsUpper(prev) && char.IsLower(next);
                    if (boundaryAfterLower || boundaryBetweenUppers)
                    {
                        builder.Append('-');
                    }
                }
                builder.Append(char.ToLowerInvariant(ch));
            }
            return builder.ToString();
        }

        // Walk the conventional "<parent>/Logs/<name>" / "<parent>/logs/<name>" /
        // "<parent>/Logs" patterns for one parent directory.
        private static IEnumerable<string> CandidatesForParent(string? parent, IEnumerable<string> nameVariants)
        {
            if (string.IsNullOrWhiteSpace(parent)) yield break;

            // <parent>/Logs/<name>
            foreach (var variant in nameVariants)
            {
                yield return Path.Combine(parent, "Logs", variant);
            }
            // <parent>/logs/<name>
            foreach (var variant in nameVariants)
            {
                yield return Path.Combine(parent, "logs", variant);
            }
            // <parent>/Logs/  (covers cases where the user dumps every project's
            // logs into a single folder and names them inside the file).
            yield return Path.Combine(parent, "Logs");
            // <parent>/logs/  (legacy convention).
            yield return Path.Combine(parent, "logs");
        }

        // Strip surrounding whitespace and quotes; mirrors what the rest of the
        // codebase does when reading user-typed paths from the JSON.
        private static string? Normalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            return path.Trim().Trim('"');
        }

        /// <summary>
        /// Switch the watcher to a different project. Resolves the logs directory
        /// from the new project's TargetPath and (re)starts the underlying watcher.
        /// </summary>
        public void TrackProject(Models.ManagedProject? project)
        {
            StopWatcher();

            if (project is null)
            {
                lock (_gate) { _logsDirectory = null; _watchedFile = null; _readOffset = 0; }
                return;
            }

            var dir = ResolveLogsDirectory(project);
            lock (_gate)
            {
                _logsDirectory = dir;
                _watchedFile = null;
                _readOffset = 0;
            }

            if (dir is null || !Directory.Exists(dir))
            {
                return;
            }

            StartWatcher(dir);
        }

        /// <summary>
        /// Update the sink the watcher reports tail output to. Lets the view-model
        /// swap to a new Progress&lt;string&gt; when the user selects a different project
        /// without restarting the underlying FileSystemWatcher.
        /// </summary>
        public void SetSink(IProgress<string>? sink)
        {
            lock (_gate) _currentSink = sink;
        }

        /// <summary>
        /// Reset the read offset so the next emission replays the most recent N bytes
        /// from the watched file (used when the user re-selects a project).
        /// </summary>
        public void ResetOffset()
        {
            lock (_gate) _readOffset = 0;
        }

        private void StartWatcher(string directory)
        {
            try
            {
                _readCts = new CancellationTokenSource();
                var token = _readCts.Token;

                _watcher = new FileSystemWatcher(directory)
                {
                    NotifyFilter = NotifyFilters.LastWrite
                                 | NotifyFilters.FileName
                                 | NotifyFilters.Size
                                 | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                };
                _watcher.Changed += (_, _) => ScheduleRead();
                _watcher.Created += (_, _) => ScheduleRead();
                _watcher.Renamed += (_, _) => ScheduleRead();

                // Pick the freshest existing file and tail it. If no files exist yet we
                // wait for FileSystemWatcher events to fire when the service starts writing.
                var initial = PickLatestFile(directory);
                if (initial is not null)
                {
                    SwitchWatchedFile(initial);
                }
            }
            catch
            {
                // FileSystemWatcher can throw on locked-down paths; fall back to
                // polling by leaving _watcher null. ReadTick below is still called
                // periodically from the UI dispatcher, so the file will still be tailed.
                _watcher?.Dispose();
                _watcher = null;
            }

            // Always start a low-frequency polling fallback. Some Windows apps write
            // log files with FILE_SHARE_READ but no LastWrite event for small writes,
            // and FileSystemWatcher can miss those. Poll every 2s to catch stragglers.
            if (_readCts is null) return;
            var ctsToken = _readCts.Token;
            _ = PollLoop(directory, ctsToken);
        }

        private async Task PollLoop(string directory, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(2000, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested) break;
                    ScheduleRead();
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private void ScheduleRead()
        {
            // Debounce bursts of file events. Multiple Changed notifications arrive
            // for a single write (LastWrite + Size + Created) and we only want to
            // emit the file contents once they're fully flushed.
            Task.Delay(250).ContinueWith(_ =>
            {
                try { ReadTick(); } catch { }
            });
        }

        private void ReadTick()
        {
            string? dir;
            IProgress<string>? sink;
            lock (_gate)
            {
                dir = _logsDirectory;
                sink = _currentSink;
            }
            if (dir is null || !Directory.Exists(dir)) return;

            var latest = PickLatestFile(dir);
            if (latest is null) return;

            string? previous;
            long previousOffset;
            lock (_gate)
            {
                previous = _watchedFile;
                previousOffset = _readOffset;
            }

            if (latest != previous)
            {
                SwitchWatchedFile(latest);
                return;
            }

            // Same file: read whatever's been appended since last offset.
            try
            {
                using var fs = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length <= previousOffset)
                {
                    // File was truncated/rotated: reset offset and read everything fresh.
                    lock (_gate) _readOffset = 0;
                    previousOffset = 0;
                    fs.Position = 0;
                }
                else
                {
                    fs.Position = previousOffset;
                }

                long newOffset = fs.Length;
                var len = (int)Math.Min(newOffset - fs.Position, 64 * 1024);
                if (len <= 0) return;
                var buffer = new byte[len];
                var read = fs.Read(buffer, 0, len);
                if (read <= 0) return;
                lock (_gate) _readOffset = fs.Position;

                Emit(buffer, read, sink);
            }
            catch
            {
                // File locked by another writer (typical mid-flush); skip this tick.
            }
        }

        private void SwitchWatchedFile(string newFile)
        {
            IProgress<string>? sink;
            lock (_gate)
            {
                _watchedFile = newFile;
                _readOffset = 0;
                sink = _currentSink;
            }
            // Emit a banner so the user can see which file is being followed.
            sink?.Report($"─── tailing {Path.GetFileName(newFile)} ───");
            try
            {
                using var fs = new FileStream(newFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                // Start with the last 4 KiB so the user gets context immediately.
                var startFrom = Math.Max(0, fs.Length - 4096);
                fs.Position = startFrom;
                var len = (int)(fs.Length - startFrom);
                if (len <= 0) return;
                var buffer = new byte[len];
                var read = fs.Read(buffer, 0, len);
                lock (_gate) _readOffset = fs.Position;
                Emit(buffer, read, sink);
            }
            catch { /* file may be locked by another writer; will retry on next tick */ }
        }

        private static string? PickLatestFile(string directory)
        {
            try
            {
                var files = new DirectoryInfo(directory)
                    .EnumerateFiles()
                    .Where(f => !f.Name.StartsWith("."))
                    .OrderByDescending(f => SafeLastWrite(f))
                    .ThenByDescending(f => SafeCreation(f))
                    .ToList();

                // A new daily log file is often created before the first write.
                // Prefer the newest file that has content so the pane still shows
                // useful history; as soon as today's file receives data it becomes
                // the selected newest non-empty file on the next poll.
                return files.FirstOrDefault(f => SafeLength(f) > 0)?.FullName
                    ?? files.FirstOrDefault()?.FullName;
            }
            catch
            {
                return null;
            }
        }

        private static DateTime SafeLastWrite(FileInfo f)
        {
            try { return f.LastWriteTime; } catch { return DateTime.MinValue; }
        }

        private static DateTime SafeCreation(FileInfo f)
        {
            try { return f.CreationTime; } catch { return DateTime.MinValue; }
        }

        private static long SafeLength(FileInfo f)
        {
            try { return f.Length; } catch { return 0; }
        }

        private static void Emit(byte[] buffer, int length, IProgress<string>? sink)
        {
            if (sink is null || length <= 0) return;
            var text = Encoding.UTF8.GetString(buffer, 0, length);
            foreach (var line in SplitLines(text))
            {
                if (line.Length > 0) sink.Report(line);
            }
        }

        private static IEnumerable<string> SplitLines(string text)
        {
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\n')
                {
                    var end = i;
                    if (end > start && text[end - 1] == '\r') end--;
                    yield return text.Substring(start, end - start);
                    start = i + 1;
                }
            }
            if (start < text.Length)
            {
                yield return text.Substring(start);
            }
        }

        private IProgress<string>? _currentSink;

        private void StopWatcher()
        {
            try { _readCts?.Cancel(); } catch { }
            _readCts?.Dispose();
            _readCts = null;
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }

        public void Dispose() => StopWatcher();
    }
}
