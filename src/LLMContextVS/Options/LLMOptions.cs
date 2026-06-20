using Community.VisualStudio.Toolkit;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LLMContextVS.Options
{
    public class LLMOptions : BaseOptionModel<LLMOptions>
    {
        // --- Connection ---
        [Category("Connection")]
        [DisplayName("Provider")]
        [Description("Select your local LLM provider. Both Ollama and LM Studio support the OpenAI-compatible /v1/chat/completions API.")]
        [DefaultValue("Ollama")]
        [TypeConverter(typeof(ProviderConverter))]
        public string Provider { get; set; } = "Ollama";

        [Category("Connection")]
        [DisplayName("Endpoint Base URL")]
        [Description("Base URL for the chat completions endpoint (without trailing /chat/completions). Ollama default: http://localhost:11434/v1    |   LM Studio default: http://localhost:1234/v1")]
        [DefaultValue("http://localhost:11434/v1")]
        public string Endpoint { get; set; } = "http://localhost:11434/v1";

        [Category("Connection")]
        [DisplayName("Model Name")]
        [Description("Select from installed Ollama models (dropdown refreshes from the running Ollama server), or type any model name manually.")]
        [DefaultValue("llama3")]
        [TypeConverter(typeof(OllamaModelConverter))]
        public string Model { get; set; } = "llama3";

        // --- Generation params ---
        [Category("Generation")]
        [DisplayName("Temperature")]
        [Description("Controls randomness. 0.0 = deterministic, 0.7-0.9 typical for chat/code.")]
        [DefaultValue(0.7)]
        public double Temperature { get; set; } = 0.7;

        [Category("Generation")]
        [DisplayName("Max Output Tokens")]
        [Description("Maximum tokens the model should generate in a response.")]
        [DefaultValue(2048)]
        public int MaxTokens { get; set; } = 2048;

        // --- Context control ---
        [Category("Context")]
        [DisplayName("Max Context Characters")]
        [Description("Hard limit on total characters of source code + structure sent in a prompt (rough proxy for tokens). Larger values require models with big context windows.")]
        [DefaultValue(120000)]
        public int MaxContextChars { get; set; } = 120000;

        [Category("Context")]
        [DisplayName("Max File Size (KB)")]
        [Description("Individual source files larger than this will be skipped or heavily truncated.")]
        [DefaultValue(80)]
        public int MaxFileSizeKb { get; set; } = 80;

        [Category("Context")]
        [DisplayName("Include Solution Structure")]
        [Description("Always prepend a compact tree view of the solution/projects/files.")]
        [DefaultValue(true)]
        public bool IncludeStructure { get; set; } = true;

        [Category("Context")]
        [DisplayName("Ignored Patterns (semicolon separated)")]
        [Description("Folders and files matching these substrings or extensions will be excluded. Example: bin;obj;.git;node_modules;*.min.js;*.designer.cs")]
        [DefaultValue("bin;obj;.vs;.git;node_modules;packages;dist;build;out;target;*.exe;*.dll;*.pdb;*.min.js;*.min.css;*.map;*.designer.cs;*.g.cs")]
        public string IgnoredPatterns { get; set; } = "bin;obj;.vs;.git;node_modules;packages;dist;build;out;target;*.exe;*.dll;*.pdb;*.min.js;*.min.css;*.map;*.designer.cs;*.g.cs";

        [Category("Context")]
        [DisplayName("Allowed File Extensions (semicolon)")]
        [Description("Only files with these extensions are considered for full-text inclusion. Leave empty to allow all non-ignored text files.")]
        [DefaultValue(".cs;.vb;.xaml;.xml;.json;.yml;.yaml;.md;.txt;.sql;.js;.ts;.tsx;.jsx;.html;.css;.scss;.less;.py;.go;.rs;.java;.kt;.swift;.c;.cpp;.h;.hpp;.csproj;.sln;.props;.targets;.config")]
        public string AllowedExtensions { get; set; } = ".cs;.vb;.xaml;.xml;.json;.yml;.yaml;.md;.txt;.sql;.js;.ts;.tsx;.jsx;.html;.css;.scss;.less;.py;.go;.rs;.java;.kt;.swift;.c;.cpp;.h;.hpp;.csproj;.sln;.props;.targets;.config";

        // --- UX ---
        [Category("User Experience")]
        [DisplayName("Auto Open Chat on Ask Command")]
        [Description("When using context menu commands (Explain file, Ask about selection), automatically open the chat window.")]
        [DefaultValue(true)]
        public bool AutoOpenChat { get; set; } = true;

        [Category("User Experience")]
        [DisplayName("Enable Agent Mode")]
        [Description("Allow the model to autonomously read, search, list, and write files inside the current solution.")]
        [DefaultValue(false)]
        public bool EnableAgentMode { get; set; } = false;

        [Category("User Experience")]
        [DisplayName("Agent Max Iterations")]
        [Description("Maximum number of tool-call rounds the agent can perform per prompt. Increase for large tasks. 0 = unlimited (use with caution).")]
        [DefaultValue(50)]
        public int AgentMaxIterations { get; set; } = 50;

        [Category("User Experience")]
        [DisplayName("Default Context Mode")]
        [Description("What context to attach by default when you send a message from the chat window.")]
        [DefaultValue(ContextMode.FullSolution)]
        [TypeConverter(typeof(ContextModeConverter))]
        public ContextMode DefaultContextMode { get; set; } = ContextMode.FullSolution;
    }

    public enum ContextMode
    {
        None,
        CurrentFile,
        Selection,
        FullSolution,
        ActiveDocuments
    }

    internal class ProviderConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
            => new StandardValuesCollection(new[] { "Ollama", "LM Studio", "OpenAI Compatible (Custom)" });
    }

    internal class ContextModeConverter : EnumConverter
    {
        public ContextModeConverter() : base(typeof(ContextMode))
        {
        }
    }

    /// <summary>
    /// Populates the Model dropdown by querying the Ollama /api/tags endpoint.
    /// Falls back to a curated list if Ollama is not running.
    /// </summary>
    internal class OllamaModelConverter : StringConverter
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        private static readonly string[] _fallback =
        {
            "llama3:latest", "llama3.2:3b", "llama3.1:8b",
            "qwen2.5-coder:7b", "qwen2.5-coder:3b",
            "mistral:7b", "mistral-nemo:latest",
            "phi3.5:latest", "deepseek-coder-v2:16b", "codellama:7b"
        };

        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;

        public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => false; // allow typing custom values

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
        {
            var models = FetchOllamaModels();
            return new StandardValuesCollection(models.ToArray());
        }

        private static List<string> FetchOllamaModels()
        {
            try
            {
                // Derive the Ollama base from the configured endpoint, stripping /v1
                var endpoint = LLMOptions.Instance.Endpoint?.TrimEnd('/') ?? "http://localhost:11434/v1";
                var baseUrl = endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                    ? endpoint.Substring(0, endpoint.Length - 3)
                    : endpoint;

                var json = _http.GetStringAsync(baseUrl.TrimEnd('/') + "/api/tags").GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);
                var names = doc.RootElement
                    .GetProperty("models")
                    .EnumerateArray()
                    .Select(m => m.GetProperty("name").GetString() ?? string.Empty)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .OrderBy(n => n)
                    .ToList();

                return names.Count > 0 ? names : _fallback.ToList();
            }
            catch
            {
                return _fallback.ToList();
            }
        }
    }
}