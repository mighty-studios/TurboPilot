# Welcome to TurboPilot

Use the menu to start or resume a session with a local or cloud LLM model.
Once connected, write your prompts below and the reply will be presented here.

## 1. Settings

Opened by **Begin Session**; choices persist between runs.

- **Workspace folder**: the folder the session works in.
- **Model service**: *Copilot CLI* (bundled GitHub Copilot runtime) or *BYOK*, any OpenAI-compatible server addressed by host, port, API path, and key.
- **Models and options**: **Query Service** to list models, then pick a model, see its context window, and set reasoning effort.
- **Session**: pick a **Mode** (Standard, Plan, Autopilot, or a custom agent) and toggle Apply Instructions, Preload Skills, Link Files in Rendered Output, and Play Sounds.

## 2. Permissions

Controls what the model may do on its own. Scope is the active workspace while a session runs, or the application defaults when none is.

- **Folder grants**: paths outside the workspace it may Read, Write, or Read/Write. Use `*` and `?` within a name, and end a path with `\...` to include subfolders.
- **Operations allowed without asking**: toggles for write/edit files, read files, shell commands, MCP tools, MCP sampling, memory, custom tools, fetch URLs, and hooks. Unchecked means it stops for confirmation the first time.
- A workspace inherits the defaults until you change its toggles; **Use Defaults** restores that. **Save/Load Options** move an option set between workspaces.

## 3. Customizations

Adds your own items to a session.

- **Search paths**: folders scanned for items; your user folder and the workspace are included automatically.
- **Item types** (tabs): Prompts, Agents, Skills, Instructions, MCP Servers. Check to enable an item; select it to read its details.
- Enabled **Agents** show up as session Modes; enabled **Instructions** and **Skills** load per the Settings toggles.
- **Save/Load Options** carry the enabled-item choices (not the folder list).

## 4. Typed commands

Type `/` as the first non-space character in the User Prompt area to open the command list. Continue typing to filter it, then press Enter or Tab, or click an item, to complete the command. A command must occupy the whole prompt on one line; use `/help` to print the full command list in the transcript.

## 5. Prompt references

Type `[f` in the User Prompt area to choose a pending attachment and insert `[file:name]`. Type `[s` to choose an enabled skill loaded in the current session and insert `[skill:name]`. Continue typing the full prefix, such as `[file:read` or `[skill:dou`, to filter the list.
