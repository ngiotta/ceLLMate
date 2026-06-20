using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace LLMContextVS.ToolWindows
{
    [Guid(LLMContextVSPackage.ChatToolWindowGuidString)]
    public class LLMChatToolWindow : BaseToolWindow<LLMChatToolWindow>
    {
        public override string GetTitle(int toolWindowId) => "ceLLMate";

        public override Type PaneType => typeof(Pane);

        public override async Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            // Pass a fresh view model / control
            return await Task.FromResult(new LLMChatWindowControl());
        }

        [Guid("a2f5e3c1-7b2d-4f8a-9e1c-3d5b6f8a2c1e")]
        public class Pane : ToolWindowPane
        {
            public Pane()
            {
                // Bitmap image for the tab if desired
                BitmapImageMoniker = Microsoft.VisualStudio.Imaging.KnownMonikers.StatusInformation;
            }
        }
    }
}
