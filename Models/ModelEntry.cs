using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ModelCopier.Models
{
    /// <summary>
    /// Represents one Tekla model that the user wants to back up.
    /// Implements INotifyPropertyChanged so the DataGrid updates live.
    /// </summary>
    public class ModelEntry : INotifyPropertyChanged
    {
        private string _id = Guid.NewGuid().ToString();
        private string _name = string.Empty;
        private string _sourcePath = string.Empty;
        private string _reserveTargetPath = string.Empty;
        private string _checkerTargetPath = string.Empty;
        private string _incrementalTargetPath = string.Empty;
        private DateTime? _lastReserveBackup;
        private DateTime? _lastCheckerBackup;
        private DateTime? _lastIncrementalBackup;
        private bool _includeBakFiles = false;
        private string _targetFolderSuffix = "_bak";
        private bool _enableAutoBackup = false;
        private bool _enableIncrementalBackup = false;

        // ── Persisted properties ──────────────────────────────────────────

        public string Id
        {
            get => _id;
            set => SetField(ref _id, value);
        }

        /// <summary>Display name of the model (folder name inside the source path).</summary>
        public string Name
        {
            get => _name;
            set
            {
                if (SetField(ref _name, value))
                {
                    if (!string.IsNullOrWhiteSpace(_sourcePath))
                    {
                        string normalized = NormalizeSourcePath(_sourcePath, _name);
                        if (!string.Equals(_sourcePath, normalized, StringComparison.OrdinalIgnoreCase))
                        {
                            _sourcePath = normalized;
                            OnPropertyChanged(nameof(SourcePath));
                        }
                    }
                    OnPropertyChanged(nameof(LiveModelPath));
                    OnPropertyChanged(nameof(ModelFolderExists));
                }
            }
        }

        /// <summary>Parent repository folder where the model folder resides (e.g. D:\LiveModels).</summary>
        public string SourcePath
        {
            get => _sourcePath;
            set
            {
                string normalized = NormalizeSourcePath(value, _name);
                if (SetField(ref _sourcePath, normalized))
                {
                    OnPropertyChanged(nameof(LiveModelPath));
                    OnPropertyChanged(nameof(ModelFolderExists));
                }
            }
        }

        /// <summary>
        /// Normalizes a source path so that it represents the parent repository directory,
        /// ensuring the model name is not duplicated at the end of the SourcePath.
        /// E.g. 'D:\LiveModels\M2610-01_FD_DUCT' with Name 'M2610-01_FD_DUCT' becomes 'D:\LiveModels'.
        /// </summary>
        public static string NormalizeSourcePath(string? sourcePath, string? modelName)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) return string.Empty;
            string clean = sourcePath.Trim().TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(modelName)) return clean;

            string lastSegment = System.IO.Path.GetFileName(clean);
            if (string.Equals(lastSegment, modelName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                string? parent = System.IO.Path.GetDirectoryName(clean);
                return !string.IsNullOrWhiteSpace(parent) ? parent : clean;
            }
            return clean;
        }

        /// <summary>
        /// Full path to the actual Tekla model folder on disk.
        /// Combines SourcePath and Name (e.g. 'D:\LiveModels\M2610-01_FD_DUCT').
        /// If SourcePath already ended with the model name, returns it directly.
        /// </summary>
        [JsonIgnore]
        public string LiveModelPath
        {
            get
            {
                if (string.IsNullOrWhiteSpace(SourcePath)) return string.Empty;
                if (string.IsNullOrWhiteSpace(Name)) return SourcePath;

                string clean = SourcePath.Trim().TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                string lastSegment = System.IO.Path.GetFileName(clean);
                if (string.Equals(lastSegment, Name.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return clean;
                }
                return System.IO.Path.Combine(clean, Name.Trim());
            }
        }

        /// <summary>Whether the specific model folder exists inside the source repository.</summary>
        [JsonIgnore]
        public bool ModelFolderExists => !string.IsNullOrWhiteSpace(LiveModelPath) && System.IO.Directory.Exists(LiveModelPath);

        /// <summary>Root folder where Reserve (Type 1) backups are written.</summary>
        public string ReserveTargetPath
        {
            get => _reserveTargetPath;
            set => SetField(ref _reserveTargetPath, value);
        }

        /// <summary>Root folder where Checker (Type 2) backups are written.</summary>
        public string CheckerTargetPath
        {
            get => _checkerTargetPath;
            set => SetField(ref _checkerTargetPath, value);
        }

        /// <summary>Target folder where live Incremental (Type 3) backups of modified files are written.</summary>
        public string IncrementalTargetPath
        {
            get => _incrementalTargetPath;
            set => SetField(ref _incrementalTargetPath, value);
        }

        /// <summary>Whether to copy *.bak files when replicating this model.</summary>
        public bool IncludeBakFiles
        {
            get => _includeBakFiles;
            set => SetField(ref _includeBakFiles, value);
        }

        /// <summary>Optional custom suffix appended to the inner model folder (default _bak, e.g. _QC, _RevA).</summary>
        public string TargetFolderSuffix
        {
            get => _targetFolderSuffix;
            set => SetField(ref _targetFolderSuffix, NormalizeSuffix(value));
        }

        /// <summary>
        /// Normalizes suffix so that if not empty and doesn't start with '_', auto-adds '_'.
        /// If empty/whitespace, returns empty string so user can choose to remove it.
        /// </summary>
        public static string NormalizeSuffix(string? suffix)
        {
            if (string.IsNullOrWhiteSpace(suffix)) return string.Empty;
            string clean = suffix.Trim();
            if (!clean.StartsWith("_"))
            {
                clean = "_" + clean;
            }
            return clean;
        }

        /// <summary>Whether this specific model is included in automated scheduled backups.</summary>
        public bool EnableAutoBackup
        {
            get => _enableAutoBackup;
            set => SetField(ref _enableAutoBackup, value);
        }

        /// <summary>Whether this specific model is included in live incremental sync.</summary>
        public bool EnableIncrementalBackup
        {
            get => _enableIncrementalBackup;
            set => SetField(ref _enableIncrementalBackup, value);
        }

        /// <summary>UTC timestamp of the most recent successful Reserve backup.</summary>
        public DateTime? LastReserveBackup
        {
            get => _lastReserveBackup;
            set
            {
                SetField(ref _lastReserveBackup, value);
                OnPropertyChanged(nameof(LastReserveBackupDisplay));
            }
        }

        /// <summary>UTC timestamp of the most recent successful Checker backup.</summary>
        public DateTime? LastCheckerBackup
        {
            get => _lastCheckerBackup;
            set
            {
                SetField(ref _lastCheckerBackup, value);
                OnPropertyChanged(nameof(LastCheckerBackupDisplay));
            }
        }

        /// <summary>UTC timestamp of the most recent successful Incremental backup.</summary>
        public DateTime? LastIncrementalBackup
        {
            get => _lastIncrementalBackup;
            set
            {
                SetField(ref _lastIncrementalBackup, value);
                OnPropertyChanged(nameof(LastIncrementalBackupDisplay));
            }
        }

        // ── Computed / display-only properties (not serialised) ──────────

        [JsonIgnore]
        public string LastReserveBackupDisplay =>
            LastReserveBackup?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "Never";

        [JsonIgnore]
        public string LastCheckerBackupDisplay =>
            LastCheckerBackup?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "Never";

        [JsonIgnore]
        public string LastIncrementalBackupDisplay =>
            LastIncrementalBackup?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "Never";

        // ── INotifyPropertyChanged ────────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
