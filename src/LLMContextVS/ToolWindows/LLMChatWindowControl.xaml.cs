using Community.VisualStudio.Toolkit;
using LLMContextVS.Models;
using LLMContextVS.Options;
using LLMContextVS.Services;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace LLMContextVS.ToolWindows
{
    public partial class LLMChatWindowControl : UserControl
    {
        private readonly List<ChatMessage> _history = new();
        private readonly ChatHistoryService _chatHistoryService = new();
        private readonly LLMClient _client = new();
        private readonly SolutionContextService _contextService = new();
        private List<ChatSession> _loadedHistory = new();
        private CancellationTokenSource? _cts;
        private SolutionContextSnapshot? _lastSnapshot;

        public LLMChatWindowControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            VS.Events.SolutionEvents.OnAfterOpenSolution += OnAfterOpenSolution;
            await RefreshContextAsync(silent: true);
            LoadHistoryForCurrentSolution();
            UpdateStatus();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            SaveCurrentSessionIfAny();
            VS.Events.SolutionEvents.OnAfterOpenSolution -= OnAfterOpenSolution;
        }

        private async void OnAfterOpenSolution(Solution? solution)
        {
            SaveCurrentSessionIfAny();
            await RefreshContextAsync(silent: true);
            LoadHistoryForCurrentSolution();
            HideHistoryPanel();
        }

        private void UpdateStatus(string? message = null)
        {
            if (!string.IsNullOrEmpty(message))
            {
                StatusText.Text = message;
                return;
            }

            var opt = LLMOptions.Instance;
            StatusText.Text = $"{opt.Provider} • {opt.Model} • {opt.Endpoint}";
        }

        private async Task RefreshContextAsync(bool silent = false)
        {
            try
            {
                var mode = GetSelectedContextMode();
                _lastSnapshot = await _contextService.GetSolutionContextAsync(mode);

                int files = _lastSnapshot.TotalFilesIncluded;
                long chars = _lastSnapshot.TotalChars;
                ContextStats.Text = $"{files} files • ~{chars / 1000}k chars";

                if (!silent)
                    UpdateStatus($"Context refreshed: {files} files, {chars} chars");
            }
            catch (Exception ex)
            {
                UpdateStatus("Failed to collect context: " + ex.Message);
            }
        }

        private ContextMode GetSelectedContextMode()
        {
            return ContextModeCombo.SelectedIndex switch
            {
                0 => ContextMode.None,
                1 => ContextMode.CurrentFile,
                2 => ContextMode.Selection,
                3 => ContextMode.FullSolution,
                4 => ContextMode.ActiveDocuments,
                _ => ContextMode.FullSolution
            };
        }

        private async void ContextModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            await RefreshContextAsync(silent: true);
        }

        private void ClearChatBtn_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrentSessionIfAny();
            ClearCurrentChat();
            UpdateStatus();
        }

        private void HistoryBtn_Click(object sender, RoutedEventArgs e)
        {
            if (HistoryPanel.Visibility == Visibility.Visible)
            {
                HideHistoryPanel();
                return;
            }

            LoadHistoryForCurrentSolution();
            HistoryPanel.Visibility = Visibility.Visible;
        }

        private void CloseHistoryBtn_Click(object sender, RoutedEventArgs e)
        {
            HideHistoryPanel();
        }

        private void HistoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HistoryListBox.SelectedItem is not HistoryListItem item)
                return;

            RestoreSession(item.Session);
            HistoryListBox.SelectedIndex = -1;
        }

        private void SettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            // Opens the options page
            _ = VS.Commands.ExecuteAsync("Tools.Options", "ceLLMate.General");
        }

        private async void SendBtn_Click(object sender, RoutedEventArgs e) => await SendCurrentMessageAsync(forceFullContext: false);

        private async void SendFullBtn_Click(object sender, RoutedEventArgs e) => await SendCurrentMessageAsync(forceFullContext: true);

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        private async void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                await SendCurrentMessageAsync(forceFullContext: false);
            }
        }

        private async Task SendCurrentMessageAsync(bool forceFullContext)
        {
            string userText = InputBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(userText)) return;

            HideHistoryPanel();
            var mode = forceFullContext ? ContextMode.FullSolution : GetSelectedContextMode();

            // Add user message to UI
            AddMessageUI(ChatMessage.User(userText), isUser: true);
            _history.Add(ChatMessage.User(userText));

            InputBox.Clear();
            SendBtn.IsEnabled = false;
            CancelBtn.IsEnabled = true;
            UpdateStatus("Thinking...");

            string activeDoc = await GetActiveDocumentPathAsync();
            string? selection = await GetCurrentSelectionAsync();

            // Build context
            SolutionContextSnapshot snapshot = _lastSnapshot ?? await _contextService.GetSolutionContextAsync(mode, activeDoc, selection);
            _lastSnapshot = snapshot;

            string system = _contextService.BuildPromptPrefix(snapshot, mode, activeDoc, selection);
            string solutionRoot = string.IsNullOrWhiteSpace(snapshot.SolutionPath)
                ? string.Empty
                : Path.GetDirectoryName(snapshot.SolutionPath) ?? string.Empty;

            // Append recent history (last 6 turns)
            var recent = new StringBuilder();
            foreach (var m in _history.Skip(Math.Max(0, _history.Count - 6)))
            {
                if (m.Role == MessageRole.User)
                    recent.AppendLine("User: " + m.Content);
                else if (m.Role == MessageRole.Assistant)
                    recent.AppendLine("Assistant: " + m.Content);
            }

            string finalUser = userText;
            if (recent.Length > 0)
                finalUser = "Previous conversation:\n" + recent + "\n\nCurrent question:\n" + userText;

            // Assistant placeholder
            var assistantMsg = ChatMessage.Assistant("");
            _history.Add(assistantMsg);
            var assistantBlock = AddMessageUI(assistantMsg, isUser: false);

            _cts = new CancellationTokenSource();

            try
            {
                var sb = new StringBuilder();
                Func<string, Task> onDelta = async delta =>
                {
                    sb.Append(delta);
                    assistantMsg.Content = sb.ToString();
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    assistantBlock.Text = assistantMsg.Content;
                    ScrollToBottom();
                };

                if (LLMOptions.Instance.EnableAgentMode)
                {
                    await _client.StreamAgentAsync(system, finalUser, solutionRoot, onDelta, async activity =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        AddActivityUI(activity, assistantBlock);
                        ScrollToBottom();
                    }, _cts.Token);
                }
                else
                {
                    await _client.StreamChatAsync(system, finalUser, onDelta, _cts.Token);
                }

                UpdateStatus("Response complete.");
            }
            catch (OperationCanceledException)
            {
                assistantMsg.Content += "\n\n[Cancelled]";
                assistantBlock.Text = assistantMsg.Content;
                UpdateStatus("Cancelled.");
            }
            catch (Exception ex)
            {
                assistantMsg.Content += $"\n\n[Error: {ex.Message}]";
                assistantBlock.Text = assistantMsg.Content;
                UpdateStatus("Error calling LLM: " + ex.Message);
            }
            finally
            {
                SendBtn.IsEnabled = true;
                CancelBtn.IsEnabled = false;
                _cts = null;
                ScrollToBottom();
            }
        }

        private TextBlock AddMessageUI(ChatMessage msg, bool isUser)
        {
            var border = new Border
            {
                Background = isUser ? new SolidColorBrush(Color.FromRgb(230, 240, 255)) : new SolidColorBrush(Color.FromRgb(245, 245, 245)),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(4, 2, 4, 6),
                Padding = new Thickness(8),
                MaxWidth = 620,
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left
            };

            var tb = new TextBlock
            {
                Text = msg.Content,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas, Segoe UI, sans-serif"),
                FontSize = 12
            };

            if (isUser)
            {
                tb.FontWeight = FontWeights.SemiBold;
            }

            border.Child = tb;
            MessagesPanel.Children.Add(border);
            ScrollToBottom();
            return tb;
        }

        private void AddActivityUI(string activity, TextBlock assistantBlock)
        {
            var activityText = new TextBlock
            {
                Text = activity,
                TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic,
                Foreground = new SolidColorBrush(Color.FromRgb(110, 110, 110)),
                Margin = new Thickness(10, 0, 10, 4),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var assistantContainer = assistantBlock.Parent as UIElement;
            var insertIndex = assistantContainer == null
                ? MessagesPanel.Children.Count
                : Math.Max(0, MessagesPanel.Children.IndexOf(assistantContainer));

            MessagesPanel.Children.Insert(insertIndex, activityText);
        }

        private void AddInfoLabel(string text)
        {
            var label = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic,
                Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                Margin = new Thickness(8, 4, 8, 8),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            MessagesPanel.Children.Add(label);
        }

        private void RestoreSession(ChatSession session)
        {
            ClearCurrentChat();
            AddInfoLabel($"[Restored session from {session.Date:g}]");

            foreach (var message in session.Messages.Select(CloneMessage))
            {
                _history.Add(message);
                AddMessageUI(message, message.Role == MessageRole.User);
            }

            UpdateStatus($"Restored session from {session.Date:g}.");
        }

        private void SaveCurrentSessionIfAny()
        {
            if (_history.Count == 0)
                return;

            var solutionName = GetCurrentSolutionName();
            if (string.IsNullOrWhiteSpace(solutionName))
                return;

            try
            {
                _chatHistoryService.SaveSession(solutionName, _history);
                LoadHistoryForCurrentSolution();
            }
            catch (Exception ex)
            {
                UpdateStatus("Failed to save chat history: " + ex.Message);
            }
        }

        private void LoadHistoryForCurrentSolution()
        {
            var solutionName = GetCurrentSolutionName();
            if (string.IsNullOrWhiteSpace(solutionName))
            {
                _loadedHistory = new List<ChatSession>();
                PopulateHistoryList();
                return;
            }

            _loadedHistory = _chatHistoryService.LoadHistory(solutionName);
            PopulateHistoryList();
        }

        private void PopulateHistoryList()
        {
            var items = _loadedHistory
                .OrderByDescending(session => session.Date)
                .Select(session => new HistoryListItem
                {
                    Session = session,
                    DisplayText = $"{session.Date:g} — {GetFirstUserMessagePreview(session)}"
                })
                .ToList();

            HistoryListBox.ItemsSource = items;
            HistoryEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private string GetCurrentSolutionName()
        {
            var snapshot = _lastSnapshot;
            if (snapshot == null)
                return string.Empty;

            if (!string.IsNullOrWhiteSpace(snapshot.SolutionName))
                return snapshot.SolutionName;

            if (!string.IsNullOrWhiteSpace(snapshot.SolutionPath))
                return Path.GetFileNameWithoutExtension(snapshot.SolutionPath) ?? string.Empty;

            return string.Empty;
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

        private static string GetFirstUserMessagePreview(ChatSession session)
        {
            var preview = session.Messages
                .FirstOrDefault(message => message.Role == MessageRole.User)?.Content ?? "(No user message)";
            preview = preview.Replace("\r", " ").Replace("\n", " ").Trim();
            return preview.Length <= 80 ? preview : preview.Substring(0, 80) + "...";
        }

        private void ClearCurrentChat()
        {
            MessagesPanel.Children.Clear();
            _history.Clear();
            HideHistoryPanel();
        }

        private void HideHistoryPanel()
        {
            HistoryPanel.Visibility = Visibility.Collapsed;
        }

        private void ScrollToBottom()
        {
            ChatScroll.Dispatcher.InvokeAsync(() =>
            {
                ChatScroll.ScrollToEnd();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private async Task<string> GetActiveDocumentPathAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var docView = await VS.Documents.GetActiveDocumentViewAsync();
                return docView?.FilePath ?? "";
            }
            catch { return ""; }
        }

        private async Task<string?> GetCurrentSelectionAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var docView = await VS.Documents.GetActiveDocumentViewAsync();
                var _selection = docView?.TextView?.Selection;
                if (_selection != null && !_selection.IsEmpty && _selection.SelectedSpans.Count > 0)
                    return _selection.SelectedSpans[0].GetText();
                return null;
            }
            catch { return null; }
        }

        // Called from external commands to inject context + prompt
        public async Task SendPromptWithContextAsync(string prompt, ContextMode preferredMode, string? filePath = null, string? selection = null)
        {
            if (LLMOptions.Instance.AutoOpenChat)
            {
                // Already open since we are the control
            }

            var mode = preferredMode == ContextMode.None ? ContextMode.FullSolution : preferredMode;

            await RefreshContextAsync(silent: true);

            // Inject as if user typed
            InputBox.Text = prompt;
            await SendCurrentMessageAsync(forceFullContext: mode == ContextMode.FullSolution);
        }

        private sealed class HistoryListItem
        {
            public string DisplayText { get; set; } = string.Empty;
            public ChatSession Session { get; set; } = new ChatSession();
        }
    }
}
