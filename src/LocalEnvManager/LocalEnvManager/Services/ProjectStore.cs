using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using LocalEnvManager.Models;

namespace LocalEnvManager.Services
{
    /// <summary>
    /// Persists project categories and projects to a JSON file under the user's
    /// local app data folder. Categorisation and per-project configuration survives
    /// application restarts.
    /// </summary>
    public enum ProjectStoreLoadStatus
    {
        /// <summary>No config file exists yet — this is a genuine first run.</summary>
        Missing,
        /// <summary>A config file was read successfully. An empty category list is a
        /// legitimate user choice and must not be replaced with seed data.</summary>
        Loaded,
        /// <summary>The file exists but could not be read/deserialised. The original is
        /// backed up before the caller decides how to continue.</summary>
        Failed
    }

    public sealed record ProjectStoreLoadResult(
        ProjectStoreSnapshot Snapshot,
        ProjectStoreLoadStatus Status,
        string? ErrorMessage = null,
        string? BackupPath = null);

    public interface IProjectStore
    {
        ProjectStoreLoadResult Load();
        /// <summary>
        /// Persist <paramref name="snapshot"/> to disk. Throws on failure so callers
        /// can surface the error to the user (silent corruption was the cause of
        /// "I edited the path but it's still wrong on the next run" reports).
        /// </summary>
        void Save(ProjectStoreSnapshot snapshot);
    }

    public sealed class ProjectStoreSnapshot
    {
        public List<CategoryRecord> Categories { get; set; } = new();
    }

    public sealed class CategoryRecord
    {
        public string Name { get; set; } = string.Empty;
        public List<ProjectRecord> Projects { get; set; } = new();
    }

    public sealed class ProjectRecord
    {
        public string Name { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public DeploymentMode DeploymentMode { get; set; }
        public string TargetPath { get; set; } = string.Empty;
        public string BackupFolder { get; set; } = string.Empty;
        public string IisAppPoolName { get; set; } = string.Empty;
        public bool PullLatest { get; set; }
        public string DependsOn { get; set; } = string.Empty;
        // Optional explicit override for the runtime log directory. When blank,
        // the watcher falls back to a list of conventional locations derived
        // from SourcePath / TargetPath / project name. Kept optional so the
        // existing per-project JSON files keep loading without a migration.
        public string LogsDirectory { get; set; } = string.Empty;
        // Last run outcome, persisted so the Execute tab can show "上次的成功"
        // after a restart instead of resetting every row to 未运行. Optional with
        // defaults so older projects.json files load unchanged.
        public ExecutionState LastRunState { get; set; } = ExecutionState.Idle;
        public DateTime? LastRunFinishedAt { get; set; }
        public string LastExecutionMessage { get; set; } = string.Empty;
    }

    public sealed class JsonProjectStore : IProjectStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly string _filePath;

        public JsonProjectStore(string? filePath = null)
        {
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                // Explicit path is used by tests. The caller owns the directory.
                _filePath = filePath;
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                return;
            }

            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LocalEnvManager");
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "projects.json");
        }

        public ProjectStoreLoadResult Load()
        {
            if (!File.Exists(_filePath))
            {
                return new ProjectStoreLoadResult(
                    new ProjectStoreSnapshot(),
                    ProjectStoreLoadStatus.Missing);
            }

            try
            {
                using var stream = File.OpenRead(_filePath);
                var snapshot = JsonSerializer.Deserialize<ProjectStoreSnapshot>(stream, SerializerOptions);
                if (snapshot is null)
                {
                    throw new InvalidDataException("配置文件内容为空或不是有效的 JSON 对象。");
                }

                snapshot.Categories ??= new List<CategoryRecord>();
                return new ProjectStoreLoadResult(snapshot, ProjectStoreLoadStatus.Loaded);
            }
            catch (Exception ex)
            {
                // Keep the original bytes before the caller potentially writes seed data.
                // A transient read failure (file lock / antivirus) therefore never destroys
                // the user's configuration; it can be restored from the backup path.
                var backupPath = TryBackupCorruptFile();
                return new ProjectStoreLoadResult(
                    new ProjectStoreSnapshot(),
                    ProjectStoreLoadStatus.Failed,
                    $"无法读取项目配置 {_filePath}：{ex.Message}",
                    backupPath);
            }
        }

        private string? TryBackupCorruptFile()
        {
            try
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                var suffix = Guid.NewGuid().ToString("N")[..8];
                var backupPath = $"{_filePath}.corrupt-{stamp}-{suffix}";
                File.Copy(_filePath, backupPath, overwrite: false);
                return backupPath;
            }
            catch
            {
                // Best effort: failing to back up must not mask the original load error.
                return null;
            }
        }

        public void Save(ProjectStoreSnapshot snapshot)
        {
            // Atomic write: serialise to a temp file in the same folder, fsync, then
            // rename over the live file. This prevents the user's project config
            // from being truncated/corrupted when the app is killed mid-save.
            var tempPath = _filePath + ".tmp";
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, snapshot, SerializerOptions);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_filePath))
            {
                File.Replace(tempPath, _filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _filePath);
            }
        }
    }
}
