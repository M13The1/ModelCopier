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
        private string _name             = string.Empty;
        private string _sourcePath       = string.Empty;
        private string _reserveTargetPath = string.Empty;
        private string _checkerTargetPath = string.Empty;
        private string _validationError  = string.Empty;

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
            _name              = entry.Name;
            _sourcePath        = entry.SourcePath;
            _reserveTargetPath = entry.ReserveTargetPath;
            _checkerTargetPath = entry.CheckerTargetPath;
            IsEditMode         = true;
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

        public ICommand BrowseSourceCommand  { get; private set; } = null!;
        public ICommand BrowseReserveCommand { get; private set; } = null!;
        public ICommand BrowseCheckerCommand { get; private set; } = null!;

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
                string.IsNullOrWhiteSpace(CheckerTargetPath))
            {
                ValidationError = "At least one target path (Reserve or Checker) must be specified.";
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
            Id                = existingId ?? Guid.NewGuid().ToString(),
            Name              = Name.Trim(),
            SourcePath        = SourcePath.Trim(),
            ReserveTargetPath = ReserveTargetPath.Trim(),
            CheckerTargetPath = CheckerTargetPath.Trim()
        };
    }
}
