using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LocalEnvManager.Services
{
    /// <summary>
    /// Persisted view state that is not project configuration (column widths etc.).
    /// Kept in a separate ui-settings.json so projects.json stays purely about
    /// projects and a corrupt UI file can never block project loading.
    /// </summary>
    public sealed class UiSettings
    {
        public Dictionary<string, double> ExecuteColumnWidths { get; set; } = new();
    }

    public interface IUiSettingsStore
    {
        UiSettings Load();
        void Save(UiSettings settings);
    }

    public sealed class JsonUiSettingsStore : IUiSettingsStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly string _filePath;

        public JsonUiSettingsStore(string? filePath = null)
        {
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                _filePath = filePath;
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                return;
            }

            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LocalEnvManager");
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "ui-settings.json");
        }

        public UiSettings Load()
        {
            // UI state is non-critical: any failure falls back to defaults rather
            // than surfacing an error or blocking startup.
            try
            {
                if (!File.Exists(_filePath)) return new UiSettings();
                using var stream = File.OpenRead(_filePath);
                var settings = JsonSerializer.Deserialize<UiSettings>(stream, SerializerOptions) ?? new UiSettings();
                // An explicit "ExecuteColumnWidths": null deserializes to null and
                // overrides the property initializer; normalize so callers never
                // dereference a null dictionary.
                settings.ExecuteColumnWidths ??= new Dictionary<string, double>();
                return settings;
            }
            catch
            {
                return new UiSettings();
            }
        }

        public void Save(UiSettings settings)
        {
            using var stream = File.Create(_filePath);
            JsonSerializer.Serialize(stream, settings, SerializerOptions);
        }
    }
}