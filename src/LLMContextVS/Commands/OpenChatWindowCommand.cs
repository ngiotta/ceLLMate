using Community.VisualStudio.Toolkit;
using LLMContextVS.ToolWindows;
using Microsoft.VisualStudio.Shell;
using System;
using System.Threading.Tasks;

namespace LLMContextVS.Commands
{
    [Command(PackageGuids.CommandSetGuidString, PackageGuids.OpenChatWindowCmdId)]
    internal sealed class OpenChatWindowCommand : BaseCommand<OpenChatWindowCommand>
    {
        protected override Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            return ShowChatWindowAsync();
        }

        public static async Task ShowChatWindowAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            await LLMChatToolWindow.ShowAsync();
        }
    }
}