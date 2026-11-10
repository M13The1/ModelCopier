using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using ModelCopier.Models;
using ModelCopier.Services;
using ModelCopier.Views;

namespace ModelCopier.ViewModels
{
    /// <summary>
    /// ViewModel for the main window.
    /// Owns the model list, CRUD commands, and the async backup workflow.
    /// </summary>
    public class MainViewModel : ViewModelBase
    {
        private readonly ConfigService _configService;
        private readonly BackupService _backupService;
        private readonly AppConfig     _config;

        // Backup state
        private ModelEntry? _selectedModel;
        private bool         _createReserveBackup = true;
        private bool         _createCheckerBackup = true;
        private bool         _isBusy;
        private double       _progressPercentage;
        private int          _progressCopied;
        private int          _progressTotal;
        private string       _progressText   = string.Empty;
        private string       _statusMessage  = "Ready";
        private CancellationTokenSource? _cts;

        // ── Constructor ──────────────────────────────────────────────────

        public MainViewModel()
        {
            _configService = new ConfigService();
            _backupService = new BackupService();
            _config        = _configService.Load();
            Models         = new ObservableCollection<ModelEntry>(_config.Models);

            AddCommand          = new RelayCommand(_ => ExecuteAdd());
            EditCommand         = new RelayCommand(_ => ExecuteEdit(),
                                      _ => SelectedModel != null && IsNotBusy);
            DeleteCommand       = new RelayCommand(_ => ExecuteDelete(),
                                      _ => SelectedModel != null && IsNotBusy);
            CreateBackupCommand = new RelayCommand(_ => ExecuteCreateBackup(),
                                      _ => SelectedModel != null
                                           && (CreateReserveBackup || CreateCheckerBackup)
                                           && IsNotBusy);
            CancelBackupCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);

            SelectDualBackupCommand = new RelayCommand(_ =>
            {
                CreateReserveBackup = true;
                CreateCheckerBackup = true;
                StatusMessage = "Dual replication mode active (Reserve + Checker).";
            });
            SelectReserveOnlyCommand = new RelayCommand(_ =>
            {
                CreateReserveBackup = true;
                CreateCheckerBackup = false;
                StatusMessage = "Single replication mode active (Reserve Type 1 only).";
            });
            SelectCheckerOnlyCommand = new RelayCommand(_ =>
            {
                CreateReserveBackup = false;
                CreateCheckerBackup = true;
                StatusMessage = "Single replication mode active (Checker Type 2 only).";
            });
            OpenConfigFileCommand = new RelayCommand(_ => OpenFileOrFolder(_configService.ConfigPath));
            OpenSourceFolderCommand = new RelayCommand(_ =>
            {
                if (SelectedModel != null && Directory.Exists(SelectedModel.SourcePath))
                    OpenFileOrFolder(SelectedModel.SourcePath);
            }, _ => SelectedModel != null && !string.IsNullOrWhiteSpace(SelectedModel.SourcePath));
        }

        // ── Observable list ──────────────────────────────────────────────

        public ObservableCollection<ModelEntry> Models { get; }

        // ── Selection ────────────────────────────────────────────────────

        public ModelEntry? SelectedModel
        {
            get => _selectedModel;
            set
            {
                SetField(ref _selectedModel, value);
                OnPropertyChanged(nameof(HasSelectedModel));
            }
        }

        public bool HasSelectedModel => _selectedModel != null;

        // ── Backup options ───────────────────────────────────────────────

        public bool CreateReserveBackup
        {
            get => _createReserveBackup;
            set => SetField(ref _createReserveBackup, value);
        }

        public bool CreateCheckerBackup
        {
            get => _createCheckerBackup;
            set => SetField(ref _createCheckerBackup, value);
        }

        // ── Progress / busy state ────────────────────────────────────────

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                SetField(ref _isBusy, value);
                OnPropertyChanged(nameof(IsNotBusy));
            }
        }

        public bool IsNotBusy => !_isBusy;

        public double ProgressPercentage
        {
            get => _progressPercentage;
            private set => SetField(ref _progressPercentage, value);
        }

        public int ProgressCopied
        {
            get => _progressCopied;
            private set => SetField(ref _progressCopied, value);
        }

        public int ProgressTotal
        {
            get => _progressTotal;
            private set => SetField(ref _progressTotal, value);
        }

        public string ProgressText
        {
            get => _progressText;
            private set => SetField(ref _progressText, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetField(ref _statusMessage, value);
        }

        /// <summary>Path of the JSON config file — shown in the header bar.</summary>
        public string ConfigFilePath => _configService.ConfigPath;

        // ── Commands ─────────────────────────────────────────────────────

        public ICommand AddCommand               { get; }
        public ICommand EditCommand              { get; }
        public ICommand DeleteCommand            { get; }
        public ICommand CreateBackupCommand      { get; }
        public ICommand CancelBackupCommand      { get; }
        public ICommand SelectDualBackupCommand  { get; }
        public ICommand SelectReserveOnlyCommand { get; }
        public ICommand SelectCheckerOnlyCommand { get; }
        public ICommand OpenConfigFileCommand    { get; }
        public ICommand OpenSourceFolderCommand  { get; }

        private static void OpenFileOrFolder(string path)
        {
            try
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true
                    });
                }
            }
            catch { }
        }

        // ── CRUD handlers ────────────────────────────────────────────────

        private void ExecuteAdd()
        {
            var vm     = new AddEditViewModel();
            var dialog = new AddEditDialog(vm) { Owner = Application.Current.MainWindow };

            if (dialog.ShowDialog() == true)
            {
                var entry = vm.ToModelEntry();
                Models.Add(entry);
                _config.Models.Add(entry);
                _configService.Save(_config);
                SelectedModel = entry;
                StatusMessage = $"Model '{entry.Name}' added.";
            }
        }

        private void ExecuteEdit()
        {
            if (SelectedModel == null) return;

            var vm     = new AddEditViewModel(SelectedModel);
            var dialog = new AddEditDialog(vm) { Owner = Application.Current.MainWindow };

            if (dialog.ShowDialog() == true)
            {
                // Apply edited values back to the existing entry so INPC fires.
                SelectedModel.Name              = vm.Name.Trim();
                SelectedModel.SourcePath        = vm.SourcePath.Trim();
                SelectedModel.ReserveTargetPath = vm.ReserveTargetPath.Trim();
                SelectedModel.CheckerTargetPath = vm.CheckerTargetPath.Trim();

                _configService.Save(_config);
                StatusMessage = $"Model '{SelectedModel.Name}' updated.";
            }
        }

        private void ExecuteDelete()
        {
            if (SelectedModel == null) return;

            var result = MessageBox.Show(
                $"Remove '{SelectedModel.Name}' from the list?\n\n" +
                "This does not delete any files on disk.",
                "Confirm Remove",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            string name = SelectedModel.Name;
            _config.Models.Remove(SelectedModel);
            Models.Remove(SelectedModel);
            _configService.Save(_config);
            StatusMessage = $"Model '{name}' removed.";
        }

        // ── Backup handler ───────────────────────────────────────────────

        private async void ExecuteCreateBackup()
        {
            if (SelectedModel == null) return;

            // Guard: at least one backup type must be ticked
            if (!CreateReserveBackup && !CreateCheckerBackup)
            {
                MessageBox.Show(
                    "Please tick at least one backup type (Reserve or Checker).",
                    "No Backup Type Selected",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Guard: required target paths must be set
            if (CreateReserveBackup && string.IsNullOrWhiteSpace(SelectedModel.ReserveTargetPath))
            {
                MessageBox.Show(
                    "Reserve Backup is ticked but the Reserve Target Path is not set.\n" +
                    "Please edit the model entry first.",
                    "Missing Path", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (CreateCheckerBackup && string.IsNullOrWhiteSpace(SelectedModel.CheckerTargetPath))
            {
                MessageBox.Show(
                    "Checker Backup is ticked but the Checker Target Path is not set.\n" +
                    "Please edit the model entry first.",
                    "Missing Path", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // --- Begin copy ---
            IsBusy             = true;
            ProgressPercentage = 0;
            ProgressCopied     = 0;
            ProgressTotal      = 0;
            ProgressText       = "Preparing…";

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            var progress = new Progress<BackupProgress>(p =>
            {
                ProgressPercentage = p.Percentage;
                ProgressCopied     = p.CopiedFiles;
                ProgressTotal      = p.TotalFiles;
                ProgressText       = $"Copying {p.CopiedFiles} / {p.TotalFiles}:  {p.CurrentFile}";
            });

            // Capture to avoid closure issues if the selection changes
            var model     = SelectedModel;
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmm");

            try
            {
                if (CreateReserveBackup)
                {
                    StatusMessage = $"Creating Reserve backup for '{model.Name}'…";
                    ProgressPercentage = 0;
                    string dest = Path.Combine(model.ReserveTargetPath, timestamp);
                    await _backupService.CopyFolderAsync(model.SourcePath, dest, progress, token);
                    model.LastReserveBackup = DateTime.UtcNow;
                }

                if (CreateCheckerBackup && !token.IsCancellationRequested)
                {
                    StatusMessage = $"Creating Checker backup for '{model.Name}'…";
                    ProgressPercentage = 0;
                    string dest = Path.Combine(model.CheckerTargetPath, timestamp);
                    await _backupService.CopyFolderAsync(model.SourcePath, dest, progress, token);
                    model.LastCheckerBackup = DateTime.UtcNow;
                }

                _configService.Save(_config);
                ProgressPercentage = 100;
                ProgressText       = "Completed ✓";
                StatusMessage      = $"Backup completed for '{model.Name}'.";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Backup cancelled by user.";
                ProgressText  = "Cancelled.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Backup failed:\n\n{ex.Message}",
                    "Backup Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusMessage = $"Error: {ex.Message}";
                ProgressText  = "Failed.";
            }
            finally
            {
                IsBusy = false;
                _cts?.Dispose();
                _cts = null;
            }
        }
    }
}
