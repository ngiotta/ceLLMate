using System;

namespace LLMContextVS.Models
{
    public enum MessageRole
    {
        System,
        User,
        Assistant
    }

    public class ChatMessage
    {
        public MessageRole Role { get; set; }
        public string Content { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;

        public bool IsUser => Role == MessageRole.User;
        public bool IsAssistant => Role == MessageRole.Assistant;

        public static ChatMessage User(string content) => new ChatMessage { Role = MessageRole.User, Content = content };
        public static ChatMessage Assistant(string content) => new ChatMessage { Role = MessageRole.Assistant, Content = content };
        public static ChatMessage System(string content) => new ChatMessage { Role = MessageRole.System, Content = content };
    }
}