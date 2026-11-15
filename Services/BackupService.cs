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

        /// <summary>Cleans and sanitizes the user-provided suffix string.</summary>
        public static string CleanSuffix(string? suffix)
        {
            if (string.IsNullOrWhiteSpace(suffix)) return string.Empty;
            var invalidChars = Path.GetInvalidFileNameChars();
            var filtered = new string(suffix.Where(c => !invalidChars.Contains(c)).ToArray()).Trim();
            return filtered;
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
        /// Renames Tekla model database files (*.db1, *.db1.bak, *.db2, *.db2.bak) in the root
        /// to match the target model folder name, avoiding Tekla model name clash.
        /// </summary>
        private static string MapDestinationRelativePath(string relativePath, string sourceModelName, string targetModelName)
        {
            if (string.Equals(sourceModelName, targetModelName, StringComparison.OrdinalIgnoreCase))
                return relativePath;

            string fileName = Path.GetFileName(relativePath);
            string? dirName = Path.GetDirectoryName(relativePath);

            // Renaming applies to root files of the model
            if (string.IsNullOrEmpty(dirName))
            {
                if (fileName.EndsWith(".db1", StringComparison.OrdinalIgnoreCase))
                    return $"{targetModelName}.db1";

                if (fileName.EndsWith(".db1.bak", StringComparison.OrdinalIgnoreCase))
                    return $"{targetModelName}.db1.bak";

                if (fileName.EndsWith(".db2", StringComparison.OrdinalIgnoreCase))
                    return $"{targetModelName}.db2";

                if (fileName.EndsWith(".db2.bak", StringComparison.OrdinalIgnoreCase))
                    return $"{targetModelName}.db2.bak";

                if (fileName.StartsWith(sourceModelName + ".", StringComparison.OrdinalIgnoreCase))
                {
                    string ext = fileName.Substring(sourceModelName.Length);
                    return $"{targetModelName}{ext}";
                }
            }

            return relativePath;
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
    }
}
