using Community.VisualStudio.Toolkit;
using LLMContextVS.Models;
using LLMContextVS.Options;
using LLMContextVS.ToolWindows;
using Microsoft.VisualStudio.Shell;
using System.Threading.Tasks;

namespace LLMContextVS.Commands
{
    [Command(PackageGuids.CommandSetGuidString, PackageGuids.AskAboutProjectCmdId)]
    internal sealed class AskAboutProjectCommand : BaseCommand<AskAboutProjectCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string prompt = "Analyze the selected project or folder. Describe its responsibilities, dependencies on other projects, and key entry points.";

            var window = await LLMChatToolWindow.ShowAsync();
            var control = window?.Content as LLMChatWindowControl;

            if (control != null)
            {
                await control.SendPromptWithContextAsync(prompt, ContextMode.FullSolution);
            }
        }
    }
}