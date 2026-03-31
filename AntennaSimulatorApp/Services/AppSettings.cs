using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AntennaSimulatorApp.Services
{
    /// <summary>
    /// Persisted application-wide settings (stored in user's AppData).
    /// </summary>
    public class AppSettings
    {
        /// <summary>Custom openEMS installation directory (the folder containing openEMS.exe).
        /// Empty or null means auto-detect.</summary>
        public string OpenEmsPath { get; set; } = "";

        /// <summary>Custom Python executable path. Empty or null means auto-detect.</summary>
        public string PythonPath { get; set; } = "";

        // ── Singleton ────────────────────────────────────────────────────────

        private static AppSettings? _instance;
        private static readonly object _lock = new();

        [JsonIgnore]
        public static AppSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock) { _instance ??= Load(); }
                }
                return _instance;
            }
        }

        // ── Persistence ──────────────────────────────────────────────────────

        private static string SettingsDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "AntennaSimulatorApp");

        private static string SettingsFile =>
            Path.Combine(SettingsDir, "settings.json");

        private static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var json = File.ReadAllText(SettingsFile);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch { /* corrupt file – fall back to defaults */ }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFile, json);
            }
            catch { /* best-effort */ }
        }
    }
}
