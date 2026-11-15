using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelCopier.Models;

namespace ModelCopier.Services
{
    /// <summary>
    /// Reads and writes the models.json configuration file.
    /// Supports both local storage and shared office network UNC/drive paths.
    /// </summary>
    public class ConfigService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly string _pointerFile =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config_location.txt");

        private string? _customConfigPath;

        public ConfigService()
        {
            LoadPointer();
        }

        private void LoadPointer()
        {
            try
            {
                if (File.Exists(_pointerFile))
                {
                    string path = File.ReadAllText(_pointerFile).Trim();
                    if (!string.IsNullOrWhiteSpace(path))
                        _customConfigPath = path;
                }
            }
            catch { }
        }

        public string DefaultLocalConfigPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models.json");

        public string ConfigPath =>
            !string.IsNullOrWhiteSpace(_customConfigPath) ? _customConfigPath : DefaultLocalConfigPath;

        public bool IsUsingSharedConfig =>
            !string.IsNullOrWhiteSpace(_customConfigPath) &&
            !string.Equals(_customConfigPath, DefaultLocalConfigPath, StringComparison.OrdinalIgnoreCase);

        /// <summary>Sets a shared network or custom location for models.json.</summary>
        public void SetCustomConfigPath(string customPath)
        {
            _customConfigPath = customPath.Trim();
            try
            {
                File.WriteAllText(_pointerFile, _customConfigPath);
            }
            catch { }
        }

        /// <summary>Resets configuration to the local application directory models.json.</summary>
        public void ResetToDefault()
        {
            _customConfigPath = null;
            try
            {
                if (File.Exists(_pointerFile))
                    File.Delete(_pointerFile);
            }
            catch { }
        }

        /// <summary>
        /// Loads the configuration from disk.
        /// Returns an empty <see cref="AppConfig"/> if the file does not exist or is invalid.
        /// </summary>
        public AppConfig Load()
        {
            if (!File.Exists(ConfigPath))
                return new AppConfig();

            try
            {
                string json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            }
            catch (Exception)
            {
                return new AppConfig();
            }
        }

        /// <summary>Serialises and writes the configuration to disk.</summary>
        public void Save(AppConfig config)
        {
            try
            {
                string? dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(config, JsonOptions);
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }
    }
}
