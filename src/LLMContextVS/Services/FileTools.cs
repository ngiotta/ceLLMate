using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace LLMContextVS.Services
{
    public class FileTools
    {
        private const int MaxReadChars = 8000;
        private const int MaxResultChars = 12000;
        private const int MaxCommandOutputChars = 8000;
        private const int CommandTimeoutSeconds = 60;
        private readonly string _solutionRoot;
        private bool _backupDone;

        public FileTools(string solutionRoot)
        {
            _solutionRoot = NormalizeRoot(solutionRoot);
        }

        /// <summary>Runs a shell command in the solution directory and returns stdout+stderr.</summary>
        public string RunCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return "Error: command cannot be empty.";

            if (string.IsNullOrWhiteSpace(_solutionRoot) || !Directory.Exists(_solutionRoot))
                return "Error: no valid solution directory available.";

            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c " + command)
                {
                    WorkingDirectory = _solutionRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = new Process { StartInfo = psi };
                var output = new StringBuilder();
                var error  = new StringBuilder();

                proc.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                var finished = proc.WaitForExit(CommandTimeoutSeconds * 1000);
                if (!finished)
                {
                    try { proc.Kill(); } catch { }
                    return $"[TIMEOUT after {CommandTimeoutSeconds}s]\n" + Truncate(output.ToString() + error.ToString());
                }

                var combined = output.ToString() + error.ToString();
                return $"[Exit code: {proc.ExitCode}]\n{Truncate(combined)}";
            }
            catch (Exception ex)
            {
                return "Error running command: " + ex.Message;
            }
        }

        private string Truncate(string text)
        {
            if (text.Length <= MaxCommandOutputChars) return text;
            return text.Substring(0, MaxCommandOutputChars) + "\n...[output truncated]";
        }

        /// <summary>Returns a flat list of all file paths matching the pattern (no content).</summary>
        public string ListFiles(string filePattern)
        {
            try
            {
                var pattern = string.IsNullOrWhiteSpace(filePattern) ? "*.cs" : filePattern.Trim();
                var files = EnumerateFilesFiltered(_solutionRoot, pattern)
                    .Select(MakeDisplayPath)
                    .OrderBy(p => p)
                    .ToList();
                if (files.Count == 0)
                    return "No files found matching: " + pattern;
                return string.Join("\n", files);
            }
            catch (Exception ex)
            {
                return "Error listing files: " + ex.Message;
            }
        }

        public string ReadFile(string path)
        {
            try
            {
                var fullPath = ResolvePath(path, requireExists: true, allowDirectory: false);
                var content = File.ReadAllText(fullPath);
                if (content.Length <= MaxReadChars)
                    return content;
                return content.Substring(0, MaxReadChars) + "\r\n...[truncated]";
            }
            catch (Exception ex)
            {
                return "Error reading file: " + ex.Message;
            }
        }

        public string WriteFile(string path, string content)
        {
            try
            {
                var fullPath = ResolvePath(path, requireExists: false, allowDirectory: false);
                var directory = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrWhiteSpace(directory))
                    return "Error writing file: Invalid file path.";

                EnsureBackup(Directory.Exists(Path.Combine(_solutionRoot, ".git")));
                Directory.CreateDirectory(directory);
                File.WriteAllText(fullPath, content ?? string.Empty);
                return $"Wrote {content?.Length ?? 0} characters to {MakeDisplayPath(fullPath)}";
            }
            catch (Exception ex)
            {
                return "Error writing file: " + ex.Message;
            }
        }

        public string SearchInFiles(string query, string filePattern)
        {
            if (string.IsNullOrWhiteSpace(query))
                return "Error searching files: Query cannot be empty.";
            try
            {
                var pattern = string.IsNullOrWhiteSpace(filePattern) ? "*.*" : filePattern.Trim();
                var builder = new StringBuilder();
                var matches = 0;
                foreach (var file in EnumerateFiles(pattern))
                {
                    string[] lines;
                    try { lines = File.ReadAllLines(file); }
                    catch { continue; }
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        builder.Append(MakeDisplayPath(file));
                        builder.Append(':');
                        builder.Append(i + 1);
                        builder.Append(": ");
                        builder.AppendLine(lines[i]);
                        matches++;
                        if (matches >= 200 || builder.Length >= MaxResultChars)
                        {
                            builder.AppendLine("...[results truncated]");
                            return builder.ToString();
                        }
                    }
                }
                return matches == 0 ? "No matches found." : builder.ToString();
            }
            catch (Exception ex)
            {
                return "Error searching files: " + ex.Message;
            }
        }

        public string ListDirectory(string path)
        {
            try
            {
                var fullPath = ResolvePath(path, requireExists: true, allowDirectory: true);
                if (!Directory.Exists(fullPath))
                    return "Error listing directory: Directory not found.";
                var builder = new StringBuilder();
                builder.AppendLine("Directories:");
                foreach (var directory in Directory.GetDirectories(fullPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    builder.AppendLine("[D] " + MakeDisplayPath(directory));
                    if (builder.Length >= MaxResultChars) { builder.AppendLine("...[listing truncated]"); return builder.ToString(); }
                }
                builder.AppendLine("Files:");
                foreach (var file in Directory.GetFiles(fullPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    builder.AppendLine("[F] " + MakeDisplayPath(file));
                    if (builder.Length >= MaxResultChars) { builder.AppendLine("...[listing truncated]"); return builder.ToString(); }
                }
                return builder.ToString();
            }
            catch (Exception ex)
            {
                return "Error listing directory: " + ex.Message;
            }
        }

        private void EnsureBackup(bool isGitRepo)
        {
            if (_backupDone)
                return;

            if (string.IsNullOrWhiteSpace(_solutionRoot) || !Directory.Exists(_solutionRoot))
                throw new InvalidOperationException("No valid solution directory is available.");

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            if (isGitRepo)
            {
                var result = RunCommand($"git add -A && git stash --include-untracked -m \"ceLLMate agent backup {timestamp}\"");
                if (!result.StartsWith("[Exit code: 0]", StringComparison.Ordinal))
                    throw new InvalidOperationException("Failed to create git backup: " + result);
            }
            else
            {
                var backupRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ceLLMate",
                    "backups");
                Directory.CreateDirectory(backupRoot);

                var zipPath = Path.Combine(backupRoot, $"{SanitizeFileName(GetSolutionName())}_{timestamp}.zip");
                using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                foreach (var file in EnumerateBackupFiles(_solutionRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var entryName = MakeDisplayPath(file).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
                }
            }

            _backupDone = true;
        }

        private string GetSolutionName()
        {
            var normalizedRoot = _solutionRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var solutionName = Path.GetFileName(normalizedRoot);
            return string.IsNullOrWhiteSpace(solutionName) ? "solution" : solutionName;
        }

        private static string SanitizeFileName(string value)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
            return string.IsNullOrWhiteSpace(sanitized) ? "solution" : sanitized;
        }

        private static IEnumerable<string> EnumerateBackupFiles(string directory)
        {
            foreach (var file in Directory.GetFiles(directory))
            {
                var extension = Path.GetExtension(file);
                if (string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".xaml", StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }

            foreach (var subDirectory in Directory.GetDirectories(directory))
            {
                var name = Path.GetFileName(subDirectory);
                if (_ignoredDirs.Contains(name))
                    continue;

                foreach (var file in EnumerateBackupFiles(subDirectory))
                    yield return file;
            }
        }

        private static readonly HashSet<string> _ignoredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".vs", ".git", "node_modules", "packages", "dist", "out", "build", "target"
        };

        private IEnumerable<string> EnumerateFiles(string filePattern)
        {
            var baseDirectory = _solutionRoot;
            var searchPattern = filePattern;
            if (filePattern.IndexOf(Path.DirectorySeparatorChar) >= 0 || filePattern.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
            {
                var normalizedPattern = filePattern.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                var relativeDirectory = Path.GetDirectoryName(normalizedPattern);
                var fileNamePattern = Path.GetFileName(normalizedPattern);
                if (!string.IsNullOrWhiteSpace(relativeDirectory))
                    baseDirectory = ResolvePath(relativeDirectory, requireExists: true, allowDirectory: true);
                searchPattern = string.IsNullOrWhiteSpace(fileNamePattern) ? "*.*" : fileNamePattern;
            }
            return EnumerateFilesFiltered(baseDirectory, searchPattern);
        }

        private static IEnumerable<string> EnumerateFilesFiltered(string directory, string pattern)
        {
            foreach (var file in Directory.GetFiles(directory, pattern))
                yield return file;
            foreach (var sub in Directory.GetDirectories(directory))
            {
                var name = Path.GetFileName(sub);
                if (_ignoredDirs.Contains(name)) continue;
                foreach (var f in EnumerateFilesFiltered(sub, pattern))
                    yield return f;
            }
        }

        private string ResolvePath(string path, bool requireExists, bool allowDirectory)
        {
            if (string.IsNullOrWhiteSpace(_solutionRoot) || !Directory.Exists(_solutionRoot))
                throw new InvalidOperationException("No valid solution directory is available.");
            var candidate = string.IsNullOrWhiteSpace(path)
                ? _solutionRoot
                : Path.IsPathRooted(path) ? path : Path.Combine(_solutionRoot, path);
            var fullPath = Path.GetFullPath(candidate);
            if (!IsWithinSolution(fullPath))
                throw new InvalidOperationException("Path must stay within the current solution directory.");
            if (requireExists && !File.Exists(fullPath) && !Directory.Exists(fullPath))
                throw new FileNotFoundException("Path not found.", fullPath);
            if (!allowDirectory && Directory.Exists(fullPath))
                throw new InvalidOperationException("Expected a file path but received a directory.");
            return fullPath;
        }

        private bool IsWithinSolution(string fullPath)
        {
            var normalizedPath = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedRoot = _solutionRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private string MakeDisplayPath(string fullPath)
        {
            if (string.Equals(fullPath, _solutionRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return ".";
            var relative = fullPath.Substring(_solutionRoot.Length);
            return string.IsNullOrWhiteSpace(relative) ? "." : relative;
        }

        private static string NormalizeRoot(string solutionRoot)
        {
            if (string.IsNullOrWhiteSpace(solutionRoot)) return string.Empty;
            var fullPath = Path.GetFullPath(solutionRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullPath + Path.DirectorySeparatorChar;
        }
    }
}