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

        /// <summary>
        /// Local "scratch" directory where openEMS sim_data (HDF5 dumps, NF2FF
        /// recordings, port time-domain data) is written during simulation.
        /// Set this to a real local disk (e.g. C:\PCBAntSimRuns) when the
        /// project itself lives on a cloud-synced folder (Google Drive,
        /// OneDrive, Dropbox, ...) — writing GB-scale HDF5 files to those
        /// virtual drives causes file corruption and extreme slowdowns.
        /// Empty string = use the in-project Sim/sim_data folder (default).
        /// </summary>
        public string SimDataScratchRoot { get; set; } = "";

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
