# TurboPilot

A retro Windows desktop interface for modern language models, with streamed chat through the GitHub Copilot SDK or an OpenAI-compatible provider.

## Basic chat

Open **Session -> New Session**, choose an existing workspace and service, query the available models, and select a model, reasoning effort, and mode. **Begin Session** connects the runtime. Cloud chat uses your authenticated CLI profile; BYOK chat uses the configured endpoint and API key.

Send a prompt with **Send** or **Ctrl+Enter**. Sending while a response is running interrupts that turn before starting the replacement. **Stop** interrupts without sending another prompt. The attachment button manages the next prompt's files; the arrows recall earlier prompts and restore an unsent draft.

The **Model Output** heading sits above the Raw and Rendered tabs, and the **User Prompt** editor fills the lower pane. Drag the divider to adjust the split; both panes continue filling the window when it is resized.

The **Raw** tab preserves streamed conversation text. **Rendered** supports markdown and Mermaid diagrams, plus links and image previews for local files that replies mention. Questions and permission requests appear in chat: reply with an option number or its text, or a freeform answer when offered. Invalid answers leave the question pending. Plan approval also happens in chat.

The status line shows `Starting..`, `Ready..`, `Working..`, or `Waiting..`, followed by context usage in whole Ki tokens. Cloud sessions also display whole AI Credits as `AiC=<value>`.

## File links in Rendered output

With **Link Files in Rendered Output** checked in Session Settings (the default), each completed reply is prepared for Rendered without any model: file references that resolve to a readable file become links, and referenced images get inline previews. References resolve against the workspace; a bare file name or partial path such as `MainWindow.xaml` links when exactly one workspace file matches, skipping build and tool folders such as `bin`, `obj`, and `.git`. Code blocks, inline code that is not a path, existing links, and URLs are left as written, and Raw is unchanged.

## Changing a running session

**Session -> Settings** also changes a running session. The model, reasoning effort, Standard/Plan/Autopilot mode, and file-link setting apply to the running session, which keeps its conversation. Changes requested during a turn wait until that turn ends, or apply just before your next prompt if you interrupt it; choosing the running settings again withdraws a waiting change. Other changes start a new session: the service, endpoint, or key; instruction or skill loading; a custom agent mode; or a different context window for a BYOK model.

When the replaced session has a conversation, a confirmation offers to carry it forward. The current session model writes a hand-off summary, and your opening request and four most recent requests are quoted exactly, so constraints you stated are not lost to summarizing. The context accompanies the first prompt of the new session; it does not trigger autonomous work or appear in the transcript. Declining starts fresh.

## Customization and permissions

**Session -> Customization** controls the enabled instructions, skills, agents, and MCP servers. **Apply Instructions** loads only enabled instruction bodies, retaining their file scopes. **Preload Skills** loads enabled skill bodies and registers their resource folders; turning it off also disables automatic skill loading. Custom agent modes load their prompts and tool restrictions.

Customization changes apply when starting or resuming a session. Automatic discovery is suppressed so it cannot restore unchecked items. Permissions remain live and apply to the next request. **Autopilot approves every permission request**, so use it only with a trusted workspace and tools.

## Saved sessions

**Session -> Past Sessions** searches saved sessions by ID, workspace, or prompt. **View** opens a transcript without connecting. **Resume** restores the original conversation, workspace, model settings, usage, and prompt history, using the current customization and permission selections. Ending a session or exiting preserves its history.

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
