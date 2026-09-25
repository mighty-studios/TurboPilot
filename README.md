# TurboPilot

TurboPilot is a retro Windows desktop client for language-model coding sessions. It provides streamed chat through the GitHub Copilot SDK or an OpenAI-compatible provider, with a narrow layout designed to sit beside an editor.

## Requirements

- Windows
- .NET 9 SDK or later
- WebView2 Runtime for Rendered output
- An authenticated Copilot CLI profile for cloud sessions, or an OpenAI-compatible endpoint for BYOK sessions

## Build and run

Initialize the theme submodule, then build the solution:

```powershell
git submodule update --init --recursive
dotnet build .\TurboPilot.sln
dotnet run --project .\src\TurboPilot
```

Start a session with **Session -> Begin Session**. Choose a workspace, provider, model, reasoning effort, and mode. Use **Ctrl+Enter** or **Send** to submit a prompt. Sending while a response is running interrupts that response before sending the new prompt.

TurboPilot displays streamed text in **Raw** and **Rendered** tabs. Rendered output supports Markdown, Mermaid diagrams, links, and local image previews. Questions and permission requests appear as reply cards; answer with a choice number, choice text, or the offered freeform response.

## Workspace tools

The **Tools** menu opens programs in the active workspace:

- **Open PowerShell** starts a shell with TurboPilot helper functions loaded.
- **Open Explorer** opens the workspace in File Explorer.
- **Open VSCode** opens the workspace with the `code` launcher.

## Commands

Type `/` in the prompt box to see available commands. Common commands include:

| Command | Action |
| --- | --- |
| `/help` | List commands |
| `/plan` | Show the current plan |
| `/attach` | Attach files to the next prompt |
| `/compact` | Compact the conversation context |
| `/reset` | Reset context while keeping session settings |
| `/session` | Begin or change the session |
| `/details` | Show active session settings |
| `/save` | Save the transcript |
| `/past` | Open saved sessions |

## Project context and instructions

When a new workspace contains a root `README.md` or `README.txt`, TurboPilot offers to attach it to an opening prompt. The prompt asks the model to understand the project and wait for instructions without changing files.

TurboPilot also appends the user-editable file below to new and resumed sessions:

```text
%LOCALAPPDATA%\TurboPilot\instructions\turbopilot.instructions.md
```

If the file does not exist, it is created from the bundled defaults. The defaults ask for Markdown responses, links, images, and Mermaid diagrams. Edit or empty the file to change those presentation instructions.

## Session management

- **Compact Context** summarizes the conversation without changing the visible transcript.
- **Reset Context** starts a fresh conversation with the same settings.
- **Past Sessions** lets you view, resume, or delete saved sessions. Use Ctrl or Shift to select multiple sessions for deletion.
- The active session cannot be deleted while it is running.
- Session summaries are generated when a session ends and shown in the Past Sessions list.
- Workspace changes are shown after each turn that modifies files. Files can be reviewed, compared, or reverted where Git history is available.

Sessions are stored under:

```text
%LOCALAPPDATA%\TurboPilot\sessions
```

Transcripts, rendered transcripts, and session metadata are saved separately. Provider credentials are not copied into session metadata.

## Regression checks

Run the dependency-free checks from the repository root:

```powershell
dotnet run --project .\tests\TurboPilot.Tests
```

Additional checks:

```powershell
dotnet run --project .\tests\TurboPilot.Tests -- --runtime --ui
```

- `--runtime` exercises the SDK runtime with a temporary loopback provider.
- `--ui` drives the WPF and WebView2 controls.
- `--session-features` checks links, workspace changes, and session hand-offs.
- `--ui-close` checks exit confirmation and cleanup.
- `--cloud` performs an optional authenticated cloud smoke test and may consume credits.
