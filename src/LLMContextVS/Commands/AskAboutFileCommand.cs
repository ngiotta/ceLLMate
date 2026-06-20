using Community.VisualStudio.Toolkit;
using EnvDTE;
using LLMContextVS.Models;
using LLMContextVS.Options;
using LLMContextVS.ToolWindows;
using Microsoft.VisualStudio.Shell;
using System.IO;
using System.Threading.Tasks;

namespace LLMContextVS.Commands
{
    [Command(PackageGuids.CommandSetGuidString, PackageGuids.AskAboutFileCmdId)]
    internal sealed class AskAboutFileCommand : BaseCommand<AskAboutFileCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = await VS.GetServiceAsync<DTE, DTE>();
            var selectedItems = dte?.SelectedItems;

            string? targetPath = null;
            string prompt = "Explain this file and its role in the project. Highlight important logic, dependencies, and potential improvements.";

            if (selectedItems != null && selectedItems.Count > 0)
            {
                foreach (SelectedItem item in selectedItems)
                {
                    if (item.ProjectItem != null)
                    {
                        string? file = item.ProjectItem.FileNames[1];
                        if (!string.IsNullOrEmpty(file) && File.Exists(file))
                        {
                            targetPath = file;
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(targetPath))
            {
                var doc = await VS.Documents.GetActiveDocumentViewAsync();
                targetPath = doc?.FilePath;
            }

            if (!string.IsNullOrEmpty(targetPath))
            {
                prompt = $"Explain the file \"{Path.GetFileName(targetPath)}\" in detail. Include its purpose, key classes/methods, and any notable patterns.";
            }

            await OpenChatAndSendAsync(prompt, ContextMode.CurrentFile, targetPath);
        }

        private static async Task OpenChatAndSendAsync(string prompt, ContextMode mode, string? file = null)
        {
            var window = await LLMChatToolWindow.ShowAsync();
            var control = window?.Content as LLMChatWindowControl;
            if (control != null)
            {
                await control.SendPromptWithContextAsync(prompt, mode, file);
            }
        }
    }
}
