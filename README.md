# TurboPilot

A retro Windows desktop interface for modern language models, with streamed chat through the GitHub Copilot SDK or an OpenAI-compatible provider.

## Basic chat

Open **Session -> Begin Session**, choose an existing workspace and service, query the available models, and select a model, reasoning effort, and mode. The primary button names what it will do: **Begin Session** starts one, **Apply Changes** adjusts the running session, and **OK** simply closes when nothing was changed. Cloud chat uses your authenticated CLI profile; BYOK chat uses the configured endpoint and API key. The BYOK host, port, path, and key are remembered while the CLI is selected, so returning to BYOK finds the last server already filled in.

Send a prompt with **Send** or **Ctrl+Enter**. Sending while a response is running interrupts that turn before starting the replacement. **Stop** interrupts without sending another prompt. The attachment button manages the next prompt's files; the arrows recall earlier prompts and restore an unsent draft.

The **Model Output** heading sits above the Raw and Rendered tabs, and the **User Prompt** editor fills the lower pane. Drag the divider to adjust the split; both panes continue filling the window when it is resized.

The **Raw** tab preserves streamed conversation text. **Rendered** supports markdown and Mermaid diagrams, plus links and image previews for local files that replies mention. Questions and permission requests appear in chat: reply with an option number or its text, or a freeform answer when offered. Invalid answers leave the question pending. Plan approval also happens in chat. In **Rendered**, a question or permission request is framed as a card with its choices numbered, tool steps and errors appear as tagged status lines, and each session starts under a banner; **Raw** keeps the same information as plain lines.

The status line shows `Starting..`, `Ready..`, `Working..`, or `Waiting..`, followed by context usage in whole Ki tokens. Cloud sessions also display whole AI Credits as `AiC=<value>`. Long model IDs and session IDs are shortened to keep the status line and the Past Sessions list at a readable width; hover either to see the full value.

## Seeing what changed on disk

At the end of every turn that touched the workspace, a card lists what changed:

```
Changes (3)
~ src/TurboPilot/MainWindow.xaml.cs   [compare] [undo]
+ src/TurboPilot/Tools/Sounds.cs      [compare] [undo]
- src/TurboPilot/Old.cs               [compare] [undo]
```

- Click the **path** to read the diff inline, in the transcript, beside the turn that made it.
- Click **[compare]** to hand the file to whatever `git difftool` is configured to open.
- Click **[undo]** to put the file back as it was before the turn. It asks first.
- Click **Review all** for every file the session has changed so far, in one dialog.

The list is taken from the workspace, not from what the agent said it did: a tool can write a file nobody was told about, and a reported edit can fail.

In a Git workspace the comparison point is a commit object made with `git stash create`, which records the working tree without touching it or the index. That means a file you had already modified before the turn is not blamed on the turn, and its later edit is still reported. Nothing is committed, staged, or stashed on your behalf.

Outside a Git workspace there is nothing to compare against, so the card lists which files changed and clicking one opens it. Compare and undo are not offered, because there is no earlier copy to offer them from.

A turn that changed nothing writes no card.

## Seeing what the agent did

Every tool the agent runs leaves a card in the transcript. The headline is the one line worth seeing at a glance: the shell command, the file being read, the pattern being searched. Click it to open the full arguments and whatever the tool reported back.

```
[+] powershell  git status --short
[+] view  src/TurboPilot/MainWindow.xaml.cs
[+] edit  src/TurboPilot/MainWindow.xaml.cs
```

An edit shows as the change it makes, with the removed lines marked `-` and the added lines `+`, rather than two quoted blocks to compare by eye. A tool that failed is colored as such and also writes a plain line to the transcript, so a failure is never reachable only by opening a card.

Detail is bounded: a tool handed a very large file leaves a card the same size as any other, with the cut marked.

## Typed commands
Type `/` in the prompt box and a list of commands opens above it. Arrow keys move, `Tab` or `Enter` completes, `Esc` dismisses. `Ctrl+Enter` still sends whatever is typed.

| Command | Does |
| --- | --- |
| `/help` | List these commands |
| `/plan` | Show the agent's current plan |
| `/attach` | Choose files to send with the next prompt |
| `/compact` | Summarize the conversation to reclaim context |
| `/reset` | Start the conversation over, keeping the settings |
| `/session` | Open settings to begin or change the session |
| `/details` | List what the session is running with |
| `/save` | Save the transcript to a file |
| `/past` | Reopen an earlier session |
| `/terminal` | Open a shell in the workspace |
| `/files` | Open the workspace folder |
| `/editor` | Open the workspace in the code editor |

A command is recognized only when the prompt is the command and nothing else. A message that mentions `/help`, or a path that begins with a slash, or anything spanning two lines, goes to the model unchanged. So does a command that is not in the list.

## Following the agent's plan

When the agent plans work as a list of steps, the status line says where it is:

```
Working.. [2/5] Writing tests    32Ki
```

The full list prints once in the transcript as a checklist card, and is corrected in place as the agent revises it rather than reprinted. `[x]` is finished, `[>]` is the step in progress, `[!]` is blocked, `[ ]` is not started. Each turn gets its own checklist. A plan of a single step is not reported: the position would say nothing the prompt did not.

## Audio cues

TurboPilot is built to sit in a narrow column beside an editor, which means you are usually reading the editor. **Play Sounds** in Session Settings (the default) chimes at the three moments worth looking back for: a prompt leaving, the model stopping to ask something, and a turn finishing. The stock Windows chimes are used, so the cues follow the system volume and mute. The setting takes effect at once and never restarts a session.

## Keeping the CLI current

A session backed by the Copilot CLI checks once per run whether a newer CLI has been released, and writes one transcript line when there is:

```
[update] Copilot CLI v1.0.37 -> v1.0.41 available. Open a Copilot terminal and run /update
```

Nothing is said when the CLI is current, when the check cannot reach GitHub, or when the session uses BYOK. The check never blocks startup.

## Managing context

**Session -> Compact Context** asks the session to summarize its own history, freeing the space that history occupied while keeping what the conversation established. **Session -> Reset Context** goes further and starts the session over with an empty history on the same settings. Both confirm first, both interrupt a running turn, and both leave the on-screen transcript intact: what you see is unchanged, only what the model still remembers is reduced. Neither is available until a session is ready.

## Opening a project

When a session starts on a workspace you were not already working in, TurboPilot looks for `README.md` (or `README.txt`) in the workspace root. If one is there, it offers to send it. Accepting attaches the file to an opening prompt that asks the model to read it, summarize the project in a few lines, and wait for your instructions without changing anything. Declining sends nothing.

The offer is skipped when the workspace has no readme in its root, when you resume a saved session, and when a restart carries a hand-off summary forward, since that session already knows the project. Restarting the same workspace to change a model or a setting does not ask again.

## File links in Rendered output

With **Link Files in Rendered Output** checked in Session Settings (the default), each completed reply is prepared for Rendered without any model: file references that resolve to a readable file become links, and referenced images get inline previews. References resolve against the workspace; a bare file name or partial path such as `MainWindow.xaml` links when exactly one workspace file matches, skipping build and tool folders such as `bin`, `obj`, and `.git`. Code blocks, inline code that is not a path, existing links, and URLs are left as written, and Raw is unchanged.

## Tools

The **Tools** menu opens an external program on the workspace folder of the running session. It is disabled until a session starts.

- **Open Powershell** opens a shell in the workspace with your helper functions loaded. PowerShell 7 (`pwsh.exe`) is used when it is on `PATH`, falling back to Windows PowerShell. The helper file is `%LOCALAPPDATA%\TurboPilot\scripts\scripts.ps1`, created from bundled defaults if missing and never overwritten when it exists; delete it to restore the defaults. It is dotted into the shell, so its functions stay defined for that window. Deleting it leaves a plain shell rather than blocking the tool.
- **Open Explorer** opens a File Explorer window on the workspace.
- **Open VSCode** opens Visual Studio Code on the workspace, using the `code` launcher on `PATH` (VS Code -> Command Palette -> "Install 'code' command in PATH").

Each program runs on its own. TurboPilot neither waits for it nor closes it, and a program it cannot start is reported without interrupting the session.

## Changing a running session

**Session -> Change Session** reopens the same settings dialog on a running session, and Customization and Permissions open from inside it. The model, reasoning effort, Standard/Plan/Autopilot mode, and file-link setting apply to the running session, which keeps its conversation. Accepting the dialog without changing anything does nothing at all. Changes requested during a turn wait until that turn ends, or apply just before your next prompt if you interrupt it; choosing the running settings again withdraws a waiting change. Other changes start a new session: the service, endpoint, or key; instruction or skill loading; a custom agent mode; or a different context window for a BYOK model.

When the replaced session has a conversation, a confirmation offers to carry it forward. The current session model writes a hand-off summary, and your opening request and four most recent requests are quoted exactly, so constraints you stated are not lost to summarizing. The context accompanies the first prompt of the new session; it does not trigger autonomous work or appear in the transcript. Declining starts fresh.

## Customization and permissions

TurboPilot appends its own presentation instructions after the enabled instructions and preloaded skills, without replacing the runtime's system instructions. The central, user-editable file is `%LOCALAPPDATA%\TurboPilot\instructions\turbopilot.instructions.md`, outside the workspace. It is created from bundled defaults if missing and never overwritten when it exists. The defaults require Markdown in every reply: a bold lead answer, sections, lists, tables for comparisons, backticks around literals, language-tagged fences, Mermaid diagrams for flows and structures, embedded images, file links, and URL links. Edit the file to change or relax any of it.

The file is read on every new or resumed session, even when **Apply Instructions** is off (that toggle controls customization instructions only). Edits do not change an already running session or a live model switch. An empty file intentionally supplies no app guidance; deleting it restores the defaults on the next start. An unreadable file or malformed YAML header reports an error and prevents startup rather than silently dropping the instructions.

**Session -> Customization** controls the enabled instructions, skills, agents, and MCP servers, and opens from the settings dialog. **Apply Instructions** loads only enabled instruction bodies, retaining their file scopes. **Preload Skills** loads enabled skill bodies and registers their resource folders; turning it off also disables automatic skill loading. Custom agent modes load their prompts and tool restrictions.

Customization changes apply when starting or resuming a session. Automatic discovery is suppressed so it cannot restore unchecked items. Permissions remain live and apply to the next request. **Autopilot approves every permission request**, so use it only with a trusted workspace and tools.

Selecting an item in the Customization tabs fills **Item Details**. **Add To Prompt** names that item for the next request: skills, agents, and MCP servers by the name the runtime knows them by, everything else by the file path the model can read. The picks are dropped into the prompt box when the settings dialog closes, under whatever is already typed, and left there to edit. Nothing is sent on your behalf, the same pick twice still counts once, and Cancel in Customization discards the picks along with the rest of the dialog.

**Session -> Session Details** writes what the running session is configured with into the transcript: the model, mode, and workspace; the instructions, skills, agents, and MCP servers actually loaded, each named with the file it came from; the presentation instructions file; and the folder grants and pre-approved operations in force. The settings dialog says what will be asked for, this says what was granted, and the two can differ after a resume or a customization edit. A switch that is off says so rather than showing an empty list.

## Saved sessions
**Session -> Past Sessions** searches saved sessions by ID, workspace, summary, or prompt. **View** opens a transcript without connecting. **Resume** restores the original conversation, workspace, model settings, usage, and prompt history.

Each row reads `date-time | workspace folder | summary`. The summary is one short label the model is asked for as the session ends, saying what was worked on: `Read and summarized README then wrote a short story about a robot assistant`. It is written as an action rather than a narration, because the subject is the same on every row and a list is scanned rather than read. The question and its answer are kept out of the window and out of the saved transcript, so the conversation ends where you left it. It is asked once per session end, skipped when nothing has been asked since the last one, skipped when the session is closed in the middle of a turn or while a hand-off is being written, and bounded so it cannot hold up a shutdown. An answer that does not amount to a label is discarded rather than shown, so a row never reads as a fragment. A session with no usable summary falls back to its opening prompt, and one with neither falls back to its ID. The model, mode, service, and full workspace path are in the details pane below the list, because they are the same across most rows and so tell one row from another least.

**Session -> Save Transcript...** writes the transcript to a file. The extension you pick decides the form: `.html` saves the Rendered tab as it stands, `.md` saves the Markdown it was built from, and anything else saves the Raw tab as plain text. The web page is standalone, with its styling and its font inlined and no script in it, so diagrams stay drawn, code stays colored, and the tool and change cards still open and shut in any browser. Saving a page needs the Rendered tab to be working; the text forms always do.

A session records the customization selections and permissions it ran under, so resuming returns to the setup you had rather than whatever is configured now. Restored permissions are noted in the transcript. Sessions saved before this was recorded fall back to the current selections. Exiting preserves a session's history.

The window close button and **Session -> Exit** share one confirmation. Declining leaves the app running; accepting finishes cleanup before closing, without repeating the confirmation if another close request arrives.

Original transcripts, prepared rendered transcripts, and metadata are stored under `%LOCALAPPDATA%\TurboPilot\sessions`. Both output versions are restored by View and Resume. Runtime conversation state remains in the SDK's session storage. Provider credentials are not copied into transcript metadata; resuming a session that used an API key requires the matching endpoint and key in Settings. Missing history, connection failures, and save failures are reported explicitly.

## Build

Windows and the .NET 9 SDK or later are required. WebView2 Runtime is required for rendered output.

```powershell
git submodule update --init --recursive
dotnet build .\TurboPilot.sln
```

## Regression checks

The dependency-free console runner uses the application's existing dependencies. Core checks do not require authentication or a model server:

```powershell
dotnet run --project .\tests\TurboPilot.Tests
```

Add `--runtime` to exercise the bundled SDK runtime against a temporary loopback provider, or `--ui` to drive real WPF controls and WebView2. These checks use isolated temporary workspaces and do not change application settings:

```powershell
dotnet run --project .\tests\TurboPilot.Tests -- --runtime --ui
```

The optional `--cloud` check sends one short synthetic prompt through the authenticated cloud service and may consume credits. It removes its test conversation afterward.

`--ui-close` exercises the real exit-confirmation and cleanup paths without the other desktop flows.

`--session-features` runs the file-link, live-change, and hand-off checks alone.
