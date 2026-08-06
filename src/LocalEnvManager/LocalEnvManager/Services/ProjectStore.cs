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
    public interface IProjectStore
    {
        ProjectStoreSnapshot Load();
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
    }

    public sealed class JsonProjectStore : IProjectStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly string _filePath;

        public JsonProjectStore()
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LocalEnvManager");
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "projects.json");
        }

        public ProjectStoreSnapshot Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return new ProjectStoreSnapshot();
                }

                using var stream = File.OpenRead(_filePath);
                var snapshot = JsonSerializer.Deserialize<ProjectStoreSnapshot>(stream, SerializerOptions);
                return snapshot ?? new ProjectStoreSnapshot();
            }
            catch (Exception ex)
            {
                // If the file is corrupt or unreadable, return an empty snapshot AND surface
                // the failure via the exception's message — the caller (ShellViewModel) logs
                // it so the user knows their config wasn't actually loaded.
                throw new InvalidDataException(
                    $"无法读取项目配置 {_filePath}：{ex.Message}", ex);
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
