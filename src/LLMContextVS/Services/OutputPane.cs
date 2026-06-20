using Community.VisualStudio.Toolkit;
using System;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System.Threading.Tasks;

namespace LLMContextVS.Services
{
    public static class OutputPane
    {
        private static IVsOutputWindowPane? _pane;

        public static async Task WriteLineAsync(string text)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_pane == null)
            {
                var outWindow = await VS.Services.GetOutputWindowAsync();
                if (outWindow != null)
                {
                    var guid = new Guid("A7F8C3D2-1E4F-4A2B-9B5D-8E6C7F1A2B3C");
                    outWindow.CreatePane(ref guid, "ceLLMate", 1, 1);
                    outWindow.GetPane(ref guid, out _pane);
                }
            }

            _pane?.OutputStringThreadSafe(text + "\r\n");
        }

        public static async Task ClearAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _pane?.Clear();
        }
    }
}
