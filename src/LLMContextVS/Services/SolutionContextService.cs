using Community.VisualStudio.Toolkit;
using EnvDTE;
using LLMContextVS.Models;
using LLMContextVS.Options;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;
using Project = EnvDTE.Project;

namespace LLMContextVS.Services
{
    public interface ISolutionContextService
    {
        Task<SolutionContextSnapshot> GetSolutionContextAsync(ContextMode mode = ContextMode.FullSolution, string? activeFile = null, string? selectionText = null);
        string BuildPromptPrefix(SolutionContextSnapshot snapshot, ContextMode mode, string? activeFile = null, string? selectionText = null);
        List<string> GetIgnoredPatterns();
    }

    public class SolutionContextService : ISolutionContextService
    {
        private readonly LLMOptions _options;

        public SolutionContextService(LLMOptions? options = null)
        {
            _options = options ?? LLMOptions.Instance;
        }

        public List<string> GetIgnoredPatterns()
        {
            if (string.IsNullOrWhiteSpace(_options.IgnoredPatterns))
                return new List<string>();

            return _options.IgnoredPatterns
                .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim().ToLowerInvariant())
                .ToList();
        }

        private bool ShouldIgnore(string pathOrName)
        {
            if (string.IsNullOrWhiteSpace(pathOrName)) return false;

            var lower = pathOrName.ToLowerInvariant();
            var patterns = GetIgnoredPatterns();

            foreach (var p in patterns)
            {
                if (p.StartsWith("*."))
                {
                    if (lower.EndsWith(p.Substring(1))) return true;
                }
                else if (lower.Contains(p) || Path.GetFileName(lower).Contains(p))
                {
                    return true;
                }
            }
            return false;
        }

        private bool IsAllowedExtension(string path)
        {
            if (string.IsNullOrWhiteSpace(_options.AllowedExtensions))
                return true;

            var exts = _options.AllowedExtensions
                .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim().ToLowerInvariant())
                .ToList();

            var ext = Path.GetExtension(path).ToLowerInvariant();
            return exts.Any(e => e == ext || e == "*");
        }

        public async Task<SolutionContextSnapshot> GetSolutionContextAsync(ContextMode mode = ContextMode.FullSolution, string? activeFile = null, string? selectionText = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var snapshot = new SolutionContextSnapshot();

            try
            {
                var dte = await VS.GetServiceAsync<DTE, DTE>();
                if (dte?.Solution == null || !dte.Solution.IsOpen)
                {
                    snapshot.SolutionName = "No solution open";
                    return snapshot;
                }

                snapshot.SolutionName = Path.GetFileNameWithoutExtension(dte.Solution.FullName);
                snapshot.SolutionPath = dte.Solution.FullName;

                var projects = GetDTEProjects(dte.Solution.Projects);
                int maxChars = _options.MaxContextChars;
                long currentChars = 0;

                foreach (var proj in projects)
                {
                    if (ShouldIgnore(proj.Name) || ShouldIgnore(proj.FullName))
                        continue;

                    var pctx = new SolutionProjectContext
                    {
                        Name = proj.Name,
                        FullPath = proj.FullName,
                        Kind = proj.Kind ?? ""
                    };

                    // Get project items recursively (files only)
                    var items = await GetProjectItemsRecursiveAsync(proj);

                    foreach (var item in items)
                    {
                        try
                        {
                            if (string.IsNullOrEmpty(item.FilePath) || !File.Exists(item.FilePath))
                                continue;

                            if (ShouldIgnore(item.FilePath) || !IsAllowedExtension(item.FilePath))
                                continue;

                            var fi = new FileInfo(item.FilePath);
                            if (fi.Length > _options.MaxFileSizeKb * 1024L)
                            {
                                // Include stub for very large files
                                var stub = $"// [File too large: {fi.Length / 1024} KB - truncated in context]\r\n";
                                continue;
                            }

                            string content = await Task.Run(() => System.IO.File.ReadAllText(item.FilePath));
                            int add = content.Length + 50; // path overhead

                            if (currentChars + add > maxChars && mode == ContextMode.FullSolution)
                            {
                                // Stop adding full files once limit reached
                                break;
                            }

                            var fileCtx = new SolutionFileContext
                            {
                                FullPath = item.FilePath,
                                RelativePath = MakeRelative(snapshot.SolutionPath, item.FilePath),
                                ProjectName = proj.Name,
                                Content = content,
                                SizeBytes = fi.Length,
                                Included = true
                            };

                            pctx.Files.Add(fileCtx);
                            currentChars += add;

                            if (currentChars > maxChars && mode == ContextMode.FullSolution)
                                break;
                        }
                        catch { /* skip unreadable files */ }
                    }

                    if (pctx.Files.Any())
                        snapshot.Projects.Add(pctx);

                    if (currentChars > maxChars && mode == ContextMode.FullSolution)
                        break;
                }

                // If selection or current file mode, we still keep structure but will prioritize in prompt builder
                snapshot.TotalFilesIncluded = snapshot.Projects.Sum(p => p.Files.Count(f => f.Included));
                snapshot.TotalChars = currentChars;
            }
            catch (Exception ex)
            {
                snapshot.SolutionName = "Error collecting context: " + ex.Message;
            }

            return snapshot;
        }

        private async Task<List<(string FilePath, string Name)>> GetProjectItemsRecursiveAsync(Project project)
        {
            var files = new List<(string, string)>();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            void Collect(ProjectItems items)
            {
                if (items == null) return;

                foreach (ProjectItem item in items)
                {
                    try
                    {
                        if (item.FileCount > 0)
                        {
                            string? file = item.FileNames[1]; // 1-based
                            if (!string.IsNullOrEmpty(file) && File.Exists(file))
                            {
                                files.Add((file, item.Name));
                            }
                        }

                        if (item.ProjectItems != null && item.ProjectItems.Count > 0)
                        {
                            Collect(item.ProjectItems);
                        }

                        // Some projects nest further
                        if (item.SubProject != null && item.SubProject.ProjectItems != null)
                        {
                            Collect(item.SubProject.ProjectItems);
                        }
                    }
                    catch { }
                }
            }

            if (project.ProjectItems != null)
                Collect(project.ProjectItems);

            return files;
        }

        private static string MakeRelative(string solutionPath, string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(solutionPath)) return Path.GetFileName(filePath);
                string solutionDir = Path.GetDirectoryName(solutionPath) ?? "";
                if (string.IsNullOrEmpty(solutionDir)) return Path.GetFileName(filePath);

                var rel = MakeRelativeUri(solutionDir, filePath);
                return rel.Replace('\\', '/');
            }
            catch
            {
                return Path.GetFileName(filePath);
            }
        }

        private static string MakeRelativeUri(string fromDir, string toPath)
        {
            if (!fromDir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                fromDir += Path.DirectorySeparatorChar;
            var fromUri = new Uri(fromDir);
            var toUri = new Uri(toPath);
            var relativeUri = fromUri.MakeRelativeUri(toUri);
            return Uri.UnescapeDataString(relativeUri.ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private static List<EnvDTE.Project> GetDTEProjects(EnvDTE.Projects projects)
        {
            var result = new List<EnvDTE.Project>();
            foreach (EnvDTE.Project project in projects)
            {
                if (project == null) continue;
                try
                {
                    if (project.Kind == EnvDTE.Constants.vsProjectKindSolutionItems && project.ProjectItems != null)
                        result.AddRange(GetDTEProjectsFromItems(project.ProjectItems));
                    else
                        result.Add(project);
                }
                catch { }
            }
            return result;
        }

        private static List<EnvDTE.Project> GetDTEProjectsFromItems(EnvDTE.ProjectItems items)
        {
            var result = new List<EnvDTE.Project>();
            if (items == null) return result;
            foreach (EnvDTE.ProjectItem item in items)
            {
                try
                {
                    if (item?.SubProject?.ProjectItems != null)
                        result.AddRange(GetDTEProjectsFromItems(item.SubProject.ProjectItems));
                }
                catch { }
            }
            return result;
        }

        public string BuildPromptPrefix(SolutionContextSnapshot snapshot, ContextMode mode, string? activeFile = null, string? selectionText = null)
        {
            var sb = new StringBuilder(4096);

            sb.AppendLine("You are an expert software engineer with complete access to the user's Visual Studio solution.");
            sb.AppendLine("You have been given the full relevant source code context below.");
            sb.AppendLine();

            sb.AppendLine($"SOLUTION: {snapshot.SolutionName}");
            sb.AppendLine($"Generated: {snapshot.GeneratedAt}");
            sb.AppendLine();

            if (_options.IncludeStructure && snapshot.Projects.Any())
            {
                sb.AppendLine("=== SOLUTION STRUCTURE ===");
                foreach (var proj in snapshot.Projects)
                {
                    sb.AppendLine($"- Project: {proj.Name}");
                    foreach (var f in proj.Files.Take(60)) // limit tree display
                    {
                        sb.AppendLine($"    • {f.RelativePath}");
                    }
                    if (proj.Files.Count > 60)
                        sb.AppendLine($"    ... ({proj.Files.Count - 60} more files)");
                }
                sb.AppendLine();
            }

            // Include actual file contents according to mode
            if (mode == ContextMode.Selection && !string.IsNullOrWhiteSpace(selectionText) && !string.IsNullOrWhiteSpace(activeFile))
            {
                sb.AppendLine("=== CURRENT SELECTION ===");
                sb.AppendLine($"File: {activeFile}");
                sb.AppendLine("```");
                sb.AppendLine(selectionText.Length > 8000 ? selectionText.Substring(0, 8000) + "\n...[truncated]" : selectionText);
                sb.AppendLine("```");
                sb.AppendLine();
            }
            else if ((mode == ContextMode.CurrentFile || mode == ContextMode.ActiveDocuments) && !string.IsNullOrWhiteSpace(activeFile))
            {
                sb.AppendLine("=== ACTIVE / CURRENT FILE ===");
                var match = FindFile(snapshot, activeFile);
                if (match != null && !string.IsNullOrEmpty(match.Content))
                {
                    sb.AppendLine($"File: {match.RelativePath}");
                    sb.AppendLine("```" + Path.GetExtension(activeFile).TrimStart('.'));
                    string content = match.Content;
                    if (content.Length > 12000) content = content.Substring(0, 12000) + "\n// ... [truncated for context window]";
                    sb.AppendLine(content);
                    sb.AppendLine("```");
                }
                else
                {
                    sb.AppendLine($"File path: {activeFile}");
                }
                sb.AppendLine();
            }
            else if (mode == ContextMode.FullSolution || mode == ContextMode.ActiveDocuments)
            {
                sb.AppendLine("=== SOURCE FILES ===");
                int included = 0;
                foreach (var proj in snapshot.Projects)
                {
                    foreach (var file in proj.Files.Where(f => f.Included))
                    {
                        if (included > 35) break; // safety for very large solutions
                        sb.AppendLine();
                        sb.AppendLine($"--- {file.RelativePath} ---");
                        string content = file.Content;
                        int limit = 6000;
                        if (content.Length > limit)
                            content = content.Substring(0, limit) + "\n// [content truncated to fit context budget]";

                        string lang = Path.GetExtension(file.FullPath).TrimStart('.');
                        sb.AppendLine("```" + lang);
                        sb.AppendLine(content);
                        sb.AppendLine("```");
                        included++;
                    }
                }

                if (snapshot.TotalFilesIncluded > included)
                {
                    sb.AppendLine();
                    sb.AppendLine($"[Note: {snapshot.TotalFilesIncluded - included} additional files were collected but truncated to respect context limits.]");
                }
            }

            sb.AppendLine();
            sb.AppendLine("=== INSTRUCTIONS ===");
            sb.AppendLine("Use the provided code context to answer accurately. ");
            sb.AppendLine("When referring to code, mention the file path. Prefer concise, actionable answers. ");
            sb.AppendLine("If asked to generate or modify code, produce complete compilable snippets where possible.");

            return sb.ToString();
        }

        private SolutionFileContext? FindFile(SolutionContextSnapshot snapshot, string path)
        {
            foreach (var p in snapshot.Projects)
            {
                var match = p.Files.FirstOrDefault(f =>
                    f.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                    f.RelativePath.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));

                if (match != null) return match;
            }
            return null;
        }
    }
}
