using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelCopier.Models;

namespace ModelCopier.Services
{
    /// <summary>
    /// Reads and writes the models.json configuration file.
    /// The file lives next to the application executable.
    /// </summary>
    public class ConfigService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public string ConfigPath { get; } =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models.json");

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
                // Corrupted file — start fresh rather than crashing.
                return new AppConfig();
            }
        }

        /// <summary>Serialises and writes the configuration to disk.</summary>
        public void Save(AppConfig config)
        {
            string json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
    }
}
