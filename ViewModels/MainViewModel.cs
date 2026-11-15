using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using ModelCopier.Models;
using ModelCopier.Services;
using ModelCopier.Views;

namespace ModelCopier.ViewModels
{
    /// <summary>
    /// ViewModel for the main window.
    /// Owns the model list, search filtering, scheduled automated backups,
    /// free space validation, Tekla launcher, shared network config, and backup workflow.
    /// </summary>
    public class MainViewModel : ViewModelBase
    {
        private readonly ConfigService _configService;
        private readonly BackupService _backupService;
        private AppConfig              _config;

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

        // Search & Filter
        private string _searchText = string.Empty;

        // Storage metrics
        private string _sourceModelSizeText = "—";
        private string _reserveTargetFreeSpaceText = "—";
        private string _checkerTargetFreeSpaceText = "—";

        // Auto-Backup Timer
        private readonly DispatcherTimer _autoBackupTimer;
        private TimeSpan _timeUntilNextAutoBackup;

        // In-App Toast & Chime
        private string _toastTitle = string.Empty;
        private string _toastMessage = string.Empty;
        private bool   _isToastVisible;
        private bool   _toastIsSuccess = true;
        private readonly DispatcherTimer _toastTimer;

        // ── Constructor ──────────────────────────────────────────────────

        public MainViewModel()
        {
            _configService = new ConfigService();
            _backupService = new BackupService();
            _config        = _configService.Load();
            Models         = new ObservableCollection<ModelEntry>(_config.Models);

            ModelsView = CollectionViewSource.GetDefaultView(Models);
            ModelsView.Filter = FilterModel;

            // Commands
            AddCommand          = new RelayCommand(_ => ExecuteAdd());
            EditCommand         = new RelayCommand(_ => ExecuteEdit(),
                                      _ => SelectedModel != null && IsNotBusy);
            DeleteCommand       = new RelayCommand(_ => ExecuteDelete(),
                                      _ => SelectedModel != null && IsNotBusy);
            CreateBackupCommand = new RelayCommand(_ => ExecuteCreateBackup(isAutomated: false),
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

            ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty);
            LaunchSelectedModelInTeklaCommand = new RelayCommand(_ => ExecuteLaunchInTekla(), _ => SelectedModel != null);

            SelectSharedConfigCommand = new RelayCommand(_ => ExecuteSelectSharedConfig());
            ResetLocalConfigCommand   = new RelayCommand(_ => ExecuteResetLocalConfig());
            ReloadConfigCommand       = new RelayCommand(_ => ExecuteReloadConfig());
            DismissToastCommand       = new RelayCommand(_ => IsToastVisible = false);

            // Toast Timer (5 seconds)
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _toastTimer.Tick += (_, _) =>
            {
                IsToastVisible = false;
                _toastTimer.Stop();
            };

            // Auto-backup interval timer (ticks every second for live countdown)
            _autoBackupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _autoBackupTimer.Tick += OnAutoBackupTimerTick;
            ResetAutoBackupTimer();
            if (_config.AutoBackupEnabled)
                _autoBackupTimer.Start();

            UpdateDestinationPreview();
        }

        // ── Observable list & Filtered View ──────────────────────────────

        public ObservableCollection<ModelEntry> Models { get; }
        public ICollectionView ModelsView { get; }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetField(ref _searchText, value))
                {
                    ModelsView.Refresh();
                    OnPropertyChanged(nameof(HasSearchText));
                }
            }
        }

        public bool HasSearchText => !string.IsNullOrEmpty(_searchText);

        private bool FilterModel(object obj)
        {
            if (string.IsNullOrWhiteSpace(_searchText))
                return true;

            if (obj is ModelEntry entry)
            {
                string query = _searchText.Trim();
                return (entry.Name != null && entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                       (entry.SourcePath != null && entry.SourcePath.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                       (entry.ReserveTargetPath != null && entry.ReserveTargetPath.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                       (entry.CheckerTargetPath != null && entry.CheckerTargetPath.Contains(query, StringComparison.OrdinalIgnoreCase));
            }
            return false;
        }

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
                    _ = UpdateStorageInfoAsync();
                }
            }
        }

        public bool HasSelectedModel => _selectedModel != null;

        // ── Storage metrics & Pre-check ──────────────────────────────────

        public string SourceModelSizeText
        {
            get => _sourceModelSizeText;
            private set => SetField(ref _sourceModelSizeText, value);
        }

        public string ReserveTargetFreeSpaceText
        {
            get => _reserveTargetFreeSpaceText;
            private set => SetField(ref _reserveTargetFreeSpaceText, value);
        }

        public string CheckerTargetFreeSpaceText
        {
            get => _checkerTargetFreeSpaceText;
            private set => SetField(ref _checkerTargetFreeSpaceText, value);
        }

        private async Task UpdateStorageInfoAsync()
        {
            if (SelectedModel == null)
            {
                SourceModelSizeText = "—";
                ReserveTargetFreeSpaceText = "—";
                CheckerTargetFreeSpaceText = "—";
                return;
            }

            var model = SelectedModel;

            // Query drive free space immediately
            long resFree = BackupService.GetDriveFreeSpace(model.ReserveTargetPath);
            ReserveTargetFreeSpaceText = resFree >= 0 ? $"{BackupService.FormatBytes(resFree)} free" : "Unknown";

            long chkFree = BackupService.GetDriveFreeSpace(model.CheckerTargetPath);
            CheckerTargetFreeSpaceText = chkFree >= 0 ? $"{BackupService.FormatBytes(chkFree)} free" : "Unknown";

            // Calculate source model size on background thread
            SourceModelSizeText = "Calculating…";
            var options = new BackupOptions { IncludeBakFiles = IncludeBakFiles };
            long size = await Task.Run(() => BackupService.GetDirectorySize(model.SourcePath, options));

            if (SelectedModel == model)
            {
                SourceModelSizeText = BackupService.FormatBytes(size);
            }
        }

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
                    _ = UpdateStorageInfoAsync();
                }
            }
        }

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

        // ── Auto-Backup Scheduling ───────────────────────────────────────

        public bool IsAutoBackupEnabled
        {
            get => _config.AutoBackupEnabled;
            set
            {
                if (_config.AutoBackupEnabled != value)
                {
                    _config.AutoBackupEnabled = value;
                    _configService.Save(_config);
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(NextAutoBackupText));

                    if (value)
                    {
                        ResetAutoBackupTimer();
                        _autoBackupTimer.Start();
                    }
                    else
                    {
                        _autoBackupTimer.Stop();
                    }
                }
            }
        }

        public int AutoBackupIntervalMinutes
        {
            get => _config.AutoBackupIntervalMinutes <= 0 ? 60 : _config.AutoBackupIntervalMinutes;
            set
            {
                if (_config.AutoBackupIntervalMinutes != value)
                {
                    _config.AutoBackupIntervalMinutes = value;
                    _configService.Save(_config);
                    OnPropertyChanged();
                    ResetAutoBackupTimer();
                }
            }
        }

        public IReadOnlyList<int> AvailableIntervals { get; } = new[] { 15, 30, 60, 120 };

        public string NextAutoBackupText
        {
            get
            {
                if (!IsAutoBackupEnabled)
                    return "Scheduled: Inactive";
                return $"Auto-Backup in: {_timeUntilNextAutoBackup.Minutes:D2}m {_timeUntilNextAutoBackup.Seconds:D2}s";
            }
        }

        private void ResetAutoBackupTimer()
        {
            _timeUntilNextAutoBackup = TimeSpan.FromMinutes(AutoBackupIntervalMinutes);
            OnPropertyChanged(nameof(NextAutoBackupText));
        }

        private void OnAutoBackupTimerTick(object? sender, EventArgs e)
        {
            if (!IsAutoBackupEnabled) return;

            _timeUntilNextAutoBackup = _timeUntilNextAutoBackup.Subtract(TimeSpan.FromSeconds(1));
            if (_timeUntilNextAutoBackup <= TimeSpan.Zero)
            {
                ResetAutoBackupTimer();
                if (SelectedModel != null && !IsBusy)
                {
                    StatusMessage = $"Scheduled automated backup triggered for '{SelectedModel.Name}'…";
                    ExecuteCreateBackup(isAutomated: true);
                }
            }
            OnPropertyChanged(nameof(NextAutoBackupText));
        }

        // ── Shared Network Configuration ─────────────────────────────────

        public bool IsUsingSharedConfig => _configService.IsUsingSharedConfig;

        public string ConfigFilePath => _configService.ConfigPath;

        public string ConfigLocationBadge =>
            IsUsingSharedConfig ? "NETWORK SHARED CONFIG" : "LOCAL CONFIG";

        private void ExecuteSelectSharedConfig()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Shared models.json on Office Network Drive",
                Filter = "Model Registry (*.json)|*.json|All Files (*.*)|*.*",
                CheckFileExists = false
            };

            if (dialog.ShowDialog() == true)
            {
                _configService.SetCustomConfigPath(dialog.FileName);
                ExecuteReloadConfig();
                ShowToast("Shared Config Activated", $"Connected to network registry:\n{dialog.FileName}");
            }
        }

        private void ExecuteResetLocalConfig()
        {
            _configService.ResetToDefault();
            ExecuteReloadConfig();
            ShowToast("Local Config Restored", "Reverted to local application models.json.");
        }

        private void ExecuteReloadConfig()
        {
            _config = _configService.Load();
            Models.Clear();
            foreach (var m in _config.Models)
                Models.Add(m);

            OnPropertyChanged(nameof(ConfigFilePath));
            OnPropertyChanged(nameof(IsUsingSharedConfig));
            OnPropertyChanged(nameof(ConfigLocationBadge));

            SelectedModel = Models.FirstOrDefault();
            StatusMessage = "Configuration reloaded from disk/network.";
        }

        // ── Toast Notification & Sound Chime ─────────────────────────────

        public bool PlaySoundOnCompletion
        {
            get => _config.PlaySoundOnCompletion;
            set
            {
                if (_config.PlaySoundOnCompletion != value)
                {
                    _config.PlaySoundOnCompletion = value;
                    _configService.Save(_config);
                    OnPropertyChanged();
                }
            }
        }

        public string ToastTitle
        {
            get => _toastTitle;
            private set => SetField(ref _toastTitle, value);
        }

        public string ToastMessage
        {
            get => _toastMessage;
            private set => SetField(ref _toastMessage, value);
        }

        public bool IsToastVisible
        {
            get => _isToastVisible;
            private set => SetField(ref _isToastVisible, value);
        }

        public bool ToastIsSuccess
        {
            get => _toastIsSuccess;
            private set => SetField(ref _toastIsSuccess, value);
        }

        public void ShowToast(string title, string message, bool isSuccess = true)
        {
            ToastTitle = title;
            ToastMessage = message;
            ToastIsSuccess = isSuccess;
            IsToastVisible = true;

            if (PlaySoundOnCompletion)
            {
                try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
            }

            if (Application.Current?.MainWindow != null)
            {
                FlashWindow(Application.Current.MainWindow);
            }

            _toastTimer.Stop();
            _toastTimer.Start();
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

        public string AppVersion
        {
            get
            {
                var ver = typeof(MainViewModel).Assembly.GetName().Version;
                return ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v2.1.0";
            }
        }

        public string WindowTitle => $"BuildSmith Precision Suite — Tekla Model Copier ({AppVersion})";

        public string RecordsFilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Records.txt");

        // ── Commands ─────────────────────────────────────────────────────

        public ICommand AddCommand                        { get; }
        public ICommand EditCommand                       { get; }
        public ICommand DeleteCommand                     { get; }
        public ICommand CreateBackupCommand               { get; }
        public ICommand CancelBackupCommand               { get; }
        public ICommand SelectDualBackupCommand           { get; }
        public ICommand SelectReserveOnlyCommand          { get; }
        public ICommand SelectCheckerOnlyCommand          { get; }
        public ICommand OpenConfigFileCommand             { get; }
        public ICommand OpenRecordsFileCommand            { get; }
        public ICommand InquireBackupsCommand             { get; }
        public ICommand OpenSourceFolderCommand           { get; }
        public ICommand ClearSearchCommand                { get; }
        public ICommand LaunchSelectedModelInTeklaCommand { get; }
        public ICommand SelectSharedConfigCommand         { get; }
        public ICommand ResetLocalConfigCommand           { get; }
        public ICommand ReloadConfigCommand               { get; }
        public ICommand DismissToastCommand               { get; }

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

        private void ExecuteLaunchInTekla()
        {
            if (SelectedModel == null || !Directory.Exists(SelectedModel.SourcePath)) return;

            bool launched = BackupService.LaunchInTekla(SelectedModel.SourcePath, _config.TeklaStructuresExePath);
            if (!launched)
            {
                var prompt = MessageBox.Show(
                    "TeklaStructures.exe could not be detected automatically.\n\n" +
                    "Would you like to browse and locate TeklaStructures.exe manually?",
                    "Tekla Not Found",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (prompt == MessageBoxResult.Yes)
                {
                    var dlg = new OpenFileDialog
                    {
                        Title = "Locate TeklaStructures.exe",
                        Filter = "TeklaStructures (TeklaStructures.exe)|TeklaStructures.exe|All Executables (*.exe)|*.exe"
                    };
                    if (dlg.ShowDialog() == true)
                    {
                        _config.TeklaStructuresExePath = dlg.FileName;
                        _configService.Save(_config);
                        BackupService.LaunchInTekla(SelectedModel.SourcePath, _config.TeklaStructuresExePath);
                        ShowToast("Tekla Configured", $"Saved Tekla executable path:\n{dlg.FileName}");
                    }
                }
            }
            else
            {
                ShowToast("Tekla Launched", $"Opening '{SelectedModel.Name}' in Tekla Structures…");
            }
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
                ShowToast("Model Added", $"Model '{entry.Name}' registered.");
            }
        }

        private void ExecuteEdit()
        {
            if (SelectedModel == null) return;

            var vm     = new AddEditViewModel(SelectedModel);
            var dialog = new AddEditDialog(vm) { Owner = Application.Current.MainWindow };

            if (dialog.ShowDialog() == true)
            {
                SelectedModel.Name               = vm.Name.Trim();
                SelectedModel.SourcePath         = vm.SourcePath.Trim();
                SelectedModel.ReserveTargetPath  = vm.ReserveTargetPath.Trim();
                SelectedModel.CheckerTargetPath  = vm.CheckerTargetPath.Trim();
                SelectedModel.IncludeBakFiles    = vm.IncludeBakFiles;
                SelectedModel.TargetFolderSuffix = vm.TargetFolderSuffix.Trim();

                IncludeBakFiles    = vm.IncludeBakFiles;
                TargetFolderSuffix = vm.TargetFolderSuffix.Trim();

                _configService.Save(_config);
                UpdateDestinationPreview();
                _ = UpdateStorageInfoAsync();
                StatusMessage = $"Model '{SelectedModel.Name}' updated.";
                ShowToast("Model Updated", $"Changes saved for '{SelectedModel.Name}'.");
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
            ShowToast("Model Removed", $"Removed '{name}' from registry.");
        }

        // ── Backup handler ───────────────────────────────────────────────

        private async void ExecuteCreateBackup(bool isAutomated)
        {
            if (SelectedModel == null) return;

            // Guard: model source exists
            if (!Directory.Exists(SelectedModel.SourcePath))
            {
                if (!isAutomated)
                {
                    MessageBox.Show(
                        $"Source model folder does not exist:\n\n{SelectedModel.SourcePath}\n\nPlease check path!",
                        "Model Folder Not Found",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                StatusMessage = "Backup aborted: Source folder does not exist.";
                return;
            }

            // Guard: at least one backup type must be ticked
            if (!CreateReserveBackup && !CreateCheckerBackup)
            {
                if (!isAutomated)
                {
                    MessageBox.Show(
                        "Please select at least one backup mode (Reserve or Checker).",
                        "No Backup Type Selected",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }

            // Guard: required target paths must be set
            if (CreateReserveBackup && string.IsNullOrWhiteSpace(SelectedModel.ReserveTargetPath))
            {
                if (!isAutomated)
                {
                    MessageBox.Show(
                        "Reserve Backup is ticked but the Reserve Target Path is not configured.\n" +
                        "Please edit the model entry first.",
                        "Missing Path", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }
            if (CreateCheckerBackup && string.IsNullOrWhiteSpace(SelectedModel.CheckerTargetPath))
            {
                if (!isAutomated)
                {
                    MessageBox.Show(
                        "Checker Backup is ticked but the Checker Target Path is not configured.\n" +
                        "Please edit the model entry first.",
                        "Missing Path", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }

            var model = SelectedModel;
            var now = DateTime.Now;

            // Compute hierarchical destination paths
            string? reserveDest = CreateReserveBackup
                ? BackupService.BuildBackupDestination(model.ReserveTargetPath, model.Name, now, TargetFolderSuffix)
                : null;

            string? checkerDest = CreateCheckerBackup
                ? BackupService.BuildBackupDestination(model.CheckerTargetPath, model.Name, now, TargetFolderSuffix)
                : null;

            var options = new BackupOptions
            {
                IncludeBakFiles      = IncludeBakFiles,
                ExcludeMultiUserFlag = true,
                ExcludeLockFiles     = true,
                ExcludeTmpFiles      = true
            };

            // ── Target Drive Free Space Pre-Check ────────────────────────
            long sourceSize = await Task.Run(() => BackupService.GetDirectorySize(model.SourcePath, options));

            if (reserveDest != null)
            {
                long resFree = BackupService.GetDriveFreeSpace(reserveDest);
                if (resFree >= 0 && resFree < sourceSize)
                {
                    string msg = $"Insufficient disk space on Reserve backup volume:\n\n" +
                                 $"• Required: {BackupService.FormatBytes(sourceSize)}\n" +
                                 $"• Available: {BackupService.FormatBytes(resFree)}\n\n" +
                                 "Backup aborted to prevent disk exhaustion.";
                    if (!isAutomated)
                        MessageBox.Show(msg, "Insufficient Disk Space", MessageBoxButton.OK, MessageBoxImage.Error);
                    ShowToast("Disk Space Error", msg, isSuccess: false);
                    StatusMessage = "Aborted: Insufficient disk space on Reserve drive.";
                    return;
                }
            }

            if (checkerDest != null)
            {
                long chkFree = BackupService.GetDriveFreeSpace(checkerDest);
                if (chkFree >= 0 && chkFree < sourceSize)
                {
                    string msg = $"Insufficient disk space on Checker backup volume:\n\n" +
                                 $"• Required: {BackupService.FormatBytes(sourceSize)}\n" +
                                 $"• Available: {BackupService.FormatBytes(chkFree)}\n\n" +
                                 "Backup aborted to prevent disk exhaustion.";
                    if (!isAutomated)
                        MessageBox.Show(msg, "Insufficient Disk Space", MessageBoxButton.OK, MessageBoxImage.Error);
                    ShowToast("Disk Space Error", msg, isSuccess: false);
                    StatusMessage = "Aborted: Insufficient disk space on Checker drive.";
                    return;
                }
            }

            // ── Interactive collision and suffix warnings ────────────────
            if (!isAutomated)
            {
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

                // Surface Chime, Taskbar Flash, and Toast Notification
                ShowToast(
                    "Replication Succeeded ✓",
                    $"{model.Name} backed up in {FormatDuration(totalDuration)} ({totalCopied} files copied).");

                _ = UpdateStorageInfoAsync();

                if (!isAutomated)
                {
                    string finishMsg = $"Model '{model.Name}' backup completed successfully!\n\n" +
                                       $"• Files copied: {totalCopied}\n" +
                                       $"• Database files renamed: {totalRenamed} (*.db1, *.db2 matching '{Path.GetFileName(reserveDest ?? checkerDest)}')\n" +
                                       $"• Files skipped: {totalSkipped} (single-user converted, locks/bak skipped)\n" +
                                       $"• Time taken: {FormatDuration(totalDuration)}";

                    MessageBox.Show(finishMsg, "Backup Succeeded", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Backup cancelled by user.";
                ProgressText  = "Cancelled.";
                ShowToast("Backup Cancelled", "Replication was stopped by user.", isSuccess: false);
            }
            catch (Exception ex)
            {
                if (!isAutomated)
                {
                    MessageBox.Show(
                        $"Backup failed:\n\n{ex.Message}",
                        "Backup Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                StatusMessage = $"Error: {ex.Message}";
                ProgressText  = "Failed.";
                ShowToast("Backup Failed ✕", ex.Message, isSuccess: false);
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

        // ── Win32 Window Flash Helper ────────────────────────────────────

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        private const uint FLASHW_ALL = 3;
        private const uint FLASHW_TIMERNOFG = 12;

        private static void FlashWindow(Window window)
        {
            try
            {
                var helper = new WindowInteropHelper(window);
                if (helper.Handle != IntPtr.Zero)
                {
                    var info = new FLASHWINFO
                    {
                        cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                        hwnd = helper.Handle,
                        dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                        uCount = 3,
                        dwTimeout = 0
                    };
                    FlashWindowEx(ref info);
                }
            }
            catch { }
        }
    }
}
