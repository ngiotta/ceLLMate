using System;

namespace LLMContextVS
{
    /// <summary>
    /// Defines command IDs and the command set GUID used by the extension.
    /// These must match the values in the .vsct file (if used) and in ProvideMenuResource.
    /// </summary>
    internal static class PackageGuids
    {
        // Command set (a unique GUID for our menus/commands)
        public const string CommandSetGuidString = "b8c4f2a1-9e3d-4a2f-8c1b-2d7e6f5a9c1b";
        public static readonly Guid CommandSetGuid = new Guid(CommandSetGuidString);

        // Command IDs (must be unique within the command set)
        public const int OpenChatWindowCmdId = 0x0100;
        public const int AskAboutFileCmdId = 0x0110;
        public const int AskAboutProjectCmdId = 0x0111;  // Used in .vsct
        public const int AskWithSelectionCmdId = 0x0120;
        public const int AskFullContextCmdId = 0x0130;
    }
}
