using System;
using System.Collections.Generic;
using System.Linq;

namespace LLMContextVS.Models
{
    public class SolutionFileContext
    {
        public string FullPath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public bool Included { get; set; }
    }

    public class SolutionProjectContext
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public List<SolutionFileContext> Files { get; set; } = new List<SolutionFileContext>();
    }

    public class SolutionContextSnapshot
    {
        public string SolutionName { get; set; } = string.Empty;
        public string SolutionPath { get; set; } = string.Empty;
        public List<SolutionProjectContext> Projects { get; set; } = new List<SolutionProjectContext>();
        public int TotalFilesIncluded { get; set; }
        public long TotalChars { get; set; }
        public DateTime GeneratedAt { get; set; } = DateTime.Now;
    }
}