using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
        private bool         _includeBakFiles     = false;
        private string       _targetFolderSuffix  = string.Empty;
        private string       _destinationPreview  = string.Empty;
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

            OpenConfigFileCommand    = new RelayCommand(_ => OpenFileOrFolder(_configService.ConfigPath));
            OpenRecordsFileCommand   = new RelayCommand(_ => ExecuteOpenRecords());
            InquireBackupsCommand    = new RelayCommand(_ => ExecuteInquireBackups(), _ => SelectedModel != null);
            OpenSourceFolderCommand  = new RelayCommand(_ =>
            {
                if (SelectedModel != null && Directory.Exists(SelectedModel.SourcePath))
                    OpenFileOrFolder(SelectedModel.SourcePath);
            }, _ => SelectedModel != null && !string.IsNullOrWhiteSpace(SelectedModel.SourcePath));

            UpdateDestinationPreview();
        }

        // ── Observable list ──────────────────────────────────────────────

        public ObservableCollection<ModelEntry> Models { get; }

        // ── Selection ────────────────────────────────────────────────────

        public ModelEntry? SelectedModel
        {
            get => _selectedModel;
            set
            {
                if (SetField(ref _selectedModel, value))
                {
                    OnPropertyChanged(nameof(HasSelectedModel));

                    if (_selectedModel != null)
                    {
                        _includeBakFiles    = _selectedModel.IncludeBakFiles;
                        _targetFolderSuffix = _selectedModel.TargetFolderSuffix ?? string.Empty;

                        OnPropertyChanged(nameof(IncludeBakFiles));
                        OnPropertyChanged(nameof(TargetFolderSuffix));
                    }

                    UpdateDestinationPreview();
                }
            }
        }

        public bool HasSelectedModel => _selectedModel != null;

        // ── Backup options ───────────────────────────────────────────────

        public bool CreateReserveBackup
        {
            get => _createReserveBackup;
            set
            {
                if (SetField(ref _createReserveBackup, value))
                    UpdateDestinationPreview();
            }
        }

        public bool CreateCheckerBackup
        {
            get => _createCheckerBackup;
            set
            {
                if (SetField(ref _createCheckerBackup, value))
                    UpdateDestinationPreview();
            }
        }

        /// <summary>Whether to copy *.bak files when replicating models.</summary>
        public bool IncludeBakFiles
        {
            get => _includeBakFiles;
            set
            {
                if (SetField(ref _includeBakFiles, value))
                {
                    if (SelectedModel != null)
                    {
                        SelectedModel.IncludeBakFiles = value;
                        _configService.Save(_config);
                    }
                }
            }
        }

        /// <summary>Custom suffix appended to the inner model folder after HHmm (e.g. QC, RevA).</summary>
        public string TargetFolderSuffix
        {
            get => _targetFolderSuffix;
            set
            {
                if (SetField(ref _targetFolderSuffix, value))
                {
                    if (SelectedModel != null)
                    {
                        SelectedModel.TargetFolderSuffix = value;
                        _configService.Save(_config);
                    }
                    UpdateDestinationPreview();
                }
            }
        }

        public string DestinationPreview
        {
            get => _destinationPreview;
            private set => SetField(ref _destinationPreview, value);
        }

        private void UpdateDestinationPreview()
        {
            if (SelectedModel == null)
            {
                DestinationPreview = "Select a model from the registry to preview the destination path.";
                return;
            }

            var now = DateTime.Now;
            string previewRoot = CreateReserveBackup && !string.IsNullOrWhiteSpace(SelectedModel.ReserveTargetPath)
                ? SelectedModel.ReserveTargetPath
                : (!string.IsNullOrWhiteSpace(SelectedModel.CheckerTargetPath) ? SelectedModel.CheckerTargetPath : @"[TargetRoot]");

            string sample = BackupService.BuildBackupDestination(
                previewRoot, SelectedModel.Name, now, TargetFolderSuffix);

            DestinationPreview = sample;
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

        /// <summary>Path of the JSON config file — shown in the footer bar.</summary>
        public string ConfigFilePath => _configService.ConfigPath;

        /// <summary>Path of the Records.txt log file.</summary>
        public string RecordsFilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Records.txt");

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
        public ICommand OpenRecordsFileCommand   { get; }
        public ICommand InquireBackupsCommand    { get; }
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

        private void ExecuteOpenRecords()
        {
            if (!File.Exists(RecordsFilePath))
            {
                File.WriteAllText(RecordsFilePath,
                    "======================================================================\n" +
                    "           BuildSmith ModelCopier - Backup History Records            \n" +
                    "======================================================================\n\n");
            }
            OpenFileOrFolder(RecordsFilePath);
        }

        private void ExecuteInquireBackups()
        {
            if (SelectedModel == null) return;
            var vm = new InquireBackupsViewModel(SelectedModel);
            var dialog = new InquireBackupsDialog(vm) { Owner = Application.Current.MainWindow };
            dialog.ShowDialog();
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
                SelectedModel.Name               = vm.Name.Trim();
                SelectedModel.SourcePath         = vm.SourcePath.Trim();
                SelectedModel.ReserveTargetPath  = vm.ReserveTargetPath.Trim();
                SelectedModel.CheckerTargetPath  = vm.CheckerTargetPath.Trim();
                SelectedModel.IncludeBakFiles    = vm.IncludeBakFiles;
                SelectedModel.TargetFolderSuffix = vm.TargetFolderSuffix.Trim();

                // Sync viewmodel state
                IncludeBakFiles    = vm.IncludeBakFiles;
                TargetFolderSuffix = vm.TargetFolderSuffix.Trim();

                _configService.Save(_config);
                UpdateDestinationPreview();
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

            // Guard: model source exists
            if (!Directory.Exists(SelectedModel.SourcePath))
            {
                MessageBox.Show(
                    $"Source model folder does not exist:\n\n{SelectedModel.SourcePath}\n\nPlease check path!",
                    "Model Folder Not Found",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Guard: at least one backup type must be ticked
            if (!CreateReserveBackup && !CreateCheckerBackup)
            {
                MessageBox.Show(
                    "Please select at least one backup mode (Reserve or Checker).",
                    "No Backup Type Selected",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Guard: required target paths must be set
            if (CreateReserveBackup && string.IsNullOrWhiteSpace(SelectedModel.ReserveTargetPath))
            {
                MessageBox.Show(
                    "Reserve Backup is ticked but the Reserve Target Path is not configured.\n" +
                    "Please edit the model entry first.",
                    "Missing Path", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (CreateCheckerBackup && string.IsNullOrWhiteSpace(SelectedModel.CheckerTargetPath))
            {
                MessageBox.Show(
                    "Checker Backup is ticked but the Checker Target Path is not configured.\n" +
                    "Please edit the model entry first.",
                    "Missing Path", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Guard: warn if no suffix specified (avoids clashing with live model)
            if (string.IsNullOrWhiteSpace(TargetFolderSuffix))
            {
                var prompt = MessageBox.Show(
                    "No target folder suffix is specified.\n\n" +
                    "Having a suffix is helpful in avoiding clash with live Tekla Structures model when both are opened.\n\n" +
                    "Are you sure you want to proceed without a suffix?",
                    "No Suffix Specified",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (prompt != MessageBoxResult.Yes) return;
            }

            var model = SelectedModel;
            var now = DateTime.Now;

            // Compute hierarchical destination paths (suffix always appended to model name after HHmm)
            string? reserveDest = CreateReserveBackup
                ? BackupService.BuildBackupDestination(model.ReserveTargetPath, model.Name, now, TargetFolderSuffix)
                : null;

            string? checkerDest = CreateCheckerBackup
                ? BackupService.BuildBackupDestination(model.CheckerTargetPath, model.Name, now, TargetFolderSuffix)
                : null;

            // Target collision check (matching Form1.cs protection)
            if (reserveDest != null && Directory.Exists(reserveDest) && Directory.EnumerateFileSystemEntries(reserveDest).Any())
            {
                var prompt = MessageBox.Show(
                    $"The Reserve backup destination folder is not empty:\n\n{reserveDest}\n\n" +
                    "A backup may have already been created for this time slice.\n" +
                    "Do you want to overwrite existing files?\n\n" +
                    "(Click 'No' to cancel and change the suffix or wait a minute)",
                    "Folder Not Empty",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (prompt != MessageBoxResult.Yes) return;
            }

            if (checkerDest != null && Directory.Exists(checkerDest) && Directory.EnumerateFileSystemEntries(checkerDest).Any())
            {
                var prompt = MessageBox.Show(
                    $"The Checker backup destination folder is not empty:\n\n{checkerDest}\n\n" +
                    "A backup may have already been created for this time slice.\n" +
                    "Do you want to overwrite existing files?\n\n" +
                    "(Click 'No' to cancel and change the suffix or wait a minute)",
                    "Folder Not Empty",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (prompt != MessageBoxResult.Yes) return;
            }

            // --- Begin copy ---
            IsBusy             = true;
            ProgressPercentage = 0;
            ProgressCopied     = 0;
            ProgressTotal      = 0;
            ProgressText       = "Preparing backup files…";

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            var progress = new Progress<BackupProgress>(p =>
            {
                ProgressPercentage = p.Percentage;
                ProgressCopied     = p.CopiedFiles;
                ProgressTotal      = p.TotalFiles;
                ProgressText       = $"Copying {p.CopiedFiles} / {p.TotalFiles}: {p.CurrentFile}  ({p.SkippedFiles} skipped)";
            });

            var options = new BackupOptions
            {
                IncludeBakFiles      = IncludeBakFiles,
                ExcludeMultiUserFlag = true,
                ExcludeLockFiles     = true,
                ExcludeTmpFiles      = true
            };

            int totalCopied  = 0;
            int totalSkipped = 0;
            int totalRenamed = 0;
            TimeSpan totalDuration = TimeSpan.Zero;

            try
            {
                if (reserveDest != null)
                {
                    StatusMessage = $"Creating Reserve backup for '{model.Name}'…";
                    ProgressPercentage = 0;
                    var result = await _backupService.CopyFolderAsync(model.SourcePath, reserveDest, options, progress, token);
                    model.LastReserveBackup = DateTime.UtcNow;

                    totalCopied  += result.CopiedFiles;
                    totalSkipped += result.SkippedFiles;
                    totalRenamed += result.RenamedFiles.Count;
                    totalDuration += result.Duration;

                    BackupService.AppendRecord(
                        RecordsFilePath, Environment.UserName, model.Name, reserveDest, result.Duration, result.IgnoredFiles, result.RenamedFiles);
                }

                if (checkerDest != null && !token.IsCancellationRequested)
                {
                    StatusMessage = $"Creating Checker backup for '{model.Name}'…";
                    ProgressPercentage = 0;
                    var result = await _backupService.CopyFolderAsync(model.SourcePath, checkerDest, options, progress, token);
                    model.LastCheckerBackup = DateTime.UtcNow;

                    totalCopied  += result.CopiedFiles;
                    totalSkipped += result.SkippedFiles;
                    totalRenamed += result.RenamedFiles.Count;
                    totalDuration += result.Duration;

                    BackupService.AppendRecord(
                        RecordsFilePath, Environment.UserName, model.Name, checkerDest, result.Duration, result.IgnoredFiles, result.RenamedFiles);
                }

                _configService.Save(_config);
                ProgressPercentage = 100;
                ProgressText       = $"Completed ✓ ({totalCopied} files copied, {totalSkipped} skipped)";
                StatusMessage      = $"Backup completed in {FormatDuration(totalDuration)} ({totalCopied} copied, {totalSkipped} skipped).";

                string finishMsg = $"Model '{model.Name}' backup completed successfully!\n\n" +
                                   $"• Files copied: {totalCopied}\n" +
                                   $"• Database files renamed: {totalRenamed} (*.db1, *.db2 matching '{Path.GetFileName(reserveDest ?? checkerDest)}')\n" +
                                   $"• Files skipped: {totalSkipped} (single-user converted, locks/bak skipped)\n" +
                                   $"• Time taken: {FormatDuration(totalDuration)}";

                MessageBox.Show(finishMsg, "Backup Succeeded", MessageBoxButton.OK, MessageBoxImage.Information);
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

        private static string FormatDuration(TimeSpan ts)
        {
            if (ts.TotalSeconds >= 60)
                return $"{(int)ts.TotalMinutes} min {ts.Seconds} sec";
            return $"{ts.TotalSeconds:0.0} sec";
        }
    }
}
