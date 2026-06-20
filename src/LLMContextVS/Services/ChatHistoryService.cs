using LLMContextVS.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LLMContextVS.Services
{
    public sealed class ChatSession
    {
        public DateTime Date { get; set; }
        public List<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
    }

    public sealed class ChatHistoryService
    {
        private const int MaxSessions = 20;

        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        public void SaveSession(string solutionName, List<ChatMessage> messages)
        {
            if (string.IsNullOrWhiteSpace(solutionName) || messages == null || messages.Count == 0)
                return;

            var history = LoadHistory(solutionName);
            history.Add(new ChatSession
            {
                Date = DateTime.Now,
                Messages = messages.Select(CloneMessage).ToList()
            });

            if (history.Count > MaxSessions)
            {
                history = history
                    .OrderBy(session => session.Date)
                    .Skip(Math.Max(0, history.Count - MaxSessions))
                    .ToList();
            }

            var historyPath = GetHistoryPath(solutionName);
            var historyDirectory = Path.GetDirectoryName(historyPath);
            if (!string.IsNullOrWhiteSpace(historyDirectory))
                Directory.CreateDirectory(historyDirectory);

            var serialized = JsonSerializer.Serialize(history.Select(ToDto).ToList(), SerializerOptions);
            File.WriteAllText(historyPath, serialized);
        }

        public List<ChatSession> LoadHistory(string solutionName)
        {
            if (string.IsNullOrWhiteSpace(solutionName))
                return new List<ChatSession>();

            var historyPath = GetHistoryPath(solutionName);
            if (!File.Exists(historyPath))
                return new List<ChatSession>();

            try
            {
                var serialized = File.ReadAllText(historyPath);
                var sessions = JsonSerializer.Deserialize<List<ChatSessionDto>>(serialized, SerializerOptions) ?? new List<ChatSessionDto>();
                return sessions
                    .Select(FromDto)
                    .Where(session => session.Messages.Count > 0)
                    .OrderBy(session => session.Date)
                    .ToList();
            }
            catch
            {
                return new List<ChatSession>();
            }
        }

        private static ChatSessionDto ToDto(ChatSession session)
        {
            return new ChatSessionDto
            {
                Date = session.Date,
                Messages = session.Messages.Select(message => new ChatMessageDto
                {
                    Role = ToRoleString(message.Role),
                    Content = message.Content ?? string.Empty
                }).ToList()
            };
        }

        private static ChatSession FromDto(ChatSessionDto session)
        {
            return new ChatSession
            {
                Date = session.Date,
                Messages = (session.Messages ?? new List<ChatMessageDto>())
                    .Select(message => new ChatMessage
                    {
                        Role = ParseRole(message.Role),
                        Content = message.Content ?? string.Empty,
                        Timestamp = session.Date
                    })
                    .ToList()
            };
        }

        private static ChatMessage CloneMessage(ChatMessage message)
        {
            return new ChatMessage
            {
                Role = message.Role,
                Content = message.Content,
                Timestamp = message.Timestamp
            };
        }

        private static MessageRole ParseRole(string role)
        {
            return role?.Trim().ToLowerInvariant() switch
            {
                "user" => MessageRole.User,
                "assistant" => MessageRole.Assistant,
                _ => MessageRole.System
            };
        }

        private static string ToRoleString(MessageRole role)
        {
            return role switch
            {
                MessageRole.User => "user",
                MessageRole.Assistant => "assistant",
                _ => "system"
            };
        }

        private static string GetHistoryPath(string solutionName)
        {
            var historyRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ceLLMate",
                "history");
            return Path.Combine(historyRoot, SanitizeFileName(solutionName) + ".json");
        }

        private static string SanitizeFileName(string solutionName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(solutionName
                .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
                .ToArray())
                .Trim();

            return string.IsNullOrWhiteSpace(sanitized) ? "unknown-solution" : sanitized;
        }

        private sealed class ChatSessionDto
        {
            public DateTime Date { get; set; }
            public List<ChatMessageDto> Messages { get; set; } = new List<ChatMessageDto>();
        }

        private sealed class ChatMessageDto
        {
            public string Role { get; set; } = string.Empty;
            public string Content { get; set; } = string.Empty;
        }
    }
}
