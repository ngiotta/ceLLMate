# LLM Companion - Full Solution Context for Local LLMs

A complete Visual Studio extension that gives local LLMs (Ollama, LM Studio, or any OpenAI-compatible server) **full context** of your Visual Studio solution.

Works great with Visual Studio 2022 and Visual Studio 2026.

***************************************************************************************************************************
HIGHLY RECOMMENDED THAT YOUR CODE IS IN GIT.  IF YOU ARE USING INFERIOR LLM'S, IT COULD WIPE OUT CODE YOU DON'T WANT IT TO!
---------------------------------------------------------------------------------------------------------------------------
ALL 6B AND EVEN SOME 13B MODELS AREN'T ENOUGH TO HANDLE MULTI-TURN TOOL CALLING
***************************************************************************************************************************


## Features

- **Full Solution Context**: Automatically traverses your solution and injects relevant source files, project structure, and metadata into prompts.
- **Configurable Context**: Control max size, ignored folders/files, allowed extensions, and more.
- **Streaming Chat**: Real-time responses in a dedicated tool window.
- **Context Modes**:
  - Full solution
  - Current file
  - Current selection
  - Open documents
  - None (pure chat)
- **Context Menu Integration**:
  - Right-click files/folders/projects → "Ask LLM about this..."
  - Editor selection → "Ask LLM about selection"
- **Works 100% Locally**: Your code never leaves your machine.

## Supported Local LLM Servers

| Provider     | Default Endpoint              | Notes                              |
|--------------|-------------------------------|------------------------------------|
| Ollama       | `http://localhost:11434/v1`   | Enable OpenAI compatibility (default on recent versions) |
| LM Studio    | `http://localhost:1234/v1`    | Excellent OpenAI-compatible server |
| Custom       | any `/v1` base URL            | Any server that speaks chat completions |

**Recommended models** (2026): `qwen2.5-coder`, `deepseek-coder`, `codellama`, `llama-3.1`, `command-r`, etc.

## Installation

### From Source (Recommended for now)

1. Open the solution in **Visual Studio 2022 or 2026**.
2. Make sure you have the **Visual Studio extension development** workload installed.
3. Build the `LLMContextVS` project in **Debug** or **Release**.
4. The `.vsix` is produced in `bin\Debug\` or `bin\Release\`.
5. Double-click the generated `.vsix` or use **Extensions → Manage Extensions → Install from VSIX**.

### Requirements

- Visual Studio 2022 (17.0+) or Visual Studio 2026 (18.0+)
- .NET Framework 4.7.2 runtime (usually already present)
- A running local LLM server (Ollama or LM Studio)

## Configuration

Go to **Tools → Options → LLM Companion → General**

Key settings:

- **Provider** / **Endpoint** / **Model**
- **Temperature** and **Max Output Tokens**
- **Max Context Characters** (very important for large solutions)
- **Ignored Patterns** and **Allowed Extensions** (semicolon separated)
- Default context mode

There is a **Test** button coming in a future update. For now just send a message and watch the Output window ("LLM Companion" pane) if you need diagnostics.

## Usage

1. Start Ollama or LM Studio and load a model.
2. In Visual Studio: **Tools → LLM Companion → Open Chat Window** (or use the command).
3. Choose context level from the dropdown in the chat window.
4. Type questions like:
   - "Give me an overview of the architecture"
   - "Where is authentication handled?"
   - "Refactor the user service to use dependency injection"
   - "Find all places that use the old config system"

Use **Ctrl+Enter** to send.

Context menu actions automatically open the chat window and attach appropriate context.

## How Context Collection Works

- Walks all projects using the Visual Studio DTE / solution APIs.
- Respects your ignore list and file size limits.
- Only includes text files with allowed extensions.
- Builds a compact tree + selected file contents.
- Truncates intelligently when hitting your configured character budget.
- Always includes a solution overview.

## Building & Developing

```powershell
# From the repo root
dotnet restore
msbuild src\LLMContextVS\LLMContextVS.csproj /p:Configuration=Debug /t:Build /p:DeployExtension=false
```

The output VSIX will be in `src\LLMContextVS\bin\Debug\`.

To debug the extension:

- Set the project as startup
- Debug → Start Debugging (it will launch an experimental instance of Visual Studio)

## Architecture Notes

- Uses `Community.VisualStudio.Toolkit.17` for simplified extensibility.
- Pure OpenAI `/v1/chat/completions` streaming for maximum compatibility.
- All context gathering happens on demand (no background indexing by default).
- WPF tool window + XAML for the chat UI.

## Roadmap / Ideas for Enhancements

- Context file picker / exclusion tree dialog
- Semantic search over the solution using local embeddings
- "Apply patch" button for generated code
- Multi-file edit suggestions
- Support for non-OpenAI native Ollama endpoints
- Token counting + cost estimation (even if local)
- Project-specific profiles

## License

MIT / Community project. Feel free to fork and improve.

## Credits

Built with the excellent [Community.VisualStudio.Toolkit](https://github.com/VsixCommunity/Community.VisualStudio.Toolkit).

---

Enjoy chatting with your entire codebase locally!
