# TurboPilot

A retro Windows desktop interface for modern language models, with streamed chat through the GitHub Copilot SDK or an OpenAI-compatible provider.

## Basic chat

Open **Session -> New Session**, choose an existing workspace and service, query the available models, and select a model, reasoning effort, and mode. **Begin Session** connects the runtime. Cloud chat uses your authenticated CLI profile; BYOK chat uses the configured endpoint and API key.

Send a prompt with **Send** or **Ctrl+Enter**. Sending while a response is running interrupts that turn before starting the replacement. **Stop** interrupts without sending another prompt. The attachment button manages the next prompt's files; the arrows recall earlier prompts and restore an unsent draft.

The **Raw** and **Rendered** tabs show the same transcript. Rendered supports markdown and Mermaid diagrams. Questions and permission requests appear in chat: reply with an option number or its text, or a freeform answer when offered. Invalid answers leave the question pending. Plan approval also happens in chat.

The status line shows `Starting..`, `Ready..`, `Working..`, or `Waiting..`, followed by context usage in whole Ki tokens. Cloud sessions also display whole AI Credits as `AiC=<value>`.

## Customization and permissions

**Session -> Customization** controls the enabled instructions, skills, agents, and MCP servers. **Apply Instructions** loads only enabled instruction bodies, retaining their file scopes. **Preload Skills** loads enabled skill bodies and registers their resource folders; turning it off also disables automatic skill loading. Custom agent modes load their prompts and tool restrictions.

Customization changes apply when starting or resuming a session. Automatic discovery is suppressed so it cannot restore unchecked items. Permissions remain live and apply to the next request. **Autopilot approves every permission request**, so use it only with a trusted workspace and tools.

## Saved sessions

**Session -> Past Sessions** searches saved sessions by ID, workspace, or prompt. **View** opens a transcript without connecting. **Resume** restores the original conversation, workspace, model settings, usage, and prompt history, using the current customization and permission selections. Ending a session or exiting preserves its history.

Display transcripts and metadata are stored under `%LOCALAPPDATA%\TurboPilot\sessions`. Runtime conversation state remains in the SDK's session storage. Provider credentials are not copied into transcript metadata; resuming a session that used an API key requires the matching endpoint and key in Settings. Missing history, connection failures, and save failures are reported explicitly.

## Local Mediator

**Session -> Mediator** lists text models compatible with the installed local execution providers. The preferred model is `phi-3.5-mini`. **Download Model** caches a model before it can be enabled; **Prepare Acceleration** installs available GPU/NPU execution providers and refreshes the catalog. CPU execution is available without that optional preparation.

Options are opt-in and saved only on **OK**. Downloads are retained when the dialog is canceled. Editable instructions and task skills live under `%LOCALAPPDATA%\TurboPilot\mediator`; **Open Configuration Folder** opens them for an external editor. Existing edits are never overwritten.

The embedded runtime and settings are available. Chat transformation, summary handoff, and monitoring integration are still in progress.

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
