using System;
using System.Windows.Input;
using Microsoft.Win32;
using ModelCopier.Models;

namespace ModelCopier.ViewModels
{
    /// <summary>
    /// ViewModel for the Add / Edit Model Entry dialog.
    /// Provides Browse commands for picking folders and validates required fields.
    /// </summary>
    public class AddEditViewModel : ViewModelBase
    {
        // Backing fields
        private string _name               = string.Empty;
        private string _sourcePath         = string.Empty;
        private string _reserveTargetPath   = string.Empty;
        private string _checkerTargetPath   = string.Empty;
        private string _incrementalTargetPath = string.Empty;
        private bool   _includeBakFiles     = false;
        private string _targetFolderSuffix  = string.Empty;
        private bool   _enableAutoBackup    = false;
        private bool   _enableIncrementalBackup = false;
        private string _validationError    = string.Empty;

        // ── Constructors ─────────────────────────────────────────────────

        /// <summary>Creates a blank entry (Add mode).</summary>
        public AddEditViewModel()
        {
            IsEditMode = false;
            InitCommands();
        }

        /// <summary>Pre-populates fields from an existing entry (Edit mode).</summary>
        public AddEditViewModel(ModelEntry entry)
        {
            _name                 = entry.Name;
            _sourcePath           = entry.SourcePath;
            _reserveTargetPath     = entry.ReserveTargetPath;
            _checkerTargetPath     = entry.CheckerTargetPath;
            _incrementalTargetPath = entry.IncrementalTargetPath;
            _includeBakFiles       = entry.IncludeBakFiles;
            _targetFolderSuffix    = entry.TargetFolderSuffix;
            _enableAutoBackup      = entry.EnableAutoBackup;
            _enableIncrementalBackup = entry.EnableIncrementalBackup;
            IsEditMode            = true;
            InitCommands();
        }

        // ── Properties ───────────────────────────────────────────────────

        public bool IsEditMode { get; }

        public string WindowTitle => IsEditMode ? "Edit Model Entry" : "Add Model Entry";

        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        public string SourcePath
        {
            get => _sourcePath;
            set => SetField(ref _sourcePath, value);
        }

        public string ReserveTargetPath
        {
            get => _reserveTargetPath;
            set => SetField(ref _reserveTargetPath, value);
        }

        public string CheckerTargetPath
        {
            get => _checkerTargetPath;
            set => SetField(ref _checkerTargetPath, value);
        }

        public string IncrementalTargetPath
        {
            get => _incrementalTargetPath;
            set => SetField(ref _incrementalTargetPath, value);
        }

        public bool IncludeBakFiles
        {
            get => _includeBakFiles;
            set => SetField(ref _includeBakFiles, value);
        }

        public string TargetFolderSuffix
        {
            get => _targetFolderSuffix;
            set => SetField(ref _targetFolderSuffix, value);
        }

        public bool EnableAutoBackup
        {
            get => _enableAutoBackup;
            set => SetField(ref _enableAutoBackup, value);
        }

        public bool EnableIncrementalBackup
        {
            get => _enableIncrementalBackup;
            set => SetField(ref _enableIncrementalBackup, value);
        }

        public string ValidationError
        {
            get => _validationError;
            private set
            {
                SetField(ref _validationError, value);
                OnPropertyChanged(nameof(IsValidationErrorVisible));
            }
        }

        public bool IsValidationErrorVisible => !string.IsNullOrEmpty(_validationError);

        // ── Commands ─────────────────────────────────────────────────────

        public ICommand BrowseSourceCommand      { get; private set; } = null!;
        public ICommand BrowseReserveCommand     { get; private set; } = null!;
        public ICommand BrowseCheckerCommand     { get; private set; } = null!;
        public ICommand BrowseIncrementalCommand { get; private set; } = null!;

        private void InitCommands()
        {
            BrowseSourceCommand = new RelayCommand(_ =>
            {
                string? path = BrowseFolder("Select Source Model Folder");
                if (path != null) SourcePath = path;
            });

            BrowseReserveCommand = new RelayCommand(_ =>
            {
                string? path = BrowseFolder("Select Reserve Backup Target Folder");
                if (path != null) ReserveTargetPath = path;
            });

            BrowseCheckerCommand = new RelayCommand(_ =>
            {
                string? path = BrowseFolder("Select Checker Backup Target Folder");
                if (path != null) CheckerTargetPath = path;
            });

            BrowseIncrementalCommand = new RelayCommand(_ =>
            {
                string? path = BrowseFolder("Select Incremental Backup Target Folder");
                if (path != null) IncrementalTargetPath = path;
            });
        }

        private static string? BrowseFolder(string title)
        {
            var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
            return dialog.ShowDialog() == true ? dialog.FolderName : null;
        }

        // ── Validation ───────────────────────────────────────────────────

        /// <summary>Returns true if all required fields pass validation.</summary>
        public bool Validate()
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                ValidationError = "Model name is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(SourcePath))
            {
                ValidationError = "Source folder path is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(ReserveTargetPath) &&
                string.IsNullOrWhiteSpace(CheckerTargetPath) &&
                string.IsNullOrWhiteSpace(IncrementalTargetPath))
            {
                ValidationError = "At least one target path (Reserve, Checker, or Incremental) must be specified.";
                return false;
            }

            ValidationError = string.Empty;
            return true;
        }

        /// <summary>
        /// Returns a new <see cref="ModelEntry"/> from the current field values.
        /// Pass <paramref name="existingId"/> to preserve the ID when editing.
        /// </summary>
        public ModelEntry ToModelEntry(string? existingId = null) => new()
        {
            Id                    = existingId ?? Guid.NewGuid().ToString(),
            Name                  = Name.Trim(),
            SourcePath            = SourcePath.Trim(),
            ReserveTargetPath     = ReserveTargetPath.Trim(),
            CheckerTargetPath     = CheckerTargetPath.Trim(),
            IncrementalTargetPath = IncrementalTargetPath?.Trim() ?? string.Empty,
            IncludeBakFiles       = IncludeBakFiles,
            TargetFolderSuffix    = TargetFolderSuffix?.Trim() ?? string.Empty,
            EnableAutoBackup      = EnableAutoBackup,
            EnableIncrementalBackup = EnableIncrementalBackup
        };
    }
}
