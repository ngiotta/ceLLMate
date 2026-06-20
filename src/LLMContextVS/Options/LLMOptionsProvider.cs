using Community.VisualStudio.Toolkit;
using System.Runtime.InteropServices;

namespace LLMContextVS.Options
{
    /// <summary>
    /// Options page for ceLLMate. BaseOptionPage wires up the property grid
    /// automatically to the LLMOptions BaseOptionModel instance.
    /// </summary>
    [ComVisible(true)]
    public class LLMOptionsProvider : BaseOptionPage<LLMOptions> { }
}