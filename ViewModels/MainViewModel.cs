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
        private string       _targetFolderSuffix  = "_bak";
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
        private string _incrementalTargetFreeSpaceText = "—";
        private string _incrementalDestinationPreview = string.Empty;

        // Auto-Backup Timer
        private readonly DispatcherTimer _autoBackupTimer;
        private TimeSpan _timeUntilNextAutoBackup;

        // Incremental Live Sync (Session-only)
        private bool _isIncrementalSyncRunning;
        private bool _isIncrementalBusy;
        private string _incrementalStatusText = "Stopped (Session only)";
        private readonly DispatcherTimer _incrementalTimer;
        private TimeSpan _timeUntilNextIncrementalSync;

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

            foreach (var m in Models)
                HookModelPropertyChanged(m);

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
                if (SelectedModel != null)
                {
                    string target = Directory.Exists(SelectedModel.LiveModelPath)
                        ? SelectedModel.LiveModelPath
                        : SelectedModel.SourcePath;
                    if (Directory.Exists(target))
                        OpenFileOrFolder(target);
                }
            }, _ => SelectedModel != null && (!string.IsNullOrWhiteSpace(SelectedModel.LiveModelPath) || !string.IsNullOrWhiteSpace(SelectedModel.SourcePath)));

            ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty);
            LaunchSelectedModelInTeklaCommand = new RelayCommand(_ => ExecuteLaunchInTekla(), _ => SelectedModel != null);

            SelectSharedConfigCommand = new RelayCommand(_ => ExecuteSelectSharedConfig());
            ResetLocalConfigCommand   = new RelayCommand(_ => ExecuteResetLocalConfig());
            ReloadConfigCommand       = new RelayCommand(_ => ExecuteReloadConfig());
            DismissToastCommand       = new RelayCommand(_ => IsToastVisible = false);

            StartIncrementalSyncCommand  = new RelayCommand(_ => ExecuteStartIncrementalSync());
            StopIncrementalSyncCommand   = new RelayCommand(_ => ExecuteStopIncrementalSync());
            ToggleIncrementalSyncCommand = new RelayCommand(_ =>
            {
                if (IsIncrementalSyncRunning) ExecuteStopIncrementalSync();
                else ExecuteStartIncrementalSync();
            });
            SyncIncrementalNowCommand    = new RelayCommand(_ => _ = ExecuteIncrementalBackupForSelectedModelsAsync(isAutomated: false),
                                               _ => !IsIncrementalBusy && !IsBusy);
            OpenIncrementalFolderCommand = new RelayCommand(_ =>
            {
                if (!string.IsNullOrWhiteSpace(IncrementalDestinationPreview) && Directory.Exists(IncrementalDestinationPreview))
                    OpenFileOrFolder(IncrementalDestinationPreview);
                else if (SelectedModel != null && Directory.Exists(SelectedModel.IncrementalTargetPath))
                    OpenFileOrFolder(SelectedModel.IncrementalTargetPath);
            }, _ => SelectedModel != null && !string.IsNullOrWhiteSpace(SelectedModel.IncrementalTargetPath));

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

            // Incremental interval timer (session-only, tick every second for live countdown)
            _incrementalTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _incrementalTimer.Tick += OnIncrementalTimerTick;
            ResetIncrementalTimer();

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
                       (entry.LiveModelPath != null && entry.LiveModelPath.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                       (entry.ReserveTargetPath != null && entry.ReserveTargetPath.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                       (entry.CheckerTargetPath != null && entry.CheckerTargetPath.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                       (entry.IncrementalTargetPath != null && entry.IncrementalTargetPath.Contains(query, StringComparison.OrdinalIgnoreCase));
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
                        _targetFolderSuffix = !string.IsNullOrWhiteSpace(_selectedModel.TargetFolderSuffix) ? _selectedModel.TargetFolderSuffix : "_bak";

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

        public string IncrementalTargetFreeSpaceText
        {
            get => _incrementalTargetFreeSpaceText;
            private set => SetField(ref _incrementalTargetFreeSpaceText, value);
        }

        public string IncrementalDestinationPreview
        {
            get => _incrementalDestinationPreview;
            private set => SetField(ref _incrementalDestinationPreview, value);
        }

        private async Task UpdateStorageInfoAsync()
        {
            if (SelectedModel == null)
            {
                SourceModelSizeText = "—";
                ReserveTargetFreeSpaceText = "—";
                CheckerTargetFreeSpaceText = "—";
                IncrementalTargetFreeSpaceText = "—";
                return;
            }

            var model = SelectedModel;

            // Query drive free space immediately
            long resFree = BackupService.GetDriveFreeSpace(model.ReserveTargetPath);
            ReserveTargetFreeSpaceText = resFree >= 0 ? $"{BackupService.FormatBytes(resFree)} free" : "Unknown";

            long chkFree = BackupService.GetDriveFreeSpace(model.CheckerTargetPath);
            CheckerTargetFreeSpaceText = chkFree >= 0 ? $"{BackupService.FormatBytes(chkFree)} free" : "Unknown";

            long incFree = BackupService.GetDriveFreeSpace(model.IncrementalTargetPath);
            IncrementalTargetFreeSpaceText = incFree >= 0 ? $"{BackupService.FormatBytes(incFree)} free" : "Unknown";

            // Calculate source model size on background thread
            SourceModelSizeText = "Calculating…";
            var options = new BackupOptions { IncludeBakFiles = IncludeBakFiles };
            long size = await Task.Run(() => BackupService.GetDirectorySize(model.LiveModelPath, options));

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
                IncrementalDestinationPreview = string.Empty;
                return;
            }

            var now = DateTime.Now;
            string previewRoot = CreateReserveBackup && !string.IsNullOrWhiteSpace(SelectedModel.ReserveTargetPath)
                ? SelectedModel.ReserveTargetPath
                : (!string.IsNullOrWhiteSpace(SelectedModel.CheckerTargetPath) ? SelectedModel.CheckerTargetPath : @"[TargetRoot]");

            string sample = BackupService.BuildBackupDestination(
                previewRoot, SelectedModel.Name, now, TargetFolderSuffix);

            DestinationPreview = sample;

            IncrementalDestinationPreview = BackupService.BuildIncrementalDestination(
                SelectedModel.IncrementalTargetPath, SelectedModel.Name, TargetFolderSuffix);
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

        public int AutoBackupSelectedCount => Models.Count(m => m.EnableAutoBackup);
        public int IncrementalSelectedCount => Models.Count(m => m.EnableIncrementalBackup);

        public string NextAutoBackupText
        {
            get
            {
                if (!IsAutoBackupEnabled)
                    return "Scheduled: Inactive";
                int count = AutoBackupSelectedCount;
                string label = count == 0 ? "no models selected" : $"{count} model{(count == 1 ? "" : "s")} selected";
                return $"Auto-Backup in: {_timeUntilNextAutoBackup.Minutes:D2}m {_timeUntilNextAutoBackup.Seconds:D2}s ({label})";
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
                if (!IsBusy)
                {
                    _ = ExecuteAutoBackupForSelectedModelsAsync();
                }
            }
            OnPropertyChanged(nameof(NextAutoBackupText));
        }

        private async Task ExecuteAutoBackupForSelectedModelsAsync()
        {
            var targets = Models.Where(m => m.EnableAutoBackup).ToList();
            if (targets.Count == 0)
            {
                StatusMessage = "Auto-backup skipped: No models selected. Check 'Auto' on the models you wish to schedule.";
                return;
            }

            if (IsBusy)
            {
                StatusMessage = "Backup engine busy; scheduled auto-backup deferred.";
                return;
            }

            IsBusy = true;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            int modelsProcessed = 0;
            int totalCopiedAcrossModels = 0;
            var now = DateTime.Now;

            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    if (token.IsCancellationRequested) break;

                    var model = targets[i];
                    if (!Directory.Exists(model.LiveModelPath))
                    {
                        StatusMessage = $"Auto-backup skipped '{model.Name}': Model folder not found at '{model.LiveModelPath}'.";
                        continue;
                    }

                    StatusMessage = $"Auto-backup ({i + 1}/{targets.Count}): Backing up '{model.Name}'…";

                    var options = new BackupOptions
                    {
                        IncludeBakFiles      = model.IncludeBakFiles,
                        ExcludeMultiUserFlag = true,
                        ExcludeLockFiles     = true,
                        ExcludeTmpFiles      = true
                    };

                    string? reserveDest = !string.IsNullOrWhiteSpace(model.ReserveTargetPath)
                        ? BackupService.BuildBackupDestination(model.ReserveTargetPath, model.Name, now, model.TargetFolderSuffix)
                        : null;

                    string? checkerDest = !string.IsNullOrWhiteSpace(model.CheckerTargetPath)
                        ? BackupService.BuildBackupDestination(model.CheckerTargetPath, model.Name, now, model.TargetFolderSuffix)
                        : null;

                    var progress = new Progress<BackupProgress>(p =>
                    {
                        ProgressPercentage = p.Percentage;
                        ProgressCopied     = p.CopiedFiles;
                        ProgressTotal      = p.TotalFiles;
                        ProgressText       = $"[{model.Name}] Copying {p.CopiedFiles}/{p.TotalFiles}: {p.CurrentFile}";
                    });

                    if (reserveDest != null && !token.IsCancellationRequested)
                    {
                        var result = await _backupService.CopyFolderAsync(model.LiveModelPath, reserveDest, options, progress, token);
                        model.LastReserveBackup = DateTime.UtcNow;
                        totalCopiedAcrossModels += result.CopiedFiles;
                        BackupService.AppendRecord(
                            RecordsFilePath, Environment.UserName, model.Name, reserveDest, result.Duration, result.IgnoredFiles, result.RenamedFiles);
                    }

                    if (checkerDest != null && !token.IsCancellationRequested)
                    {
                        var result = await _backupService.CopyFolderAsync(model.LiveModelPath, checkerDest, options, progress, token);
                        model.LastCheckerBackup = DateTime.UtcNow;
                        totalCopiedAcrossModels += result.CopiedFiles;
                        BackupService.AppendRecord(
                            RecordsFilePath, Environment.UserName, model.Name, checkerDest, result.Duration, result.IgnoredFiles, result.RenamedFiles);
                    }

                    modelsProcessed++;
                }

                _configService.Save(_config);
                StatusMessage = $"Scheduled auto-backup completed for {modelsProcessed} model(s) ({totalCopiedAcrossModels} files copied).";
                ShowToast("Auto-Backup Finished", $"Successfully backed up {modelsProcessed} selected model(s).");
                _ = UpdateStorageInfoAsync();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Auto-backup error: {ex.Message}";
                ShowToast("Auto-Backup Error", ex.Message, isSuccess: false);
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ── Incremental Live Sync (Modified Files Only — Session Only) ───

        public bool IsIncrementalSyncRunning
        {
            get => _isIncrementalSyncRunning;
            private set
            {
                if (SetField(ref _isIncrementalSyncRunning, value))
                {
                    OnPropertyChanged(nameof(IsIncrementalSyncStopped));
                    OnPropertyChanged(nameof(IsIncrementalSyncEnabled));
                    OnPropertyChanged(nameof(NextIncrementalSyncText));
                }
            }
        }

        /// <summary>Two-way bindable property for checkbox/tick mark control.</summary>
        public bool IsIncrementalSyncEnabled
        {
            get => _isIncrementalSyncRunning;
            set
            {
                if (value != _isIncrementalSyncRunning)
                {
                    if (value) ExecuteStartIncrementalSync();
                    else ExecuteStopIncrementalSync();
                    OnPropertyChanged(nameof(IsIncrementalSyncEnabled));
                }
            }
        }

        public bool IsIncrementalSyncStopped => !_isIncrementalSyncRunning;

        public bool IsIncrementalBusy
        {
            get => _isIncrementalBusy;
            private set => SetField(ref _isIncrementalBusy, value);
        }

        public int IncrementalIntervalMinutes
        {
            get => _config.IncrementalIntervalMinutes <= 0 ? 15 : _config.IncrementalIntervalMinutes;
            set
            {
                if (_config.IncrementalIntervalMinutes != value)
                {
                    _config.IncrementalIntervalMinutes = value;
                    _configService.Save(_config);
                    OnPropertyChanged();
                    ResetIncrementalTimer();
                }
            }
        }

        public IReadOnlyList<int> AvailableIncrementalIntervals { get; } = new[] { 5, 10, 15, 30, 60 };

        public string NextIncrementalSyncText
        {
            get
            {
                int count = IncrementalSelectedCount;
                string label = count == 0 ? "no models selected" : $"{count} model{(count == 1 ? "" : "s")} selected";
                if (!IsIncrementalSyncRunning)
                    return $"Incremental Sync: Stopped ({label})";
                return $"Next sync in: {_timeUntilNextIncrementalSync.Minutes:D2}m {_timeUntilNextIncrementalSync.Seconds:D2}s ({label})";
            }
        }

        public string IncrementalStatusText
        {
            get => _incrementalStatusText;
            private set => SetField(ref _incrementalStatusText, value);
        }

        private void ResetIncrementalTimer()
        {
            _timeUntilNextIncrementalSync = TimeSpan.FromMinutes(IncrementalIntervalMinutes);
            OnPropertyChanged(nameof(NextIncrementalSyncText));
        }

        private void OnIncrementalTimerTick(object? sender, EventArgs e)
        {
            if (!IsIncrementalSyncRunning) return;

            _timeUntilNextIncrementalSync = _timeUntilNextIncrementalSync.Subtract(TimeSpan.FromSeconds(1));
            if (_timeUntilNextIncrementalSync <= TimeSpan.Zero)
            {
                ResetIncrementalTimer();
                if (!IsBusy && !IsIncrementalBusy)
                {
                    _ = ExecuteIncrementalBackupForSelectedModelsAsync(isAutomated: true);
                }
            }
            OnPropertyChanged(nameof(NextIncrementalSyncText));
        }

        private void ExecuteStartIncrementalSync()
        {
            var selectedModels = Models.Where(m => m.EnableIncrementalBackup).ToList();
            if (selectedModels.Count == 0)
            {
                if (SelectedModel != null)
                {
                    if (string.IsNullOrWhiteSpace(SelectedModel.IncrementalTargetPath))
                    {
                        var ask = MessageBox.Show(
                            $"Model '{SelectedModel.Name}' does not have an Incremental Target Directory configured.\n\n" +
                            "Would you like to edit this model now to select an Incremental Target folder?",
                            "Incremental Target Required",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);

                        if (ask == MessageBoxResult.Yes)
                        {
                            ExecuteEdit();
                        }
                        return;
                    }

                    SelectedModel.EnableIncrementalBackup = true;
                    _configService.Save(_config);
                    selectedModels.Add(SelectedModel);
                }
                else
                {
                    MessageBox.Show("Please check the 'Incremental' box on the model(s) you want to include in live sync.",
                        "No Models Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }

            IsIncrementalSyncRunning = true;
            ResetIncrementalTimer();
            _incrementalTimer.Start();
            IncrementalStatusText = $"Active • Syncing {selectedModels.Count} model(s) every {IncrementalIntervalMinutes}m";
            ShowToast("Incremental Sync Active", $"Live sync enabled for {selectedModels.Count} selected model(s). Modified files will be copied every {IncrementalIntervalMinutes}m.");
            _ = ExecuteIncrementalBackupForSelectedModelsAsync(isAutomated: true);
        }

        private void ExecuteStopIncrementalSync()
        {
            IsIncrementalSyncRunning = false;
            _incrementalTimer.Stop();
            IncrementalStatusText = "Stopped (Session only)";
            OnPropertyChanged(nameof(NextIncrementalSyncText));
            ShowToast("Incremental Sync Stopped", "Live incremental sync has been paused.");
        }

        public async Task ExecuteIncrementalBackupForSelectedModelsAsync(bool isAutomated)
        {
            var targets = Models.Where(m => m.EnableIncrementalBackup).ToList();
            if (targets.Count == 0)
            {
                if (!isAutomated && SelectedModel != null)
                {
                    targets.Add(SelectedModel);
                }
                else
                {
                    IncrementalStatusText = "No models selected for incremental sync";
                    StatusMessage = "Incremental sync: No models selected. Check 'Incremental' on models to include.";
                    return;
                }
            }

            if (IsBusy || IsIncrementalBusy)
            {
                StatusMessage = "Engine currently busy; incremental sync deferred.";
                return;
            }

            try
            {
                IsIncrementalBusy = true;
                IncrementalStatusText = $"Comparing & syncing {targets.Count} model(s)…";
                StatusMessage = $"Running incremental sync for {targets.Count} selected model(s)…";

                int totalCopiedAcrossModels = 0;
                int totalScannedAcrossModels = 0;
                int modelsUpdatedCount = 0;

                for (int i = 0; i < targets.Count; i++)
                {
                    var model = targets[i];
                    if (string.IsNullOrWhiteSpace(model.IncrementalTargetPath))
                    {
                        if (!isAutomated && targets.Count == 1)
                        {
                            MessageBox.Show(
                                $"Model '{model.Name}' does not have an Incremental Target Directory configured.\n\n" +
                                "Click 'Edit' to set an Incremental Target folder.",
                                "Incremental Path Required",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                        }
                        continue;
                    }

                    if (!Directory.Exists(model.LiveModelPath))
                    {
                        continue;
                    }

                    string destination = BackupService.BuildIncrementalDestination(
                        model.IncrementalTargetPath, model.Name, model.TargetFolderSuffix);

                    var options = new BackupOptions { IncludeBakFiles = model.IncludeBakFiles };

                    var progress = new Progress<BackupProgress>(p =>
                    {
                        IncrementalStatusText = $"[{model.Name}] Syncing {p.CopiedFiles}/{p.TotalFiles} modified files…";
                    });

                    using var cts = new CancellationTokenSource();
                    var result = await _backupService.CopyFolderIncrementalAsync(
                        model.LiveModelPath, destination, options, progress, cts.Token);

                    model.LastIncrementalBackup = DateTime.UtcNow;
                    totalCopiedAcrossModels += result.CopiedFiles;
                    totalScannedAcrossModels += result.TotalScannedFiles;
                    if (result.CopiedFiles > 0) modelsUpdatedCount++;

                    BackupService.AppendIncrementalRecord(
                        RecordsFilePath, Environment.UserName, model.Name, destination, result);
                }

                _configService.Save(_config);
                _ = UpdateStorageInfoAsync();

                if (totalCopiedAcrossModels > 0)
                {
                    IncrementalStatusText = $"Last sync: {totalCopiedAcrossModels} file(s) updated ({modelsUpdatedCount}/{targets.Count} models modified)";
                    StatusMessage = $"Incremental sync: {totalCopiedAcrossModels} file(s) updated across {targets.Count} selected model(s).";
                    ShowToast("Incremental Sync Complete", $"{totalCopiedAcrossModels} file(s) updated across {targets.Count} selected model(s).");
                }
                else
                {
                    IncrementalStatusText = $"Last sync: All {targets.Count} model(s) up-to-date";
                    StatusMessage = $"Incremental sync: All {targets.Count} selected model(s) are up-to-date.";
                    if (!isAutomated)
                    {
                        ShowToast("Incremental Sync", "All selected models are up-to-date. No files were modified.");
                    }
                }
            }
            catch (Exception ex)
            {
                IncrementalStatusText = "Error during incremental sync";
                StatusMessage = $"Incremental sync error: {ex.Message}";
                ShowToast("Incremental Sync Error", ex.Message, isSuccess: false);
            }
            finally
            {
                IsIncrementalBusy = false;
                if (IsIncrementalSyncRunning)
                {
                    ResetIncrementalTimer();
                }
            }
        }

        private void HookModelPropertyChanged(ModelEntry model)
        {
            model.PropertyChanged -= OnModelPropertyChanged;
            model.PropertyChanged += OnModelPropertyChanged;
        }

        private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ModelEntry.EnableAutoBackup) ||
                e.PropertyName == nameof(ModelEntry.EnableIncrementalBackup))
            {
                _configService.Save(_config);
                OnPropertyChanged(nameof(AutoBackupSelectedCount));
                OnPropertyChanged(nameof(IncrementalSelectedCount));
                OnPropertyChanged(nameof(NextAutoBackupText));
                OnPropertyChanged(nameof(NextIncrementalSyncText));
            }
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
            {
                Models.Add(m);
                HookModelPropertyChanged(m);
            }

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
        public ICommand StartIncrementalSyncCommand       { get; }
        public ICommand StopIncrementalSyncCommand        { get; }
        public ICommand ToggleIncrementalSyncCommand      { get; }
        public ICommand SyncIncrementalNowCommand         { get; }
        public ICommand OpenIncrementalFolderCommand      { get; }

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
            if (SelectedModel == null) return;
            if (!Directory.Exists(SelectedModel.LiveModelPath))
            {
                MessageBox.Show(
                    $"Cannot launch in Tekla: Model folder '{SelectedModel.Name}' does not exist inside source path:\n\n{SelectedModel.SourcePath}\n\nExpected full path:\n{SelectedModel.LiveModelPath}",
                    "Model Folder Not Found",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            bool launched = BackupService.LaunchInTekla(SelectedModel.LiveModelPath, _config.TeklaStructuresExePath);
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
                        BackupService.LaunchInTekla(SelectedModel.LiveModelPath, _config.TeklaStructuresExePath);
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
                HookModelPropertyChanged(entry);
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
                SelectedModel.ReserveTargetPath     = vm.ReserveTargetPath.Trim();
                SelectedModel.CheckerTargetPath     = vm.CheckerTargetPath.Trim();
                SelectedModel.IncrementalTargetPath = vm.IncrementalTargetPath.Trim();
                SelectedModel.IncludeBakFiles       = vm.IncludeBakFiles;
                SelectedModel.TargetFolderSuffix = vm.TargetFolderSuffix.Trim();
                SelectedModel.EnableAutoBackup      = vm.EnableAutoBackup;
                SelectedModel.EnableIncrementalBackup = vm.EnableIncrementalBackup;

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
            if (!Directory.Exists(SelectedModel.LiveModelPath))
            {
                if (!isAutomated)
                {
                    MessageBox.Show(
                        $"Model folder '{SelectedModel.Name}' was not found in source path:\n\n{SelectedModel.SourcePath}\n\nExpected full path:\n{SelectedModel.LiveModelPath}\n\nThe application will only copy the model folder if it exists. Backup aborted.",
                        "Model Folder Not Found",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                StatusMessage = $"Backup aborted: Model folder '{SelectedModel.Name}' does not exist in '{SelectedModel.SourcePath}'.";
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
            long sourceSize = await Task.Run(() => BackupService.GetDirectorySize(model.LiveModelPath, options));

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
                    var result = await _backupService.CopyFolderAsync(model.LiveModelPath, reserveDest, options, progress, token);
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
                    var result = await _backupService.CopyFolderAsync(model.LiveModelPath, checkerDest, options, progress, token);
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
