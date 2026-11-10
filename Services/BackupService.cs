using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ModelCopier.Services
{
    /// <summary>Progress snapshot reported after each file is copied.</summary>
    public sealed class BackupProgress
    {
        public int TotalFiles  { get; init; }
        public int CopiedFiles { get; init; }
        public string CurrentFile { get; init; } = string.Empty;

        public double Percentage =>
            TotalFiles == 0 ? 0 : (double)CopiedFiles / TotalFiles * 100.0;
    }

    /// <summary>
    /// Performs an async, recursive folder copy with progress reporting.
    /// </summary>
    public class BackupService
    {
        /// <summary>
        /// Copies every file from <paramref name="sourceDir"/> (recursively) into
        /// <paramref name="targetDir"/>, creating the target directory tree as needed.
        /// Existing files in the target are overwritten.
        /// </summary>
        /// <exception cref="DirectoryNotFoundException">Source does not exist.</exception>
        /// <exception cref="OperationCanceledException">Token was cancelled.</exception>
        public async Task CopyFolderAsync(
            string sourceDir,
            string targetDir,
            IProgress<BackupProgress> progress,
            CancellationToken cancellationToken)
        {
            if (!Directory.Exists(sourceDir))
                throw new DirectoryNotFoundException(
                    $"Source folder not found:\n{sourceDir}");

            var allFiles = CollectFiles(sourceDir);
            int total  = allFiles.Count;
            int copied = 0;

            Directory.CreateDirectory(targetDir);

            foreach (string srcFile in allFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string relative = Path.GetRelativePath(sourceDir, srcFile);
                string destFile = Path.Combine(targetDir, relative);

                string? destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                // Capture loop variables for the lambda.
                string src = srcFile;
                string dst = destFile;
                await Task.Run(() => File.Copy(src, dst, overwrite: true), cancellationToken);

                copied++;
                progress?.Report(new BackupProgress
                {
                    TotalFiles  = total,
                    CopiedFiles = copied,
                    CurrentFile = relative
                });
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private static List<string> CollectFiles(string directory)
        {
            var files = new List<string>();
            try
            {
                files.AddRange(
                    Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly));

                foreach (string sub in Directory.GetDirectories(directory))
                    files.AddRange(CollectFiles(sub));
            }
            catch (UnauthorizedAccessException) { /* skip inaccessible dirs */ }
            catch (IOException)                 { /* skip IO errors           */ }
            return files;
        }
    }
}
