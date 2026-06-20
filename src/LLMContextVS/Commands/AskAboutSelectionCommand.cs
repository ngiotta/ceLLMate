using Community.VisualStudio.Toolkit;
using LLMContextVS.Models;
using LLMContextVS.Options;
using LLMContextVS.ToolWindows;
using Microsoft.VisualStudio.Shell;
using System.Threading.Tasks;

namespace LLMContextVS.Commands
{
    [Command(PackageGuids.CommandSetGuidString, PackageGuids.AskWithSelectionCmdId)]
    internal sealed class AskAboutSelectionCommand : BaseCommand<AskAboutSelectionCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var doc = await VS.Documents.GetActiveDocumentViewAsync();
            string file = doc?.FilePath ?? "active document";
            var _sel = doc?.TextView?.Selection;
            string sel = (_sel != null && !_sel.IsEmpty && _sel.SelectedSpans.Count > 0)
                ? _sel.SelectedSpans[0].GetText()
                : "";

            string prompt = string.IsNullOrWhiteSpace(sel)
                ? "Explain the current file and suggest improvements."
                : "Explain the following code selection and suggest improvements, bug fixes, or refactors:\n\n" + sel;

            var window = await LLMChatToolWindow.ShowAsync();
            var control = window?.Content as LLMChatWindowControl;

            if (control != null)
            {
                await control.SendPromptWithContextAsync(prompt, ContextMode.Selection, file, sel);
            }
        }
    }
}