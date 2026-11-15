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
        private string _targetFolderSuffix = string.Empty;
        private bool _enableAutoBackup = false;
        private bool _enableIncrementalBackup = false;

        // ── Persisted properties ──────────────────────────────────────────

        public string Id
        {
            get => _id;
            set => SetField(ref _id, value);
        }

        /// <summary>Display name shown in the list.</summary>
        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        /// <summary>Full path of the Tekla model folder to copy from.</summary>
        public string SourcePath
        {
            get => _sourcePath;
            set => SetField(ref _sourcePath, value);
        }

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

        /// <summary>Optional custom suffix appended to the inner model folder (e.g. QC, RevA).</summary>
        public string TargetFolderSuffix
        {
            get => _targetFolderSuffix;
            set => SetField(ref _targetFolderSuffix, value);
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
