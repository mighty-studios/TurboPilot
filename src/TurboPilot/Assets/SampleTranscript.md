# TurboPilot Output Showcase

A sample transcript that exercises every themed element in both output
views. The **Raw** tab holds this text verbatim; the **Rendered** tab shows
the same bytes laid out by the Borland Vision theme. Replace this file's
contents once live transcript traffic takes over.

## 1. Headings

Headings step down the classic intensity ladder: yellow, white, cyan,
green, pink, then gray.

### H3 Cyan: section
#### H4 Green: subsection
##### H5 Pink: detail
###### H6 Gray: footnote

## 2. Inline styles 🎨

**Bold white** for the words that matter, *italic pink* for emphasis,
~~struck-through light red~~ for what the model dropped, and
`inline code` in yellow inside a black well. A keycap: <kbd>Ctrl</kbd> +
<kbd>Enter</kbd> sends the prompt.

> Block quotes are green and slanted, the way the help screens drew
> explanatory text. They carry a yellow left rule.

## 3. Links 🔗

- Web link: [TurboPilot on GitHub](https://github.com/mighty-studios/TurboPilot)
- File link (dotted rule, opens in the default editor):
  [README.md](kp-path:D%3A%5Cdev%5Cprojects%5CTurboPilot%5CTurboPilot%5CREADME.md)

## 4. Lists 📋

- Planning model
- Coding model
  - Vision model
  - Listening model

1. Pick a workspace
2. Pick a model per purpose
3. Send

Task list:

- [x] Dual output views
- [x] Raw is the source of truth
- [ ] Mediator prompt optimization
- [ ] Session history browser

## 5. Code 🧪

```csharp
// The Mediator reduces a rambling prompt into one clear instruction.
using Microsoft.AI.Foundry.Local;

var manager = await FoundryLocalManager.CreateAsync(new Configuration { AppName = "TurboPilot" });
var catalog = await manager.GetCatalogAsync();
var model = await catalog.GetModelAsync("phi-3.5-mini");

await model.DownloadAsync(progress => Console.WriteLine($"Downloaded {progress}%"));
var client = await model.GetChatClientAsync();

string Reduce(string prompt)
{
    // Never invent requirements the user did not state.
    return string.IsNullOrWhiteSpace(prompt) ? prompt : prompt.Trim();
}
```

## 6. Table 📦

| Purpose  | Model        | Host          | Streaming |
| -------- | ------------ | ------------- | :-------: |
| Planning | phi-3.5-mini | Foundry Local |    Yes    |
| Coding   | gpt-4o       | Cloud         |    Yes    |
| Vision   | llama-3.2-vl | Local         |    No     |
| Listening| whisper      | Local         |    No     |

## 7. Diagrams 🤖

```mermaid
flowchart TD
    U([User prompt]) --> M{Mediator}
    M -->|attachment| V[Vision or Listening pass]
    M -->|plain text| P[Reduce to one instruction]
    V --> P
    P --> R[Remote LLM]
    R --> O[Raw transcript]
    O --> W[Rendered WebView2]
```

```mermaid
sequenceDiagram
    participant U as User
    participant M as Mediator
    participant L as Remote LLM
    U->>M: Craft a prompt
    activate M
    M->>L: Optimized instruction
    activate L
    L-->>M: Streamed tokens
    deactivate L
    M-->>U: Raw plus Rendered
    deactivate M
    Note over U,M: Both tabs always show the same bytes
```

```mermaid
pie showData
    title Where the tokens went
    "Coding" : 62
    "Planning" : 21
    "Vision" : 11
    "Listening" : 6
```

---

Status line: ready ⏱ Done ✅ Broken ❌ Careful ⚠️
