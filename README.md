# TurboPilot

A retro Windows desktop interface for modern language models, with streamed chat through the GitHub Copilot SDK or an OpenAI-compatible provider.

## Basic chat

Open **Session -> New Session**, choose an existing workspace and service, query the available models, and select a model, reasoning effort, and mode. **Begin Session** connects the runtime. Cloud chat uses your authenticated CLI profile; BYOK chat uses the configured endpoint and API key.

Send a prompt with **Send** or **Ctrl+Enter**. Sending while a response is running interrupts that turn before starting the replacement. **Stop** interrupts without sending another prompt. The attachment button manages the next prompt's files; the arrows recall earlier prompts and restore an unsent draft.

The **Model Output** heading sits above the Raw and Rendered tabs, and the **User Prompt** editor fills the lower pane. Drag the divider to adjust the split; both panes continue filling the window when it is resized.

The **Raw** tab preserves streamed conversation text. **Rendered** supports markdown and Mermaid diagrams, plus optional Mediator formatting of completed responses. Questions and permission requests appear in chat: reply with an option number or its text, or a freeform answer when offered. Invalid answers leave the question pending. Plan approval also happens in chat.

The status line shows `Starting..`, `Ready..`, `Working..`, or `Waiting..`, followed by context usage in whole Ki tokens. Cloud sessions also display whole AI Credits as `AiC=<value>`.

## Customization and permissions

**Session -> Customization** controls the enabled instructions, skills, agents, and MCP servers. **Apply Instructions** loads only enabled instruction bodies, retaining their file scopes. **Preload Skills** loads enabled skill bodies and registers their resource folders; turning it off also disables automatic skill loading. Custom agent modes load their prompts and tool restrictions.

Customization changes apply when starting or resuming a session. Automatic discovery is suppressed so it cannot restore unchecked items. Permissions remain live and apply to the next request. **Autopilot approves every permission request**, so use it only with a trusted workspace and tools.

## Saved sessions

**Session -> Past Sessions** searches saved sessions by ID, workspace, or prompt. **View** opens a transcript without connecting. **Resume** restores the original conversation, workspace, model settings, usage, and prompt history, using the current customization and permission selections. Ending a session or exiting preserves its history.

The window close button and **Session -> Exit** share one confirmation. Declining leaves the app running; accepting finishes cleanup before closing, without repeating the confirmation if another close request arrives.

Original transcripts, prepared rendered transcripts, and metadata are stored under `%LOCALAPPDATA%\TurboPilot\sessions`. Both output versions are restored by View and Resume. Runtime conversation state remains in the SDK's session storage. Provider credentials are not copied into transcript metadata; resuming a session that used an API key requires the matching endpoint and key in Settings. Missing history, connection failures, and save failures are reported explicitly.

## Local Mediator

**Session -> Mediator** lists text models compatible with the installed local execution providers. The preferred model is `phi-3.5-mini`. **Download Model** caches a model before it can be enabled; **Prepare Acceleration** installs available GPU/NPU execution providers and refreshes the catalog. CPU execution is available without that optional preparation.

Options are opt-in and saved only on **OK**. Downloads are retained when the dialog is canceled. Editable instructions and task skills live under `%LOCALAPPDATA%\TurboPilot\mediator`; **Open Configuration Folder** opens them for an external editor. Unedited copies are refreshed when a newer default ships; edited files are never overwritten. `defaults.json` in that folder records which default each copy came from.

The Mediator runs entirely on-device through the embedded Foundry Local SDK, without a local HTTP server or API key. Its options apply to the running session on OK:

| Option | Behavior |
| --- | --- |
| Reword Prompts | Removes redundant wording from prompts of at least 100 locally counted tokens (`minimumRewriteTokens` in `settings.json`); shorter prompts are sent unchanged. Rewrites must use fewer tokens and preserve protected paths, numbers, literals, negations, and ordering words such as before, after, until, and yet, compared as whole words; otherwise the original is sent. |
| Beautify Output | Prepares Rendered with validated file links and permitted local image previews, even when the local model is unavailable. The local model is asked only about each turn's final response, and only when a plain standalone line could become a heading; earlier messages in the turn receive links only. Raw remains unchanged. Code and existing links are preserved. |
| Maintain Summary | Updates a concise summary once per completed turn and on demand, batching the turn's prompts, answers, and responses into as few local calls as possible. Answers are recorded with their question; permission replies and tool-request messages without text are not. Tool results are kept as bounded excerpts in the worklog but are not summarized. Superseded decisions are replaced in the summary, not erased from history. |
| Monitor Output | Reviews each turn's final response against the latest request, the most recent summary, and short excerpts of that request's tool results, reporting possible repetition, contradictions, or unsupported claims with an excerpt. Warnings without an exact response excerpt are discarded. These are review hints, not proof of a hallucination. |
| Raw diagnostics | Shows local requests and responses in Raw only. They are not forwarded to the session model, written to its transcript, or shown in Rendered. |

The fixed local o200k tokenizer provides a consistent reduction comparison, not an exact billing estimate for every provider. Every local operation has a bounded context/output budget of at most 4,096 locally counted tokens, even when a model advertises a larger window, and a default 60-second timeout, including at most one correction of an invalid response schema. Content too large for the budget is skipped without counting as a failure. A failure retains original chat text and reports the problem below the input box and in the status line. Three consecutive failures disable local inference until options are reapplied or a new session starts. Stop and End Session cancel local work as well as the active remote turn; a new prompt cancels it only when interrupting a turn or when the prompt will be reworded.

**Session -> Mediator Summary** displays the current restart summary on demand. When Begin Session changes parameters in the same workspace, a separate confirmation offers to carry the summary forward. Accepted context is attached after the new session starts and accompanies its first user prompt; it does not trigger autonomous work or a synthetic visible conversation. Declining starts without the summary. Stale or unavailable summaries are not silently used.

Original worklog entries and summary state are stored under `%LOCALAPPDATA%\TurboPilot\workspaces\<workspace-key>\sessions\<session-id>`. The model cache and local runtime logs are under `%LOCALAPPDATA%\TurboPilot\foundry`. Turning the Mediator off does not stop conversation/worklog recording.

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

`--mediator-chat` runs the mediated runtime checks alone. `--mediator-native` queries compatible on-device models and exercises the default model if it is cached. Add `--download-mediator` to explicitly download that model first; the model stays cached for later use. The native check includes generation cancellation and all four processing contracts.
