using System.Collections.Generic;

namespace ModelCopier.Models
{
    /// <summary>Root configuration serialised to / from models.json.</summary>
    public class AppConfig
    {
        public List<ModelEntry> Models { get; set; } = new();

        /// <summary>Optional custom path to TeklaStructures.exe if auto-detection is overridden.</summary>
        public string? TeklaStructuresExePath { get; set; }

        /// <summary>Whether automated background interval backups are enabled.</summary>
        public bool AutoBackupEnabled { get; set; } = false;

        /// <summary>Interval in minutes for automated backups (e.g. 30, 60, 120).</summary>
        public int AutoBackupIntervalMinutes { get; set; } = 60;

        /// <summary>Interval in minutes for incremental backups of modified files (e.g. 5, 10, 15, 30, 60).</summary>
        public int IncrementalIntervalMinutes { get; set; } = 15;

        /// <summary>Whether to play a sound chime on backup completion.</summary>
        public bool PlaySoundOnCompletion { get; set; } = true;
    }
}
