using LLMContextVS.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LLMContextVS.Services
{
    public class LLMClient : IDisposable
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        private readonly LLMOptions _options;
        private static readonly object[] _agentTools = BuildAgentTools();
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public LLMClient(LLMOptions? options = null)
        {
            _options = options ?? LLMOptions.Instance;
        }

        public async Task<string> SendChatAsync(string systemPrompt, string userMessage, CancellationToken cancellationToken = default)
        {
            var messages = new[]
            {
                new ChatApiMessage { Role = "system", Content = systemPrompt },
                new ChatApiMessage { Role = "user", Content = userMessage }
            };

            return await SendMessagesAsync(messages, stream: false, cancellationToken);
        }

        public async Task StreamChatAsync(
            string systemPrompt,
            string userMessage,
            Func<string, Task> onDelta,
            CancellationToken cancellationToken = default)
        {
            var messages = new[]
            {
                new ChatApiMessage { Role = "system", Content = systemPrompt },
                new ChatApiMessage { Role = "user", Content = userMessage }
            };

            await StreamMessagesAsync(messages, onDelta, cancellationToken);
        }

        // ReAct-style agent: works with any chat model regardless of tool-calling API support.
        // The model outputs structured TOOL_CALL blocks in text; we parse and execute them.
        public async Task StreamAgentAsync(
            string systemPrompt,
            string userMessage,
            string solutionRoot,
            Func<string, Task> onDelta,
            Func<string, Task> onToolActivity,
            CancellationToken ct)
        {
            var fileTools = new FileTools(solutionRoot);

            // Pre-fetch the file list and inject it so the model never needs to call list_files first.
            var initialFileList = fileTools.ListFiles("*.cs");
            await onToolActivity("📋 Fetching file list...");

            var messages = new List<ChatApiMessage>
            {
                new ChatApiMessage { Role = "system", Content = BuildReActSystemPrompt(systemPrompt, solutionRoot) },
                new ChatApiMessage
                {
                    Role = "user",
                    Content = userMessage + "\n\n" +
                              "Here is the current list of .cs files in the solution (you do NOT need to call list_files):\n" +
                              initialFileList
                }
            };

            var maxIter = LLMOptions.Instance.AgentMaxIterations;
            var seenToolCalls = new HashSet<string>();
            // Keep a sliding window of the last N tool turns to avoid context overflow.
            // Always preserve: [0]=system, [1]=initial user. Rotate from index 2 onward.
            const int MaxHistoryTurns = 8; // each turn = 2 messages (assistant + user)

            for (var iteration = 0; maxIter <= 0 || iteration < maxIter; iteration++)
            {
                ct.ThrowIfCancellationRequested();

                // Trim conversation: keep system + initial user + last MaxHistoryTurns*2 messages
                var trimmed = TrimMessages(messages, MaxHistoryTurns);

                var fullResponse = await GetNonStreamingResponseAsync(trimmed, ct);
                await OutputPane.WriteLineAsync("[ceLLMate Agent] Turn " + iteration + ": " + fullResponse.Substring(0, Math.Min(300, fullResponse.Length)));
                fullResponse = TruncateAtHallucinatedResult(fullResponse);

                var allToolCalls = ParseReActToolCalls(fullResponse);
                var toolCalls = allToolCalls.Take(1).ToList(); // only execute first

                if (toolCalls.Count == 0)
                {
                    if (iteration == 0)
                    {
                        // Model described steps instead of acting — force first tool call
                        await OutputPane.WriteLineAsync("[ceLLMate Agent] No tool call on turn 0 - forcing first action.");
                        messages.Add(new ChatApiMessage { Role = "assistant", Content = fullResponse });
                        messages.Add(new ChatApiMessage
                        {
                            Role = "user",
                            Content = "Stop planning. You already have the file list above. " +
                                      "Call read_file on the FIRST file in the list RIGHT NOW. " +
                                      "Output only: TOOL_CALL: read_file(path=\"<first file path>\")"
                        });
                        continue;
                    }

                    // No tool call = final answer
                    await onDelta(CleanReActResponse(fullResponse));
                    return;
                }

                // Show thinking text (before the TOOL_CALL line)
                var thinking = StripToolCallBlocks(fullResponse).Trim();
                if (!string.IsNullOrWhiteSpace(thinking))
                    await onDelta(thinking + "\n");

                var (toolName, toolArgs) = toolCalls[0];
                var callKey = toolName + "|" + toolArgs;

                string result;
                if (seenToolCalls.Contains(callKey))
                {
                    // Silently unblock — just tell the model to move on, don't give it a question to answer
                    result = $"You already have the result for this call. Move on to the next file or give your final answer.";
                    await OutputPane.WriteLineAsync("[ceLLMate Agent] Duplicate blocked: " + toolName);
                }
                else
                {
                    seenToolCalls.Add(callKey);
                    await onToolActivity($"🔧 {toolName}: {toolArgs.Substring(0, Math.Min(60, toolArgs.Length))}");
                    result = ExecuteReActTool(fileTools, toolName, toolArgs);
                    await OutputPane.WriteLineAsync($"[ceLLMate Tool Result] {toolName}: {result.Substring(0, Math.Min(200, result.Length))}");
                }

                messages.Add(new ChatApiMessage { Role = "assistant", Content = fullResponse });
                messages.Add(new ChatApiMessage
                {
                    Role = "user",
                    Content = $"TOOL_RESULT for {toolName}:\n{result}\n\nNext TOOL_CALL or final answer:"
                });
            }

            await StreamMessagesAsync(messages, onDelta, ct);
        }

        /// <summary>Keep system prompt + initial user message + last N turn pairs to prevent context overflow.</summary>
        private static IReadOnlyList<ChatApiMessage> TrimMessages(List<ChatApiMessage> messages, int maxTurns)
        {
            // messages[0] = system, messages[1] = initial user (with file list)
            // from index 2: alternating assistant/user pairs
            const int fixedCount = 2;
            if (messages.Count <= fixedCount + maxTurns * 2)
                return messages;
            var recent = messages.Skip(messages.Count - maxTurns * 2).ToList();
            return new[] { messages[0], messages[1] }.Concat(recent).ToList();
        }

        private async Task<string> GetNonStreamingResponseAsync(IReadOnlyList<ChatApiMessage> messages, CancellationToken ct)
        {
            var request = BuildRequest(messages, stream: false);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            return ExtractContentFromResponse(body);
        }

        private static readonly System.Text.RegularExpressions.Regex _toolCallRegex =
            new System.Text.RegularExpressions.Regex(
                @"TOOL_CALL\s*:\s*(\w+)\s*\(([^)]*)\)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

        private static List<(string Name, string Args)> ParseReActToolCalls(string text)
        {
            var results = new List<(string, string)>();
            foreach (System.Text.RegularExpressions.Match m in _toolCallRegex.Matches(text))
                results.Add((m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()));
            return results;
        }

        private static string StripToolCallBlocks(string text)
            => _toolCallRegex.Replace(text, string.Empty);

        // If the model hallucinated a TOOL_RESULT after its TOOL_CALL, truncate everything from
        // that point so we only see the real TOOL_CALL and the model stops generating beyond it.
        private static string TruncateAtHallucinatedResult(string text)
        {
            var idx = text.IndexOf("TOOL_RESULT", StringComparison.OrdinalIgnoreCase);
            return idx > 0 ? text.Substring(0, idx).TrimEnd() : text;
        }

        private static string CleanReActResponse(string text)
            => StripToolCallBlocks(text).Trim();

        private static string ExecuteReActTool(FileTools fileTools, string toolName, string args)
        {
            try
            {
                // Parse key=value pairs: path="...", content="..."
                var parsed = ParseSimpleArgs(args);
                return toolName.ToLowerInvariant() switch
                {
                    "read_file"       => fileTools.ReadFile(Get(parsed, "path")),
                    "write_file"      => fileTools.WriteFile(Get(parsed, "path"), Get(parsed, "content")),
                    "search_in_files" => fileTools.SearchInFiles(Get(parsed, "query"), Get(parsed, "file_pattern", "*.cs")),
                    "list_directory"  => fileTools.ListDirectory(Get(parsed, "path")),
                    "list_files"      => fileTools.ListFiles(Get(parsed, "file_pattern", "*.cs")),
                    "run_command"     => fileTools.RunCommand(Get(parsed, "command")),
                    _                 => $"Unknown tool: {toolName}"
                };
            }
            catch (Exception ex) { return "Tool error: " + ex.Message; }
        }

        private static Dictionary<string, string> ParseSimpleArgs(string args)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Match: key="value" or key=value
            var re = new System.Text.RegularExpressions.Regex(@"(\w+)\s*=\s*""([^""]*)""|(\w+)\s*=\s*(\S+)");
            foreach (System.Text.RegularExpressions.Match m in re.Matches(args))
            {
                if (m.Groups[1].Success) d[m.Groups[1].Value] = m.Groups[2].Value;
                else if (m.Groups[3].Success) d[m.Groups[3].Value] = m.Groups[4].Value;
            }
            return d;
        }

        private static string Get(Dictionary<string, string> d, string key, string fallback = "")
            => d.TryGetValue(key, out var v) ? v : fallback;

        private static string BuildReActSystemPrompt(string systemPrompt, string solutionRoot)
        {
            var rootInfo = string.IsNullOrWhiteSpace(solutionRoot)
                ? string.Empty
                : $"\nSolution root directory: {solutionRoot}\nUse this root as the base for all paths.\n";

            return systemPrompt +
                "\n\n== AGENT MODE ==\n" +
                "You are an autonomous agent that can read, write, and search files in the codebase.\n" +
                rootInfo +
                "\nTo use a tool, output EXACTLY one line like this, then STOP:\n" +
                "  TOOL_CALL: tool_name(key=\"value\", key2=\"value2\")\n\n" +
                "Available tools:\n" +
                "  TOOL_CALL: list_files(file_pattern=\"*.cs\")            <- get all file paths matching pattern (no content)\n" +
                "  TOOL_CALL: list_files(file_pattern=\"*.cs\")            <- get all file paths matching a pattern (paths only, no content)\n" +
                "  TOOL_CALL: list_directory(path=\"<dir>\")               <- list immediate children of a directory\n" +
                "  TOOL_CALL: read_file(path=\"<file>\")                   <- read a file\n" +
                "  TOOL_CALL: search_in_files(query=\"<text>\", file_pattern=\"*.cs\")  <- find lines containing text\n" +
                "  TOOL_CALL: write_file(path=\"<file>\", content=\"<full content>\")   <- overwrite a file\n" +
                "  TOOL_CALL: run_command(command=\"<cmd>\")               <- run any shell command (dotnet build, git status, dotnet test, etc.)\n\n" +
                "Workflow rules:\n" +
                "- To explore: use list_files(file_pattern=\"*.cs\") to get all source file paths, then read_file for each.\n" +
                "- After making code changes: verify with run_command(command=\"dotnet build\")  fix any errors before finishing.\n" +
                "- To check compile errors first: run_command(command=\"dotnet build\").\n" +
                "- Do NOT call the same tool with the same arguments twice.\n" +
                "- Output ONE TOOL_CALL per turn, then STOP. The system returns the real result next turn.\n" +
                "- NEVER write TOOL_RESULT yourself. The system provides it.\n" +
                "- Do NOT describe plans. Act immediately.\n" +
                "- When all work is done, give your final summary WITHOUT any TOOL_CALL lines.";
        }
        private async Task<string> SendMessagesAsync(IReadOnlyList<ChatApiMessage> messages, bool stream, CancellationToken ct)
        {
            var request = BuildRequest(messages, stream);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync();
            return ExtractContentFromResponse(body);
        }

        private async Task StreamMessagesAsync(IReadOnlyList<ChatApiMessage> messages, Func<string, Task> onDelta, CancellationToken ct)
        {
            await StreamCompletionAsync(messages, onDelta, null, null, ct);
        }

        private async Task<StreamCompletionResult> StreamCompletionAsync(
            IReadOnlyList<ChatApiMessage> messages,
            Func<string, Task> onDelta,
            object[]? tools,
            string? toolChoice,
            CancellationToken ct)
        {
            var request = BuildRequest(messages, stream: true, tools, toolChoice);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            var content = new StringBuilder();
            var toolCalls = new Dictionary<int, ToolCallAccumulator>();

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    continue;
                }

                var data = line.Substring(6).Trim();
                if (data == "[DONE]")
                {
                    break;
                }

                ChatCompletionChunk? chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize<ChatCompletionChunk>(data);
                }
                catch
                {
                    continue;
                }

                var delta = chunk?.Choices?.FirstOrDefault()?.Delta;
                if (delta == null)
                {
                    continue;
                }

                var deltaContent = delta.Content;
                if (!string.IsNullOrEmpty(deltaContent))
                {
                    var chunkText = deltaContent!;
                    content.Append(chunkText);
                    await onDelta(chunkText);
                }

                if (delta.ToolCalls == null)
                {
                    continue;
                }

                foreach (var toolCall in delta.ToolCalls)
                {
                    if (toolCall == null)
                    {
                        continue;
                    }

                    if (!toolCalls.TryGetValue(toolCall.Index, out var accumulator))
                    {
                        accumulator = new ToolCallAccumulator();
                        toolCalls[toolCall.Index] = accumulator;
                    }

                    var toolCallId = toolCall.Id;
                    if (!string.IsNullOrWhiteSpace(toolCallId))
                    {
                        accumulator.Id = toolCallId!;
                    }

                    var toolCallType = toolCall.Type;
                    if (!string.IsNullOrWhiteSpace(toolCallType))
                    {
                        accumulator.Type = toolCallType!;
                    }

                    var functionName = toolCall.Function?.Name;
                    if (!string.IsNullOrWhiteSpace(functionName))
                    {
                        accumulator.Name = functionName!;
                    }

                    var functionArguments = toolCall.Function?.Arguments;
                    if (!string.IsNullOrEmpty(functionArguments))
                    {
                        accumulator.Arguments.Append(functionArguments);
                    }
                }
            }

            return new StreamCompletionResult
            {
                Content = content.ToString(),
                ToolCalls = toolCalls
                    .OrderBy(kvp => kvp.Key)
                    .Select(kvp => kvp.Value.ToToolCall(kvp.Key))
                    .ToList()
            };
        }

        private HttpRequestMessage BuildRequest(IReadOnlyList<ChatApiMessage> messages, bool stream, object[]? tools = null, string? toolChoice = null)
        {
            string url = _options.Endpoint.TrimEnd('/') + "/chat/completions";

            var payload = new ChatCompletionRequest
            {
                Model = _options.Model,
                Messages = messages,
                Temperature = _options.Temperature,
                MaxTokens = _options.MaxTokens,
                Stream = stream,
                Tools = tools,
                ToolChoice = toolChoice
            };

            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            req.Headers.Add("Authorization", "Bearer lm-studio");
            return req;
        }

        private static string ExecuteToolCall(FileTools fileTools, ChatApiToolCall toolCall)
        {
            try
            {
                using var args = JsonDocument.Parse(toolCall.Function?.Arguments ?? "{}");
                var root = args.RootElement;

                return toolCall.Function?.Name switch
                {
                    "read_file" => fileTools.ReadFile(GetString(root, "path")),
                    "write_file" => fileTools.WriteFile(GetString(root, "path"), GetString(root, "content")),
                    "search_in_files" => fileTools.SearchInFiles(GetString(root, "query"), GetString(root, "file_pattern")),
                    "list_directory" => fileTools.ListDirectory(GetString(root, "path")),
                    _ => "Unknown tool: " + (toolCall.Function?.Name ?? "(missing)")
                };
            }
            catch (Exception ex)
            {
                return "Tool execution failed: " + ex.Message;
            }
        }

        private static string BuildToolActivity(ChatApiToolCall toolCall)
        {
            try
            {
                using var args = JsonDocument.Parse(toolCall.Function?.Arguments ?? "{}");
                var root = args.RootElement;
                return toolCall.Function?.Name switch
                {
                    "read_file" => "📄 Reading: " + GetString(root, "path"),
                    "write_file" => "✍ Writing: " + GetString(root, "path"),
                    "search_in_files" => $"🔎 Searching: {GetString(root, "query")} ({GetString(root, "file_pattern")})",
                    "list_directory" => "📂 Listing dir: " + GetString(root, "path"),
                    "list_files" => "📋 Listing files: " + GetString(root, "file_pattern"),
                    "run_command" => "⚙ Running: " + GetString(root, "command"),
                    _ => "🛠 Tool: " + (toolCall.Function?.Name ?? "unknown")
                };
            }
            catch
            {
                return "🛠 Tool: " + (toolCall.Function?.Name ?? "unknown");
            }
        }

        private static string BuildAgentSystemPrompt(string systemPrompt)
        {
            return systemPrompt +
                "\r\n\r\n== AGENT MODE ==\r\n" +
                "You have access to tools that interact with the codebase. " +
                "You MUST use these tools to perform tasks rather than describing what to do.\r\n\r\n" +
                "Rules:\r\n" +
                "- ALWAYS call tools first. Do not guess or list steps.\r\n" +
                "- To check for errors: use read_file and search_in_files on actual source files.\r\n" +
                "- To explore: use list_directory and read_file.\r\n" +
                "- To fix issues: use write_file to apply changes directly.\r\n" +
                "- Do not describe what you would do. Just do it using the tools.\r\n" +
                "- Stay within the solution directory at all times.\r\n\r\n" +
                "Start every response by calling at least one tool.";
        }

        private static string GetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

        private static string ExtractContentFromResponse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var content = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                return content ?? string.Empty;
            }
            catch
            {
                return json;
            }
        }

        private static object[] BuildAgentTools()
        {
            return new object[]
            {
                new
                {
                    type = "function",
                    function = new
                    {
                        name = "read_file",
                        description = "Read the contents of a file",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                path = new { type = "string", description = "Absolute path to the file to read" }
                            },
                            required = new[] { "path" }
                        }
                    }
                },
                new
                {
                    type = "function",
                    function = new
                    {
                        name = "write_file",
                        description = "Write or replace the contents of a file",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                path = new { type = "string", description = "Absolute path to the file to write" },
                                content = new { type = "string", description = "Full file contents to write" }
                            },
                            required = new[] { "path", "content" }
                        }
                    }
                },
                new
                {
                    type = "function",
                    function = new
                    {
                        name = "search_in_files",
                        description = "Search for text in files matching a file pattern",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                query = new { type = "string", description = "Text to search for" },
                                file_pattern = new { type = "string", description = "File pattern such as *.cs or *.xaml" }
                            },
                            required = new[] { "query", "file_pattern" }
                        }
                    }
                },
                new
                {
                    type = "function",
                    function = new
                    {
                        name = "list_directory",
                        description = "List files and directories in a directory",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                path = new { type = "string", description = "Absolute path to the directory to list" }
                            },
                            required = new[] { "path" }
                        }
                    }
                }
            };
        }

        public void Dispose()
        {
        }

        private class ChatApiMessage
        {
            [JsonPropertyName("role")]
            public string Role { get; set; } = "user";

            [JsonPropertyName("content")]
            public string? Content { get; set; }

            [JsonPropertyName("tool_call_id")]
            public string? ToolCallId { get; set; }

            [JsonPropertyName("tool_calls")]
            public List<ChatApiToolCall>? ToolCalls { get; set; }
        }

        private class ChatApiToolCall
        {
            [JsonPropertyName("id")]
            public string Id { get; set; } = string.Empty;

            [JsonPropertyName("type")]
            public string Type { get; set; } = "function";

            [JsonPropertyName("function")]
            public ChatApiFunctionCall Function { get; set; } = new ChatApiFunctionCall();
        }

        private class ChatApiFunctionCall
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("arguments")]
            public string Arguments { get; set; } = string.Empty;
        }

        private class ChatCompletionRequest
        {
            [JsonPropertyName("model")]
            public string Model { get; set; } = string.Empty;

            [JsonPropertyName("messages")]
            public IReadOnlyList<ChatApiMessage> Messages { get; set; } = Array.Empty<ChatApiMessage>();

            [JsonPropertyName("temperature")]
            public double Temperature { get; set; }

            [JsonPropertyName("max_tokens")]
            public int MaxTokens { get; set; }

            [JsonPropertyName("stream")]
            public bool Stream { get; set; }

            [JsonPropertyName("tools")]
            public object[]? Tools { get; set; }

            [JsonPropertyName("tool_choice")]
            public string? ToolChoice { get; set; }
        }

        private class ChatCompletionChunk
        {
            [JsonPropertyName("choices")]
            public Choice[]? Choices { get; set; }

            public class Choice
            {
                [JsonPropertyName("delta")]
                public Delta? Delta { get; set; }
            }

            public class Delta
            {
                [JsonPropertyName("content")]
                public string? Content { get; set; }

                [JsonPropertyName("tool_calls")]
                public ToolCallDelta[]? ToolCalls { get; set; }
            }

            public class ToolCallDelta
            {
                [JsonPropertyName("index")]
                public int Index { get; set; }

                [JsonPropertyName("id")]
                public string? Id { get; set; }

                [JsonPropertyName("type")]
                public string? Type { get; set; }

                [JsonPropertyName("function")]
                public FunctionDelta? Function { get; set; }
            }

            public class FunctionDelta
            {
                [JsonPropertyName("name")]
                public string? Name { get; set; }

                [JsonPropertyName("arguments")]
                public string? Arguments { get; set; }
            }
        }

        private class StreamCompletionResult
        {
            public string Content { get; set; } = string.Empty;
            public List<ChatApiToolCall> ToolCalls { get; set; } = new List<ChatApiToolCall>();
        }

        private class ToolCallAccumulator
        {
            public string Id { get; set; } = string.Empty;
            public string Type { get; set; } = "function";
            public string Name { get; set; } = string.Empty;
            public StringBuilder Arguments { get; } = new StringBuilder();

            public ChatApiToolCall ToToolCall(int index)
            {
                return new ChatApiToolCall
                {
                    Id = string.IsNullOrWhiteSpace(Id) ? $"call_{index}" : Id,
                    Type = string.IsNullOrWhiteSpace(Type) ? "function" : Type,
                    Function = new ChatApiFunctionCall
                    {
                        Name = Name,
                        Arguments = Arguments.ToString()
                    }
                };
            }
        }
    }
}
