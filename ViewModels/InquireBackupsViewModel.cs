using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using ModelCopier.Models;
using ModelCopier.Services;

namespace ModelCopier.ViewModels
{
    public class InquireBackupsViewModel : ViewModelBase
    {
        private readonly ModelEntry _model;
        private int _liveFileCount;
        private int _liveSubFolderCount;
        private int _liveBakCount;
        private bool _hasReserveBackups;
        private bool _hasCheckerBackups;

        public InquireBackupsViewModel(ModelEntry model)
        {
            _model = model;
            ModelName = model.Name;
            SourcePath = model.SourcePath;
            ReserveTargetPath = model.ReserveTargetPath;
            CheckerTargetPath = model.CheckerTargetPath;

            ReserveBackups = new ObservableCollection<BackupSnapshotInfo>();
            CheckerBackups = new ObservableCollection<BackupSnapshotInfo>();

            OpenFolderCommand = new RelayCommand(param =>
            {
                if (param is string path && Directory.Exists(path))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = path,
                            UseShellExecute = true
                        });
                    }
                    catch { }
                }
            });

            RefreshCommand = new RelayCommand(_ => Refresh());
            Refresh();
        }

        public string ModelName { get; }
        public string SourcePath { get; }
        public string ReserveTargetPath { get; }
        public string CheckerTargetPath { get; }

        public int LiveFileCount
        {
            get => _liveFileCount;
            private set => SetField(ref _liveFileCount, value);
        }

        public int LiveSubFolderCount
        {
            get => _liveSubFolderCount;
            private set => SetField(ref _liveSubFolderCount, value);
        }

        public int LiveBakCount
        {
            get => _liveBakCount;
            private set => SetField(ref _liveBakCount, value);
        }

        public bool HasReserveBackups
        {
            get => _hasReserveBackups;
            private set => SetField(ref _hasReserveBackups, value);
        }

        public bool HasCheckerBackups
        {
            get => _hasCheckerBackups;
            private set => SetField(ref _hasCheckerBackups, value);
        }

        public ObservableCollection<BackupSnapshotInfo> ReserveBackups { get; }
        public ObservableCollection<BackupSnapshotInfo> CheckerBackups { get; }

        public ICommand OpenFolderCommand { get; }
        public ICommand RefreshCommand { get; }

        public void Refresh()
        {
            // 1. Live stats
            var stats = BackupService.GetModelStats(_model.SourcePath);
            LiveFileCount = stats.FileCount;
            LiveSubFolderCount = stats.SubFolderCount;
            LiveBakCount = stats.BakFileCount;

            // 2. Reserve snapshots today
            ReserveBackups.Clear();
            var reserveList = BackupService.GetTodayBackups(_model.ReserveTargetPath, _model.Name, "Reserve");
            foreach (var b in reserveList)
                ReserveBackups.Add(b);
            HasReserveBackups = ReserveBackups.Count > 0;

            // 3. Checker snapshots today
            CheckerBackups.Clear();
            var checkerList = BackupService.GetTodayBackups(_model.CheckerTargetPath, _model.Name, "Checker");
            foreach (var b in checkerList)
                CheckerBackups.Add(b);
            HasCheckerBackups = CheckerBackups.Count > 0;
        }
    }
}
