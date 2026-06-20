using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace LLMContextVS
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideToolWindow(typeof(ToolWindows.LLMChatToolWindow.Pane))]
    [ProvideOptionPage(typeof(Options.LLMOptionsProvider), "ceLLMate", "General", 0, 0, true, SupportsProfiles = true)]
    [ProvideProfile(typeof(Options.LLMOptionsProvider), "ceLLMate", "General", 0, 0, true)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    public sealed class LLMContextVSPackage : ToolkitPackage
    {
        public const string PackageGuidString = "0b271c26-d001-4c5a-81c5-b66b74fc1727";
        public const string ChatToolWindowGuidString = "a2f5e3c1-7b2d-4f8a-9e1c-3d5b6f8a2c1e";

        public LLMContextVSPackage()
        {
        }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // This registers all commands decorated with the [Command] attribute in this assembly.
            await this.RegisterCommandsAsync();

            // Register tool windows so BaseToolWindow<T>.ShowAsync() can find them.
            this.RegisterToolWindows(GetType().Assembly);

            // Force options to load
            _ = Options.LLMOptions.Instance;

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        }
    }
}
