using Community.VisualStudio.Toolkit;
using LLMContextVS.Models;
using LLMContextVS.Options;
using LLMContextVS.ToolWindows;
using Microsoft.VisualStudio.Shell;
using System.Threading.Tasks;

namespace LLMContextVS.Commands
{
    [Command(PackageGuids.CommandSetGuidString, PackageGuids.AskFullContextCmdId)]
    internal sealed class AskWithFullContextCommand : BaseCommand<AskWithFullContextCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string prompt = "Using the complete solution context, give me a high-level overview of the architecture of this codebase. Identify main layers, key projects, and how they interact. Then answer: what are the biggest opportunities for improvement?";

            var window = await LLMChatToolWindow.ShowAsync();
            var control = window?.Content as LLMChatWindowControl;

            if (control != null)
            {
                await control.SendPromptWithContextAsync(prompt, ContextMode.FullSolution);
            }
        }
    }
}