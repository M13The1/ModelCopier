using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModelCopier.Models;

namespace ModelCopier.Services
{
    /// <summary>Options for filtering files during backup replication.</summary>
    public sealed class BackupOptions
    {
        /// <summary>If false, skips all *.bak files (default).</summary>
        public bool IncludeBakFiles { get; init; } = false;

        /// <summary>If true, skips .This_is_multiuser_model to convert backup to single-user mode.</summary>
        public bool ExcludeMultiUserFlag { get; init; } = true;

        /// <summary>If true, skips *.locked files created during active sessions.</summary>
        public bool ExcludeLockFiles { get; init; } = true;

        /// <summary>If true, skips *.tmp temporary files.</summary>
        public bool ExcludeTmpFiles { get; init; } = true;
    }

    /// <summary>Progress snapshot reported after each file is copied.</summary>
    public sealed class BackupProgress
    {
        public int TotalFiles { get; init; }
        public int CopiedFiles { get; init; }
        public int SkippedFiles { get; init; }
        public string CurrentFile { get; init; } = string.Empty;

        public double Percentage =>
            TotalFiles == 0 ? 0 : (double)CopiedFiles / TotalFiles * 100.0;
    }

    /// <summary>Outcome of a completed folder copy operation.</summary>
    public sealed class BackupResult
    {
        public int TotalFiles { get; init; }
        public int CopiedFiles { get; init; }
        public int SkippedFiles { get; init; }
        public List<string> IgnoredFiles { get; init; } = new();
        public List<string> RenamedFiles { get; init; } = new();
        public TimeSpan Duration { get; init; }
        public string DestinationPath { get; init; } = string.Empty;
    }

    /// <summary>Outcome of a live incremental folder sync operation.</summary>
    public sealed class IncrementalBackupResult
    {
        public int TotalScannedFiles { get; init; }
        public int CopiedFiles { get; init; }
        public int UnchangedFiles { get; init; }
        public int SkippedFiles { get; init; }
        public List<string> IgnoredFiles { get; init; } = new();
        public List<string> RenamedFiles { get; init; } = new();
        public List<string> UpdatedFiles { get; init; } = new();
        public TimeSpan Duration { get; init; }
        public string DestinationPath { get; init; } = string.Empty;
        public bool HasModifications => CopiedFiles > 0;
    }

    /// <summary>Information about a previous backup snapshot for inquiry.</summary>
    public sealed class BackupSnapshotInfo
    {
        public string FolderName { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public DateTime Timestamp { get; init; }
        public int FileCount { get; init; }
        public int SubFolderCount { get; init; }
        public string BackupType { get; init; } = string.Empty;
    }

    /// <summary>Live model statistics for integrity checking.</summary>
    public sealed class ModelStats
    {
        public int FileCount { get; init; }
        public int SubFolderCount { get; init; }
        public int BakFileCount { get; init; }
    }

    /// <summary>
    /// Performs async, recursive folder copying with Tekla-specific filtering,
    /// path structuring (<TargetRoot>\<Model>\<yyyyMM>\<dd>\<HHmm>\<Model_Suffix>),
    /// database file renaming (*.db1, *.db2 matching the new folder name),
    /// and progress reporting.
    /// </summary>
    public class BackupService
    {
        /// <summary>
        /// Constructs the standardized destination folder path according to the
        /// target hierarchy: <TargetRoot>\<ModelName>\<yyyyMM>\<dd>\<HHmm>\<ModelName>_<Suffix>
        /// Suffix is always appended to the model name folder that comes after the HHmm folder.
        /// If targetRoot already ends with modelName, duplicate modelName segment is avoided.
        /// </summary>
        public static string BuildBackupDestination(
            string targetRoot,
            string modelName,
            DateTime timestamp,
            string? suffix)
        {
            if (string.IsNullOrWhiteSpace(targetRoot))
                return string.Empty;

            string monthStamp = timestamp.ToString("yyyyMM");
            string dayStamp   = timestamp.ToString("dd");
            string timeFolder = timestamp.ToString("HHmm");

            string cleanSuffix = CleanSuffix(suffix);
            string innerModelFolder;

            if (string.IsNullOrWhiteSpace(cleanSuffix))
            {
                innerModelFolder = modelName;
            }
            else
            {
                string sep = (cleanSuffix.StartsWith("_") || cleanSuffix.StartsWith("-")) ? "" : "_";
                innerModelFolder = $"{modelName}{sep}{cleanSuffix}";
            }

            // Prevent duplicate model name folder if targetRoot already ends with modelName
            string cleanRoot = targetRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string lastFolder = Path.GetFileName(cleanRoot);
            string modelRoot = string.Equals(lastFolder, modelName, StringComparison.OrdinalIgnoreCase)
                ? cleanRoot
                : Path.Combine(cleanRoot, modelName);

            return Path.Combine(modelRoot, monthStamp, dayStamp, timeFolder, innerModelFolder);
        }

        /// <summary>Cleans, sanitizes, and ensures user-provided suffix starts with '_' if not empty.</summary>
        public static string CleanSuffix(string? suffix)
        {
            if (string.IsNullOrWhiteSpace(suffix)) return string.Empty;
            var invalidChars = Path.GetInvalidFileNameChars();
            var filtered = new string(suffix.Where(c => !invalidChars.Contains(c)).ToArray()).Trim();
            if (string.IsNullOrWhiteSpace(filtered)) return string.Empty;
            if (!filtered.StartsWith("_"))
            {
                filtered = "_" + filtered;
            }
            return filtered;
        }

        /// <summary>
        /// Computes the persistent destination folder for live incremental backups.
        /// Does NOT include timestamp subfolders (yyyyMM\dd\HHmm), maintaining a continuous mirror.
        /// Destination path: &lt;TargetRoot&gt;\&lt;ModelName&gt; or &lt;TargetRoot&gt;\&lt;ModelName&gt;_&lt;Suffix&gt;
        /// </summary>
        public static string BuildIncrementalDestination(
            string targetRoot,
            string modelName,
            string? suffix)
        {
            if (string.IsNullOrWhiteSpace(targetRoot))
                return string.Empty;

            string cleanSuffix = CleanSuffix(suffix);
            string innerModelFolder;

            if (string.IsNullOrWhiteSpace(cleanSuffix))
            {
                innerModelFolder = modelName;
            }
            else
            {
                string sep = (cleanSuffix.StartsWith("_") || cleanSuffix.StartsWith("-")) ? "" : "_";
                innerModelFolder = $"{modelName}{sep}{cleanSuffix}";
            }

            string cleanRoot = targetRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string lastFolder = Path.GetFileName(cleanRoot);

            if (string.Equals(lastFolder, innerModelFolder, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(lastFolder, modelName, StringComparison.OrdinalIgnoreCase))
            {
                return cleanRoot;
            }

            return Path.Combine(cleanRoot, innerModelFolder);
        }

        /// <summary>
        /// Copies every non-filtered file from <paramref name="sourceDir"/> (recursively) into
        /// <paramref name="targetDir"/>, creating the target directory tree as needed.
        /// Also renames *.db1, *.db1.bak, *.db2, *.db2.bak in the model root to match the target folder name.
        /// </summary>
        public async Task<BackupResult> CopyFolderAsync(
            string sourceDir,
            string targetDir,
            BackupOptions options,
            IProgress<BackupProgress>? progress,
            CancellationToken cancellationToken)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            if (!Directory.Exists(sourceDir))
                throw new DirectoryNotFoundException($"Source folder not found:\n{sourceDir}");

            string sourceModelName = Path.GetFileName(sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string targetModelName = Path.GetFileName(targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            var allFiles = CollectFiles(sourceDir);
            var filesToCopy = new List<string>();
            var ignoredFiles = new List<string>();

            foreach (var file in allFiles)
            {
                if (ShouldIgnoreFile(file, options, out string reason))
                {
                    string rel = Path.GetRelativePath(sourceDir, file);
                    ignoredFiles.Add($"{rel} [{reason}]");
                }
                else
                {
                    filesToCopy.Add(file);
                }
            }

            int total = filesToCopy.Count;
            int copied = 0;
            var renamedFiles = new List<string>();

            Directory.CreateDirectory(targetDir);

            foreach (string srcFile in filesToCopy)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string relative = Path.GetRelativePath(sourceDir, srcFile);
                string destRelative = MapDestinationRelativePath(relative, sourceModelName, targetModelName);
                string destFile = Path.Combine(targetDir, destRelative);

                if (!string.Equals(relative, destRelative, StringComparison.OrdinalIgnoreCase))
                {
                    renamedFiles.Add($"{relative} -> {destRelative}");
                }

                string? destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                string src = srcFile;
                string dst = destFile;
                await Task.Run(() => File.Copy(src, dst, overwrite: true), cancellationToken);

                copied++;
                progress?.Report(new BackupProgress
                {
                    TotalFiles   = total,
                    CopiedFiles  = copied,
                    SkippedFiles = ignoredFiles.Count,
                    CurrentFile  = destRelative
                });
            }

            // Ensure empty model directories (e.g. drawings, attributes) are also mirrored
            MirrorEmptyDirectories(sourceDir, targetDir);

            sw.Stop();

            return new BackupResult
            {
                TotalFiles      = total,
                CopiedFiles     = copied,
                SkippedFiles    = ignoredFiles.Count,
                IgnoredFiles    = ignoredFiles,
                RenamedFiles    = renamedFiles,
                Duration        = sw.Elapsed,
                DestinationPath = targetDir
            };
        }

        /// <summary>
        /// Incrementally copies only new or modified files from <paramref name="sourceDir"/> into <paramref name="targetDir"/>.
        /// Compares file size and write timestamps (with 1.0s tolerance).
        /// Preserves LastWriteTimeUtc on destination files for accurate future diffs.
        /// Also renames *.db1, *.db2 database files in model root if target folder is suffixed.
        /// </summary>
        public async Task<IncrementalBackupResult> CopyFolderIncrementalAsync(
            string sourceDir,
            string targetDir,
            BackupOptions options,
            IProgress<BackupProgress>? progress,
            CancellationToken cancellationToken)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            if (!Directory.Exists(sourceDir))
                throw new DirectoryNotFoundException($"Source folder not found:\n{sourceDir}");

            string sourceModelName = Path.GetFileName(sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string targetModelName = Path.GetFileName(targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            var allFiles = CollectFiles(sourceDir);
            var validSourceFiles = new List<string>();
            var ignoredFiles = new List<string>();

            foreach (var file in allFiles)
            {
                if (ShouldIgnoreFile(file, options, out string reason))
                {
                    string rel = Path.GetRelativePath(sourceDir, file);
                    ignoredFiles.Add($"{rel} [{reason}]");
                }
                else
                {
                    validSourceFiles.Add(file);
                }
            }

            var filesToCopy = new List<(string Source, string Dest, string DestRelative)>();
            var updatedFiles = new List<string>();
            var renamedFiles = new List<string>();
            int unchanged = 0;

            foreach (string srcFile in validSourceFiles)
            {
                string relative = Path.GetRelativePath(sourceDir, srcFile);
                string destRelative = MapDestinationRelativePath(relative, sourceModelName, targetModelName);
                string destFile = Path.Combine(targetDir, destRelative);

                if (!string.Equals(relative, destRelative, StringComparison.OrdinalIgnoreCase))
                {
                    renamedFiles.Add($"{relative} -> {destRelative}");
                }

                bool needsCopy = false;
                if (!File.Exists(destFile))
                {
                    needsCopy = true;
                }
                else
                {
                    var srcInfo = new FileInfo(srcFile);
                    var dstInfo = new FileInfo(destFile);

                    // Compare file size or modification time (with 1.0s tolerance for filesystem rounding)
                    if (srcInfo.Length != dstInfo.Length ||
                        Math.Abs((srcInfo.LastWriteTimeUtc - dstInfo.LastWriteTimeUtc).TotalSeconds) > 1.0)
                    {
                        needsCopy = true;
                    }
                }

                if (needsCopy)
                {
                    filesToCopy.Add((srcFile, destFile, destRelative));
                    updatedFiles.Add(destRelative);
                }
                else
                {
                    unchanged++;
                }
            }

            int totalToCopy = filesToCopy.Count;
            int copied = 0;

            Directory.CreateDirectory(targetDir);

            foreach (var item in filesToCopy)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? destDir = Path.GetDirectoryName(item.Dest);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                var srcInfo = new FileInfo(item.Source);
                string src = item.Source;
                string dst = item.Dest;

                await Task.Run(() =>
                {
                    File.Copy(src, dst, overwrite: true);
                    File.SetLastWriteTimeUtc(dst, srcInfo.LastWriteTimeUtc);
                }, cancellationToken);

                copied++;
                progress?.Report(new BackupProgress
                {
                    TotalFiles   = totalToCopy,
                    CopiedFiles  = copied,
                    SkippedFiles = ignoredFiles.Count,
                    CurrentFile  = item.DestRelative
                });
            }

            // Ensure empty model directories (e.g. drawings, attributes) are mirrored
            MirrorEmptyDirectories(sourceDir, targetDir);

            sw.Stop();

            return new IncrementalBackupResult
            {
                TotalScannedFiles = validSourceFiles.Count,
                CopiedFiles       = copied,
                UnchangedFiles    = unchanged,
                SkippedFiles      = ignoredFiles.Count,
                IgnoredFiles      = ignoredFiles,
                RenamedFiles      = renamedFiles,
                UpdatedFiles      = updatedFiles,
                Duration          = sw.Elapsed,
                DestinationPath   = targetDir
            };
        }

        /// <summary>
        /// Renames Tekla model database files ((ModelFolderName).db1, (ModelFolderName).db1.bak, 
        /// (ModelFolderName).db2, (ModelFolderName).db2.bak) in the model root
        /// to match the target model folder name, avoiding Tekla model name clash.
        /// Files whose name is not the model folder name (such as xslib.db1, xslib.db1.bak, xslib.db2, xslib.db2.bak)
        /// are NOT renamed.
        /// </summary>
        public static string MapDestinationRelativePath(string relativePath, string sourceModelName, string targetModelName)
        {
            if (string.Equals(sourceModelName, targetModelName, StringComparison.OrdinalIgnoreCase))
                return relativePath;

            string fileName = Path.GetFileName(relativePath);
            string? dirName = Path.GetDirectoryName(relativePath);

            // Renaming strictly applies only to root database files matching (ModelFolderName).db1, .db1.bak, .db2, .db2.bak
            if (string.IsNullOrEmpty(dirName))
            {
                string cleanSource = sourceModelName.Trim();
                string cleanTarget = targetModelName.Trim();

                if (string.Equals(fileName, $"{cleanSource}.db1", StringComparison.OrdinalIgnoreCase))
                    return $"{cleanTarget}.db1";

                if (string.Equals(fileName, $"{cleanSource}.db1.bak", StringComparison.OrdinalIgnoreCase))
                    return $"{cleanTarget}.db1.bak";

                if (string.Equals(fileName, $"{cleanSource}.db2", StringComparison.OrdinalIgnoreCase))
                    return $"{cleanTarget}.db2";

                if (string.Equals(fileName, $"{cleanSource}.db2.bak", StringComparison.OrdinalIgnoreCase))
                    return $"{cleanTarget}.db2.bak";
            }

            return relativePath;
        }

        // ── Disk Space & Tekla Launch Helpers ────────────────────────────

        /// <summary>Returns available free space in bytes on the drive containing the specified path.</summary>
        public static long GetDriveFreeSpace(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return -1;
            try
            {
                string fullPath = Path.GetFullPath(path);
                string? root = Path.GetPathRoot(fullPath);
                if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                {
                    var drive = new DriveInfo(root);
                    return drive.AvailableFreeSpace;
                }
            }
            catch { }
            return -1;
        }

        /// <summary>Calculates total bytes for files to be copied from a directory.</summary>
        public static long GetDirectorySize(string directory, BackupOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return 0;

            long totalBytes = 0;
            try
            {
                var allFiles = CollectFiles(directory);
                foreach (var f in allFiles)
                {
                    if (options != null && ShouldIgnoreFile(f, options, out _))
                        continue;

                    try
                    {
                        totalBytes += new FileInfo(f).Length;
                    }
                    catch { }
                }
            }
            catch { }
            return totalBytes;
        }

        /// <summary>Formats bytes into human readable format (e.g. 1.25 GB, 350 MB).</summary>
        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "Unknown";
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            double d = bytes;
            while (d >= 1024 && i < suffixes.Length - 1)
            {
                d /= 1024;
                i++;
            }
            return $"{d:0.##} {suffixes[i]}";
        }

        /// <summary>Finds installed TeklaStructures.exe binaries on the machine.</summary>
        public static List<string> FindTeklaInstallations()
        {
            var found = new List<string>();
            string[] searchBases = {
                @"C:\Program Files\Trimble\Tekla Structures",
                @"C:\Program Files\Tekla Structures",
                @"C:\TeklaStructures"
            };

            foreach (var b in searchBases)
            {
                if (Directory.Exists(b))
                {
                    try
                    {
                        var matches = Directory.GetFiles(b, "TeklaStructures.exe", SearchOption.AllDirectories);
                        found.AddRange(matches);
                    }
                    catch { }
                }
            }

            return found.Distinct().OrderByDescending(p => p).ToList();
        }

        /// <summary>Launches a model folder directly in Tekla Structures.</summary>
        public static bool LaunchInTekla(string modelPath, string? customTeklaExe = null)
        {
            if (!Directory.Exists(modelPath)) return false;

            string? exePath = customTeklaExe;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                var installed = FindTeklaInstallations();
                exePath = installed.FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return false;

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"\"{modelPath}\"",
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private static bool ShouldIgnoreFile(string filePath, BackupOptions options, out string reason)
        {
            string fileName = Path.GetFileName(filePath);
            string ext = Path.GetExtension(filePath);

            if (options.ExcludeMultiUserFlag &&
                (string.Equals(fileName, ".This_is_multiuser_model", StringComparison.OrdinalIgnoreCase) ||
                 fileName.EndsWith(".This_is_multiuser_model", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(ext, ".This_is_multiuser_model", StringComparison.OrdinalIgnoreCase)))
            {
                reason = "Converted to single-user mode";
                return true;
            }

            if (options.ExcludeLockFiles &&
                (string.Equals(ext, ".locked", StringComparison.OrdinalIgnoreCase) ||
                 fileName.EndsWith(".locked", StringComparison.OrdinalIgnoreCase)))
            {
                reason = "Active lock file";
                return true;
            }

            if (options.ExcludeTmpFiles &&
                string.Equals(ext, ".tmp", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Temporary file";
                return true;
            }

            if (!options.IncludeBakFiles &&
                (string.Equals(ext, ".bak", StringComparison.OrdinalIgnoreCase) ||
                 fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)))
            {
                reason = "Tekla backup file (*.bak)";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        private static List<string> CollectFiles(string directory)
        {
            var files = new List<string>();
            try
            {
                files.AddRange(Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly));
                foreach (string sub in Directory.GetDirectories(directory))
                    files.AddRange(CollectFiles(sub));
            }
            catch (UnauthorizedAccessException) { /* skip inaccessible dirs */ }
            catch (IOException)                 { /* skip IO errors           */ }
            return files;
        }

        private static void MirrorEmptyDirectories(string sourceDir, string targetDir)
        {
            try
            {
                foreach (string subDir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(sourceDir, subDir);
                    string destSub = Path.Combine(targetDir, rel);
                    if (!Directory.Exists(destSub))
                        Directory.CreateDirectory(destSub);
                }
            }
            catch { }
        }

        /// <summary>Scans live model directory and returns file/folder stats.</summary>
        public static ModelStats GetModelStats(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath) || !Directory.Exists(modelPath))
                return new ModelStats();

            try
            {
                int fileCount = Directory.GetFiles(modelPath, "*", SearchOption.AllDirectories).Length;
                int bakCount = Directory.GetFiles(modelPath, "*.bak", SearchOption.AllDirectories).Length;
                int folderCount = Directory.GetDirectories(modelPath, "*", SearchOption.AllDirectories).Length;

                return new ModelStats
                {
                    FileCount = Math.Max(0, fileCount - bakCount),
                    BakFileCount = bakCount,
                    SubFolderCount = folderCount
                };
            }
            catch
            {
                return new ModelStats();
            }
        }

        /// <summary>Finds today's backup snapshots under <TargetRoot>\<ModelName>\<yyyyMM>\<dd>\.</summary>
        public static List<BackupSnapshotInfo> GetTodayBackups(string targetRoot, string modelName, string backupType)
        {
            var list = new List<BackupSnapshotInfo>();
            if (string.IsNullOrWhiteSpace(targetRoot))
                return list;

            string cleanRoot = targetRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string lastFolder = Path.GetFileName(cleanRoot);
            string modelRoot = string.Equals(lastFolder, modelName, StringComparison.OrdinalIgnoreCase)
                ? cleanRoot
                : Path.Combine(cleanRoot, modelName);

            if (!Directory.Exists(modelRoot))
                return list;

            string todayFolder = Path.Combine(
                modelRoot,
                DateTime.Now.ToString("yyyyMM"),
                DateTime.Now.ToString("dd"));

            if (!Directory.Exists(todayFolder))
                return list;

            try
            {
                var dirs = new DirectoryInfo(todayFolder).GetDirectories()
                    .OrderByDescending(d => d.CreationTime)
                    .Take(10);

                foreach (var dir in dirs)
                {
                    // Find the inner model folder (e.g. ModelName_Bak or ModelName)
                    var subDirs = dir.GetDirectories();
                    var modelDir = subDirs.FirstOrDefault(d => d.Name.StartsWith(modelName, StringComparison.OrdinalIgnoreCase))
                                   ?? subDirs.FirstOrDefault();

                    string searchPath = modelDir?.FullName ?? dir.FullName;
                    string displayLabel = modelDir != null ? $"{dir.Name}  ({modelDir.Name})" : dir.Name;

                    int fileCount = 0;
                    int subFolderCount = 0;

                    try
                    {
                        int allFiles = Directory.GetFiles(searchPath, "*", SearchOption.AllDirectories).Length;
                        int bakFiles = Directory.GetFiles(searchPath, "*.bak", SearchOption.AllDirectories).Length
                                     + Directory.GetFiles(searchPath, "*.tmp", SearchOption.AllDirectories).Length
                                     + Directory.GetFiles(searchPath, "*.locked", SearchOption.AllDirectories).Length;
                        fileCount = Math.Max(0, allFiles - bakFiles);
                        subFolderCount = Directory.GetDirectories(searchPath, "*", SearchOption.AllDirectories).Length;
                    }
                    catch { }

                    list.Add(new BackupSnapshotInfo
                    {
                        FolderName     = displayLabel,
                        FullPath       = searchPath,
                        Timestamp      = dir.CreationTime,
                        FileCount      = fileCount,
                        SubFolderCount = subFolderCount,
                        BackupType     = backupType
                    });
                }
            }
            catch { }

            return list;
        }

        /// <summary>Appends a structured record of a completed backup to Records.txt.</summary>
        public static void AppendRecord(
            string recordFilePath,
            string userName,
            string modelName,
            string destinationPath,
            TimeSpan duration,
            IReadOnlyList<string> ignoredFiles,
            IReadOnlyList<string>? renamedFiles = null)
        {
            try
            {
                using var writer = File.AppendText(recordFilePath);
                writer.WriteLine("----------------------------------------------------------------------");
                writer.WriteLine($"Timestamp    : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine($"UserName     : {userName}");
                writer.WriteLine($"Model        : {modelName}");
                writer.WriteLine($"Destination  : {destinationPath}");
                writer.WriteLine($"Time taken   : {duration.Minutes} min {duration.Seconds}.{duration.Milliseconds / 100:0} sec");

                if (renamedFiles != null && renamedFiles.Count > 0)
                {
                    writer.WriteLine($"Renamed database files ({renamedFiles.Count}):");
                    foreach (var r in renamedFiles)
                    {
                        writer.WriteLine($"  * {r}");
                    }
                }

                writer.WriteLine($"Files skipped: {ignoredFiles.Count}");
                if (ignoredFiles.Count > 0)
                {
                    writer.WriteLine("List of files not copied:");
                    foreach (var f in ignoredFiles)
                    {
                        writer.WriteLine($"  - {f}");
                    }
                }
                writer.WriteLine();
            }
            catch { }
        }

        /// <summary>Appends a structured record of an incremental sync to Records.txt.</summary>
        public static void AppendIncrementalRecord(
            string recordFilePath,
            string userName,
            string modelName,
            string destinationPath,
            IncrementalBackupResult result)
        {
            try
            {
                using var writer = File.AppendText(recordFilePath);
                writer.WriteLine("----------------------------------------------------------------------");
                writer.WriteLine($"[INCREMENTAL SYNC] Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine($"UserName     : {userName}");
                writer.WriteLine($"Model        : {modelName}");
                writer.WriteLine($"Destination  : {destinationPath}");
                writer.WriteLine($"Time taken   : {result.Duration.Minutes} min {result.Duration.Seconds}.{result.Duration.Milliseconds / 100:0} sec");
                writer.WriteLine($"Total Scanned: {result.TotalScannedFiles}");
                writer.WriteLine($"Files Updated: {result.CopiedFiles} (new or modified)");
                writer.WriteLine($"Unchanged    : {result.UnchangedFiles}");
                writer.WriteLine($"Files Skipped: {result.SkippedFiles} (*.bak, *.locked, *.tmp, etc.)");

                if (result.UpdatedFiles.Count > 0 && result.UpdatedFiles.Count <= 20)
                {
                    writer.WriteLine("Updated files:");
                    foreach (var u in result.UpdatedFiles)
                        writer.WriteLine($"  * {u}");
                }
                else if (result.UpdatedFiles.Count > 20)
                {
                    writer.WriteLine($"Updated files ({result.UpdatedFiles.Count} total - showing first 15):");
                    foreach (var u in result.UpdatedFiles.Take(15))
                        writer.WriteLine($"  * {u}");
                    writer.WriteLine($"  ... and {result.UpdatedFiles.Count - 15} more files.");
                }

                writer.WriteLine();
            }
            catch { }
        }
    }
}
